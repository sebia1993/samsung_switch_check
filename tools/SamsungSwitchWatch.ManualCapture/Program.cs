using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;
using SamsungSwitchWatch.Viewer;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;
using SamsungSwitchWatch.Viewer.Views;
using AgentSetupWindow = SamsungSwitchWatch.Agent.Setup.MainWindow;

namespace SamsungSwitchWatch.ManualCapture;

internal static class Program
{
    private const string ManualProductVersion = "0.11.0-poc";

    private static readonly string[] ExpectedScreenshotNames =
    [
        "00-agent-setup.png",
        "00-agent-setup-recovery-failed.png",
        "01-dashboard.png",
        "02-agent-connection.png",
        "02-agent-connection-failed.png",
        "03-device-management.png",
        "04-command-output.png",
        "05-mini-window.png",
        "06-alert-popup.png"
    ];

    private static readonly DateTimeOffset DemoNow =
        new(2026, 7, 23, 10, 24, 18, TimeSpan.FromHours(9));

    [STAThread]
    private static int Main(string[] args)
    {
        var previewAgentSetup = args.Length == 0 || args.Any(argument =>
            string.Equals(argument, "--preview-agent-setup", StringComparison.Ordinal));
        var previewDashboard = args.Any(argument =>
            string.Equals(argument, "--preview-dashboard", StringComparison.Ordinal));
        var outputArgument = args.FirstOrDefault(argument =>
            !argument.StartsWith("--", StringComparison.Ordinal));
        var outputDirectory = outputArgument is not null
            ? Path.GetFullPath(outputArgument)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "manual-images"));
        Directory.CreateDirectory(outputDirectory);
        DeleteLegacyScreenshots(outputDirectory);

        var scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-ManualCapture",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDirectory);

        App.SuppressRuntimeStartupForManualCapture = true;
        var app = new App();
        app.InitializeComponent();
        var uiContext = new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        SynchronizationContext.SetSynchronizationContext(uiContext);

        var settingsStore = new ViewerSettingsStore(
            Path.Combine(scratchDirectory, "viewer-settings.json"));
        var settings = new ViewerSettings
        {
            DemoMode = true,
            AgentUri = "https://192.0.2.20:18443",
            MiniTopmost = true,
            MainWidth = 1440,
            MainHeight = 900
        };
        var deviceStore = new ManagedDeviceStore(
            Path.Combine(scratchDirectory, "viewer-devices.json"));
        var monitoringStore = new ViewerMonitoringStore(
            Path.Combine(scratchDirectory, "viewer-monitor-state.json"));
        var profiles = SeedManagedDevices(deviceStore);
        SeedMonitoringEvents(monitoringStore, profiles);

        var viewModel = new DashboardViewModel(
            settings,
            settingsStore,
            new ManualAgentClientFactory(),
            synchronizationContext: uiContext,
            deviceStore,
            monitoringStore);

        try
        {
            WaitForTask(viewModel.InitializeAsync());
            DrainDispatcher();
            viewModel.SelectedDevice = viewModel.Devices.First(item =>
                item.Id == profiles["critical"].Id);

            var setupFileSystem = new PhysicalSetupFileSystem();
            var setupPaths = new DeploymentPaths(
                scratchDirectory,
                Path.Combine(scratchDirectory, "Agent"),
                Path.Combine(scratchDirectory, "Data"),
                Path.Combine(scratchDirectory, "Operations"));
            var setupPackage = new AgentPackageValidator(setupFileSystem);
            var setupServices = new WindowsServiceManager();
            var setupFirewall = new WindowsFirewallManager();
            var setupHealth = new HttpsAgentHealthProbe();
            var setupAdministrator = new WindowsAdministratorChecker();
            var setupDiagnostics = new SetupDiagnosticsService(
                setupPackage,
                setupFileSystem,
                setupServices,
                setupFirewall,
                setupHealth,
                setupAdministrator,
                setupPaths);
            var setupDeployment = new AgentDeploymentOrchestrator(
                setupPackage,
                setupFileSystem,
                setupServices,
                setupFirewall,
                setupHealth,
                setupAdministrator,
                new WindowsMachineDeploymentLock(),
                setupPaths);
            using (var setupLifetime = new WindowLifetime(
                new AgentSetupWindow(
                            setupDiagnostics,
                            setupDeployment,
                            diagnosticsOnly: false)
                       {
                            Width = 760,
                            Height = 700,
                           ShowInTaskbar = previewAgentSetup,
                           WindowStartupLocation = WindowStartupLocation.Manual,
                           Left = 48,
                           Top = 48
                       }))
            {
                ShowAndLayout(setupLifetime.Window);
                var resultItems = (ItemsControl)setupLifetime.Window.FindName(
                    "ResultItemsControl");
                resultItems.ItemsSource = new[]
                {
                    SamsungSwitchWatch.Agent.Setup.ResultRow.From(
                        new SetupStepResult(
                            "SERVICE_CONFIGURED",
                            "Agent 서비스",
                            SetupStepState.Succeeded,
                            "창 없는 Windows 서비스 설치와 시작을 완료했습니다.")),
                    SamsungSwitchWatch.Agent.Setup.ResultRow.From(
                        new SetupStepResult(
                            SetupErrorCodes.AgentLocalConnectionUnconfirmed,
                            "연결 준비 확인",
                            SetupStepState.Warning,
                            "Agent는 설치되어 실행 중입니다. Viewer에서 Agent 연결 테스트를 실행하세요."))
                };
                var recoveryStatusBorder = (Border)setupLifetime.Window.FindName(
                    "RecoveryStatusBorder");
                recoveryStatusBorder.Visibility = Visibility.Collapsed;
                var recoveryStatusTitle = (TextBlock)setupLifetime.Window.FindName(
                    "RecoveryStatusTitle");
                var recoveryStatusText = (TextBlock)setupLifetime.Window.FindName(
                    "RecoveryStatusText");
                var actionGuidance = (TextBlock)setupLifetime.Window.FindName(
                    "ActionGuidanceText");
                actionGuidance.Text =
                    "설치는 유지됩니다. Viewer에서 Agent 연결 테스트를 실행하세요.";
                var copyDiagnostics = (Button)setupLifetime.Window.FindName(
                    "CopyDiagnosticsButton");
                copyDiagnostics.Visibility = Visibility.Collapsed;
                var diagnosticsFeedback = (TextBlock)setupLifetime.Window.FindName(
                    "DiagnosticsCopyFeedbackText");
                diagnosticsFeedback.Visibility = Visibility.Collapsed;
                var recoverButton = (Button)setupLifetime.Window.FindName(
                    "RecoverButton");
                recoverButton.Visibility = Visibility.Collapsed;
                var installButton = (Button)setupLifetime.Window.FindName(
                    "InstallButton");
                installButton.IsEnabled = true;
                var operationState = (TextBlock)setupLifetime.Window.FindName(
                    "OperationStateText");
                operationState.Text = "설치 완료 · 연결 확인 필요";
                operationState.Foreground = Brushes.DarkGoldenrod;
                RefreshLayout(setupLifetime.Window);
                Capture(
                    setupLifetime.Window,
                    Path.Combine(outputDirectory, "00-agent-setup.png"),
                    "Viewer IP와 관리망 CIDR 입력 없이 Agent 서비스 설치 완료와 연결 확인 경고를 보여 주는 Agent Setup 화면");

                setupLifetime.Window.Height = 900;
                resultItems.ItemsSource = new[]
                {
                    SamsungSwitchWatch.Agent.Setup.ResultRow.From(
                        new SetupStepResult(
                            SetupErrorCodes.ServiceFailed,
                            "최초 설치 실패",
                            SetupStepState.Failed,
                            "설치 단계의 최초 실패 원인은 그대로 보존됩니다.")),
                    SamsungSwitchWatch.Agent.Setup.ResultRow.From(
                        new SetupStepResult(
                            "ROLLBACK_JOURNAL_CLEANUP_FAILED",
                            "복구 기록 정리",
                            SetupStepState.Failed,
                            "작업 기록 정리와 삭제 확인이 끝나지 않아 기록을 보존했습니다."))
                };
                recoveryStatusBorder.Background =
                    new SolidColorBrush(Color.FromRgb(254, 242, 242));
                recoveryStatusBorder.BorderBrush =
                    new SolidColorBrush(Color.FromRgb(220, 38, 38));
                recoveryStatusBorder.Visibility = Visibility.Visible;
                recoveryStatusTitle.Text =
                    "이전 상태를 완전히 복구하지 못했습니다";
                recoveryStatusTitle.Foreground = Brushes.Firebrick;
                recoveryStatusText.Text =
                    "설치 자료 정리 미완료 · 작업 기록 보존\n" +
                    "복구를 다시 시도하고 반복되면 익명 진단을 저장하세요.";
                actionGuidance.Text =
                    "복구 다시 시도 · 반복 시 익명 진단 저장";
                copyDiagnostics.Visibility = Visibility.Visible;
                var saveFieldDiagnostic = (Button)setupLifetime.Window.FindName(
                    "SaveFieldDiagnosticButton");
                saveFieldDiagnostic.Visibility = Visibility.Visible;
                var agentSupportCode = CreateAgentFailureSupportCode();
                var agentSupportCodeBorder = (Border)setupLifetime.Window.FindName(
                    "SupportCodeBorder");
                var agentSupportCodeTextBox = (TextBox)setupLifetime.Window.FindName(
                    "SupportCodeTextBox");
                agentSupportCodeTextBox.Text = agentSupportCode;
                agentSupportCodeBorder.Visibility = Visibility.Visible;
                recoverButton.Content = "복구 다시 시도";
                recoverButton.Visibility = Visibility.Visible;
                recoverButton.IsEnabled = true;
                installButton.IsEnabled = false;
                operationState.Text =
                    "복구 실패 · SETUP_ROLLBACK_FAILED";
                operationState.Foreground = Brushes.Firebrick;
                RefreshLayout(setupLifetime.Window);
                Capture(
                    setupLifetime.Window,
                    Path.Combine(
                        outputDirectory,
                        "00-agent-setup-recovery-failed.png"),
                    "복구 실패 상위 상태와 journal 대상별 정리 실패, 선택 가능한 SWD1 지원 코드를 구분하고 작업 기록과 설치 잠금을 유지하며 복구 재시도와 익명 진단 저장을 안내하는 Agent Setup 화면");
                Console.WriteLine($"Agent failure support code: {agentSupportCode}");
                if (previewAgentSetup)
                {
                    var previewDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
                    while (DateTime.UtcNow < previewDeadline)
                    {
                        DrainDispatcher();
                        Thread.Sleep(20);
                    }
                }
            }

            using var dashboardLifetime = new WindowLifetime(
                new MainWindow(viewModel)
                {
                    Width = 1440,
                    Height = 900,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 24,
                    Top = 24
                });
            var dashboard = (MainWindow)dashboardLifetime.Window;
            ShowAndLayout(dashboard);

            var detailsTabs = FindVisualChildren<TabControl>(dashboard)
                .OrderByDescending(control => control.Items.Count)
                .First(control => control.Items.Count >= 5);
            detailsTabs.SelectedIndex = 0;
            RefreshLayout(dashboard);
            Capture(
                dashboard,
                Path.Combine(outputDirectory, "01-dashboard.png"),
                "Viewer가 등록 장비, 선택 장비 상태, 최근 이벤트와 Viewer 감시 상태를 보여 주는 대시보드");

            var connectionSettings = ViewerSettingsSanitizer.Copy(settings);
            connectionSettings.DemoMode = false;
            using (var connectionLifetime = new WindowLifetime(
                       new ConnectionSettingsWindow(
                           connectionSettings,
                           (_, _) => Task.CompletedTask,
                           new ManualSuccessfulAgentConnectionProbe())
                       {
                            Width = 650,
                            Height = 820,
                            ShowInTaskbar = false,
                            WindowStartupLocation = WindowStartupLocation.Manual,
                           Left = 80,
                           Top = 80
                       }))
            {
                ShowAndLayout(connectionLifetime.Window);
                var saveButton = (Button)connectionLifetime.Window.FindName(
                    "SaveButton");
                var progressPanel = (Border)connectionLifetime.Window.FindName(
                    "ConnectionProgressPanel");
                saveButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(
                    () => progressPanel.Visibility == Visibility.Visible
                          && !string.IsNullOrWhiteSpace(
                              ((TextBlock)connectionLifetime.Window.FindName(
                                  "ValidationText")).Text),
                    TimeSpan.FromSeconds(3));
                RefreshLayout(connectionLifetime.Window);
                ScrollAllToTop(connectionLifetime.Window);
                Capture(
                    connectionLifetime.Window,
                    Path.Combine(outputDirectory, "02-agent-connection.png"),
                    "Agent PC 주소 하나만 입력하고 HTTPS 18443, Agent API와 호환 버전을 자동 확인한 연결 설정 창");
            }

            using (var connectionFailureLifetime = new WindowLifetime(
                       new ConnectionSettingsWindow(
                           connectionSettings,
                           (_, _) => Task.CompletedTask,
                           new ManualFailingAgentConnectionProbe())
                       {
                           Width = 650,
                           Height = 820,
                           ShowInTaskbar = false,
                           WindowStartupLocation = WindowStartupLocation.Manual,
                           Left = 80,
                           Top = 80
                       }))
            {
                ShowAndLayout(connectionFailureLifetime.Window);
                var saveButton = (Button)connectionFailureLifetime.Window.FindName(
                    "SaveButton");
                var supportCodePanel = (Border)connectionFailureLifetime.Window.FindName(
                    "SupportCodePanel");
                saveButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(
                    () => supportCodePanel.Visibility == Visibility.Visible,
                    TimeSpan.FromSeconds(3));

                var snapshot =
                    ((ConnectionSettingsWindow)connectionFailureLifetime.Window)
                    .FieldDiagnosticSnapshot
                    ?? throw new InvalidOperationException(
                        "The Viewer failure diagnostic was not created.");
                var viewerSupportCode =
                    ViewerFieldDiagnostic.CreateSupportCode(snapshot);
                EnsureSupportCodeDecodes(
                    typeof(ConnectionSettingsWindow).Assembly,
                    viewerSupportCode);
                var viewerSupportCodeTextBox =
                    (TextBox)connectionFailureLifetime.Window.FindName(
                        "SupportCodeTextBox");
                if (!string.Equals(
                        viewerSupportCodeTextBox.Text,
                        viewerSupportCode,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The Viewer failure UI does not show the formatter output.");
                }

                RefreshLayout(connectionFailureLifetime.Window);
                ScrollAllToTop(connectionFailureLifetime.Window);
                Capture(
                    connectionFailureLifetime.Window,
                    Path.Combine(
                        outputDirectory,
                        "02-agent-connection-failed.png"),
                    "Agent TCP 18443 연결 거부 단계와 선택 가능한 SWD1 지원 코드를 함께 보여 주고 익명 진단 저장을 유지하는 Viewer 연결 실패 화면");
                Console.WriteLine(
                    $"Viewer failure support code: {viewerSupportCode}");
            }

            using (var deviceLifetime = new WindowLifetime(
                       new DeviceManagementWindow(viewModel)
                       {
                           Width = 980,
                           Height = 690,
                           ShowInTaskbar = false,
                           WindowStartupLocation = WindowStartupLocation.Manual,
                           Left = 60,
                           Top = 60
                       }))
            {
                ShowAndLayout(deviceLifetime.Window);
                var passwordBoxes = FindVisualChildren<PasswordBox>(deviceLifetime.Window).ToArray();
                if (passwordBoxes.Length > 0) passwordBoxes[0].Password = "DEMO-LOGIN-PW";
                if (passwordBoxes.Length > 1) passwordBoxes[1].Password = "DEMO-ENABLE-PW";
                RefreshLayout(deviceLifetime.Window);
                Capture(
                    deviceLifetime.Window,
                    Path.Combine(outputDirectory, "03-device-management.png"),
                    "장비명, 모델, IPv4, 계정 ID, 로그인 비밀번호, enable 비밀번호와 감시 설정을 입력하는 장비 관리 창");
            }

            viewModel.SelectedDevice = viewModel.Devices.First(item =>
                item.Id == profiles["normal"].Id);
            viewModel.ReadOnlyQueryCommand = "show port status";
            detailsTabs.SelectedIndex = 3;
            RefreshLayout(dashboard);
            if (viewModel.ExecuteReadOnlyQueryCommand.CanExecute(null))
            {
                viewModel.ExecuteReadOnlyQueryCommand.Execute(null);
                WaitUntil(
                    () => !viewModel.IsReadOnlyQueryRunning
                          && viewModel.ReadOnlyQueryStatusText.StartsWith("완료", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));
            }
            RefreshLayout(dashboard);
            Capture(
                dashboard,
                Path.Combine(outputDirectory, "04-command-output.png"),
                "장비 명령 탭에서 show port status를 실행하고 익명화된 데모 결과를 확인하는 화면");

            using (var miniLifetime = new WindowLifetime(
                       new MiniWindow(viewModel, true)
                       {
                           Width = 360,
                           Height = 220,
                           ShowInTaskbar = false,
                           WindowStartupLocation = WindowStartupLocation.Manual,
                           Left = 80,
                           Top = 80
                       },
                       allowClose: true))
            {
                ShowAndLayout(miniLifetime.Window);
                Capture(
                    miniLifetime.Window,
                    Path.Combine(outputDirectory, "05-mini-window.png"),
                    "정상, 경고, 장애 수와 최근 문제를 보여 주는 항상 위 미니 창");
            }

            var alertEvent = new EventViewModel(new SwitchEventDto(
                9001,
                "manual-demo-critical",
                profiles["critical"].Id,
                profiles["critical"].DisplayName,
                DemoNow,
                DeviceHealth.Critical,
                "상태 변경",
                "업링크 포트 26 DOWN",
                "동작 상태: UP → DOWN · 합성 데모",
                false,
                false,
                "demo-port-26-link"));
            using (var popupLifetime = new WindowLifetime(
                       new AlertPopup(alertEvent)
                       {
                           ShowInTaskbar = false,
                           WindowStartupLocation = WindowStartupLocation.Manual
                       }))
            {
                ShowAndLayout(popupLifetime.Window);
                Capture(
                    popupLifetime.Window,
                    Path.Combine(outputDirectory, "06-alert-popup.png"),
                    "합성 데모 업링크 포트 Down 장애와 발생 시각을 보여 주는 알림 팝업");
            }

            if (previewDashboard)
            {
                var previewDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (DateTime.UtcNow < previewDeadline)
                {
                    DrainDispatcher();
                    Thread.Sleep(20);
                }
            }

            WaitForTask(viewModel.DisposeAsync().AsTask());
            app.Shutdown();

            var generated = Directory.GetFiles(outputDirectory, "*.png")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Console.WriteLine($"Created {generated.Length} sanitized WPF screenshots in {outputDirectory}");
            foreach (var item in generated) Console.WriteLine($"  {item}");

            return generated.SequenceEqual(
                ExpectedScreenshotNames.OrderBy(
                    name => name,
                    StringComparer.Ordinal),
                StringComparer.Ordinal)
                ? 0
                : 2;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratchDirectory))
                {
                    Directory.Delete(scratchDirectory, true);
                }
            }
            catch
            {
                // A failed cleanup must not hide a screenshot/build failure.
            }
        }
    }

    private sealed class ManualSuccessfulAgentConnectionProbe : IAgentConnectionProbe
    {
        public Task<AgentConnectionProbeResult> ProbeAsync(
            ViewerSettings settings,
            IProgress<AgentConnectionProbeUpdate>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (stage, detail) in new[]
                     {
                         (AgentConnectionProbeStage.Address, "Agent 주소 형식을 확인했습니다."),
                         (AgentConnectionProbeStage.Dns, "Agent PC IPv4를 확인했습니다."),
                         (AgentConnectionProbeStage.Tcp, "TCP/18443 연결에 성공했습니다."),
                         (AgentConnectionProbeStage.Https, "HTTPS 보호 연결을 확인했습니다."),
                         (AgentConnectionProbeStage.Identity, $"Agent {ManualProductVersion} · API v4 확인")
                     })
            {
                progress?.Report(new AgentConnectionProbeUpdate(
                    stage,
                    AgentConnectionProbeState.Succeeded,
                    detail));
            }

            var identity = new AgentIdentityDto(
                4,
                "manual-agent",
                "manual-instance",
                new string('A', 64),
                "https",
                8,
                65_536)
            {
                ProductVersion = ManualProductVersion
            };
            return Task.FromResult(AgentConnectionProbeResult.Success(
                identity,
                $"Agent {ManualProductVersion} · API v4 호환"));
        }
    }

    private sealed class ManualFailingAgentConnectionProbe : IAgentConnectionProbe
    {
        public Task<AgentConnectionProbeResult> ProbeAsync(
            ViewerSettings settings,
            IProgress<AgentConnectionProbeUpdate>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AgentConnectionProbeUpdate(
                AgentConnectionProbeStage.Address,
                AgentConnectionProbeState.Succeeded,
                "Agent 주소 형식을 확인했습니다."));
            progress?.Report(new AgentConnectionProbeUpdate(
                AgentConnectionProbeStage.Dns,
                AgentConnectionProbeState.Succeeded,
                "Agent PC IPv4를 확인했습니다."));
            progress?.Report(new AgentConnectionProbeUpdate(
                AgentConnectionProbeStage.Tcp,
                AgentConnectionProbeState.Failed,
                "Agent PC의 TCP/18443 연결이 거부되었습니다.",
                "AGENT_CONNECTION_REFUSED"));

            return Task.FromResult(
                AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Tcp,
                    "AGENT_CONNECTION_REFUSED",
                    "Agent PC의 TCP/18443 연결이 거부되었습니다.")
                with
                {
                    StageSnapshots =
                    [
                        new(
                            AgentConnectionProbeStage.Address,
                            AgentConnectionProbeState.Succeeded,
                            4),
                        new(
                            AgentConnectionProbeStage.Dns,
                            AgentConnectionProbeState.Succeeded,
                            7),
                        new(
                            AgentConnectionProbeStage.Tcp,
                            AgentConnectionProbeState.Failed,
                            12)
                    ]
                });
        }
    }

    private static Dictionary<string, ManagedDeviceProfile> SeedManagedDevices(
        ManagedDeviceStore store)
    {
        var normal = store.Save(new ManagedDeviceDraft
        {
            DisplayName = "ACCESS-SW-DEMO-01",
            Model = "IES4224GP",
            Host = "198.51.100.11",
            Username = "demo-operator",
            Password = "DEMO-ONLY-NOT-A-SECRET",
            EnablePassword = "DEMO-ENABLE-NOT-A-SECRET",
            MonitoringEnabled = true,
            ConnectionVerified = true,
            LastConnectionTestUtc = DemoNow.AddMinutes(-2),
            LastConnectionTestCode = "OK"
        });
        var warning = store.Save(new ManagedDeviceDraft
        {
            DisplayName = "ACCESS-SW-DEMO-02",
            Model = "IES4028XP",
            Host = "198.51.100.12",
            Username = "demo-operator",
            Password = "DEMO-ONLY-NOT-A-SECRET",
            MonitoringEnabled = true,
            ConnectionVerified = true,
            LastConnectionTestUtc = DemoNow.AddMinutes(-3),
            LastConnectionTestCode = "OK"
        });
        var critical = store.Save(new ManagedDeviceDraft
        {
            DisplayName = "ACCESS-SW-DEMO-03",
            Model = "IES4226XP",
            Host = "198.51.100.13",
            Username = "demo-operator",
            Password = "DEMO-ONLY-NOT-A-SECRET",
            EnablePassword = "DEMO-ENABLE-NOT-A-SECRET",
            MonitoringEnabled = true,
            ConnectionVerified = true,
            LastConnectionTestUtc = DemoNow.AddMinutes(-4),
            LastConnectionTestCode = "OK"
        });
        return new Dictionary<string, ManagedDeviceProfile>(StringComparer.Ordinal)
        {
            ["normal"] = normal,
            ["warning"] = warning,
            ["critical"] = critical
        };
    }

    private static void SeedMonitoringEvents(
        ViewerMonitoringStore store,
        IReadOnlyDictionary<string, ManagedDeviceProfile> profiles)
    {
        store.RecordOutput(
            profiles["warning"],
            "show sylog tail num 100",
            "[99] 10:01:03 System ready");
        store.RecordOutput(
            profiles["warning"],
            "show sylog tail num 100",
            "[100] 10:23:42 STP root change notification.\r\n[99] 10:01:03 System ready");

        store.RecordOutput(
            profiles["critical"],
            "show port status",
            "Port Admin Link Speed Duplex\r\n26 Up Up 10G Full");
        store.RecordOutput(
            profiles["critical"],
            "show port status",
            "Port Admin Link Speed Duplex\r\n26 Up Down - -");
        store.RecordFailure(profiles["critical"], "TCP_TIMEOUT");
    }

    private static void ShowAndLayout(Window window)
    {
        window.Show();
        RefreshLayout(window);
    }

    private static void DeleteLegacyScreenshots(string outputDirectory)
    {
        foreach (var fileName in ExpectedScreenshotNames.Concat(
                     [
                         "01-dashboard-demo.png",
                         "02-new-log.png",
                         "03-collector-diagnostics.png",
                         "04-agent-connection-demo.png"
                     ]))
        {
            var path = Path.Combine(outputDirectory, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string CreateAgentFailureSupportCode()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.RollbackFailed,
            "복구 자료 정리를 완료하지 못했습니다.",
            [
                new SetupStepResult(
                    SetupErrorCodes.ServiceFailed,
                    "최초 설치 실패",
                    SetupStepState.Failed,
                    "설치 단계의 최초 실패 원인은 그대로 보존됩니다."),
                new SetupStepResult(
                    SetupErrorCodes.RollbackJournalCleanupFailed,
                    "복구 기록 정리",
                    SetupStepState.Failed,
                    "작업 기록 정리와 삭제 확인이 끝나지 않았습니다.")
            ])
        with
        {
            PrimaryFailureCode = SetupErrorCodes.ServiceFailed,
            RollbackFailureCodes =
            [
                SetupErrorCodes.RollbackJournalCleanupFailed
            ]
        };
        var recovery = new PendingRecoveryInspection(
            true,
            true,
            SetupErrorCodes.RecoveryRequired,
            "이전 설치 상태 복구가 필요합니다.")
        {
            JournalFormatVersion = 1,
            JournalStage = "rollback-completed",
            PrimaryFailureCode = SetupErrorCodes.ServiceFailed,
            RollbackFailureCodes =
            [
                SetupErrorCodes.RollbackJournalCleanupFailed
            ],
            ServiceState = "stopped",
            EvidenceStateKnown = true,
            InstallDirectoryExists = true,
            StagingDirectoryExists = false,
            BackupDirectoryExists = false,
            FailedDirectoryExists = false,
            DataDirectoryExists = true
        };

        var setupAssembly = typeof(AgentSetupWindow).Assembly;
        var contextType = setupAssembly.GetType(
            "SamsungSwitchWatch.Agent.Setup.SetupFieldDiagnosticContext",
            throwOnError: true)!;
        var formatterType = setupAssembly.GetType(
            "SamsungSwitchWatch.Agent.Setup.SetupFieldDiagnosticFormatter",
            throwOnError: true)!;
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic,
            binder: null,
            args:
            [
                ManualProductVersion,
                DemoNow,
                "10.0.22631.0",
                "X64",
                "recovery",
                TimeSpan.FromMilliseconds(812),
                result,
                recovery
            ],
            culture: null)
            ?? throw new InvalidOperationException(
                "The Agent support-code context could not be created.");
        var method = formatterType.GetMethod(
            "CreateSupportCode",
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                formatterType.FullName,
                "CreateSupportCode");
        var code = method.Invoke(null, [context]) as string;
        if (string.IsNullOrWhiteSpace(code) ||
            code.Length != 24 ||
            !code.StartsWith("SWD1-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Agent formatter did not produce a valid SWD1 shape.");
        }
        EnsureSupportCodeDecodes(setupAssembly, code);
        return code;
    }

    private static void EnsureSupportCodeDecodes(
        Assembly assembly,
        string code)
    {
        var codecType = assembly.GetType(
            "SamsungSwitchWatch.Support.Swd1SupportCode",
            throwOnError: true)!;
        var method = codecType.GetMethod(
            "TryDecode",
            BindingFlags.Static |
            BindingFlags.Public)
            ?? throw new MissingMethodException(
                codecType.FullName,
                "TryDecode");
        object?[] arguments = [code, null];
        if (method.Invoke(null, arguments) is not true ||
            arguments[1] is null)
        {
            throw new InvalidOperationException(
                "The generated SWD1 support code did not decode.");
        }
    }

    private static void RefreshLayout(Window window)
    {
        window.UpdateLayout();
        DrainDispatcher();
        window.UpdateLayout();
    }

    private static void ScrollAllToTop(Window window)
    {
        Keyboard.ClearFocus();
        DrainDispatcher();

        var scrollViewers = FindVisualChildren<ScrollViewer>(window).ToArray();
        foreach (var scrollViewer in scrollViewers)
        {
            scrollViewer.ScrollToTop();
        }

        // Focus and BringIntoView requests can be queued by validation UI updates.
        // Drain them, then restore the deterministic top-of-form capture position.
        DrainDispatcher();
        foreach (var scrollViewer in scrollViewers)
        {
            scrollViewer.ScrollToTop();
        }
        DrainDispatcher();
    }

    private static void WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            DrainDispatcher();
            Thread.Sleep(20);
        }
        if (!predicate())
        {
            throw new TimeoutException("The manual capture state did not become ready.");
        }
    }

    private static void WaitForTask(Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Capture(Window window, string path, string altText)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var metadata = new BitmapMetadata("png");
        metadata.SetQuery("/tEXt/Description", altText);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.Background,
            new Action(() => { }));

    private sealed class WindowLifetime : IDisposable
    {
        private readonly bool _allowClose;

        public WindowLifetime(Window window, bool allowClose = false)
        {
            Window = window;
            _allowClose = allowClose;
        }

        public Window Window { get; }

        public void Dispose()
        {
            if (!Window.IsLoaded) return;
            if (Window is MainWindow main) main.AllowClose();
            if (_allowClose && Window is MiniWindow mini) mini.AllowClose();
            Window.Close();
        }
    }

    private sealed class ManualAgentClientFactory : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => new ManualAgentClient();
    }

    private sealed class ManualAgentClient : IAgentClient
    {
        private readonly DemoAgentClient _inner = new();

        public event EventHandler<AgentEventChangeDto>? EventChanged
        {
            add => _inner.EventChanged += value;
            remove => _inner.EventChanged -= value;
        }

        public event EventHandler<AgentConnectionState>? ConnectionStateChanged
        {
            add => _inner.ConnectionStateChanged += value;
            remove => _inner.ConnectionStateChanged -= value;
        }

        public bool SupportsStatelessV4 => true;

        public Task StartAsync(CancellationToken cancellationToken) =>
            _inner.StartAsync(cancellationToken);

        public Task<AgentIdentityDto> GetIdentityAsync(CancellationToken cancellationToken) =>
            _inner.GetIdentityAsync(cancellationToken);

        public Task<TelnetExecutionResultDto> TestTelnetAsync(
            TelnetTargetDto target,
            CancellationToken cancellationToken) =>
            _inner.TestTelnetAsync(target, cancellationToken);

        public async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            await Task.Delay(80, cancellationToken);
            var outputs = request.Commands.Select(command => new TelnetCommandOutputDto(
                command,
                BuildSanitizedOutput(command),
                false,
                DateTimeOffset.UtcNow)).ToArray();
            var completed = DateTimeOffset.UtcNow;
            return new TelnetExecutionResultDto(
                4,
                request.RequestId,
                true,
                string.IsNullOrEmpty(request.EnablePassword) ? "user" : "privileged",
                string.IsNullOrEmpty(request.EnablePassword) ? ">" : "#",
                started,
                completed,
                Math.Max(1, (long)(completed - started).TotalMilliseconds),
                outputs);
        }

        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
            _inner.GetSnapshotAsync(cancellationToken);

        public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(
            int limit,
            CancellationToken cancellationToken) =>
            _inner.GetRecentEventsAsync(limit, cancellationToken);

        public Task<EventChangePageDto> GetEventChangesAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken) =>
            _inner.GetEventChangesAsync(cursor, limit, cancellationToken);

        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(
            string deviceId,
            string commandId,
            CancellationToken cancellationToken) =>
            _inner.ExecuteRegisteredCheckAsync(deviceId, commandId, cancellationToken);

        public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
            string deviceId,
            string command,
            CancellationToken cancellationToken) =>
            _inner.ExecuteReadOnlyQueryAsync(deviceId, command, cancellationToken);

        public Task<bool> AcknowledgeAsync(
            string eventId,
            CancellationToken cancellationToken) =>
            _inner.AcknowledgeAsync(eventId, cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private static string BuildSanitizedOutput(string command)
        {
            if (command.Equals("show running-config", StringComparison.OrdinalIgnoreCase))
            {
                return """
                       ! SANITIZED DEMO OUTPUT - NOT FROM A COMPANY DEVICE
                       hostname ACCESS-SW-DEMO-01
                       !
                       interface ethernet 1/1
                        switchport access vlan 20
                        no shutdown
                       !
                       interface ethernet 1/24
                        description DEMO-UPLINK
                        switchport mode trunk
                       !
                       username demo-operator password <protected>
                       enable password <protected>
                       end
                       """;
            }
            if (command.Equals("show port status", StringComparison.OrdinalIgnoreCase))
            {
                return """
                       Port  Admin  Link  Speed  Duplex
                       1     Up     Up    1G     Full
                       24    Up     Up    1G     Full
                       """;
            }
            if (command.Contains("sylog", StringComparison.OrdinalIgnoreCase)
                || command.Contains("syslog", StringComparison.OrdinalIgnoreCase))
            {
                return """
                       [100] 10:23:42 STP root change notification.
                       [99]  10:01:03 System ready
                       """;
            }
            return $"SANITIZED DEMO OUTPUT\r\nCommand: {command}";
        }
    }
}
