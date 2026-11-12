using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

/// <summary>
/// Keeps the "today / tomorrow" wording of the todo list honest.
///
/// The widget is designed to stay on the desktop for days at a time, but every
/// relative due label is computed from <c>DateTimeOffset.Now</c> and cached in a
/// binding until something raises a property change. Nothing did at midnight, so
/// a task due 11-12 11:00 kept showing "明天 11:00" on 11-12 because the string had
/// been produced on 11-11. This ticker watches the calendar day and re-raises the
/// wall-clock dependent properties, which also lets overdue rows turn red on
/// time.
/// </summary>
public sealed partial class TodoWidgetViewModel
{
    private DispatcherQueueTimer? _relativeTimeTicker;
    private DateTime _relativeTimeDay = DateTime.MinValue;

    private void AttachRelativeTimeTicker()
    {
        try
        {
            DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                return;
            }

            _relativeTimeDay = DateTime.Now.Date;
            _relativeTimeTicker = queue.CreateTimer();
            _relativeTimeTicker.Interval = TimeSpan.FromSeconds(30);
            _relativeTimeTicker.IsRepeating = true;
            _relativeTimeTicker.Tick += OnRelativeTimeTick;
            _relativeTimeTicker.Start();
        }
        catch (Exception ex)
        {
            App.Log($"[TodoTime] ticker unavailable: {ex.Message}");
        }
    }

    private void OnRelativeTimeTick(DispatcherQueueTimer sender, object args)
    {
        DateTime today = DateTime.Now.Date;
        if (today == _relativeTimeDay)
        {
            return;
        }

        _relativeTimeDay = today;
        foreach (TodoItemViewModel item in Items)
        {
            item.RaiseRelativeTimeChanged();
        }

        OnPropertyChanged(nameof(TodayFilterCount));
        OnPropertyChanged(nameof(ThisWeekFilterCount));
        OnPropertyChanged(nameof(ThisMonthFilterCount));
    }
}
