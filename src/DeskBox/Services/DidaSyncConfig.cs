using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace DeskBox.Services;

/// <summary>
/// Dida365 (dida365.com) sync configuration.
///
/// The settings window edits this file directly; the main process re-reads it on
/// every poll, so a token entered in Settings takes effect without a restart and
/// without any IPC contract change.
/// </summary>
internal sealed class DidaSyncConfig
{
    internal const string FileName = "dida-sync.json";
    internal const string DefaultBaseUrl = "https://api.dida365.com/open/v1";

    public bool Enabled { get; set; }

    public string Token { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Legacy single-project target. Kept so an older dida-sync.json keeps
    /// working; new writes use <see cref="ProjectIds"/>.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Projects that feed the single Todo widget. The literal value "inbox"
    /// stands for the Dida365 Inbox, which is not returned by /project.
    /// </summary>
    public List<string> ProjectIds { get; } = new();

    public int PollSeconds { get; set; } = 12;

    public int CompletedLookbackDays { get; set; } = 7;

    /// <summary>Effective sync scope: explicit list first, legacy field as fallback.</summary>
    internal IReadOnlyList<string> SyncProjectIds
    {
        get
        {
            if (ProjectIds.Count > 0)
            {
                return ProjectIds;
            }

            return string.IsNullOrWhiteSpace(ProjectId) ? Array.Empty<string>() : new[] { ProjectId };
        }
    }

    /// <summary>Where brand-new DeskBox items are created.</summary>
    internal string PrimaryProjectId
    {
        get
        {
            IReadOnlyList<string> ids = SyncProjectIds;
            return ids.Count > 0 ? ids[0] : string.Empty;
        }
    }

    public bool IsUsable =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Token) &&
        SyncProjectIds.Count > 0;

    internal static string FilePath =>
        Path.Combine(DeskBoxDataPathService.Current.RootPath, FileName);

    internal static DidaSyncConfig Load()
    {
        var config = new DidaSyncConfig();
        try
        {
            if (!File.Exists(FilePath))
            {
                return config;
            }

            string json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return config;
            }

            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return config;
            }

            config.Enabled = ReadBool(root, "enabled");
            config.Token = ReadString(root, "token");
            string baseUrl = ReadString(root, "baseUrl");
            config.BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            config.ProjectId = ReadString(root, "projectId");
            config.ProjectName = ReadString(root, "projectName");
            if (root["projectIds"] is JsonArray ids)
            {
                foreach (JsonNode? node in ids)
                {
                    string id = node?.GetValue<string>()?.Trim() ?? string.Empty;
                    if (id.Length > 0 && !config.ProjectIds.Contains(id, StringComparer.Ordinal))
                    {
                        config.ProjectIds.Add(id);
                    }
                }
            }

            config.PollSeconds = Math.Clamp(ReadInt(root, "pollSeconds", 12), 5, 600);
            config.CompletedLookbackDays = Math.Clamp(ReadInt(root, "completedLookbackDays", 7), 1, 365);
        }
        catch (Exception ex)
        {
            App.Log($"[DidaSync] Config unreadable, using defaults: {ex.Message}");
        }

        return config;
    }

    internal void Save()
    {
        var projectIds = new JsonArray();
        foreach (string id in SyncProjectIds)
        {
            projectIds.Add(id);
        }

        var root = new JsonObject
        {
            ["enabled"] = Enabled,
            ["token"] = Token ?? string.Empty,
            ["baseUrl"] = string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl,
            ["projectId"] = PrimaryProjectId,
            ["projectName"] = ProjectName ?? string.Empty,
            ["projectIds"] = projectIds,
            ["pollSeconds"] = PollSeconds,
            ["completedLookbackDays"] = CompletedLookbackDays
        };

        string path = FilePath;
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static string ReadString(JsonObject root, string name)
    {
        try
        {
            return root[name]?.GetValue<string>()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ReadBool(JsonObject root, string name)
    {
        try
        {
            return root[name]?.GetValue<bool>() ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadInt(JsonObject root, string name, int fallback)
    {
        try
        {
            return root[name]?.GetValue<int>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
