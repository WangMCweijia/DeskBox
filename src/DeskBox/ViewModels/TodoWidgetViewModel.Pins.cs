namespace DeskBox.ViewModels;

/// <summary>
/// Pin / unpin a todo row so it floats above the manual order.
/// </summary>
/// <remarks>
/// Pinning deliberately does not touch <see cref="Models.TodoItem.UpdatedAt"/>:
/// the sync engine treats a newer timestamp as a local edit and would push the
/// whole task back to Dida365, overwriting anything changed on the phone. A
/// pin is a local view preference, so it stays out of the merge decision.
/// </remarks>
public sealed partial class TodoWidgetViewModel
{
    public async Task<bool> SetPinnedAsync(string itemId, bool isPinned)
    {
        TodoItemViewModel? item = FindItem(itemId);
        if (item is null)
        {
            return false;
        }

        if (item.IsPinned == isPinned)
        {
            return true;
        }

        item.ApplyPinned(isPinned);
        // SortOrder is left untouched, so unpinning drops the row back to the
        // place it occupied before it was pinned.
        RefreshVisibleItems(item);
        await SaveAsync();
        return true;
    }
}
