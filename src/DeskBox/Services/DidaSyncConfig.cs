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

    public string ProjectId { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public int PollSeconds { get; set; } = 12;

    public int CompletedLookbackDays { get; set; } = 7;

    public bool IsUsable =>
        Enabled &&
        !string.IsNullOrWhiteSpace(Token) &&
        !string.IsNullOrWhiteSpace(ProjectId);

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
        var root = new JsonObject
        {
            ["enabled"] = Enabled,
            ["token"] = Token ?? string.Empty,
            ["baseUrl"] = string.IsNullOrWhiteSpace(BaseUrl) ? DefaultBaseUrl : BaseUrl,
            ["projectId"] = ProjectId ?? string.Empty,
            ["projectName"] = ProjectName ?? string.Empty,
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
