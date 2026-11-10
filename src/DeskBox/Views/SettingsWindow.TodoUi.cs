using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

/// <summary>
/// Todo widget appearance switches.
///
/// The widget is a separate process, so a change here is written to
/// todo-ui.json and announced with the existing todo.sync signal; the widget
/// reloads its list and re-evaluates the visibility bindings.
/// </summary>
public sealed partial class SettingsWindow
{
    private bool _todoUiInitialized;

    private ToggleSwitch? TodoColorFilterSwitch =>
        FindCreatedSectionElement<ToggleSwitch>("TodoSettings", "TodoColorFilterSwitch");

    private void TodoUiSection_Loaded(object sender, RoutedEventArgs e)
    {
        if (_todoUiInitialized)
        {
            return;
        }

        _todoUiInitialized = true;
        if (TodoColorFilterSwitch is { } toggle)
        {
            toggle.IsOn = TodoUiOptions.ShowColorFilter;
        }
    }

    private void TodoColorFilter_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_todoUiInitialized)
        {
            return;
        }

        bool show = TodoColorFilterSwitch?.IsOn ?? true;
        TodoUiOptions.SetShowColorFilter(show);
        TodoUiOptions.PokeTodoSignal();
    }
}
