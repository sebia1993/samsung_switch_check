using System.Collections.Concurrent;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Views;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class AlertPopupService
{
    private readonly ConcurrentQueue<EventViewModel> _pending = new();
    private AlertPopup? _active;

    public void Enqueue(EventViewModel item)
    {
        _pending.Enqueue(item);
        ShowNext();
    }

    private void ShowNext()
    {
        if (_active is not null || !_pending.TryDequeue(out var item)) return;
        _active = new AlertPopup(item);
        _active.Closed += (_, _) =>
        {
            _active = null;
            ShowNext();
        };
        _active.Show();
    }
}
