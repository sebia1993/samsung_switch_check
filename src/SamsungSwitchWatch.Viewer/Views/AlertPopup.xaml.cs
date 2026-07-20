using System.Windows;
using System.Windows.Threading;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class AlertPopup : Window
{
    private readonly DispatcherTimer _timer;

    public AlertPopup(EventViewModel item)
    {
        InitializeComponent();
        DataContext = item;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _timer.Tick += (_, _) => ClosePopup();
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
        (Application.Current as App)?.ShowDashboard();
        ClosePopup();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => ClosePopup();

    private void ClosePopup()
    {
        _timer.Stop();
        Close();
    }
}
