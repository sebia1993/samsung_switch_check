using System.Windows;
using System.Windows.Threading;
using SamsungSwitchWatch.Viewer.Models;
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
    private SingleInstanceCoordinator? _singleInstance;
    private ConnectionSettingsWindow? _connectionDialog;
    private DeviceManagementWindow? _deviceManagementDialog;
    private ManagedDeviceStore? _deviceStore;
    private ViewerMonitoringStore? _monitoringStore;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyAccessibilityTheme();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.TryAcquire())
        {
            try { SingleInstanceCoordinator.NotifyExistingAsync().GetAwaiter().GetResult(); } catch { }
            Shutdown();
            return;
        }
        _singleInstance.ActivationRequested += (_, _) => Dispatcher.BeginInvoke(ShowDashboard);

        _settingsStore = new ViewerSettingsStore();
        var settings = _settingsStore.Load();
        _deviceStore = new ManagedDeviceStore();
        _monitoringStore = new ViewerMonitoringStore();
        _viewModel = new DashboardViewModel(
            settings,
            _settingsStore,
            synchronizationContext: SynchronizationContext.Current,
            deviceStore: _deviceStore,
            monitoringStore: _monitoringStore);
        _mainWindow = new MainWindow(_viewModel);
        MainWindow = _mainWindow;
        _trayIcon = new TrayIconService(_viewModel, ShowDashboard, ShowMiniWindow, OpenConnectionSettings, ExitApplication);
        _alertService = new AlertPopupService(OpenAlert, _trayIcon);
        _viewModel.AlertRaised += OnAlertRaised;

        var needsConnection = _settingsStore.LastLoadStatus is ViewerSettingsLoadStatus.Missing
                or ViewerSettingsLoadStatus.NeedsConnection
                or ViewerSettingsLoadStatus.Corrupt
                           || (!settings.DemoMode && !ViewerSettingsSanitizer.IsValidForLiveConnection(settings, out _));
        if (StartupWindowPolicy.ShouldShowMainWindow(settings, needsConnection))
        {
            _mainWindow.Show();
        }
        _ = InitializeApplicationAsync();
        if (needsConnection)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ShowDashboard();
                OpenConnectionSettings();
            });
        }
    }

    private async Task InitializeApplicationAsync()
    {
        if (_viewModel is null) return;
        try { await _viewModel.InitializeAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch
        {
            // Initialization failures are represented by the ViewModel's Offline/Stale state.
        }
    }

    public void ShowDashboard()
    {
        if (_mainWindow is null) return;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ApplyAccessibilityTheme()
    {
        if (!SystemParameters.HighContrast) return;
        Resources["CanvasBrush"] = System.Windows.SystemColors.WindowBrush;
        Resources["SurfaceBrush"] = System.Windows.SystemColors.WindowBrush;
        Resources["TextBrush"] = System.Windows.SystemColors.WindowTextBrush;
        Resources["MutedTextBrush"] = System.Windows.SystemColors.GrayTextBrush;
        Resources["BorderBrush"] = System.Windows.SystemColors.WindowTextBrush;
        Resources["PrimaryBrush"] = System.Windows.SystemColors.HighlightBrush;
        Resources["PrimaryHoverBrush"] = System.Windows.SystemColors.HotTrackBrush;
    }

    private void OpenAlert(EventViewModel item)
    {
        ShowDashboard();
        if (_viewModel?.NavigateToEvent(item.NavigationEventId) == true) _mainWindow?.FocusSelectedEvent();
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

    public void OpenConnectionSettings()
    {
        if (_viewModel is null || _mainWindow is null || _settingsStore is null) return;
        if (_connectionDialog is { IsVisible: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var dialog = new ConnectionSettingsWindow(_viewModel.CurrentSettings, PersistAndSwitchClientAsync)
        {
            Owner = _mainWindow
        };
        _connectionDialog = dialog;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            if (ReferenceEquals(_connectionDialog, dialog)) _connectionDialog = null;
        }
    }

    public void OpenDeviceManagement()
    {
        if (_viewModel is null || _mainWindow is null) return;
        if (_deviceManagementDialog is { IsVisible: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var dialog = new DeviceManagementWindow(_viewModel) { Owner = _mainWindow };
        _deviceManagementDialog = dialog;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            if (ReferenceEquals(_deviceManagementDialog, dialog)) _deviceManagementDialog = null;
        }
    }

    private async Task PersistAndSwitchClientAsync(ViewerSettings settings, CancellationToken cancellationToken)
    {
        if (_settingsStore is null || _viewModel is null) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _viewModel.SwitchClientAsync(settings, linked.Token);
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

    public async void ExitApplication() => await ExitApplicationAsync();

    private async Task ExitApplicationAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _lifetime.Cancel();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
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

            if (_viewModel is not null) _viewModel.AlertRaised -= OnAlertRaised;
            _alertService?.Dispose();
            _alertService = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
            _miniWindow?.AllowClose();
            _miniWindow?.Close();
            _mainWindow?.AllowClose();
            _mainWindow?.Close();
            if (_viewModel is not null)
            {
                try { await _viewModel.DisposeAsync().AsTask().WaitAsync(deadline.Token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }
            if (_singleInstance is not null)
            {
                try { await _singleInstance.DisposeAsync().AsTask().WaitAsync(deadline.Token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }
        }
        finally
        {
            _lifetime.Dispose();
            Shutdown();
        }
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
        if (e.Exception is OperationCanceledException && _lifetime.IsCancellationRequested)
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        MessageBox.Show("복구할 수 없는 화면 오류가 발생해 프로그램을 안전하게 종료합니다.", "Samsung Switch Watch", MessageBoxButton.OK, MessageBoxImage.Error);
        Dispatcher.BeginInvoke(ExitApplication);
    }

    private void OnAlertRaised(object? sender, EventViewModel item) => _alertService?.Enqueue(item);
}

internal static class StartupWindowPolicy
{
    public static bool ShouldShowMainWindow(ViewerSettings settings, bool needsConnection) =>
        needsConnection || !settings.StartMinimizedToTray;
}
