using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

/// <summary>
/// Dida365 sync settings UI.
///
/// Settings runs in its own process (SettingsHost), so this page only edits
/// dida-sync.json and reads dida-sync-state.json for the last sync status. The
/// main process re-reads the config on every poll, which means a token typed here
/// takes effect within one poll interval without any IPC change or restart.
///
/// Sync scope is a multi-selection list: the Inbox plus every project the user
/// ticks ends up merged into the same Todo widget.
/// </summary>
public sealed partial class SettingsWindow
{
    private const string InboxProjectId = "inbox";

    private bool _didaUiInitialized;
    private bool _didaSuppressEvents;
    private DidaSyncConfig _didaConfig = new();

    private ToggleSwitch? DidaEnabledSwitch =>
        FindCreatedSectionElement<ToggleSwitch>("TodoSettings", "DidaSyncEnabledSwitch");

    private TextBox? DidaTokenInput =>
        FindCreatedSectionElement<TextBox>("TodoSettings", "DidaTokenBox");

    private ListView? DidaProjectList =>
        FindCreatedSectionElement<ListView>("TodoSettings", "DidaProjectList");

    private TextBlock? DidaStatusLabel =>
        FindCreatedSectionElement<TextBlock>("TodoSettings", "DidaStatusText");

    private void DidaSyncSection_Loaded(object sender, RoutedEventArgs e)
    {
        if (_didaUiInitialized)
        {
            UpdateDidaStatusLabel();
            return;
        }

        _didaUiInitialized = true;
        _didaConfig = DidaSyncConfig.Load();
        _didaSuppressEvents = true;
        try
        {
            if (DidaEnabledSwitch is { } toggle)
            {
                toggle.IsOn = _didaConfig.Enabled;
            }

            if (DidaTokenInput is { } input)
            {
                input.Text = _didaConfig.Token;
            }

            // Seed the list with the saved scope so the page is usable before
            // "测试并载入清单" is pressed; the connect step replaces it with the
            // account's real project names.
            if (DidaProjectList is { } list && list.ItemsSource is null)
            {
                var seeded = new List<DidaProjectItem>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                IReadOnlyList<string> scopeIds = _didaConfig.SyncProjectIds;
                string? storedName = !string.IsNullOrWhiteSpace(_didaConfig.ProjectName)
                    ? _didaConfig.ProjectName
                    : null;
                foreach (string id in scopeIds)
                {
                    if (seen.Add(id))
                    {
                        seeded.Add(new DidaProjectItem(
                            id,
                            scopeIds.Count == 1 && storedName is not null ? storedName : DisplayName(id)));
                    }
                }

                if (seeded.Count > 0)
                {
                    ApplyProjectList(list, seeded, _didaConfig.SyncProjectIds);
                }
            }
        }
        finally
        {
            _didaSuppressEvents = false;
        }

        UpdateDidaStatusLabel();
    }

    private void DidaSyncEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (_didaSuppressEvents)
        {
            return;
        }

        PersistDidaConfig();
        UpdateDidaStatusLabel();
    }

    private void DidaProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_didaSuppressEvents)
        {
            return;
        }

        PersistDidaConfig();
        UpdateDidaStatusLabel();
    }

    private void DidaSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (DidaProjectList is not { } list)
        {
            return;
        }

        _didaSuppressEvents = true;
        try
        {
            list.SelectAll();
        }
        finally
        {
            _didaSuppressEvents = false;
        }

        PersistDidaConfig();
        SetDidaStatus("已选中全部清单，点“保存”或等待自动同步。");
        UpdateDidaStatusLabel();
    }

    private void DidaSelectNone_Click(object sender, RoutedEventArgs e)
    {
        if (DidaProjectList is not { } list)
        {
            return;
        }

        _didaSuppressEvents = true;
        try
        {
            list.SelectedItems.Clear();
        }
        finally
        {
            _didaSuppressEvents = false;
        }

        PersistDidaConfig();
        UpdateDidaStatusLabel();
    }

    private void DidaSave_Click(object sender, RoutedEventArgs e) => PersistAndReport();

    private async void DidaConnect_Click(object sender, RoutedEventArgs e)
    {
        string token = DidaTokenInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            SetDidaStatus("请先粘贴滴答清单的 API 口令。");
            return;
        }

        SetDidaStatus("正在验证口令并读取清单…");
        try
        {
            var client = new DidaApiClient(DidaSyncConfig.DefaultBaseUrl, token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonArray projects = await client.ListProjectsAsync(timeout.Token);

            ListView? list = DidaProjectList;
            if (list is null)
            {
                SetDidaStatus("设置界面尚未就绪，请重新打开此页。");
                return;
            }

            var items = new List<DidaProjectItem>
            {
                new(InboxProjectId, "📥 收集箱（Inbox）")
            };
            foreach (JsonNode? node in projects)
            {
                if (node is not JsonObject project)
                {
                    continue;
                }

                string id = project["id"]?.GetValue<string>() ?? string.Empty;
                string name = project["name"]?.GetValue<string>() ?? "(未命名清单)";
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                items.Add(new DidaProjectItem(id, name));
            }

            if (items.Count == 1)
            {
                SetDidaStatus("口令有效，但账号里没有可用的清单。");
                return;
            }

            // Keep the saved scope when it still exists, otherwise tick
            // everything: a different API token brings entirely different
            // project ids, and an empty selection would look like a broken sync.
            IReadOnlyList<string> saved = _didaConfig.SyncProjectIds;
            bool anyKnown = saved.Count > 0 && items.Any(item => saved.Contains(item.Id));
            IReadOnlyList<string> wanted = anyKnown
                ? saved
                : items.Select(item => item.Id).ToList();

            _didaSuppressEvents = true;
            try
            {
                ApplyProjectList(list, items, wanted);
            }
            finally
            {
                _didaSuppressEvents = false;
            }

            PersistDidaConfig();
            int selected = list.SelectedItems.Count;
            SetDidaStatus($"口令有效，已载入 {items.Count - 1} 个清单，当前选中 {selected} 个（含收集箱）。");
        }
        catch (DidaApiException ex)
        {
            SetDidaStatus(ex.TokenRejected
                ? "口令被拒绝（401）。请到滴答清单网页版重新生成 API 口令。"
                : "验证失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            SetDidaStatus("验证失败：" + ex.Message);
        }
    }

    private void ApplyProjectList(
        ListView list,
        IReadOnlyList<DidaProjectItem> items,
        IReadOnlyCollection<string> selectedIds)
    {
        list.ItemsSource = null;
        list.SelectedItems.Clear();
        list.ItemsSource = items;

        foreach (DidaProjectItem item in items)
        {
            if (selectedIds.Contains(item.Id))
            {
                list.SelectedItems.Add(item);
            }
        }
    }

    private void PersistAndReport()
    {
        PersistDidaConfig();
        SetDidaStatus("已保存。DeskBox 会在 10 秒内按新配置同步。");
    }

    private void PersistDidaConfig()
    {
        if (!_didaUiInitialized)
        {
            return;
        }

        _didaConfig.Enabled = DidaEnabledSwitch?.IsOn ?? false;
        _didaConfig.Token = DidaTokenInput?.Text?.Trim() ?? string.Empty;

        ListView? list = DidaProjectList;
        if (list?.ItemsSource is not null)
        {
            var ids = new List<string>();
            var names = new List<string>();
            foreach (object entry in list.SelectedItems)
            {
                if (entry is DidaProjectItem item)
                {
                    ids.Add(item.Id);
                    names.Add(item.Name);
                }
            }

            _didaConfig.ProjectIds.Clear();
            _didaConfig.ProjectIds.AddRange(ids);
            _didaConfig.ProjectName = ShortenScope(names);
        }

        try
        {
            _didaConfig.Save();
        }
        catch (Exception ex)
        {
            App.Log($"[DidaSync] Config save failed: {ex.Message}");
            SetDidaStatus("配置保存失败：" + ex.Message);
        }
    }

    private void UpdateDidaStatusLabel()
    {
        IReadOnlyList<string> scope = _didaConfig.SyncProjectIds;

        if (!_didaConfig.Enabled)
        {
            SetDidaStatus("未启用。填入 API 口令、勾选清单，再打开开关。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_didaConfig.Token) || scope.Count == 0)
        {
            SetDidaStatus("已启用，但还缺 API 口令或同步清单。");
            return;
        }

        string scopeLabel = scope.Count == 1
            ? DisplayName(scope[0])
            : $"{scope.Count} 个清单";

        string statePath = Path.Combine(DeskBoxDataPathService.Current.RootPath, "dida-sync-state.json");
        try
        {
            if (File.Exists(statePath) &&
                JsonNode.Parse(File.ReadAllText(statePath)) is JsonObject state &&
                state["lastStatus"]?.GetValue<string>() is { Length: > 0 } status)
            {
                SetDidaStatus("同步范围：" + scopeLabel + "　·　" + status);
                return;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DidaSync] Status read failed: {ex.Message}");
        }

        SetDidaStatus("已启用，等待首次同步（同步范围：" + scopeLabel + "）。");
    }

    private static string DisplayName(string projectId) =>
        string.Equals(projectId, InboxProjectId, StringComparison.OrdinalIgnoreCase)
            ? "📥 收集箱（Inbox）"
            : projectId;

    private static string ShortenScope(List<string> names)
    {
        if (names.Count == 0)
        {
            return string.Empty;
        }

        string joined = string.Join("、", names);
        return joined.Length <= 60 ? joined : joined[..60] + "…";
    }

    private void SetDidaStatus(string text)
    {
        if (DidaStatusLabel is { } label)
        {
            label.Text = text;
        }
    }
}

/// <summary>Checkable row for a Dida365 project. ToString drives the display.</summary>
internal sealed class DidaProjectItem
{
    internal DidaProjectItem(string id, string name)
    {
        Id = id;
        Name = name;
    }

    internal string Id { get; }

    internal string Name { get; }

    public override string ToString() => Name;
}
