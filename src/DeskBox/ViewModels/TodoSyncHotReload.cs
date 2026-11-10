using System.Collections.Concurrent;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;

namespace DeskBox.ViewModels;

/// <summary>
/// External-change hot reload for the Todo widget.
///
/// The sync agent writes todo.json and then writes a "todo.sync" signal file.
/// DeskBox only watches the signal, so its own SaveAsync writes never trigger
/// a reload and no "pull -> dirty -> push -> pull" loop can form. This is the
/// ApplyMergedDataAsync entry point described in
/// docs/architecture/cloud-backup-audit-and-live-sync-20260921.md section 4.2,
/// applied to a single shared Todo collection.
/// </summary>
public sealed partial class TodoWidgetViewModel
{
    private DispatcherQueue? _syncDispatcherQueue;
    private string _syncLastSignal = string.Empty;
    private int _syncInFlight;

    internal void AttachSync(TodoWidgetStore store)
    {
        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            App.Log("[TodoSync] No DispatcherQueue on the creating thread; hot reload disabled.");
            return;
        }

        _syncDispatcherQueue = queue;
        TodoSyncWatcher.Attach(this, store);
    }

    internal void DetachSync() => TodoSyncWatcher.Detach(this);

    internal void OnSyncSignal(string signal)
    {
        if (_isDisposed || _syncDispatcherQueue is null || string.IsNullOrWhiteSpace(signal))
        {
            return;
        }

        if (string.Equals(signal, _syncLastSignal, StringComparison.Ordinal))
        {
            return;
        }

        _syncLastSignal = signal;
        if (Interlocked.Exchange(ref _syncInFlight, 1) != 0)
        {
            return;
        }

        DispatcherQueue queue = _syncDispatcherQueue;
        _ = Task.Run(async () =>
        {
            try
            {
                // Coalesce the Created/Changed/Renamed burst from an atomic
                // replace, and let the agent finish committing todo.json.
                await Task.Delay(220);
                TodoWidgetData data = await _store.LoadAsync();
                if (_isDisposed)
                {
                    return;
                }

                queue.TryEnqueue(() => TryApplySyncDataOnUiThread(data, 0));
            }
            catch (Exception ex)
            {
                App.Log($"[TodoSync] Reload failed: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _syncInFlight, 0);
            }
        });
    }

    private void TryApplySyncDataOnUiThread(TodoWidgetData data, int attempt)
    {
        if (_isDisposed || _syncDispatcherQueue is null)
        {
            return;
        }

        // A rebuild would discard keystrokes in the detail editor or an
        // inline title edit. Defer a few beats instead of losing them.
        if (IsDetailPageOpen || Items.Any(item => item.IsEditing))
        {
            if (attempt >= 10)
            {
                App.Log("[TodoSync] Deferred reload gave up: detail editor stayed open.");
                return;
            }

            DispatcherQueueTimer timer = _syncDispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.IsRepeating = false;
            timer.Tick += (sender, _) =>
            {
                sender.Stop();
                TryApplySyncDataOnUiThread(data, attempt + 1);
            };
            timer.Start();
            return;
        }

        try
        {
            ApplySyncData(data);
        }
        catch (Exception ex)
        {
            App.Log($"[TodoSync] Apply failed: {ex}");
        }
    }

    private void ApplySyncData(TodoWidgetData data)
    {
        string? selectedId = SelectedDetailItem?.Id;
        Items.Clear();
        _tombstones.Clear();
        foreach (TodoItem item in data.Items
                     .OrderBy(item => item.SortOrder)
                     .ThenByDescending(item => item.UpdatedAt))
        {
            if (item.IsDeleted)
            {
                _tombstones.Add(item);
                continue;
            }

            Items.Add(new TodoItemViewModel(item, _localizationService));
        }

        SelectedDetailItem = null;
        IsCreatingDetailItem = false;
        NormalizeSortOrders();
        RefreshVisibleItems();
        RefreshCountProperties();
        OnPropertyChanged(nameof(VisibleItemsSource));

        if (selectedId is not null)
        {
            TodoItemViewModel? restored = Items.FirstOrDefault(
                item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));
            if (restored is not null)
            {
                SelectedDetailItem = restored;
            }
        }
    }
}

internal static class TodoSyncWatcher
{
    internal const string SignalFileName = "todo.sync";

    private sealed class Registration
    {
        public required FileSystemWatcher Watcher { get; init; }
        public required WeakReference<TodoWidgetViewModel> Owner { get; init; }
        public required string SignalPath { get; init; }

        public void Dispose()
        {
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
        }
    }

    private static readonly ConcurrentDictionary<TodoWidgetViewModel, Registration> s_registrations = new();

    public static void Attach(TodoWidgetViewModel owner, TodoWidgetStore store)
    {
        try
        {
            string? directory = Path.GetDirectoryName(store.StorePath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory, SignalFileName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };
            var registration = new Registration
            {
                Watcher = watcher,
                Owner = new WeakReference<TodoWidgetViewModel>(owner),
                SignalPath = Path.Combine(directory, SignalFileName)
            };

            watcher.Created += (_, args) => HandleSignal(registration, args);
            watcher.Changed += (_, args) => HandleSignal(registration, args);
            watcher.Renamed += (_, args) => HandleSignal(registration, args);
            watcher.Error += (_, args) =>
                App.Log($"[TodoSync] Watcher error: {args.GetException().Message}");
            watcher.EnableRaisingEvents = true;

            s_registrations[owner] = registration;
        }
        catch (Exception ex)
        {
            App.Log($"[TodoSync] Watcher start failed: {ex}");
        }
    }

    public static void Detach(TodoWidgetViewModel owner)
    {
        if (s_registrations.TryRemove(owner, out Registration? registration))
        {
            registration.Dispose();
        }
    }

    private static void HandleSignal(Registration registration, FileSystemEventArgs args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120);
                string signal = await ReadSignalAsync(registration.SignalPath);
                if (string.IsNullOrWhiteSpace(signal))
                {
                    return;
                }

                if (registration.Owner.TryGetTarget(out TodoWidgetViewModel? owner))
                {
                    owner.OnSyncSignal(signal.Trim());
                }
            }
            catch (Exception ex)
            {
                App.Log($"[TodoSync] Signal handling failed: {ex.Message}");
            }
        });
    }

    private static async Task<string> ReadSignalAsync(string path)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(60);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                await Task.Delay(60);
            }
        }

        return string.Empty;
    }
}
