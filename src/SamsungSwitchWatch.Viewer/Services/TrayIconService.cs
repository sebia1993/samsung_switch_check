using System.ComponentModel;
using System.Drawing;
using System.Windows.Input;
using SamsungSwitchWatch.Viewer.ViewModels;
using Forms = System.Windows.Forms;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly DashboardViewModel _viewModel;
    private bool _hintShown;

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
        menu.Items.Add("지금 전체 점검", null, (_, _) => Execute(viewModel.RefreshCommand));
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
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateIcon();
    }

    public void ShowCloseToTrayHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(2500, "Samsung Switch Watch", "모니터링은 계속됩니다. 완전히 종료하려면 트레이 메뉴의 '프로그램 종료'를 선택하세요.", Forms.ToolTipIcon.Info);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DashboardViewModel.CriticalCount) or nameof(DashboardViewModel.WarningCount)
            or nameof(DashboardViewModel.DisconnectedCount) or nameof(DashboardViewModel.ConnectionState))
        {
            UpdateIcon();
        }
    }

    private void UpdateIcon()
    {
        _icon.Icon = _viewModel.CriticalCount > 0 ? SystemIcons.Error
            : _viewModel.WarningCount > 0 || _viewModel.DisconnectedCount > 0 ? SystemIcons.Warning
            : SystemIcons.Information;
        _icon.Text = _viewModel.CriticalCount > 0
            ? $"Switch Watch · 장애 {_viewModel.CriticalCount}"
            : _viewModel.WarningCount > 0 ? $"Switch Watch · 경고 {_viewModel.WarningCount}" : "Switch Watch · 정상";
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
