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
/// </summary>
public sealed partial class SettingsWindow
{
    private bool _didaUiInitialized;
    private bool _didaSuppressEvents;
    private DidaSyncConfig _didaConfig = new();

    private ToggleSwitch? DidaEnabledSwitch =>
        FindCreatedSectionElement<ToggleSwitch>("TodoSettings", "DidaSyncEnabledSwitch");

    private TextBox? DidaTokenInput =>
        FindCreatedSectionElement<TextBox>("TodoSettings", "DidaTokenBox");

    private ComboBox? DidaProjectCombo =>
        FindCreatedSectionElement<ComboBox>("TodoSettings", "DidaProjectBox");

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

            if (DidaProjectCombo is { } combo && !string.IsNullOrWhiteSpace(_didaConfig.ProjectId))
            {
                combo.Items.Clear();
                combo.Items.Add(new DidaProjectItem(_didaConfig.ProjectId, _didaConfig.ProjectName));
                combo.SelectedIndex = 0;
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

            ComboBox? combo = DidaProjectCombo;
            if (combo is null)
            {
                SetDidaStatus("设置界面尚未就绪，请重新打开此页。");
                return;
            }

            string previousId = (combo.SelectedItem as DidaProjectItem)?.Id ?? _didaConfig.ProjectId;
            _didaSuppressEvents = true;
            try
            {
                combo.Items.Clear();
                int selected = -1;
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

                    combo.Items.Add(new DidaProjectItem(id, name));
                    if (id == previousId)
                    {
                        selected = combo.Items.Count - 1;
                    }
                }

                if (combo.Items.Count == 0)
                {
                    SetDidaStatus("口令有效，但账号里没有可用的清单。");
                    return;
                }

                combo.SelectedIndex = selected >= 0 ? selected : 0;
            }
            finally
            {
                _didaSuppressEvents = false;
            }

            PersistDidaConfig();
            SetDidaStatus($"口令有效，已载入 {combo.Items.Count} 个清单。打开上方开关即可开始同步。");
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
        if (DidaProjectCombo?.SelectedItem is DidaProjectItem project)
        {
            _didaConfig.ProjectId = project.Id;
            _didaConfig.ProjectName = project.Name;
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
        if (!_didaConfig.Enabled)
        {
            SetDidaStatus("未启用。填入 API 口令、选好清单，再打开开关。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_didaConfig.Token) || string.IsNullOrWhiteSpace(_didaConfig.ProjectId))
        {
            SetDidaStatus("已启用，但还缺 API 口令或同步清单。");
            return;
        }

        string statePath = Path.Combine(DeskBoxDataPathService.Current.RootPath, "dida-sync-state.json");
        try
        {
            if (File.Exists(statePath) &&
                JsonNode.Parse(File.ReadAllText(statePath)) is JsonObject state &&
                state["lastStatus"]?.GetValue<string>() is { Length: > 0 } status)
            {
                SetDidaStatus("清单：" + _didaConfig.ProjectName + "　·　" + status);
                return;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DidaSync] Status read failed: {ex.Message}");
        }

        SetDidaStatus("已启用，等待首次同步（清单：" + _didaConfig.ProjectName + "）。");
    }

    private void SetDidaStatus(string text)
    {
        if (DidaStatusLabel is { } label)
        {
            label.Text = text;
        }
    }
}

/// <summary>ComboBox row for a Dida365 project. ToString drives the display.</summary>
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
