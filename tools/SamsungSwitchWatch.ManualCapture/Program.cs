using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SamsungSwitchWatch.Viewer;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;
using SamsungSwitchWatch.Viewer.Views;

namespace SamsungSwitchWatch.ManualCapture;

internal static class Program
{
    private static readonly DateTimeOffset DemoNow =
        new(2026, 7, 23, 10, 24, 18, TimeSpan.FromHours(9));

    [STAThread]
    private static int Main(string[] args)
    {
        var outputDirectory = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "manual-images"));
        Directory.CreateDirectory(outputDirectory);
        DeleteLegacyScreenshots(outputDirectory);

        var scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-ManualCapture",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDirectory);

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
                       new ConnectionSettingsWindow(connectionSettings, (_, _) => Task.CompletedTask)
                       {
                           Width = 620,
                           Height = 500,
                           ShowInTaskbar = false,
                           WindowStartupLocation = WindowStartupLocation.Manual,
                           Left = 80,
                           Top = 80
                       }))
            {
                ShowAndLayout(connectionLifetime.Window);
                Capture(
                    connectionLifetime.Window,
                    Path.Combine(outputDirectory, "02-agent-connection.png"),
                    "Agent 주소만 입력하고 HTTPS 18443을 자동 사용하는 연결 설정 창");
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
            viewModel.ReadOnlyQueryCommand = "show running-config";
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
                "장비 명령 탭에서 show running-config를 실행하고 익명화된 데모 결과를 확인하는 화면");

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

            // An argument is supplied by the manual build. With no argument, keep the
            // sanitized dashboard visible briefly so Windows UI verification can inspect
            // the real WPF controls without touching a user's Viewer profile.
            if (args.Length == 0)
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

            return generated.Length >= 6 ? 0 : 2;
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
        foreach (var fileName in new[]
                 {
                     "01-dashboard-demo.png",
                     "02-new-log.png",
                     "03-collector-diagnostics.png",
                     "04-agent-connection-demo.png"
                 })
        {
            var path = Path.Combine(outputDirectory, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void RefreshLayout(Window window)
    {
        window.UpdateLayout();
        DrainDispatcher();
        window.UpdateLayout();
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
