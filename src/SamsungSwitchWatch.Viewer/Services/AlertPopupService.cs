using System.Windows;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Views;

namespace SamsungSwitchWatch.Viewer.Services;

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
            _items.AddLast((key, item));
            while (_items.Count > _capacity)
            {
                var removed = _items.First!.Value;
                _items.RemoveFirst();
                _keys.Remove(removed.Key);
            }
            return true;
        }
    }

    public static string KeyFor(EventViewModel item) => string.IsNullOrWhiteSpace(item.ConditionKey)
        ? item.AgentEventId
        : $"{item.DeviceId}:{item.ConditionKey}";

    public bool TryDequeue(out EventViewModel? item)
    {
        lock (_sync)
        {
            if (_items.First is null)
            {
                item = null;
                return false;
            }
            var removed = _items.First.Value;
            _items.RemoveFirst();
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
    private AlertPopup? _active;
    private string? _activeKey;
    private bool _disposed;

    public void Enqueue(EventViewModel item)
    {
        if (_disposed || string.Equals(_activeKey, BoundedAlertQueue.KeyFor(item), StringComparison.Ordinal)
            || !_pending.Enqueue(item)) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) ShowNext();
        else dispatcher.BeginInvoke(ShowNext);
    }

    private void ShowNext()
    {
        if (_disposed || _active is not null || !_pending.TryDequeue(out var item) || item is null) return;
        _active = new AlertPopup(item);
        _activeKey = BoundedAlertQueue.KeyFor(item);
        _active.Closed += OnPopupClosed;
        _active.Show();
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (_active is not null) _active.Closed -= OnPopupClosed;
        _active = null;
        _activeKey = null;
        ShowNext();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending.Clear();
        if (_active is not null)
        {
            _active.Closed -= OnPopupClosed;
            _active.Close();
            _active = null;
            _activeKey = null;
        }
    }
}
