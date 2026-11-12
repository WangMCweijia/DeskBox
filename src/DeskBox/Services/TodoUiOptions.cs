using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskBox.Services;

/// <summary>
/// Small UI switches for the Todo widget, stored next to dida-sync.json.
///
/// The settings window runs in its own process, so the widget cannot be told
/// about a change over IPC. Instead the file's timestamp is the cache key: the
/// view model re-reads the value the next time it refreshes, and the settings
/// side pokes the existing "todo.sync" signal to force that refresh.
/// </summary>
internal static class TodoUiOptions
{
    private const string FileName = "todo-ui.json";
    private const string ShowColorFilterKey = "showColorFilter";

    private static readonly object Gate = new();
    private static long _stamp = long.MinValue;
    private static bool _showColorFilter = true;

    internal static string FilePath =>
        Path.Combine(DeskBoxDataPathService.Current.RootPath, FileName);

    /// <summary>Whether the colour filter strip above the task list is shown.</summary>
    internal static bool ShowColorFilter
    {
        get
        {
            Refresh();
            lock (Gate)
            {
                return _showColorFilter;
            }
        }
    }

    internal static void SetShowColorFilter(bool value)
    {
        try
        {
            // Same AOT trap as dida-sync.json: JsonObject["k"] = bool and
            // ToJsonString() both need reflection metadata. Stream it instead.
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteBoolean(ShowColorFilterKey, value);
                writer.WriteEndObject();
            }

            string path = FilePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, Encoding.UTF8.GetString(buffer.ToArray()));
            File.Move(temp, path, overwrite: true);

            lock (Gate)
            {
                _showColorFilter = value;
                _stamp = long.MinValue;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TodoUi] Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Touches every widget's todo.sync so the running widget rebuilds its list
    /// and re-evaluates the visibility bindings immediately.
    /// </summary>
    internal static void PokeTodoSignal()
    {
        try
        {
            string widgetsRoot = Path.Combine(DeskBoxDataPathService.Current.RootPath, "data", "widgets");
            if (!Directory.Exists(widgetsRoot))
            {
                return;
            }

            string payload = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (string directory in Directory.GetDirectories(widgetsRoot))
            {
                try
                {
                    File.WriteAllText(Path.Combine(directory, "todo.sync"), payload);
                }
                catch (Exception ex)
                {
                    App.Log($"[TodoUi] Signal failed in {directory}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TodoUi] Signal dispatch failed: {ex.Message}");
        }
    }

    private static void Refresh()
    {
        long stamp;
        try
        {
            var info = new FileInfo(FilePath);
            stamp = info.Exists ? info.LastWriteTimeUtc.Ticks : 0L;
        }
        catch
        {
            return;
        }

        lock (Gate)
        {
            if (stamp == _stamp)
            {
                return;
            }

            _stamp = stamp;
            if (stamp == 0L)
            {
                _showColorFilter = true;
                return;
            }
        }

        // Parsing outside the lock: the file is a few bytes, and a torn read
        // only costs one refresh cycle.
        bool parsed = true;
        try
        {
            JsonObject root = ReadRoot();
            parsed = root[ShowColorFilterKey]?.GetValue<bool>() ?? true;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoUi] Read failed: {ex.Message}");
            parsed = true;
        }

        lock (Gate)
        {
            _showColorFilter = parsed;
        }
    }

    private static JsonObject ReadRoot()
    {
        try
        {
            if (File.Exists(FilePath) &&
                JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject root)
            {
                return root;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TodoUi] Ignoring unreadable options: {ex.Message}");
        }

        return new JsonObject();
    }
}
