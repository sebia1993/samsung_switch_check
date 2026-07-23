using System.Windows;
using System.Windows.Threading;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class AlertPopup : Window
{
    private readonly DispatcherTimer _timer;
    private readonly EventViewModel _item;
    private readonly Action<EventViewModel>? _openRequested;

    public AlertPopup(EventViewModel item, Action<EventViewModel>? openRequested = null)
    {
        InitializeComponent();
        _item = item;
        _openRequested = openRequested;
        DataContext = item;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _timer.Tick += Timer_Tick;
        Closed += AlertPopup_Closed;
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 16;
            Top = work.Bottom - Height - 16;
            _timer.Start();
        };
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        if (_openRequested is not null) _openRequested(_item);
        else (Application.Current as App)?.ShowDashboard();
        ClosePopup();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => ClosePopup();

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (AlertPopupAutoClosePolicy.ShouldClose(IsMouseOver, IsKeyboardFocusWithin))
        {
            ClosePopup();
        }
    }

    private void AlertPopup_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        Closed -= AlertPopup_Closed;
    }

    private void ClosePopup()
    {
        _timer.Stop();
        Close();
    }
}

internal static class AlertPopupAutoClosePolicy
{
    public static bool ShouldClose(bool isMouseOver, bool isKeyboardFocusWithin) =>
        !isMouseOver && !isKeyboardFocusWithin;
}
