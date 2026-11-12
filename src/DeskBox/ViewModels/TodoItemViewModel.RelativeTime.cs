namespace DeskBox.ViewModels;

/// <summary>
/// Wall-clock dependent properties of a todo row.
///
/// <c>DueStatusText</c> ("今天 11:00" / "明天 11:00") and <c>IsOverdue</c> are
/// plain getters, so the list only re-reads them when something raises a change
/// notification. A widget that stays open while the date rolls over therefore
/// keeps yesterday's wording: a task due today keeps saying "明天 11:00" until an
/// unrelated edit happens to refresh the row. <see cref="TodoWidgetViewModel"/>
/// calls this once per calendar day.
/// </summary>
public sealed partial class TodoItemViewModel
{
    internal void RaiseRelativeTimeChanged()
    {
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(DueStatusText));
        OnPropertyChanged(nameof(DueStatusNormalVisibility));
        OnPropertyChanged(nameof(DueStatusOverdueVisibility));
        OnPropertyChanged(nameof(MetadataDueDateText));
    }
}
