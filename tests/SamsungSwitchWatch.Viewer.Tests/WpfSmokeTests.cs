using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;
using SamsungSwitchWatch.Viewer.Views;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Support;
using System.IO;
using System.Windows;
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
                Assert.Equal(
                    ViewerInstallSmokeCheck.SuccessExitCode,
                    ViewerInstallSmokeCheck.Run(app.Resources));
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
                    (_, _) => Task.CompletedTask,
                    new NeverCalledAgentConnectionProbe());
                connection.Show();
                connection.UpdateLayout();
                Assert.Equal("monitor-pc", connection.AgentAddressTextBox.Text);
                Assert.Equal("API가 호환되면 연결합니다", connection.TransportWarningText.Text);
                Assert.Equal(System.Windows.Visibility.Collapsed, connection.ConnectionProgressPanel.Visibility);
                Assert.Equal(
                    System.Windows.Visibility.Collapsed,
                    connection.DiagnosticSaveButton.Visibility);
                AssertHiddenSupportCode(connection);
                Assert.True(connection.SaveButton.IsVisible);
                Assert.Contains("TCP/18443", connection.TcpProbeText.Text, StringComparison.Ordinal);
                Assert.Equal("Viewer 실행 시 트레이로 최소화",
                    connection.StartMinimizedCheckBox.Content);
                Assert.Same(connection.AgentAddressTextBox, System.Windows.Input.FocusManager.GetFocusedElement(connection));
                connection.Close();
                var legacyLoopbackConnection = new ConnectionSettingsWindow(
                    new ViewerSettings { DemoMode = false, AgentUri = "https://localhost:18443" },
                    (_, _) => Task.CompletedTask,
                    new NeverCalledAgentConnectionProbe());
                legacyLoopbackConnection.Show();
                legacyLoopbackConnection.UpdateLayout();
                Assert.Empty(legacyLoopbackConnection.ValidationText.Text);
                Assert.Equal("localhost", legacyLoopbackConnection.AgentAddressTextBox.Text);
                Assert.Equal(
                    System.Windows.Visibility.Collapsed,
                    legacyLoopbackConnection.ConnectionProgressPanel.Visibility);
                legacyLoopbackConnection.Close();
                var appliedCount = 0;
                var successfulConnection = new ConnectionSettingsWindow(
                    new ViewerSettings
                    {
                        DemoMode = false,
                        AgentUri = "https://monitor-pc:18443"
                    },
                    (_, _) =>
                    {
                        appliedCount++;
                        return Task.CompletedTask;
                    },
                    new SuccessfulAgentConnectionProbe());
                successfulConnection.Show();
                successfulConnection.SaveButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                successfulConnection.UpdateLayout();

                Assert.True(successfulConnection.IsVisible);
                Assert.Equal(1, appliedCount);
                Assert.NotNull(successfulConnection.Result);
                Assert.Equal(
                    System.Windows.Visibility.Visible,
                    successfulConnection.DiagnosticSaveButton.Visibility);
                AssertHiddenSupportCode(successfulConnection);
                Assert.True(successfulConnection.DiagnosticSaveButton.IsEnabled);
                Assert.False(successfulConnection.SaveButton.IsEnabled);
                Assert.Equal("저장 완료", successfulConnection.SaveButton.Content);
                Assert.Equal("닫기", successfulConnection.CancelButton.Content);
                successfulConnection.Close();
                var versionWarningConnection = new ConnectionSettingsWindow(
                    new ViewerSettings
                    {
                        DemoMode = false,
                        AgentUri = "https://monitor-pc:18443"
                    },
                    (_, _) => Task.CompletedTask,
                    new VersionWarningAgentConnectionProbe());
                versionWarningConnection.Show();
                versionWarningConnection.SaveButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                versionWarningConnection.UpdateLayout();

                Assert.NotNull(versionWarningConnection.Result);
                Assert.Contains("버전이 다르지만 API v4", versionWarningConnection.ValidationText.Text,
                    StringComparison.Ordinal);
                Assert.False(versionWarningConnection.SaveButton.IsEnabled);
                versionWarningConnection.Close();
                var settingsSaveFailureConnection = new ConnectionSettingsWindow(
                    new ViewerSettings
                    {
                        DemoMode = false,
                        AgentUri = "https://monitor-pc:18443"
                    },
                    (_, _) => Task.FromException(new AgentClientException(
                        "VIEWER_SETTINGS_WRITE_FAILED",
                        AgentConnectionState.Stale)),
                    new SuccessfulAgentConnectionProbe());
                settingsSaveFailureConnection.Show();
                settingsSaveFailureConnection.SaveButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                settingsSaveFailureConnection.UpdateLayout();

                var settingsDiagnostic = Assert.IsType<ViewerFieldDiagnosticSnapshot>(
                    settingsSaveFailureConnection.FieldDiagnosticSnapshot);
                Assert.Equal("FAILED", settingsDiagnostic.Result);
                Assert.Equal("NORMAL", settingsDiagnostic.Mode);
                Assert.Equal("SETTINGS", settingsDiagnostic.FailedStage);
                Assert.Equal("VIEWER_SETTINGS_WRITE_FAILED", settingsDiagnostic.ErrorCode);
                Assert.Equal("CHECK_VIEWER_STORAGE", settingsDiagnostic.RecommendedActionCode);
                Assert.Equal(
                    [11L, 12L, 13L, 14L, 15L],
                    settingsDiagnostic.Stages.Select(item => item.DurationMs));
                Assert.Contains(
                    "VIEWER_SETTINGS_WRITE_FAILED",
                    settingsSaveFailureConnection.ValidationText.Text,
                    StringComparison.Ordinal);
                AssertVisibleSupportCode(settingsSaveFailureConnection);
                Assert.True(settingsSaveFailureConnection.SaveButton.IsEnabled);
                settingsSaveFailureConnection.Close();
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
        thread.IsBackground = true;
        thread.Name = "SamsungSwitchWatch-WpfSmoke";
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var timeout = TimeSpan.FromSeconds(30);
        Assert.True(
            thread.Join(timeout),
            $"WPF smoke thread did not finish within {timeout.TotalSeconds:0} seconds. "
            + $"Thread state: {thread.ThreadState}.");
        Assert.Null(failure);
    }

    [Fact]
    public void InstallSmokeCheck_UsesOnlyExactSingleArgument()
    {
        Assert.True(ViewerInstallSmokeCheck.IsRequested(["--install-smoke-check"]));

        Assert.False(ViewerInstallSmokeCheck.IsRequested([]));
        Assert.False(ViewerInstallSmokeCheck.IsRequested(["--INSTALL-SMOKE-CHECK"]));
        Assert.False(ViewerInstallSmokeCheck.IsRequested(["--install-smoke-check", "--extra"]));
        Assert.False(ViewerInstallSmokeCheck.IsRequested(["--install-smoke-check=true"]));
    }

    [Fact]
    public void InstallSmokeCheck_CoreLogicUsesOnlyInMemoryResources()
    {
        var resources = CreateInstallSmokeResources();
        var openedResources = new List<Uri>();

        var exitCode = ViewerInstallSmokeCheck.Run(resources, uri =>
        {
            openedResources.Add(uri);
            return new MemoryStream([1]);
        });

        Assert.Equal(ViewerInstallSmokeCheck.SuccessExitCode, exitCode);
        Assert.Equal(5, openedResources.Count);
        Assert.All(openedResources, uri =>
            Assert.StartsWith("/SamsungSwitchWatch.Viewer;component/", uri.OriginalString));
    }

    [Fact]
    public void InstallSmokeCheck_FailuresReturnStableNonzeroExitCodes()
    {
        var missingApplicationResource = CreateInstallSmokeResources();
        missingApplicationResource.Remove("PrimaryButton");

        Assert.Equal(
            ViewerInstallSmokeCheck.ApplicationResourceFailureExitCode,
            ViewerInstallSmokeCheck.Run(
                missingApplicationResource,
                _ => new MemoryStream([1])));
        Assert.Equal(
            ViewerInstallSmokeCheck.ScreenResourceFailureExitCode,
            ViewerInstallSmokeCheck.Run(
                CreateInstallSmokeResources(),
                _ => null));
        Assert.Equal(
            ViewerInstallSmokeCheck.UnexpectedFailureExitCode,
            ViewerInstallSmokeCheck.Run(
                CreateInstallSmokeResources(),
                _ => throw new InvalidOperationException("synthetic")));
    }

    private static ResourceDictionary CreateInstallSmokeResources() =>
        new()
        {
            ["HealthBrush"] = new Infrastructure.HealthToBrushConverter(),
            ["HealthText"] = new Infrastructure.HealthToTextConverter(),
            ["BoolOpacity"] = new Infrastructure.BoolToOpacityConverter(),
            ["BoolVisibility"] = new System.Windows.Controls.BooleanToVisibilityConverter(),
            ["CanvasBrush"] = new System.Windows.Media.SolidColorBrush(),
            ["SurfaceBrush"] = new System.Windows.Media.SolidColorBrush(),
            ["TextBrush"] = new System.Windows.Media.SolidColorBrush(),
            ["MutedTextBrush"] = new System.Windows.Media.SolidColorBrush(),
            ["BorderBrush"] = new System.Windows.Media.SolidColorBrush(),
            ["PrimaryBrush"] = new System.Windows.Media.SolidColorBrush(),
            ["PrimaryHoverBrush"] = new System.Windows.Media.SolidColorBrush(),
            ["EmptyStateText"] = new Style(typeof(System.Windows.Controls.TextBlock)),
            ["CardStyle"] = new Style(typeof(System.Windows.Controls.Border)),
            ["PrimaryButton"] = new Style(typeof(System.Windows.Controls.Button)),
            ["SecondaryButton"] = new Style(typeof(System.Windows.Controls.Button)),
            [typeof(Window)] = new Style(typeof(Window)),
            [typeof(System.Windows.Controls.TextBlock)] =
                new Style(typeof(System.Windows.Controls.TextBlock))
        };

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
        var agentFactory = new CountingAgentClientFactory();
        var viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            agentFactory,
            synchronizationContext: SynchronizationContext.Current,
            deviceStore,
            monitoringStore,
            new ViewerSettingsSaveCoordinator(settingsStore),
            (stage, errorCode) => diagnosticEntries.Add((stage, errorCode)),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        viewModel.InitializeAsync().GetAwaiter().GetResult();

        const string futureStoreContent =
            """{"SchemaVersion":2,"Devices":{"Host":"192.0.2.99","Secret":"future-secret"}}""";
        var futurePersistence = new FaultingManagedDevicePersistence();
        futurePersistence.Seed(futureStoreContent);
        var futureStore = new ManagedDeviceStore(
            Path.Combine(folder, "future-devices.json"),
            new TestSecretProtector(),
            futurePersistence);
        var futureViewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            clientFactory: null,
            synchronizationContext: SynchronizationContext.Current,
            futureStore,
            monitoringStore,
            new ViewerSettingsSaveCoordinator(settingsStore),
            (stage, errorCode) => diagnosticEntries.Add((stage, errorCode)),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        var futureWindow = new DeviceManagementWindow(futureViewModel);
        futureWindow.Show();
        futureWindow.UpdateLayout();

        Assert.True(futureWindow.IsVisible);
        Assert.Empty(futureWindow.DeviceList.Items);
        Assert.Contains(
            "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED",
            futureWindow.ResultText.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED",
            futureViewModel.OperationMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            ("device-management-load", "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED"),
            diagnosticEntries);
        Assert.DoesNotContain(
            "VIEWER_DEVICE_STORE_CORRUPT",
            futureWindow.ResultText.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "192.0.2.99",
            futureWindow.ResultText.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "future-secret",
            futureWindow.ResultText.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "192.0.2.99",
            futureViewModel.OperationMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "future-secret",
            futureViewModel.OperationMessage,
            StringComparison.Ordinal);
        Assert.Equal(futureStoreContent, futurePersistence.Content);
        futureWindow.Close();
        futureViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();

        var initialFailurePersistence = new FaultingManagedDevicePersistence
        {
            ReadException =
                new IOException("private path host=192.0.2.15 password=initial-secret")
        };
        var initialFailureStore = new ManagedDeviceStore(
            Path.Combine(folder, "initial-fault-devices.json"),
            new TestSecretProtector(),
            initialFailurePersistence);
        var initialFailureViewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            clientFactory: null,
            synchronizationContext: SynchronizationContext.Current,
            initialFailureStore,
            monitoringStore,
            new ViewerSettingsSaveCoordinator(settingsStore),
            (stage, errorCode) => diagnosticEntries.Add((stage, errorCode)),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        var initialFailureWindow = new DeviceManagementWindow(initialFailureViewModel);
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
        initialFailureViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();

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

        window.Close();
        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        persistence.ReadException = null;
        deviceStore = new ManagedDeviceStore(
            Path.Combine(folder, "fault-devices.json"),
            new TestSecretProtector(),
            persistence);
        viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            agentFactory,
            synchronizationContext: SynchronizationContext.Current,
            deviceStore,
            monitoringStore,
            new ViewerSettingsSaveCoordinator(settingsStore),
            (stage, errorCode) => diagnosticEntries.Add((stage, errorCode)),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        viewModel.InitializeAsync().GetAwaiter().GetResult();
        window = new DeviceManagementWindow(viewModel);
        window.Show();
        window.UpdateLayout();
        original = Assert.IsType<ManagedDeviceProfile>(window.DeviceList.SelectedItem);
        originalName = window.DisplayNameTextBox.Text;

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
        Assert.NotEmpty(viewModel.Devices);
        Assert.All(viewModel.Devices, item =>
        {
            Assert.Equal(DeviceHealth.Warning, item.Health);
            Assert.Equal("DeviceStoreUnavailable", item.CollectionState);
            Assert.Equal("VIEWER_DEVICE_STORE_UNAVAILABLE", item.CollectionErrorCode);
        });
        Assert.Equal(0, viewModel.NormalCount);
        Assert.Equal(0, viewModel.MonitoredCount);
        Assert.False(viewModel.ReadOnlyQueriesEnabled);
        Assert.False(viewModel.ManualCheckCommand.CanExecute(null));
        Assert.False(viewModel.ExecuteReadOnlyQueryCommand.CanExecute(null));

        var testRequestsBeforeStickyStoreTest = agentFactory.TelnetTestRequestCount;
        window.TestButton.RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        window.UpdateLayout();

        Assert.Equal(
            testRequestsBeforeStickyStoreTest,
            agentFactory.TelnetTestRequestCount);
        Assert.Contains(
            "VIEWER_DEVICE_STORE_UNAVAILABLE",
            window.ResultText.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            ("device-management-load", "VIEWER_DEVICE_STORE_UNAVAILABLE"),
            diagnosticEntries);
        Assert.DoesNotContain("operator", window.ResultText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("test-password", window.ResultText.Text, StringComparison.Ordinal);

        window.Close();
        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        persistence.WriteException = null;
        deviceStore = new ManagedDeviceStore(
            Path.Combine(folder, "fault-devices.json"),
            new TestSecretProtector(),
            persistence);
        viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            clientFactory: null,
            synchronizationContext: SynchronizationContext.Current,
            deviceStore,
            monitoringStore,
            new ViewerSettingsSaveCoordinator(settingsStore),
            (stage, errorCode) => diagnosticEntries.Add((stage, errorCode)),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        window = new DeviceManagementWindow(viewModel);
        window.Show();
        window.UpdateLayout();
        original = Assert.IsType<ManagedDeviceProfile>(window.DeviceList.SelectedItem);
        window.DisplayNameTextBox.Text = "UNSAVED-LOCAL-NAME";

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
        var preservedStore = new ManagedDeviceStore(
            Path.Combine(folder, "fault-devices.json"),
            new TestSecretProtector(),
            persistence);
        Assert.Equal(2, preservedStore.Load().Count);
        Assert.Contains(
            ("device-management-delete", "VIEWER_DEVICE_STORE_WRITE_FAILED"),
            diagnosticEntries);
        Assert.DoesNotContain(
            diagnosticEntries,
            entry => entry.Stage.Contains("192.0.2.11", StringComparison.Ordinal)
                     || entry.Stage.Contains("secret", StringComparison.Ordinal)
                     || entry.ErrorCode.Contains("operator", StringComparison.Ordinal));

        window.Close();
        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        persistence.WriteException = null;
        deviceStore = new ManagedDeviceStore(
            Path.Combine(folder, "fault-devices.json"),
            new TestSecretProtector(),
            persistence);
        viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            clientFactory: null,
            synchronizationContext: SynchronizationContext.Current,
            deviceStore,
            monitoringStore,
            new ViewerSettingsSaveCoordinator(settingsStore),
            (stage, errorCode) => diagnosticEntries.Add((stage, errorCode)),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        window = new DeviceManagementWindow(viewModel);
        window.Show();
        window.UpdateLayout();
        original = window.DeviceList.Items
            .OfType<ManagedDeviceProfile>()
            .Single(item => item.Id == original.Id);
        window.DeviceList.SelectedItem = original;
        window.UpdateLayout();

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

        public void Seed(string content) => Content = content;

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

    private sealed class CountingAgentClientFactory : IAgentClientFactory
    {
        private int _telnetTestRequestCount;

        public int TelnetTestRequestCount => Volatile.Read(ref _telnetTestRequestCount);

        public IAgentClient Create(ViewerSettings settings) =>
            new CountingAgentClient(this);

        private sealed class CountingAgentClient(CountingAgentClientFactory owner) : IAgentClient
        {
            public bool SupportsStatelessV4 => true;
            public event EventHandler<AgentEventChangeDto>? EventChanged
            {
                add { }
                remove { }
            }

            public event EventHandler<AgentConnectionState>? ConnectionStateChanged
            {
                add { }
                remove { }
            }

            public Task StartAsync(CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task<AgentIdentityDto> GetIdentityAsync(
                CancellationToken cancellationToken) =>
                Task.FromResult(new AgentIdentityDto(
                    4,
                    "wpf-smoke-agent",
                    "wpf-smoke-instance",
                    new string('A', 64),
                    "https",
                    8,
                    65_536));

            public Task<TelnetExecutionResultDto> TestTelnetAsync(
                TelnetTargetDto target,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._telnetTestRequestCount);
                var now = DateTimeOffset.UtcNow;
                return Task.FromResult(new TelnetExecutionResultDto(
                    4,
                    target.RequestId,
                    true,
                    "user",
                    ">",
                    now,
                    now,
                    0,
                    []));
            }

            public Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
                TelnetExecuteRequestDto request,
                CancellationToken cancellationToken) =>
                Task.FromException<TelnetExecutionResultDto>(
                    new NotSupportedException());

            public Task<AgentSnapshotDto> GetSnapshotAsync(
                CancellationToken cancellationToken) =>
                Task.FromException<AgentSnapshotDto>(
                    new NotSupportedException());

            public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(
                int limit,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);

            public Task<EventChangePageDto> GetEventChangesAsync(
                long cursor,
                int limit,
                CancellationToken cancellationToken) =>
                Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));

            public Task<CommandResultDto> ExecuteRegisteredCheckAsync(
                string deviceId,
                string commandId,
                CancellationToken cancellationToken) =>
                Task.FromException<CommandResultDto>(
                    new NotSupportedException());

            public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
                string deviceId,
                string command,
                CancellationToken cancellationToken) =>
                Task.FromException<ReadOnlyQueryResultDto>(
                    new NotSupportedException());

            public Task<bool> AcknowledgeAsync(
                string eventId,
                CancellationToken cancellationToken) =>
                Task.FromResult(false);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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

    private static void AssertVisibleSupportCode(
        ConnectionSettingsWindow window)
    {
        Assert.Equal(
            System.Windows.Visibility.Visible,
            window.SupportCodePanel.Visibility);
        Assert.Equal(24, window.SupportCodeTextBox.Text.Length);
        Assert.True(
            Swd1SupportCode.TryDecode(
                window.SupportCodeTextBox.Text,
                out var decoded));
        Assert.Equal(Swd1Component.Viewer, decoded!.Common.Component);
    }

    private static void AssertHiddenSupportCode(
        ConnectionSettingsWindow window)
    {
        Assert.Equal(
            System.Windows.Visibility.Collapsed,
            window.SupportCodePanel.Visibility);
        Assert.Empty(window.SupportCodeTextBox.Text);
    }

    private sealed class CountingLocalAgentPreflight : ILocalAgentPreflight
    {
        public int CallCount { get; private set; }

        public Task<LocalAgentPreflightResult> RunAsync(
            ViewerSettings baseSettings,
            IProgress<LocalAgentPreflightUpdate>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The smoke test must not start local preflight automatically.");
        }
    }

    private sealed class NeverCalledAgentConnectionProbe : IAgentConnectionProbe
    {
        public Task<AgentConnectionProbeResult> ProbeAsync(
            ViewerSettings settings,
            IProgress<AgentConnectionProbeUpdate>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The smoke test must not probe automatically.");
    }

    private sealed class IdentityMismatchAgentConnectionProbe : IAgentConnectionProbe
    {
        public Task<AgentConnectionProbeResult> ProbeAsync(
            ViewerSettings settings,
            IProgress<AgentConnectionProbeUpdate>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AgentConnectionProbeResult.Failure(
                AgentConnectionProbeStage.Https,
                "AGENT_IDENTITY_CHANGED",
                ViewerConnectionMessages.ForCode("AGENT_IDENTITY_CHANGED")));
        }
    }

    private sealed class SuccessfulAgentConnectionProbe : IAgentConnectionProbe
    {
        public Task<AgentConnectionProbeResult> ProbeAsync(
            ViewerSettings settings,
            IProgress<AgentConnectionProbeUpdate>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SuccessfulProbeResult());
        }
    }

    private sealed class VersionWarningAgentConnectionProbe : IAgentConnectionProbe
    {
        public Task<AgentConnectionProbeResult> ProbeAsync(
            ViewerSettings settings,
            IProgress<AgentConnectionProbeUpdate>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AgentConnectionProbeResult.Success(
                Identity() with { ProductVersion = "0.10.0-poc" },
                "경고 · Agent 0.10.0-poc와 Viewer 0.11.0-poc 버전이 다르지만 API v4가 호환되어 연결합니다."));
        }
    }

    private sealed class SuccessfulLocalAgentPreflight : ILocalAgentPreflight
    {
        public Task<LocalAgentPreflightResult> RunAsync(
            ViewerSettings baseSettings,
            IProgress<LocalAgentPreflightUpdate>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ViewerSettingsSanitizer.Copy(baseSettings);
            candidate.AgentUri = "https://192.168.0.20:18443";
            return Task.FromResult(new LocalAgentPreflightResult(
                true,
                candidate,
                SuccessfulProbeResult(),
                1));
        }
    }

    private static AgentConnectionProbeResult SuccessfulProbeResult() =>
        AgentConnectionProbeResult.Success(
                Identity(),
                "Agent 연결 확인 완료")
            with
        {
            StageSnapshots =
                [
                    new(AgentConnectionProbeStage.Address, AgentConnectionProbeState.Succeeded, 11),
                    new(AgentConnectionProbeStage.Dns, AgentConnectionProbeState.Succeeded, 12),
                    new(AgentConnectionProbeStage.Tcp, AgentConnectionProbeState.Succeeded, 13),
                    new(AgentConnectionProbeStage.Https, AgentConnectionProbeState.Succeeded, 14),
                    new(AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Succeeded, 15)
                ]
        };

    private static AgentIdentityDto Identity() =>
        new(
            4,
            "agent-test",
            "instance-test",
            new string('A', 64),
            "https",
            8,
            65_536)
        {
            ProductVersion = AgentProductVersionPolicy.CurrentViewerVersion
        };
}
