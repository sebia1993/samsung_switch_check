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
                var deviceStore = new ManagedDeviceStore(Path.Combine(folder, "devices.json"));
                var viewModel = new DashboardViewModel(new ViewerSettings
                {
                    DemoMode = false,
                    AgentUri = string.Empty
                }, store, deviceStore: deviceStore);
                viewModel.InitializeAsync().GetAwaiter().GetResult();
                var window = new MainWindow(viewModel);
                window.Show();
                window.UpdateLayout();
                Assert.Equal(1280, window.MinWidth);
                Assert.Equal(720, window.MinHeight);
                Assert.True(window.IsVisible);
                Assert.Equal(System.Windows.Visibility.Visible, window.DevicesEmptyStateText.Visibility);
                Assert.Same(window.DevicesList, System.Windows.Input.FocusManager.GetFocusedElement(window));
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
                Assert.Equal("Label", AutomationNameBindingPath(window.EventFilterComboBox.ItemContainerStyle));
                var connection = new ConnectionSettingsWindow(
                    new ViewerSettings { DemoMode = false, AgentUri = "https://monitor-pc:18443" },
                    (_, _) => Task.CompletedTask);
                connection.Show();
                connection.UpdateLayout();
                Assert.Equal("monitor-pc", connection.AgentAddressTextBox.Text);
                Assert.Equal("Agent 주소만 입력하세요", connection.TransportWarningText.Text);
                Assert.Same(connection.AgentAddressTextBox, System.Windows.Input.FocusManager.GetFocusedElement(connection));
                connection.Close();
                var devices = new DeviceManagementWindow(viewModel);
                devices.Show();
                devices.UpdateLayout();
                Assert.False(devices.MonitoringCheckBox.IsEnabled);
                Assert.Equal("Viewer 로컬 주기 감시",
                    AutomationProperties.GetName(devices.MonitoringCheckBox));
                devices.Close();
                VerifyDeviceManagementFailuresStayInsideWindow(folder);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF smoke thread did not finish.");
        Assert.Null(failure);
    }

    private static void VerifyDeviceManagementFailuresStayInsideWindow(string folder)
    {
        var persistence = new FaultingManagedDevicePersistence();
        var deviceStore = new ManagedDeviceStore(
            Path.Combine(folder, "fault-devices.json"),
            new TestSecretProtector(),
            persistence);
        var firstDraft = DeviceDraft("ACCESS-SW-01", "192.0.2.11");
        firstDraft.ConnectionVerified = true;
        firstDraft.LastConnectionTestUtc = DateTimeOffset.UtcNow;
        firstDraft.LastConnectionTestCode = "OK";
        var firstSaved = deviceStore.Save(firstDraft);
        _ = deviceStore.Save(DeviceDraft("ACCESS-SW-02", "192.0.2.12"));
        var monitoringPersistence = new FaultingMonitoringPersistence();
        var monitoringStore = new ViewerMonitoringStore(
            Path.Combine(folder, "fault-monitor.json"),
            monitoringPersistence);
        monitoringStore.RecordCapability(
            firstSaved.Id,
            new CollectorCapabilityDto(
                "interface_status",
                true,
                "Supported"));
        var diagnosticEntries = new List<(string Stage, string ErrorCode)>();
        var settingsStore =
            new ViewerSettingsStore(Path.Combine(folder, "fault-settings.json"));
        var viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            clientFactory: null,
            synchronizationContext: SynchronizationContext.Current,
            deviceStore,
            monitoringStore,
            new ViewerSettingsSaveCoordinator(settingsStore),
            (stage, errorCode) => diagnosticEntries.Add((stage, errorCode)),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));

        persistence.ReadException =
            new IOException("private path host=192.0.2.15 password=initial-secret");
        var initialFailureWindow = new DeviceManagementWindow(viewModel);
        initialFailureWindow.Show();
        initialFailureWindow.UpdateLayout();

        Assert.True(initialFailureWindow.IsVisible);
        Assert.Contains(
            "VIEWER_DEVICE_STORE_UNAVAILABLE",
            initialFailureWindow.ResultText.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            ("device-management-load", "VIEWER_DEVICE_STORE_UNAVAILABLE"),
            diagnosticEntries);
        Assert.DoesNotContain(
            "192.0.2.15",
            initialFailureWindow.ResultText.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "initial-secret",
            initialFailureWindow.ResultText.Text,
            StringComparison.Ordinal);
        initialFailureWindow.Close();

        persistence.ReadException = null;
        var window = new DeviceManagementWindow(viewModel);
        window.Show();
        window.UpdateLayout();

        var original = Assert.IsType<ManagedDeviceProfile>(window.DeviceList.SelectedItem);
        var originalName = window.DisplayNameTextBox.Text;
        var other = window.DeviceList.Items
            .OfType<ManagedDeviceProfile>()
            .Single(item => item.Id != original.Id);

        persistence.ReadException =
            new IOException("private path host=192.0.2.11 password=secret");
        Assert.Throws<IOException>(() => window.Reload());
        Assert.Equal(original.Id, Assert.IsType<ManagedDeviceProfile>(
            window.DeviceList.SelectedItem).Id);
        Assert.Equal(originalName, window.DisplayNameTextBox.Text);

        window.DeviceList.SelectedItem = other;
        window.UpdateLayout();

        Assert.True(window.IsVisible);
        Assert.Equal(original.Id, Assert.IsType<ManagedDeviceProfile>(
            window.DeviceList.SelectedItem).Id);
        Assert.Equal(originalName, window.DisplayNameTextBox.Text);
        Assert.Contains(
            "VIEWER_DEVICE_STORE_UNAVAILABLE",
            window.ResultText.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.11", window.ResultText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", window.ResultText.Text, StringComparison.Ordinal);

        window.DisplayNameTextBox.Text = "UNSAVED-READ-FAILURE";
        window.SaveButton.RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.UpdateLayout();

        Assert.True(window.IsVisible);
        Assert.Equal("UNSAVED-READ-FAILURE", window.DisplayNameTextBox.Text);
        Assert.Contains(
            "VIEWER_DEVICE_STORE_UNAVAILABLE",
            window.ResultText.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "VIEWER_DEVICE_STORE_WRITE_FAILED",
            window.ResultText.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            ("device-management-load", "VIEWER_DEVICE_STORE_UNAVAILABLE"),
            diagnosticEntries);

        persistence.ReadException = null;
        persistence.WriteException =
            new UnauthorizedAccessException("private path user=operator");
        window.DisplayNameTextBox.Text = "UNSAVED-LOCAL-NAME";
        window.SaveButton.RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.UpdateLayout();

        Assert.True(window.IsVisible);
        Assert.Equal("UNSAVED-LOCAL-NAME", window.DisplayNameTextBox.Text);
        Assert.Equal(original.Id, Assert.IsType<ManagedDeviceProfile>(
            window.DeviceList.SelectedItem).Id);
        Assert.Contains(
            "VIEWER_DEVICE_STORE_WRITE_FAILED",
            window.ResultText.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("operator", window.ResultText.Text, StringComparison.Ordinal);
        Assert.Contains(
            "VIEWER_DEVICE_STORE_WRITE_FAILED",
            viewModel.OperationMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            ("device-management-load", "VIEWER_DEVICE_STORE_UNAVAILABLE"),
            diagnosticEntries);
        Assert.Contains(
            ("device-management-save", "VIEWER_DEVICE_STORE_WRITE_FAILED"),
            diagnosticEntries);

        const string injectedInvalidData =
            "host=192.0.2.11 user=operator password=secret-invalid-data";
        persistence.WriteException = new InvalidDataException(injectedInvalidData);
        window.SaveButton.RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.UpdateLayout();

        Assert.True(window.IsVisible);
        Assert.Equal("UNSAVED-LOCAL-NAME", window.DisplayNameTextBox.Text);
        Assert.Contains(
            "VIEWER_UNEXPECTED_ERROR",
            window.ResultText.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "VIEWER_UNEXPECTED_ERROR",
            viewModel.OperationMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.11", window.ResultText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", window.ResultText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-invalid-data", window.ResultText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.11", viewModel.OperationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", viewModel.OperationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-invalid-data", viewModel.OperationMessage, StringComparison.Ordinal);
        Assert.Contains(
            ("device-management-save", "VIEWER_UNEXPECTED_ERROR"),
            diagnosticEntries);

        persistence.WriteException = null;
        monitoringPersistence.WriteException =
            new IOException("private monitor path host=192.0.2.11 password=monitor-secret");
        window.SaveButton.RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.UpdateLayout();

        Assert.True(window.IsVisible);
        Assert.Equal(original.Id, Assert.IsType<ManagedDeviceProfile>(
            window.DeviceList.SelectedItem).Id);
        Assert.Equal(
            "UNSAVED-LOCAL-NAME",
            Assert.Single(
                deviceStore.Load(),
                item => item.Id.Equals(original.Id, StringComparison.Ordinal)).DisplayName);
        Assert.Contains(
            "장비를 저장했습니다.",
            window.ResultText.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "VIEWER_MONITOR_STATE_WRITE_FAILED",
            window.ResultText.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            ("device-management-save", "VIEWER_MONITOR_STATE_WRITE_FAILED"),
            diagnosticEntries);
        Assert.DoesNotContain("192.0.2.11", window.ResultText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor-secret", window.ResultText.Text, StringComparison.Ordinal);
        Assert.Single(monitoringStore.LoadCapabilities(original.Id));

        monitoringPersistence.WriteException = null;
        persistence.WriteException =
            new UnauthorizedAccessException("private path user=operator");
        Assert.False(window.DeleteConfirmed(original));
        Assert.True(window.IsVisible);
        Assert.Equal("UNSAVED-LOCAL-NAME", window.DisplayNameTextBox.Text);
        Assert.Equal(original.Id, Assert.IsType<ManagedDeviceProfile>(
            window.DeviceList.SelectedItem).Id);
        Assert.Equal(2, deviceStore.Load().Count);
        Assert.Contains(
            ("device-management-delete", "VIEWER_DEVICE_STORE_WRITE_FAILED"),
            diagnosticEntries);
        Assert.DoesNotContain(
            diagnosticEntries,
            entry => entry.Stage.Contains("192.0.2.11", StringComparison.Ordinal)
                     || entry.Stage.Contains("secret", StringComparison.Ordinal)
                     || entry.ErrorCode.Contains("operator", StringComparison.Ordinal));

        persistence.WriteException = null;
        Assert.True(window.DeleteConfirmed(original));
        Assert.Single(deviceStore.Load());

        window.Close();
        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static ManagedDeviceDraft DeviceDraft(string name, string host) => new()
    {
        DisplayName = name,
        Model = "IES4224GP",
        Host = host,
        Username = "operator",
        Password = "test-password"
    };

    private sealed class TestSecretProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText));

        public string Unprotect(string protectedText) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
    }

    private sealed class FaultingManagedDevicePersistence : IManagedDevicePersistence
    {
        public string? Content { get; private set; }
        public Exception? ReadException { get; set; }
        public Exception? WriteException { get; set; }

        public string? ReadIfExists(string path)
        {
            if (ReadException is not null) throw ReadException;
            return Content;
        }

        public void WriteAtomically(string path, string content)
        {
            if (WriteException is not null) throw WriteException;
            Content = content;
        }

        public void Quarantine(string path, string destination) => Content = null;
    }

    private sealed class FaultingMonitoringPersistence : IViewerMonitoringPersistence
    {
        public string? Content { get; private set; }
        public Exception? WriteException { get; set; }

        public string? ReadIfExists(string path) => Content;

        public void WriteAtomically(string path, string content)
        {
            if (WriteException is not null) throw WriteException;
            Content = content;
        }

        public void Quarantine(string path, string destination) => Content = null;
    }

    private static string? AutomationNameBindingPath(System.Windows.Style? style)
    {
        Assert.NotNull(style);
        var setter = Assert.Single(style.Setters.OfType<System.Windows.Setter>(),
            item => item.Property == AutomationProperties.NameProperty);
        return Assert.IsType<System.Windows.Data.Binding>(setter.Value).Path.Path;
    }
}
