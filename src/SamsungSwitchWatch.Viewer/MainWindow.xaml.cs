using System.ComponentModel;
using System.Windows;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public void AllowClose() => _allowClose = true;

    private void OnLoaded(object sender, RoutedEventArgs e) => (Application.Current as App)?.RestoreMainWindowBounds(this);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        (Application.Current as App)?.ShowTrayHint();
    }

    private void ShowMiniWindow_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowMiniWindow();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.OpenConnectionSettings();
}
