using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;
using SamsungSwitchWatch.Viewer.Views;
using SamsungSwitchWatch.Viewer.Models;
using System.IO;
using System.Windows.Automation;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class WpfSmokeTests
{
    [Fact]
    public void MainWindow_CanBeConstructedWithApplicationResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-WpfSmoke", Guid.NewGuid().ToString("N"));
            try
            {
                var app = new App();
                app.InitializeComponent();
                var store = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
                var viewModel = new DashboardViewModel(new ViewerSettings
                {
                    DemoMode = false,
                    AgentUri = string.Empty
                }, store);
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                var window = new MainWindow(viewModel);
                window.Show();
                window.UpdateLayout();
                Assert.Equal(1280, window.MinWidth);
                Assert.Equal(720, window.MinHeight);
                Assert.True(window.IsVisible);
                Assert.Equal(System.Windows.Visibility.Visible, window.DevicesEmptyStateText.Visibility);
                Assert.Same(window.DevicesList, System.Windows.Input.FocusManager.GetFocusedElement(window));
                Assert.Equal("DisplayName", window.RegisteredCheckComboBox.DisplayMemberPath);
                Assert.Equal("Id", window.RegisteredCheckComboBox.SelectedValuePath);
                Assert.NotNull(window.RegisteredCheckComboBox.ItemContainerStyle);
                Assert.NotNull(window.EventFilterComboBox.ItemContainerStyle);
                Assert.Equal(System.Windows.Visibility.Visible, window.ReadOnlyQueryUnavailablePanel.Visibility);
                Assert.Equal("장비 명령 실행 결과", AutomationProperties.GetName(window.ReadOnlyQueryOutputTextBox));
                Assert.True(window.ReadOnlyQueryOutputTextBox.IsReadOnly);
                Assert.Equal(AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(window.ReadOnlyQueryStatusTextBlock));

                var now = DateTimeOffset.UtcNow;
                viewModel.ApplySnapshot(new AgentSnapshotDto(
                    now,
                    AgentConnectionState.Connected,
                    [new DeviceSnapshotDto("sw-demo", "ACCESS-SW-DEMO", "IES4224GP", "비공개",
                        DeviceHealth.Normal, now, "정상", "1일",
                        [new DeviceMetricDto("Telnet", "정상", DeviceHealth.Normal)])],
                    2,
                    "test",
                    "test",
                    "smoke-agent",
                    ApiVersion: 3,
                    ReadOnlyQueriesEnabled: true));
                viewModel.ApplyEvents(
                [
                    new SwitchEventDto(2, "change-event", "sw-demo", "ACCESS-SW-DEMO", now,
                        DeviceHealth.Warning, "상태 변경", "포트 상태 변경", "UP → DOWN"),
                    new SwitchEventDto(1, "log-event", "sw-demo", "ACCESS-SW-DEMO", now.AddSeconds(-1),
                        DeviceHealth.Warning, "새 로그", "시스템 로그", "새 로그 1건")
                ], raiseAlerts: false);
                window.UpdateLayout();

                Assert.False(window.DevicesEmptyStateText.IsVisible);
                Assert.Equal(System.Windows.Visibility.Visible, window.ReadOnlyQueryEnabledPanel.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.ReadOnlyQueryUnavailablePanel.Visibility);
                Assert.NotNull(window.SelectedDeviceLogsList.ItemTemplate);
                Assert.NotNull(window.SelectedDeviceChangesList.ItemTemplate);
                Assert.Single(window.SelectedDeviceLogsList.Items);
                Assert.Single(window.SelectedDeviceChangesList.Items);
                Assert.Equal("Name", AutomationNameBindingPath(window.DevicesList.ItemContainerStyle));
                Assert.Equal("AccessibilityName", AutomationNameBindingPath(window.RecentEventsList.ItemContainerStyle));
                Assert.Equal("AccessibilityName", AutomationNameBindingPath(window.SelectedDeviceLogsList.ItemContainerStyle));
                Assert.Equal("AccessibilityName", AutomationNameBindingPath(window.SelectedDeviceChangesList.ItemContainerStyle));
                Assert.Equal("Label", AutomationNameBindingPath(window.SelectedDeviceMetricsList.ItemContainerStyle));
                Assert.Equal("DisplayName", AutomationNameBindingPath(window.RegisteredCheckComboBox.ItemContainerStyle));
                Assert.Equal("Label", AutomationNameBindingPath(window.EventFilterComboBox.ItemContainerStyle));
                var connection = new ConnectionSettingsWindow(
                    new ViewerSettings { DemoMode = false, AgentUri = "http://monitor-pc:18443" },
                    (_, _) => Task.CompletedTask);
                connection.Show();
                connection.UpdateLayout();
                Assert.Equal("monitor-pc", connection.AgentAddressTextBox.Text);
                Assert.Equal("18443", connection.AgentPortTextBox.Text);
                Assert.Equal("사내 관리망 전용 · 암호화/인증 없음", connection.TransportWarningText.Text);
                Assert.Same(connection.AgentAddressTextBox, System.Windows.Input.FocusManager.GetFocusedElement(connection));
                connection.Close();
                var mini = new MiniWindow(viewModel, true);
                mini.Show();
                mini.UpdateLayout();
                Assert.True(mini.IsVisible);
                mini.AllowClose();
                mini.Close();
                var popup = new AlertPopup(new EventViewModel(new SwitchEventDto(
                    1, "smoke-event", "SW-DEMO", "ACCESS-SW-DEMO", DateTimeOffset.UtcNow,
                    DeviceHealth.Critical, "상태 변경", "업링크 Down", "UP → DOWN")));
                popup.Show();
                popup.UpdateLayout();
                Assert.True(popup.IsVisible);
                popup.Close();
                window.AllowClose();
                window.Close();
                viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                app.Shutdown();
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF smoke thread did not finish.");
        Assert.Null(failure);
    }

    private static string? AutomationNameBindingPath(System.Windows.Style? style)
    {
        Assert.NotNull(style);
        var setter = Assert.Single(style.Setters.OfType<System.Windows.Setter>(),
            item => item.Property == AutomationProperties.NameProperty);
        return Assert.IsType<System.Windows.Data.Binding>(setter.Value).Path.Path;
    }
}
