using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class MiniWindow : Window
{
    private bool _allowClose;

    public MiniWindow(DashboardViewModel viewModel, bool topmost)
    {
        InitializeComponent();
        DataContext = viewModel;
        Topmost = topmost;
        TopmostToggle.IsChecked = topmost;
    }

    public void AllowClose() => _allowClose = true;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void TopmostToggle_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
        (Application.Current as App)?.SetMiniTopmost(Topmost);
    }

    private void Dashboard_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowDashboard();
    private void Hide_Click(object sender, RoutedEventArgs e) => HideAndSave();
    private void OnDeactivated(object sender, EventArgs e) { }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideAndSave();
    }

    private void HideAndSave()
    {
        (Application.Current as App)?.SaveMiniWindowBounds(this);
        Hide();
    }
}
