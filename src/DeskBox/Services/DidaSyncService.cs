using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
///
/// Request bodies are produced by <see cref="Utf8JsonWriter"/> and handed over as
/// plain strings. Building them through <see cref="JsonNode"/> looks tidier but
/// dies under Native AOT: <c>JsonObject["key"] = "text"</c> routes through
/// <c>JsonNode.ConvertFromValue</c>, which asks <c>JsonSerializerOptions.Default</c>
/// for type metadata, and an AOT build only has an empty resolver — every write
/// threw <c>NotSupportedException</c> while reads kept working. Responses are still
/// parsed with <see cref="JsonNode.Parse"/>, which needs no metadata.
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

    internal Task<JsonArray> GetCompletedTasksAsync(
        string projectId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken token) =>
        GetCompletedTasksAsync(new[] { projectId }, start, end, token);

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
        string body = DidaJson.Build(writer =>
        {
            writer.WriteStartArray("projectIds");
            foreach (string id in projectIds)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteString("startDate", FormatDida(start));
            writer.WriteString("endDate", FormatDida(end));
        });

        return await SendArrayAsync(HttpMethod.Post, "/task/completed", body, token).ConfigureAwait(false);
    }

    internal async Task<JsonObject> CreateTaskAsync(string body, CancellationToken token)
    {
        JsonNode? created = await SendAsync(HttpMethod.Post, "/task", body, token).ConfigureAwait(false);
        return created as JsonObject ?? new JsonObject();
    }

    internal Task UpdateTaskAsync(string taskId, string body, CancellationToken token) =>
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
        string? body,
        CancellationToken token)
    {
        JsonNode? result = await SendAsync(method, path, body, token).ConfigureAwait(false);
        return result as JsonArray ?? new JsonArray();
    }

    private async Task<JsonNode?> SendAsync(
        HttpMethod method,
        string path,
        string? body,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "DeskBox-DidaSync/2.1");
        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
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
/// Native AOT safe JSON text helpers. Nothing here touches the reflection based
/// serializer: values are streamed straight into <see cref="Utf8JsonWriter"/>.
/// </summary>
internal static class DidaJson
{
    internal static string Build(Action<Utf8JsonWriter> writeProperties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string BuildArray(Action<Utf8JsonWriter> writeItems)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writeItems(writer);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Serialises an already parsed node without a type resolver.</summary>
    internal static string Text(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, node);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (JsonNode? element in array)
                {
                    Write(writer, element);
                }

                writer.WriteEndArray();
                return;
            case JsonValue value:
                WritePrimitive(writer, value);
                return;
            default:
                writer.WriteNullValue();
                return;
        }
    }

    private static void WritePrimitive(Utf8JsonWriter writer, JsonValue value)
    {
        // JsonNode.Parse wraps numbers and strings in a JsonElement holder, so
        // TryGetValue<T> reports false even for an obvious string. Discriminate on
        // the value kind first and only then read the typed payload.
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetValue<string>());
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            case JsonValueKind.Number:
                if (value.TryGetValue(out int number))
                {
                    writer.WriteNumberValue(number);
                    return;
                }

                if (value.TryGetValue(out long longNumber))
                {
                    writer.WriteNumberValue(longNumber);
                    return;
                }

                if (value.TryGetValue(out double real))
                {
                    writer.WriteNumberValue(real);
                    return;
                }

                if (value.TryGetValue(out JsonElement element))
                {
                    writer.WriteRawValue(element.GetRawText());
                    return;
                }

                writer.WriteNullValue();
                return;
            default:
                writer.WriteNullValue();
                return;
        }
    }
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

        SyncState state = LoadState(statePath);

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
        if (state.TokenFingerprint.Length > 0 &&
            !string.Equals(state.TokenFingerprint, fingerprint, StringComparison.Ordinal))
        {
            state.ByLocal.Clear();
            local.Items = new List<TodoItem>();
            await store.SaveAsync(local).ConfigureAwait(false);
            App.Log("[DidaSync] API token changed: cleared local mirror and mapping for the new account.");
        }

        state.TokenFingerprint = fingerprint;
        state.ProjectId = primaryProject;
        state.ProjectIds = scope.ToList();

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
                if (task is JsonObject obj && ReadString(obj, "id") is { Length: > 0 } id)
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
            if (task is JsonObject obj && ReadString(obj, "id") is { Length: > 0 } id)
            {
                remote[id] = obj.DeepClone();
                remoteProject[id] = ResolveScopeProject(ReadString(obj, "projectId"), scope, primaryProject);
            }
        }

        var remoteOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SyncMapping> entry in state.ByLocal)
        {
            if (entry.Value.RemoteId.Length > 0)
            {
                remoteOwner[entry.Value.RemoteId] = entry.Key;
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
            state.ByLocal.TryGetValue(localId, out SyncMapping? mapping);
            string current = NormalizeLocal(item);

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

                    string candidateTitle = ReadString(candidate, "title").Trim();
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
                    state.ByLocal[localId] = new SyncMapping
                    {
                        RemoteId = adoptedId,
                        ProjectId = remoteProject.TryGetValue(adoptedId, out string? adoptedProject)
                            ? adoptedProject
                            : primaryProject,
                        LocalJson = current,
                        LocalCompleted = item.IsCompleted,
                        RemoteJson = NormalizeRemote(adoptedTask)
                    };
                    remoteOwner[adoptedId] = localId;
                    App.Log($"[DidaSync] Adopted existing remote task: {item.Text}");
                    continue;
                }

                JsonObject created = await client
                    .CreateTaskAsync(LocalToRemoteBody(item, primaryProject, null), token)
                    .ConfigureAwait(false);
                string newRemoteId = ReadString(created, "id");
                if (newRemoteId.Length == 0)
                {
                    throw new DidaApiException("create returned no task id");
                }

                state.ByLocal[localId] = new SyncMapping
                {
                    RemoteId = newRemoteId,
                    ProjectId = primaryProject,
                    LocalJson = current,
                    LocalCompleted = item.IsCompleted,
                    RemoteJson = "{}"
                };
                remoteOwner[newRemoteId] = localId;
                pushed.Add(newRemoteId);
                changes++;
                App.Log($"[DidaSync] Created remote task: {item.Text}");
                continue;
            }

            if (string.Equals(current, mapping.LocalJson, StringComparison.Ordinal))
            {
                continue;
            }

            string taskId = mapping.RemoteId;
            string mappingProject = mapping.ProjectId;
            if (mappingProject.Length > 0 && !scope.Contains(mappingProject, StringComparer.Ordinal))
            {
                // The owning project left the sync scope. Leave the local copy
                // untouched instead of re-creating it in a project the user did
                // not select.
                continue;
            }

            if (taskId.Length == 0 || remote[taskId] is not JsonObject remoteTask)
            {
                // The remote copy is gone (deleted there, or completed long ago).
                // Re-create it instead of losing the local edit.
                if (!item.IsDeleted)
                {
                    string recreateProject = mappingProject.Length > 0 ? mappingProject : primaryProject;
                    JsonObject created = await client
                        .CreateTaskAsync(LocalToRemoteBody(item, recreateProject, null), token)
                        .ConfigureAwait(false);
                    string newRemoteId = ReadString(created, "id");
                    if (newRemoteId.Length > 0)
                    {
                        mapping.RemoteId = newRemoteId;
                        mapping.ProjectId = recreateProject;
                        mapping.LocalJson = current;
                        mapping.LocalCompleted = item.IsCompleted;
                        mapping.RemoteJson = "{}";
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
                ReadString(remoteTask, "modifiedTime") is { Length: > 0 } modified
                    ? modified
                    : ReadString(remoteTask, "completedTime")) ?? DateTimeOffset.MinValue;
            bool remoteChanged = !string.Equals(mapping.RemoteJson, NormalizeRemote(remoteTask), StringComparison.Ordinal);
            if (remoteChanged && remoteTime > localTime)
            {
                continue;
            }

            string taskProject = remoteProject.TryGetValue(taskId, out string? resolvedProject)
                ? resolvedProject
                : (mappingProject.Length > 0 ? mappingProject : primaryProject);

            bool wasCompleted = mapping.LocalCompleted;
            await client
                .UpdateTaskAsync(taskId, LocalToRemoteBody(item, taskProject, taskId), token)
                .ConfigureAwait(false);

            if (item.IsCompleted && !wasCompleted)
            {
                await client.CompleteTaskAsync(taskProject, taskId, token).ConfigureAwait(false);
            }
            else if (!item.IsCompleted && wasCompleted)
            {
                // Reopen has no documented endpoint; status:0 is best effort.
                string reopen = DidaJson.Build(writer =>
                {
                    writer.WriteString("id", taskId);
                    writer.WriteString("projectId", taskProject);
                    writer.WriteNumber("status", 0);
                });
                await client.UpdateTaskAsync(taskId, reopen, token).ConfigureAwait(false);
            }

            mapping.LocalJson = current;
            mapping.LocalCompleted = item.IsCompleted;
            mapping.ProjectId = taskProject;
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
                    if (task is JsonObject obj && ReadString(obj, "id") is { Length: > 0 } id)
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
                if (task is JsonObject obj && ReadString(obj, "id") is { Length: > 0 } id)
                {
                    remote[id] = obj.DeepClone();
                    remoteProject[id] = ResolveScopeProject(
                        ReadString(obj, "projectId"),
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
                state.ByLocal[created.Id] = new SyncMapping
                {
                    RemoteId = remoteId,
                    ProjectId = remoteProject.TryGetValue(remoteId, out string? sourceProject)
                        ? sourceProject
                        : primaryProject,
                    LocalJson = NormalizeLocal(created),
                    LocalCompleted = created.IsCompleted,
                    RemoteJson = NormalizeRemote(task)
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

            if (!state.ByLocal.TryGetValue(ownerId, out SyncMapping? mapping))
            {
                continue;
            }

            string normalizedRemote = NormalizeRemote(task);
            if (string.Equals(mapping.RemoteJson, normalizedRemote, StringComparison.Ordinal))
            {
                continue;
            }

            if (!planned.TryGetValue(ownerId, out TodoItem? existing))
            {
                continue;
            }

            bool stepsSame = StepsEqual(existing, task);
            bool fieldsChanged = RemoteFieldsChanged(existing, task);
            if (!fieldsChanged && stepsSame)
            {
                mapping.RemoteJson = normalizedRemote;
                continue;
            }

            ApplyRemotePatch(existing, task, stepsSame);
            planned[existing.Id] = existing;
            mapping.RemoteJson = normalizedRemote;
            mapping.LocalJson = NormalizeLocal(existing);
            mapping.LocalCompleted = existing.IsCompleted;
            localDirty = true;
            changes++;
            App.Log($"[DidaSync] Applied remote change: {existing.Text}");
        }

        // ---- commit -------------------------------------------------------
        foreach ((string localId, TodoItem item) in planned)
        {
            if (state.ByLocal.TryGetValue(localId, out SyncMapping? mapping))
            {
                mapping.LocalJson = NormalizeLocal(item);
                mapping.LocalCompleted = item.IsCompleted;
            }
        }

        state.LastStatusAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

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

        state.LastStatus = LastStatus;
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

    private sealed class SyncMapping
    {
        internal string RemoteId { get; set; } = string.Empty;

        internal string ProjectId { get; set; } = string.Empty;

        /// <summary>Canonical local snapshot as JSON text (see NormalizeLocal).</summary>
        internal string LocalJson { get; set; } = string.Empty;

        /// <summary>Canonical remote snapshot as JSON text (see NormalizeRemote).</summary>
        internal string RemoteJson { get; set; } = string.Empty;

        internal bool LocalCompleted { get; set; }
    }

    private sealed class SyncState
    {
        internal string ProjectId { get; set; } = string.Empty;

        internal List<string> ProjectIds { get; set; } = new();

        internal string TokenFingerprint { get; set; } = string.Empty;

        internal string LastStatus { get; set; } = string.Empty;

        internal string LastStatusAt { get; set; } = string.Empty;

        internal Dictionary<string, SyncMapping> ByLocal { get; } = new(StringComparer.Ordinal);
    }

    private static SyncState LoadState(string path)
    {
        var state = new SyncState();
        try
        {
            if (!File.Exists(path))
            {
                return state;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return state;
            }

            // Keep the local mapping across project-scope edits: dropping it
            // would re-push every local item as a brand-new task.
            state.ProjectId = ReadString(root, "projectId");
            state.TokenFingerprint = ReadString(root, "tokenFingerprint");
            state.LastStatus = ReadString(root, "lastStatus");
            state.LastStatusAt = ReadString(root, "lastStatusAt");

            if (root["projectIds"] is JsonArray ids)
            {
                foreach (JsonNode? id in ids)
                {
                    if (id is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                    {
                        state.ProjectIds.Add(text);
                    }
                }
            }

            if (root["byLocal"] is JsonObject map)
            {
                foreach (KeyValuePair<string, JsonNode?> entry in map)
                {
                    if (entry.Value is not JsonObject mapping)
                    {
                        continue;
                    }

                    // Older builds stored the snapshots as JSON objects; newer
                    // ones store canonical text. Both end up as text here.
                    state.ByLocal[entry.Key] = new SyncMapping
                    {
                        RemoteId = ReadString(mapping, "remoteId"),
                        ProjectId = ReadString(mapping, "projectId"),
                        LocalJson = SnapshotText(mapping["local"]),
                        RemoteJson = SnapshotText(mapping["remote"]),
                        LocalCompleted = mapping["localCompleted"] is JsonNode completed
                            ? ReadBool(completed)
                            : SnapshotCompleted(mapping["local"])
                    };
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DidaSync] Ignoring unreadable state: {ex.Message}");
        }

        return state;
    }

    private static string SnapshotText(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out string? text) && text is not null)
            {
                return text;
            }

            if (value.GetValueKind() == JsonValueKind.String)
            {
                return value.GetValue<string>() ?? string.Empty;
            }
        }

        return DidaJson.Text(node);
    }

    private static bool SnapshotCompleted(JsonNode? node) =>
        node is JsonObject snapshot && snapshot["isCompleted"] is JsonNode flag && ReadBool(flag);

    private static bool ReadBool(JsonNode node)
    {
        try
        {
            if (node is JsonValue value && value.TryGetValue(out bool flag))
            {
                return flag;
            }

            return node.GetValueKind() == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveState(string path, SyncState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 3);
                writer.WriteString("projectId", state.ProjectId);
                writer.WriteStartArray("projectIds");
                foreach (string projectId in state.ProjectIds)
                {
                    writer.WriteStringValue(projectId);
                }

                writer.WriteEndArray();
                writer.WriteString("tokenFingerprint", state.TokenFingerprint);
                writer.WriteString("lastStatus", state.LastStatus);
                writer.WriteString("lastStatusAt", state.LastStatusAt);
                writer.WriteStartObject("byLocal");
                foreach (KeyValuePair<string, SyncMapping> entry in state.ByLocal)
                {
                    writer.WriteStartObject(entry.Key);
                    writer.WriteString("remoteId", entry.Value.RemoteId);
                    writer.WriteString("projectId", entry.Value.ProjectId);
                    writer.WriteString("local", entry.Value.LocalJson);
                    writer.WriteString("remote", entry.Value.RemoteJson);
                    writer.WriteBoolean("localCompleted", entry.Value.LocalCompleted);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, Encoding.UTF8.GetString(buffer.ToArray()));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log($"[DidaSync] State save failed: {ex.Message}");
        }
    }

    // ---- field mapping ----------------------------------------------------

    private static string NormalizeLocal(TodoItem item)
    {
        return DidaJson.Build(writer =>
        {
            writer.WriteString("text", item.Text ?? string.Empty);
            writer.WriteBoolean("isCompleted", item.IsCompleted);
            writer.WriteBoolean("isImportant", item.IsImportant);
            writer.WriteString("dueDate", CanonicalDate(item.DueDate) ?? string.Empty);
            writer.WriteString("notes", item.Notes ?? string.Empty);
            writer.WriteStartArray("steps");
            foreach (TodoStep step in item.Steps ?? new List<TodoStep>())
            {
                writer.WriteStartArray();
                writer.WriteStringValue(step.Text ?? string.Empty);
                writer.WriteBooleanValue(step.IsCompleted);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("isDeleted", item.IsDeleted);
        });
    }

    private static string NormalizeRemote(JsonObject task)
    {
        return DidaJson.Build(writer =>
        {
            writer.WriteString("title", ReadString(task, "title"));
            writer.WriteNumber("status", ReadInt(task, "status"));
            writer.WriteNumber("priority", ReadInt(task, "priority"));
            writer.WriteString("dueDate", CanonicalDate(ParseDidaTime(ReadString(task, "dueDate"))) ?? string.Empty);
            writer.WriteString("content", ReadString(task, "content"));
            writer.WriteStartArray("items");
            if (task["items"] is JsonArray source)
            {
                foreach (JsonNode? sub in source)
                {
                    if (sub is JsonObject obj)
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(ReadString(obj, "title"));
                        writer.WriteBooleanValue(ReadInt(obj, "status") == 1);
                        writer.WriteEndArray();
                    }
                }
            }

            writer.WriteEndArray();
        });
    }

    private static string LocalToRemoteBody(TodoItem item, string projectId, string? taskId)
    {
        return DidaJson.Build(writer =>
        {
            writer.WriteString("projectId", projectId);
            writer.WriteString("title", string.IsNullOrWhiteSpace(item.Text) ? "(untitled)" : item.Text);
            writer.WriteString("content", item.Notes ?? string.Empty);
            writer.WriteNumber("priority", item.IsImportant ? 5 : 0);
            writer.WriteString("timeZone", "Asia/Shanghai");

            if (!string.IsNullOrWhiteSpace(taskId))
            {
                writer.WriteString("id", taskId);
            }

            if (item.DueDate is { } due)
            {
                writer.WriteString("dueDate", DidaApiClient.FormatDida(due));
                writer.WriteBoolean("isAllDay", false);
            }

            if (item.Steps is { Count: > 0 } steps)
            {
                writer.WriteString("kind", "CHECKLIST");
                writer.WriteStartArray("items");
                for (int index = 0; index < steps.Count; index++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("title", steps[index].Text ?? string.Empty);
                    writer.WriteNumber("status", steps[index].IsCompleted ? 1 : 0);
                    writer.WriteNumber("sortOrder", index);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
        });
    }

    private static TodoItem NewLocalItem(JsonObject task)
    {
        var item = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = ReadString(task, "title") is { Length: > 0 } title ? title : "(untitled)",
            IsCompleted = ReadInt(task, "status") == 2,
            IsImportant = ReadInt(task, "priority") >= 5,
            DueDate = ParseDidaTime(ReadString(task, "dueDate")),
            Notes = ReadString(task, "content") is { Length: > 0 } notes ? notes : null,
            CompletedAt = ParseDidaTime(ReadString(task, "completedTime")),
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
                        Text = ReadString(obj, "title"),
                        IsCompleted = ReadInt(obj, "status") == 1,
                        SortOrder = index++
                    });
                }
            }
        }

        return item;
    }

    private static bool StepsEqual(TodoItem existing, JsonObject task)
    {
        if (task["items"] is not JsonArray incoming)
        {
            return (existing.Steps?.Count ?? 0) == 0;
        }

        int expected = 0;
        foreach (JsonNode? sub in incoming)
        {
            if (sub is JsonObject)
            {
                expected++;
            }
        }

        if ((existing.Steps?.Count ?? 0) != expected)
        {
            return false;
        }

        int index = 0;
        foreach (JsonNode? sub in incoming)
        {
            if (sub is not JsonObject row)
            {
                continue;
            }

            TodoStep step = existing.Steps![index++];
            if (!string.Equals(step.Text ?? string.Empty, ReadString(row, "title"), StringComparison.Ordinal) ||
                step.IsCompleted != (ReadInt(row, "status") == 1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RemoteFieldsChanged(TodoItem existing, JsonObject task)
    {
        string title = ReadString(task, "title");
        return
            !string.Equals(existing.Text ?? string.Empty, title.Length > 0 ? title : "(untitled)", StringComparison.Ordinal) ||
            existing.IsCompleted != (ReadInt(task, "status") == 2) ||
            existing.IsImportant != (ReadInt(task, "priority") >= 5) ||
            existing.DueDate != ParseDidaTime(ReadString(task, "dueDate")) ||
            !string.Equals(existing.Notes ?? string.Empty, ReadString(task, "content"), StringComparison.Ordinal);
    }

    private static void ApplyRemotePatch(TodoItem existing, JsonObject task, bool stepsSame)
    {
        string title = ReadString(task, "title");
        existing.Text = title.Length > 0 ? title : "(untitled)";
        existing.IsCompleted = ReadInt(task, "status") == 2;
        existing.IsImportant = ReadInt(task, "priority") >= 5;
        existing.DueDate = ParseDidaTime(ReadString(task, "dueDate"));
        existing.Notes = ReadString(task, "content") is { Length: > 0 } notes ? notes : null;
        existing.CompletedAt = ParseDidaTime(ReadString(task, "completedTime"));
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
                        Text = ReadString(obj, "title"),
                        IsCompleted = ReadInt(obj, "status") == 1,
                        SortOrder = index++
                    });
                }
            }
        }
    }

    private static string ReadString(JsonObject obj, string name)
    {
        try
        {
            JsonNode? node = obj[name];
            if (node is null)
            {
                return string.Empty;
            }

            if (node is JsonValue value && value.TryGetValue<string>(out string? text))
            {
                return text ?? string.Empty;
            }

            return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

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
                JsonValueKind.Number => node.GetValue<int>(),
                JsonValueKind.True => 1,
                JsonValueKind.False => 0,
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
