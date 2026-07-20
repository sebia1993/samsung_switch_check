using System.Windows;
using System.Windows.Threading;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;
using SamsungSwitchWatch.Viewer.Views;

namespace SamsungSwitchWatch.Viewer;

public partial class App : Application
{
    private ViewerSettingsStore? _settingsStore;
    private DashboardViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private MiniWindow? _miniWindow;
    private TrayIconService? _trayIcon;
    private AlertPopupService? _alertService;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _settingsStore = new ViewerSettingsStore();
        var settings = _settingsStore.Load();
        _viewModel = new DashboardViewModel(settings, _settingsStore, synchronizationContext: SynchronizationContext.Current);
        _mainWindow = new MainWindow(_viewModel);
        MainWindow = _mainWindow;
        _alertService = new AlertPopupService();
        _viewModel.AlertRaised += (_, item) => _alertService.Enqueue(item);
        _trayIcon = new TrayIconService(_viewModel, ShowDashboard, ShowMiniWindow, OpenConnectionSettings, ExitApplication);

        if (!settings.StartMinimizedToTray)
        {
            _mainWindow.Show();
        }
        _ = _viewModel.InitializeAsync();
    }

    public void ShowDashboard()
    {
        if (_mainWindow is null) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ShowMiniWindow()
    {
        if (_viewModel is null) return;
        var settings = _viewModel.CurrentSettings;
        _miniWindow ??= new MiniWindow(_viewModel, settings.MiniTopmost);
        RestoreMiniWindowBounds(_miniWindow, settings);
        if (!_miniWindow.IsVisible) _miniWindow.Show();
        _miniWindow.Activate();
    }

    public async void OpenConnectionSettings()
    {
        if (_viewModel is null || _mainWindow is null) return;
        var dialog = new ConnectionSettingsWindow(_viewModel.CurrentSettings) { Owner = _mainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        try
        {
            await _viewModel.SwitchClientAsync(dialog.Result);
        }
        catch
        {
            MessageBox.Show(_mainWindow, "연결 설정을 적용하지 못했습니다. 주소, 인증서 지문, 토큰을 확인하세요.", "Samsung Switch Watch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void RestoreMainWindowBounds(MainWindow window)
    {
        if (_viewModel is null) return;
        var settings = _viewModel.CurrentSettings;
        window.Width = settings.MainWidth;
        window.Height = settings.MainHeight;
        if (IsVisibleCoordinate(settings.MainLeft, settings.MainTop, 120, 80))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = settings.MainLeft;
            window.Top = settings.MainTop;
        }
    }

    public void SaveMiniWindowBounds(MiniWindow window)
    {
        if (_viewModel is null || _settingsStore is null) return;
        var settings = _viewModel.CurrentSettings;
        settings.MiniLeft = window.Left;
        settings.MiniTop = window.Top;
        settings.MiniTopmost = window.Topmost;
        TrySaveSettings(settings);
    }

    public void SetMiniTopmost(bool value)
    {
        if (_viewModel is null || _settingsStore is null) return;
        _viewModel.CurrentSettings.MiniTopmost = value;
        TrySaveSettings(_viewModel.CurrentSettings);
    }

    public void ShowTrayHint() => _trayIcon?.ShowCloseToTrayHint();

    public async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        if (_viewModel is not null && _settingsStore is not null)
        {
            var settings = _viewModel.CurrentSettings;
            if (_mainWindow is not null && _mainWindow.WindowState == WindowState.Normal)
            {
                settings.MainLeft = _mainWindow.Left;
                settings.MainTop = _mainWindow.Top;
                settings.MainWidth = _mainWindow.Width;
                settings.MainHeight = _mainWindow.Height;
            }
            if (_miniWindow is not null)
            {
                settings.MiniLeft = _miniWindow.Left;
                settings.MiniTop = _miniWindow.Top;
                settings.MiniTopmost = _miniWindow.Topmost;
            }
            TrySaveSettings(settings);
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _miniWindow?.AllowClose();
        _miniWindow?.Close();
        _mainWindow?.AllowClose();
        _mainWindow?.Close();
        if (_viewModel is not null) await _viewModel.DisposeAsync();
        Shutdown();
    }

    private void RestoreMiniWindowBounds(MiniWindow window, ViewerSettings settings)
    {
        if (IsVisibleCoordinate(settings.MiniLeft, settings.MiniTop, 80, 44))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = settings.MiniLeft;
            window.Top = settings.MiniTop;
            return;
        }
        var work = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = work.Right - window.Width - 20;
        window.Top = work.Top + 20;
    }

    private static bool IsVisibleCoordinate(double left, double top, double visibleWidth, double visibleHeight)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top)) return false;
        return left + visibleWidth >= SystemParameters.VirtualScreenLeft
               && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
               && top + visibleHeight >= SystemParameters.VirtualScreenTop
               && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    }

    private void TrySaveSettings(ViewerSettings settings)
    {
        try { _settingsStore?.Save(settings); } catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show("화면 처리 중 오류가 발생했습니다. 모니터링 상태를 새로 고침해 주세요.", "Samsung Switch Watch", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
