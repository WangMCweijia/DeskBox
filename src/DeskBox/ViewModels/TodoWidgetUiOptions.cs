using DeskBox.Services;

namespace DeskBox.ViewModels;

/// <summary>
/// Keeps the Todo widget consistent with the user's UI switches.
///
/// The colour filter strip can be hidden from Settings. When it is hidden and a
/// filter is still active, the list would stay filtered with no way to clear
/// it, so the filter falls back to "all" on the next refresh.
/// </summary>
public sealed partial class TodoWidgetViewModel
{
    internal void EnsureColorFilterAllowedIfHidden()
    {
        if (!TodoUiOptions.ShowColorFilter && _selectedColorFilter != TodoColorFilter.All)
        {
            SelectedColorFilter = TodoColorFilter.All;
        }
    }
}
