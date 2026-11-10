using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DeskBox.Models;

namespace DeskBox.Services;

internal sealed class DidaApiException : Exception
{
    internal DidaApiException(string message, bool tokenRejected = false) : base(message)
    {
        TokenRejected = tokenRejected;
    }

    internal bool TokenRejected { get; }
}

/// <summary>
/// Thin client over the Dida365 Open API (api.dida365.com/open/v1).
/// Every call is a plain JSON round-trip so it stays Native AOT friendly:
/// no reflection-based serializers, only <see cref="JsonNode"/>.
/// </summary>
internal sealed class DidaApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private readonly string _baseUrl;
    private readonly string _token;

    internal DidaApiClient(string baseUrl, string token)
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? DidaSyncConfig.DefaultBaseUrl
            : baseUrl.TrimEnd('/');
        _token = token;
    }

    internal Task<JsonArray> ListProjectsAsync(CancellationToken token) =>
        SendArrayAsync(HttpMethod.Get, "/project", null, token);

    internal async Task<JsonArray> GetOpenTasksAsync(string projectId, CancellationToken token)
    {
        JsonNode? data = await SendAsync(
            HttpMethod.Get,
            "/project/" + Uri.EscapeDataString(projectId) + "/data",
            null,
            token).ConfigureAwait(false);

        return data is JsonObject obj && obj["tasks"] is JsonArray tasks ? tasks : new JsonArray();
    }

    internal async Task<JsonArray> GetCompletedTasksAsync(
        string projectId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken token) =>
        await GetCompletedTasksAsync(new[] { projectId }, start, end, token).ConfigureAwait(false);

    /// <summary>
    /// One round-trip for every synced project: /task/completed accepts a list,
    /// so a multi-project scope still costs a single request.
    /// </summary>
    internal async Task<JsonArray> GetCompletedTasksAsync(
        IReadOnlyList<string> projectIds,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken token)
    {
        var ids = new JsonArray();
        foreach (string id in projectIds)
        {
            ids.Add(id);
        }

        var body = new JsonObject
        {
            ["projectIds"] = ids,
            ["startDate"] = FormatDida(start),
            ["endDate"] = FormatDida(end)
        };

        return await SendArrayAsync(HttpMethod.Post, "/task/completed", body, token).ConfigureAwait(false);
    }

    internal async Task<JsonObject> CreateTaskAsync(JsonObject body, CancellationToken token)
    {
        JsonNode? created = await SendAsync(HttpMethod.Post, "/task", body, token).ConfigureAwait(false);
        return created as JsonObject ?? new JsonObject();
    }

    internal Task UpdateTaskAsync(string taskId, JsonObject body, CancellationToken token) =>
        SendAsync(HttpMethod.Post, "/task/" + Uri.EscapeDataString(taskId), body, token);

    internal Task CompleteTaskAsync(string projectId, string taskId, CancellationToken token) =>
        SendAsync(
            HttpMethod.Post,
            "/project/" + Uri.EscapeDataString(projectId) + "/task/" + Uri.EscapeDataString(taskId) + "/complete",
            null,
            token);

    private async Task<JsonArray> SendArrayAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken token)
    {
        JsonNode? result = await SendAsync(method, path, body, token).ConfigureAwait(false);
        return result as JsonArray ?? new JsonArray();
    }

    private async Task<JsonNode?> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "DeskBox-DidaSync/2.0");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DidaApiException($"network error: {ex.Message}");
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new DidaApiException("token rejected (401)", tokenRejected: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Dida365 reports rate limiting as HTTP 500 with exceed_query_limit
                // in the body, not as 429.
                string hint = text.Contains("exceed_query_limit", StringComparison.OrdinalIgnoreCase)
                    ? " (rate limited)"
                    : string.Empty;
                string snippet = text.Length > 200 ? text[..200] : text;
                throw new DidaApiException($"HTTP {(int)response.StatusCode}{hint}: {snippet}");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(text);
            }
            catch (Exception ex)
            {
                throw new DidaApiException($"unparsable response: {ex.Message}");
            }
        }
    }

    internal static string FormatDida(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+0000'", CultureInfo.InvariantCulture);
}

/// <summary>
/// In-process replacement for the external Python sync agent.
///
/// DeskBox never lets an outside writer touch todo.json behind its back: the
/// widget rebuilds its Items only when the todo.sync signal appears. This service
/// therefore merges into <see cref="TodoWidgetData"/> through the store and then
/// writes that signal, exactly like the agent did.
/// </summary>
internal sealed class DidaSyncService
{
    private static readonly Lazy<DidaSyncService> LazyInstance = new(() => new DidaSyncService());

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private TodoWidgetStore? _store;
    private int _failStreak;
    private string _lastLoggedError = string.Empty;

    internal static DidaSyncService Instance => LazyInstance.Value;

    internal string LastStatus { get; private set; } = "未启用";

    private DidaSyncService()
    {
    }

    internal void Attach(TodoWidgetStore store)
    {
        lock (_gate)
        {
            if (_store is not null)
            {
                return;
            }

            _store = store;
            Start();
        }
    }

    private void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _ = Task.Run(() => RunAsync(token), token);
    }

    private async Task RunAsync(CancellationToken token)
    {
        // Let the shell finish launching before the first network round.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            int delaySeconds = 12;
            try
            {
                DidaSyncConfig config = DidaSyncConfig.Load();
                delaySeconds = config.PollSeconds;
                if (config.IsUsable)
                {
                    await SyncOnceAsync(config, token).ConfigureAwait(false);
                    _failStreak = 0;
                }
                else
                {
                    LastStatus = config.Enabled ? "等待填写 API 口令或选择清单" : "未启用";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (DidaApiException ex)
            {
                _failStreak++;
                LastStatus = "同步失败：" + ex.Message;
                if (!string.Equals(_lastLoggedError, ex.Message, StringComparison.Ordinal))
                {
                    _lastLoggedError = ex.Message;
                    App.Log($"[DidaSync] {ex.Message}");
                }

                // Back off on repeated failures so a bad token cannot hammer the API.
                delaySeconds = Math.Min(300, 12 * Math.Max(1, _failStreak));
                if (ex.TokenRejected)
                {
                    delaySeconds = 300;
                }
            }
            catch (Exception ex)
            {
                _failStreak++;
                LastStatus = "同步异常：" + ex.Message;
                App.Log($"[DidaSync] Unexpected: {ex}");
                delaySeconds = Math.Min(120, 12 * Math.Max(1, _failStreak));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<int> SyncOnceAsync(DidaSyncConfig config, CancellationToken token)
    {
        TodoWidgetStore? store = _store;
        if (store is null)
        {
            return 0;
        }

        var client = new DidaApiClient(config.BaseUrl, config.Token);
        string todoPath = store.StorePath;
        string signalPath = Path.Combine(Path.GetDirectoryName(todoPath)!, "todo.sync");
        string statePath = Path.Combine(DeskBoxDataPathService.Current.RootPath, "dida-sync-state.json");

        JsonObject state = LoadState(statePath);
        JsonObject byLocal = state["byLocal"] as JsonObject ?? new JsonObject();
        state["byLocal"] = byLocal;

        IReadOnlyList<string> scope = config.SyncProjectIds;
        if (scope.Count == 0)
        {
            return 0;
        }

        string primaryProject = config.PrimaryProjectId;

        TodoWidgetData local = await store.LoadAsync().ConfigureAwait(false);

        // A different API token means a different account. Replaying the old
        // local mirror into it would create every task again over there, so the
        // mapping and the mirror are dropped before the first round against a
        // new account.
        string fingerprint = TokenFingerprint(config.Token);
        if (state["tokenFingerprint"]?.GetValue<string>() is { Length: > 0 } previousFingerprint &&
            !string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
        {
            byLocal.Clear();
            local.Items = new List<TodoItem>();
            await store.SaveAsync(local).ConfigureAwait(false);
            App.Log("[DidaSync] API token changed: cleared local mirror and mapping for the new account.");
        }

        state["tokenFingerprint"] = fingerprint;

        var planned = new Dictionary<string, TodoItem>(StringComparer.Ordinal);
        foreach (TodoItem item in local.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                planned[item.Id] = item;
            }
        }

        // ---- fetch remote -------------------------------------------------
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var remote = new JsonObject();
        var remoteProject = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string projectId in scope)
        {
            foreach (JsonNode? task in await client.GetOpenTasksAsync(projectId, token).ConfigureAwait(false))
            {
                if (task is JsonObject obj && obj["id"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    remote[id] = obj.DeepClone();
                    remoteProject[id] = projectId;
                }
            }
        }

        JsonArray completed = await client
            .GetCompletedTasksAsync(scope, now.AddDays(-config.CompletedLookbackDays), now.AddDays(1), token)
            .ConfigureAwait(false);
        foreach (JsonNode? task in completed)
        {
            if (task is JsonObject obj && obj["id"]?.GetValue<string>() is { Length: > 0 } id)
            {
                remote[id] = obj.DeepClone();
                remoteProject[id] = ResolveScopeProject(obj["projectId"]?.GetValue<string>(), scope, primaryProject);
            }
        }

        var remoteOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in byLocal)
        {
            if (entry.Value is JsonObject mapping &&
                mapping["remoteId"]?.GetValue<string>() is { Length: > 0 } remoteId)
            {
                remoteOwner[remoteId] = entry.Key;
            }
        }

        var pushed = new HashSet<string>(StringComparer.Ordinal);
        bool localDirty = false;
        int changes = 0;

        // Remote tasks with no local mapping yet. Used to adopt an existing task
        // instead of creating a duplicate when local data was imported before the
        // mapping file existed (for example after replacing the Python agent).
        var unbound = new List<string>();
        foreach ((string remoteKey, JsonNode? _) in remote)
        {
            if (!remoteOwner.ContainsKey(remoteKey))
            {
                unbound.Add(remoteKey);
            }
        }

        // ---- local -> remote ----------------------------------------------
        foreach ((string localId, TodoItem item) in planned.ToList())
        {
            JsonObject? mapping = byLocal[localId] as JsonObject;
            JsonObject baseline = mapping?["local"] as JsonObject ?? new JsonObject();
            JsonObject current = NormalizeLocal(item);

            if (mapping is null)
            {
                if (item.IsDeleted)
                {
                    continue;
                }

                // Adopt a same-titled unbound remote task first: a local item that
                // already mirrors a remote one must not be pushed again as a
                // duplicate.
                string? adoptedId = null;
                foreach (string candidateId in unbound)
                {
                    if (remote[candidateId] is not JsonObject candidate)
                    {
                        continue;
                    }

                    string candidateTitle = candidate["title"]?.GetValue<string>()?.Trim() ?? string.Empty;
                    if (!string.Equals(candidateTitle, item.Text?.Trim(), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool candidateCompleted = ReadInt(candidate, "status") == 2;
                    if (candidateCompleted != item.IsCompleted && adoptedId is null)
                    {
                        // Keep looking for an exact status match, but remember it.
                        adoptedId ??= candidateId;
                        continue;
                    }

                    adoptedId = candidateId;
                    break;
                }

                if (adoptedId is not null && remote[adoptedId] is JsonObject adoptedTask)
                {
                    unbound.Remove(adoptedId);
                    byLocal[localId] = new JsonObject
                    {
                        ["remoteId"] = adoptedId,
                        ["projectId"] = remoteProject.TryGetValue(adoptedId, out string? adoptedProject)
                            ? adoptedProject
                            : primaryProject,
                        ["local"] = current.DeepClone(),
                        ["remote"] = NormalizeRemote(adoptedTask).DeepClone()
                    };
                    remoteOwner[adoptedId] = localId;
                    App.Log($"[DidaSync] Adopted existing remote task: {item.Text}");
                    continue;
                }

                JsonObject body = LocalToRemoteBody(item, primaryProject, null);
                JsonObject created = await client.CreateTaskAsync(body, token).ConfigureAwait(false);
                string? newRemoteId = created["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(newRemoteId))
                {
                    throw new DidaApiException("create returned no task id");
                }

                byLocal[localId] = new JsonObject
                {
                    ["remoteId"] = newRemoteId,
                    ["projectId"] = primaryProject,
                    ["local"] = current.DeepClone(),
                    ["remote"] = new JsonObject()
                };
                remoteOwner[newRemoteId] = localId;
                pushed.Add(newRemoteId);
                changes++;
                App.Log($"[DidaSync] Created remote task: {item.Text}");
                continue;
            }

            if (current.ToJsonString() == baseline.ToJsonString())
            {
                continue;
            }

            string? taskId = mapping["remoteId"]?.GetValue<string>();
            string mappingProject = mapping["projectId"]?.GetValue<string>() ?? string.Empty;
            if (mappingProject.Length > 0 && !scope.Contains(mappingProject, StringComparer.Ordinal))
            {
                // The owning project left the sync scope. Leave the local copy
                // untouched instead of re-creating it in a project the user did
                // not select.
                continue;
            }

            if (string.IsNullOrWhiteSpace(taskId) || remote[taskId] is not JsonObject remoteTask)
            {
                // The remote copy is gone (deleted there, or completed long ago).
                // Re-create it instead of losing the local edit.
                if (!item.IsDeleted)
                {
                    string recreateProject = mappingProject.Length > 0 ? mappingProject : primaryProject;
                    JsonObject body = LocalToRemoteBody(item, recreateProject, null);
                    JsonObject created = await client.CreateTaskAsync(body, token).ConfigureAwait(false);
                    string? newRemoteId = created["id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(newRemoteId))
                    {
                        mapping["remoteId"] = newRemoteId;
                        mapping["projectId"] = recreateProject;
                        mapping["local"] = current.DeepClone();
                        mapping["remote"] = new JsonObject();
                        remoteOwner[newRemoteId] = localId;
                        pushed.Add(newRemoteId);
                        changes++;
                    }
                }

                continue;
            }

            // Two-sided edit: newest timestamp wins.
            DateTimeOffset localTime = item.UpdatedAt;
            DateTimeOffset remoteTime = ParseDidaTime(
                remoteTask["modifiedTime"]?.GetValue<string>() ??
                remoteTask["completedTime"]?.GetValue<string>()) ?? DateTimeOffset.MinValue;
            bool remoteChanged = (mapping["remote"] as JsonObject)?.ToJsonString() != NormalizeRemote(remoteTask).ToJsonString();
            if (remoteChanged && remoteTime > localTime)
            {
                continue;
            }

            string taskProject = remoteProject.TryGetValue(taskId, out string? resolvedProject)
                ? resolvedProject
                : (mappingProject.Length > 0 ? mappingProject : primaryProject);

            bool wasCompleted = baseline["isCompleted"]?.GetValue<bool>() ?? false;
            JsonObject updateBody = LocalToRemoteBody(item, taskProject, taskId);
            await client.UpdateTaskAsync(taskId, updateBody, token).ConfigureAwait(false);

            if (item.IsCompleted && !wasCompleted)
            {
                await client.CompleteTaskAsync(taskProject, taskId, token).ConfigureAwait(false);
            }
            else if (!item.IsCompleted && wasCompleted)
            {
                // Reopen has no documented endpoint; status:0 is best effort.
                var reopen = new JsonObject
                {
                    ["id"] = taskId,
                    ["projectId"] = taskProject,
                    ["status"] = 0
                };
                await client.UpdateTaskAsync(taskId, reopen, token).ConfigureAwait(false);
            }

            mapping["local"] = current.DeepClone();
            mapping["projectId"] = taskProject;
            pushed.Add(taskId);
            changes++;
            App.Log($"[DidaSync] Pushed local change: {item.Text}");
        }

        // ---- remote -> local ----------------------------------------------
        if (pushed.Count > 0)
        {
            // Refresh baselines so our own writes do not echo back as remote edits.
            remote = new JsonObject();
            remoteProject.Clear();
            foreach (string projectId in scope)
            {
                foreach (JsonNode? task in await client.GetOpenTasksAsync(projectId, token).ConfigureAwait(false))
                {
                    if (task is JsonObject obj && obj["id"]?.GetValue<string>() is { Length: > 0 } id)
                    {
                        remote[id] = obj.DeepClone();
                        remoteProject[id] = projectId;
                    }
                }
            }

            foreach (JsonNode? task in await client
                .GetCompletedTasksAsync(scope, now.AddDays(-config.CompletedLookbackDays), now.AddDays(1), token)
                .ConfigureAwait(false))
            {
                if (task is JsonObject obj && obj["id"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    remote[id] = obj.DeepClone();
                    remoteProject[id] = ResolveScopeProject(
                        obj["projectId"]?.GetValue<string>(),
                        scope,
                        primaryProject);
                }
            }
        }

        foreach ((string remoteId, JsonNode? node) in remote)
        {
            if (node is not JsonObject task)
            {
                continue;
            }

            remoteOwner.TryGetValue(remoteId, out string? ownerId);
            if (ownerId is null)
            {
                TodoItem created = NewLocalItem(task);
                planned[created.Id] = created;
                byLocal[created.Id] = new JsonObject
                {
                    ["remoteId"] = remoteId,
                    ["projectId"] = remoteProject.TryGetValue(remoteId, out string? sourceProject)
                        ? sourceProject
                        : primaryProject,
                    ["local"] = NormalizeLocal(created).DeepClone(),
                    ["remote"] = NormalizeRemote(task).DeepClone()
                };
                remoteOwner[remoteId] = created.Id;
                localDirty = true;
                changes++;
                App.Log($"[DidaSync] Imported remote task: {created.Text}");
                continue;
            }

            if (pushed.Contains(remoteId))
            {
                continue;
            }

            if (byLocal[ownerId] is not JsonObject mapping)
            {
                continue;
            }

            JsonObject previous = mapping["remote"] as JsonObject ?? new JsonObject();
            JsonObject normalizedRemote = NormalizeRemote(task);
            if (previous.ToJsonString() == normalizedRemote.ToJsonString())
            {
                continue;
            }

            if (!planned.TryGetValue(ownerId, out TodoItem? existing))
            {
                continue;
            }

            JsonObject patch = RemoteToLocalPatch(task, existing);
            bool stepsSame = StepsEqual(existing, patch);
            bool fieldsChanged = false;
            foreach ((string key, JsonNode? value) in patch)
            {
                if (key is "steps" or "updatedAt")
                {
                    continue;
                }

                string currentValue = LocalFieldAsString(existing, key);
                string nextValue = value?.ToJsonString().Trim('"') ?? string.Empty;
                if (!string.Equals(currentValue, nextValue, StringComparison.Ordinal))
                {
                    fieldsChanged = true;
                    break;
                }
            }

            if (!fieldsChanged && stepsSame)
            {
                mapping["remote"] = normalizedRemote.DeepClone();
                continue;
            }

            ApplyRemotePatch(existing, task, stepsSame);
            planned[existing.Id] = existing;
            mapping["remote"] = normalizedRemote.DeepClone();
            localDirty = true;
            changes++;
            App.Log($"[DidaSync] Applied remote change: {existing.Text}");
        }

        // ---- commit -------------------------------------------------------
        foreach ((string localId, TodoItem item) in planned)
        {
            if (byLocal[localId] is JsonObject mapping)
            {
                mapping["local"] = NormalizeLocal(item).DeepClone();
            }
        }

        state["projectId"] = primaryProject;
        var scopeNode = new JsonArray();
        foreach (string projectId in scope)
        {
            scopeNode.Add(projectId);
        }

        state["projectIds"] = scopeNode;

        if (localDirty)
        {
            local.Items = planned.Values.OrderBy(item => item.SortOrder).ToList();
            await store.SaveAsync(local).ConfigureAwait(false);
            await WriteSignalAsync(signalPath, token).ConfigureAwait(false);
            LastStatus = $"已同步 {changes} 项 · {DateTime.Now:HH:mm:ss}";
        }
        else
        {
            LastStatus = $"已是最新 · {DateTime.Now:HH:mm:ss}";
        }

        state["lastStatus"] = LastStatus;
        state["lastStatusAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        SaveState(statePath, state);
        return changes;
    }

    /// <summary>
    /// Maps a task's own projectId (the Inbox reports a personal id such as
    /// "inbox1020017597") back onto the project key we sync with.
    /// </summary>
    private static string ResolveScopeProject(
        string? taskProjectId,
        IReadOnlyList<string> scope,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(taskProjectId))
        {
            return fallback;
        }

        foreach (string projectId in scope)
        {
            if (string.Equals(projectId, taskProjectId, StringComparison.Ordinal))
            {
                return projectId;
            }
        }

        if (taskProjectId.StartsWith("inbox", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string projectId in scope)
            {
                if (string.Equals(projectId, "inbox", StringComparison.OrdinalIgnoreCase) ||
                    projectId.StartsWith("inbox", StringComparison.OrdinalIgnoreCase))
                {
                    return projectId;
                }
            }
        }

        return fallback;
    }

    private static async Task WriteSignalAsync(string signalPath, CancellationToken token)
    {
        string payload = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        string temp = signalPath + ".tmp";
        await File.WriteAllTextAsync(temp, payload, token).ConfigureAwait(false);
        File.Move(temp, signalPath, overwrite: true);
    }

    private static string TokenFingerprint(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));
        return Convert.ToHexString(hash)[..16];
    }

    private static JsonObject LoadState(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
                {
                    // Keep the local mapping across project-scope edits: dropping
                    // it would re-push every local item as a brand-new task.
                    return root;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DidaSync] Ignoring unreadable state: {ex.Message}");
        }

        return new JsonObject
        {
            ["version"] = 2,
            ["byLocal"] = new JsonObject()
        };
    }

    private static void SaveState(string path, JsonObject state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, state.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log($"[DidaSync] State save failed: {ex.Message}");
        }
    }

    // ---- field mapping ----------------------------------------------------

    private static JsonObject NormalizeLocal(TodoItem item)
    {
        var steps = new JsonArray();
        foreach (TodoStep step in item.Steps ?? new List<TodoStep>())
        {
            steps.Add(new JsonArray(step.Text ?? string.Empty, step.IsCompleted));
        }

        return new JsonObject
        {
            ["text"] = item.Text ?? string.Empty,
            ["isCompleted"] = item.IsCompleted,
            ["isImportant"] = item.IsImportant,
            ["dueDate"] = CanonicalDate(item.DueDate),
            ["notes"] = item.Notes ?? string.Empty,
            ["steps"] = steps,
            ["isDeleted"] = item.IsDeleted
        };
    }

    private static JsonObject NormalizeRemote(JsonObject task)
    {
        var items = new JsonArray();
        if (task["items"] is JsonArray source)
        {
            foreach (JsonNode? sub in source)
            {
                if (sub is JsonObject obj)
                {
                    items.Add(new JsonArray(
                        obj["title"]?.GetValue<string>() ?? string.Empty,
                        ReadInt(obj, "status") == 1));
                }
            }
        }

        return new JsonObject
        {
            ["title"] = task["title"]?.GetValue<string>() ?? string.Empty,
            ["status"] = ReadInt(task, "status"),
            ["priority"] = ReadInt(task, "priority"),
            ["dueDate"] = CanonicalDate(ParseDidaTime(task["dueDate"]?.GetValue<string>())),
            ["content"] = task["content"]?.GetValue<string>() ?? string.Empty,
            ["items"] = items
        };
    }

    private static JsonObject LocalToRemoteBody(TodoItem item, string projectId, string? taskId)
    {
        var body = new JsonObject
        {
            ["projectId"] = projectId,
            ["title"] = string.IsNullOrWhiteSpace(item.Text) ? "(untitled)" : item.Text,
            ["content"] = item.Notes ?? string.Empty,
            ["priority"] = item.IsImportant ? 5 : 0,
            ["timeZone"] = "Asia/Shanghai"
        };

        if (!string.IsNullOrWhiteSpace(taskId))
        {
            body["id"] = taskId;
        }

        if (item.DueDate is { } due)
        {
            body["dueDate"] = DidaApiClient.FormatDida(due);
            body["isAllDay"] = false;
        }

        if (item.Steps is { Count: > 0 } steps)
        {
            body["kind"] = "CHECKLIST";
            var items = new JsonArray();
            for (int index = 0; index < steps.Count; index++)
            {
                items.Add(new JsonObject
                {
                    ["title"] = steps[index].Text ?? string.Empty,
                    ["status"] = steps[index].IsCompleted ? 1 : 0,
                    ["sortOrder"] = index
                });
            }

            body["items"] = items;
        }

        return body;
    }

    private static TodoItem NewLocalItem(JsonObject task)
    {
        var item = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = task["title"]?.GetValue<string>() ?? "(untitled)",
            IsCompleted = ReadInt(task, "status") == 2,
            IsImportant = ReadInt(task, "priority") >= 5,
            DueDate = ParseDidaTime(task["dueDate"]?.GetValue<string>()),
            Notes = task["content"]?.GetValue<string>(),
            CompletedAt = ParseDidaTime(task["completedTime"]?.GetValue<string>()),
            SortOrder = 0,
            DeviceId = "dida-sync",
            UpdatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (task["items"] is JsonArray subs)
        {
            int index = 0;
            foreach (JsonNode? sub in subs)
            {
                if (sub is JsonObject obj)
                {
                    item.Steps.Add(new TodoStep
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Text = obj["title"]?.GetValue<string>() ?? string.Empty,
                        IsCompleted = ReadInt(obj, "status") == 1,
                        SortOrder = index++
                    });
                }
            }
        }

        return item;
    }

    private static JsonObject RemoteToLocalPatch(JsonObject task, TodoItem existing)
    {
        var steps = new JsonArray();
        if (task["items"] is JsonArray subs)
        {
            foreach (JsonNode? sub in subs)
            {
                if (sub is JsonObject obj)
                {
                    steps.Add(new JsonObject
                    {
                        ["text"] = obj["title"]?.GetValue<string>() ?? string.Empty,
                        ["isCompleted"] = ReadInt(obj, "status") == 1
                    });
                }
            }
        }

        return new JsonObject
        {
            ["text"] = task["title"]?.GetValue<string>() ?? "(untitled)",
            ["isCompleted"] = ReadInt(task, "status") == 2,
            ["isImportant"] = ReadInt(task, "priority") >= 5,
            ["dueDate"] = CanonicalDate(ParseDidaTime(task["dueDate"]?.GetValue<string>())),
            ["notes"] = task["content"]?.GetValue<string>() ?? string.Empty,
            ["steps"] = steps
        };
    }

    private static bool StepsEqual(TodoItem existing, JsonObject patch)
    {
        if (patch["steps"] is not JsonArray incoming)
        {
            return (existing.Steps?.Count ?? 0) == 0;
        }

        if ((existing.Steps?.Count ?? 0) != incoming.Count)
        {
            return false;
        }

        for (int index = 0; index < incoming.Count; index++)
        {
            if (incoming[index] is not JsonObject row)
            {
                return false;
            }

            TodoStep step = existing.Steps![index];
            string text = row["text"]?.GetValue<string>() ?? string.Empty;
            bool completed = row["isCompleted"]?.GetValue<bool>() ?? false;
            if (!string.Equals(step.Text ?? string.Empty, text, StringComparison.Ordinal) ||
                step.IsCompleted != completed)
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyRemotePatch(TodoItem existing, JsonObject task, bool stepsSame)
    {
        existing.Text = task["title"]?.GetValue<string>() ?? "(untitled)";
        existing.IsCompleted = ReadInt(task, "status") == 2;
        existing.IsImportant = ReadInt(task, "priority") >= 5;
        existing.DueDate = ParseDidaTime(task["dueDate"]?.GetValue<string>());
        existing.Notes = task["content"]?.GetValue<string>();
        existing.CompletedAt = ParseDidaTime(task["completedTime"]?.GetValue<string>());
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        if (stepsSame)
        {
            // Keep step ids so the detail view does not rebuild untouched rows.
            return;
        }

        existing.Steps = [];
        if (task["items"] is JsonArray subs)
        {
            int index = 0;
            foreach (JsonNode? sub in subs)
            {
                if (sub is JsonObject obj)
                {
                    existing.Steps.Add(new TodoStep
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Text = obj["title"]?.GetValue<string>() ?? string.Empty,
                        IsCompleted = ReadInt(obj, "status") == 1,
                        SortOrder = index++
                    });
                }
            }
        }
    }

    private static string LocalFieldAsString(TodoItem item, string key) => key switch
    {
        "text" => item.Text ?? string.Empty,
        "isCompleted" => item.IsCompleted ? "true" : "false",
        "isImportant" => item.IsImportant ? "true" : "false",
        "dueDate" => CanonicalDate(item.DueDate) ?? string.Empty,
        "notes" => item.Notes ?? string.Empty,
        _ => string.Empty
    };

    private static int ReadInt(JsonObject obj, string name)
    {
        try
        {
            JsonNode? node = obj[name];
            if (node is null)
            {
                return 0;
            }

            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => node.GetValue<int>(),
                System.Text.Json.JsonValueKind.True => 1,
                System.Text.Json.JsonValueKind.False => 0,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Dida365 uses "+0000" (no colon); DeskBox uses ISO-8601 with offset.</summary>
    internal static DateTimeOffset? ParseDidaTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string text = raw.Trim();
        if (text.Length >= 5 && (text[^5] == '+' || text[^5] == '-') && text[^3] != ':')
        {
            text = text[..^2] + ":" + text[^2..];
        }

        if (text.EndsWith("Z", StringComparison.Ordinal))
        {
            text = text[..^1] + "+00:00";
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static string? CanonicalDate(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
}
