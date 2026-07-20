using System.Windows;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Views;

namespace SamsungSwitchWatch.Viewer.Services;

public interface IWindowsToastBackend
{
    bool TryShow(EventViewModel item, Action<EventViewModel> activated);
}

public sealed class BoundedAlertQueue(int capacity = 20)
{
    private readonly int _capacity = Math.Clamp(capacity, 1, 100);
    private readonly LinkedList<(string Key, EventViewModel Event)> _items = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public int Count
    {
        get { lock (_sync) return _items.Count; }
    }

    public bool Enqueue(EventViewModel item)
    {
        var key = KeyFor(item);
        lock (_sync)
        {
            if (!_keys.Add(key)) return false;
            if (_items.Count >= _capacity)
            {
                var lowestPriority = _items.Min(entry => PriorityFor(entry.Event));
                if (PriorityFor(item) < lowestPriority)
                {
                    _keys.Remove(key);
                    return false;
                }
                var candidate = _items.First;
                while (candidate is not null && PriorityFor(candidate.Value.Event) != lowestPriority)
                {
                    candidate = candidate.Next;
                }
                if (candidate is not null)
                {
                    _keys.Remove(candidate.Value.Key);
                    _items.Remove(candidate);
                }
            }
            _items.AddLast((key, item));
            return true;
        }
    }

    public static string KeyFor(EventViewModel item) => string.IsNullOrWhiteSpace(item.ConditionKey)
        ? item.AgentEventId
        : $"{item.DeviceId}:{item.ConditionKey}:{(item.Recovered ? "recovery" : "active")}";

    public static int PriorityFor(EventViewModel item) => item.Recovered ? 3 : item.Severity switch
    {
        DeviceHealth.Critical => 5,
        DeviceHealth.Disconnected => 4,
        DeviceHealth.Warning => 2,
        _ => 1
    };

    public static bool ShouldPreempt(EventViewModel active, EventViewModel incoming) =>
        PriorityFor(incoming) > PriorityFor(active);

    public bool TryDequeue(out EventViewModel? item)
    {
        lock (_sync)
        {
            if (_items.First is null)
            {
                item = null;
                return false;
            }
            var highestPriority = _items.Max(entry => PriorityFor(entry.Event));
            var node = _items.First;
            while (node is not null && PriorityFor(node.Value.Event) != highestPriority) node = node.Next;
            var removed = node!.Value;
            _items.Remove(node);
            _keys.Remove(removed.Key);
            item = removed.Event;
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
            _keys.Clear();
        }
    }
}

public sealed class AlertPopupService : IDisposable
{
    private readonly BoundedAlertQueue _pending = new(20);
    private readonly Action<EventViewModel>? _openAlert;
    private readonly IWindowsToastBackend? _nativeToast;
    private AlertPopup? _active;
    private EventViewModel? _activeItem;
    private string? _activeKey;
    private bool _disposed;

    public AlertPopupService(
        Action<EventViewModel>? openAlert = null,
        IWindowsToastBackend? nativeToast = null)
    {
        _openAlert = openAlert;
        _nativeToast = nativeToast;
    }

    public void Enqueue(EventViewModel item)
    {
        if (_disposed) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) EnqueueCore(item);
        else dispatcher.BeginInvoke(() => EnqueueCore(item));
    }

    private void EnqueueCore(EventViewModel item)
    {
        if (_disposed || string.Equals(_activeKey, BoundedAlertQueue.KeyFor(item), StringComparison.Ordinal)) return;
        if (_nativeToast is not null && _openAlert is not null)
        {
            try
            {
                if (_nativeToast.TryShow(item, _openAlert)) return;
            }
            catch
            {
                // Native notifications can be unavailable because of Windows
                // policy or an invalid shell registration. The WPF popup below
                // remains the safe, deterministic fallback.
            }
        }
        if (_activeItem is not null && BoundedAlertQueue.ShouldPreempt(_activeItem, item))
        {
            var interrupted = _activeItem;
            CloseActive();
            _pending.Enqueue(interrupted);
        }
        if (!_pending.Enqueue(item)) return;
        ShowNext();
    }

    private void ShowNext()
    {
        if (_disposed || _active is not null || !_pending.TryDequeue(out var item) || item is null) return;
        _activeItem = item;
        _active = new AlertPopup(item, _openAlert);
        _activeKey = BoundedAlertQueue.KeyFor(item);
        _active.Closed += OnPopupClosed;
        _active.Show();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (_active is not null) _active.Closed -= OnPopupClosed;
        _active = null;
        _activeItem = null;
        _activeKey = null;
        ShowNext();
    }

    private void CloseActive()
    {
        if (_active is null) return;
        var window = _active;
        window.Closed -= OnPopupClosed;
        _active = null;
        _activeItem = null;
        _activeKey = null;
        window.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending.Clear();
        CloseActive();
    }
}
