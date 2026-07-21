using System.ComponentModel;
using System.Drawing;
using System.Windows.Input;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.ViewModels;
using Forms = System.Windows.Forms;

namespace SamsungSwitchWatch.Viewer.Services;

public enum TrayIndicator
{
    Normal,
    Warning,
    Critical,
    Offline,
    NeedsConnection
}

public sealed record TrayStatus(TrayIndicator Indicator, string Text);

public static class TrayStatusProjector
{
    public static TrayStatus Create(
        AgentConnectionState connectionState,
        int criticalCount,
        int warningCount,
        int disconnectedCount,
        DateTimeOffset? lastSuccessfulReceipt)
    {
        var receipt = lastSuccessfulReceipt is null
            ? "수신 기록 없음"
            : $"수신 {lastSuccessfulReceipt.Value.LocalDateTime:MM-dd HH:mm}";
        var status = connectionState switch
        {
            AgentConnectionState.NeedsConnection => new TrayStatus(TrayIndicator.NeedsConnection, "연결 설정 필요"),
            AgentConnectionState.Offline => new TrayStatus(TrayIndicator.Offline, "Agent 오프라인"),
            AgentConnectionState.Reconnecting => new TrayStatus(TrayIndicator.Warning, "실시간 재연결 중"),
            AgentConnectionState.Stale => new TrayStatus(TrayIndicator.Warning, "현재 상태 미확인"),
            AgentConnectionState.Connecting => new TrayStatus(TrayIndicator.Warning, "Agent 연결 중"),
            _ when criticalCount > 0 => new TrayStatus(TrayIndicator.Critical, $"장애 {criticalCount}"),
            _ when warningCount > 0 || disconnectedCount > 0 =>
                new TrayStatus(TrayIndicator.Warning, $"경고 {warningCount + disconnectedCount}"),
            _ => new TrayStatus(TrayIndicator.Normal, "정상")
        };
        var text = $"Switch Watch · {status.Text} · {receipt}";
        return status with { Text = text.Length <= 63 ? text : text[..63] };
    }
}

public sealed class TrayIconService : IDisposable, IWindowsToastBackend
{
    private readonly Forms.NotifyIcon _icon;
    private readonly DashboardViewModel _viewModel;
    private bool _hintShown;
    private EventViewModel? _notificationItem;
    private Action<EventViewModel>? _notificationActivated;
    private bool _disposed;

    public TrayIconService(
        DashboardViewModel viewModel,
        Action showDashboard,
        Action showMiniWindow,
        Action openSettings,
        Action exit)
    {
        _viewModel = viewModel;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("대시보드 열기", null, (_, _) => showDashboard());
        menu.Items.Add("미니 창 표시", null, (_, _) => showMiniWindow());
        menu.Items.Add("최신 결과 새로 고침", null, (_, _) => Execute(viewModel.RefreshCommand));
        menu.Items.Add("연결 설정", null, (_, _) => openSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("프로그램 종료", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Text = "Samsung Switch Watch",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => showDashboard();
        _icon.BalloonTipClicked += (_, _) =>
        {
            var item = _notificationItem;
            var action = _notificationActivated;
            if (item is not null && action is not null) action(item);
        };
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateIcon();
    }

    public void ShowCloseToTrayHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(2500, "Samsung Switch Watch", "모니터링은 계속됩니다. 완전히 종료하려면 트레이 메뉴의 '프로그램 종료'를 선택하세요.", Forms.ToolTipIcon.Info);
    }

    public bool TryShow(EventViewModel item, Action<EventViewModel> activated)
    {
        if (_disposed || !_icon.Visible) return false;
        _notificationItem = item;
        _notificationActivated = activated;
        var title = item.Recovered
            ? "장애 복구"
            : item.Kind.Contains("동기화", StringComparison.Ordinal)
                ? item.Title
                : item.Title;
        var body = DiagnosticTextSanitizer.Clean($"{item.DeviceName} · {item.AlertDetail}");
        _icon.ShowBalloonTip(
            7000,
            title.Length <= 63 ? title : title[..63],
            body.Length <= 255 ? body : body[..255],
            item.Recovered ? Forms.ToolTipIcon.Info
                : item.Severity is DeviceHealth.Critical or DeviceHealth.Disconnected
                    ? Forms.ToolTipIcon.Error
                    : Forms.ToolTipIcon.Warning);
        return true;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.CriticalCount) or nameof(DashboardViewModel.WarningCount)
            or nameof(DashboardViewModel.DisconnectedCount) or nameof(DashboardViewModel.ConnectionState)
            or nameof(DashboardViewModel.LastSuccessfulReceiptAt))
        {
            UpdateIcon();
        }
    }

    private void UpdateIcon()
    {
        var status = TrayStatusProjector.Create(
            _viewModel.ConnectionState,
            _viewModel.CriticalCount,
            _viewModel.WarningCount,
            _viewModel.DisconnectedCount,
            _viewModel.LastSuccessfulReceiptAt);
        _icon.Icon = status.Indicator switch
        {
            TrayIndicator.Critical or TrayIndicator.Offline => SystemIcons.Error,
            TrayIndicator.Warning or TrayIndicator.NeedsConnection => SystemIcons.Warning,
            _ => SystemIcons.Information
        };
        _icon.Text = status.Text;
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
