using DeskBox.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

/// <summary>Right-click command that pins a todo row to the top, or releases it.</summary>
public sealed partial class TodoWidgetContent
{
    private MenuFlyoutItem CreatePinToggleCommand(TodoItemViewModel item)
    {
        var pinItem = new MenuFlyoutItem
        {
            Text = item.PinToggleText,
            Icon = new FontIcon { Glyph = item.PinToggleGlyph }
        };
        pinItem.Click += (_, _) =>
        {
            if (ViewModel is not { } viewModel)
            {
                return;
            }

            _ = viewModel.SetPinnedAsync(item.Id, !item.IsPinned);
        };
        return pinItem;
    }
}
