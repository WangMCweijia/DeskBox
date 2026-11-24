using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

/// <summary>
/// Local-only "pin to the top of the list" state for a todo row.
/// </summary>
/// <remarks>
/// Pinning is a view preference, not task content: the Dida365 projection has
/// no equivalent field, so <see cref="Models.TodoItem.IsPinned"/> is never
/// pushed upstream and the sync engine never reads it. The flag rides along in
/// todo.json like the other local fields.
/// </remarks>
public sealed partial class TodoItemViewModel
{
    public bool IsPinned => _item.IsPinned;

    /// <summary>Small marker shown in the row's metadata line while pinned.</summary>
    public Visibility PinVisibility => _item.IsPinned ? Visibility.Visible : Visibility.Collapsed;

    public string PinToggleText => LocalizedText(_item.IsPinned
        ? "Todo.Menu.Unpin"
        : "Todo.Menu.Pin");

    /// <summary>E77A is the "unpin" glyph, E718 the "pin" one.</summary>
    public string PinToggleGlyph => _item.IsPinned ? "\uE77A" : "\uE718";

    internal void ApplyPinned(bool isPinned)
    {
        if (_item.IsPinned == isPinned)
        {
            return;
        }

        _item.IsPinned = isPinned;
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinVisibility));
        OnPropertyChanged(nameof(PinToggleText));
        OnPropertyChanged(nameof(PinToggleGlyph));
    }
}
