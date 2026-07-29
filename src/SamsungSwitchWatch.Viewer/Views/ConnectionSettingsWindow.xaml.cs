using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SamsungSwitchWatch.Viewer.Services;
using MediaBrushes = System.Windows.Media.Brushes;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class ConnectionSettingsWindow : Window
{
    private readonly ViewerSettings _original;
    private readonly Func<ViewerSettings, CancellationToken, Task> _applySettingsAsync;
    private readonly IAgentConnectionProbe _connectionProbe;
    private readonly ILocalAgentPreflight _localAgentPreflight;
    private readonly ViewerFieldDiagnosticWriter _fieldDiagnosticWriter = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ViewerSettings? _identityMismatchCandidate;
    private ViewerSettings? _localPreflightCandidate;
    private ViewerFieldDiagnosticSnapshot? _lastFieldDiagnostic;
    private AgentConnectionProbeResult? _lastDiagnosticProbeResult;
    private string _lastDiagnosticMode = "NORMAL";
    private int _lastDiagnosticCandidateCount;
    private bool _settingDiscoveredAddress;
    private bool _addressTextInitialized;
    private bool _localPreflightRunning;
    private bool _settingsApplied;

    public ConnectionSettingsWindow(
        ViewerSettings settings,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync)
        : this(settings, applySettingsAsync, new AgentConnectionProbe(), null)
    {
    }

    internal ConnectionSettingsWindow(
        ViewerSettings settings,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync,
        IAgentConnectionProbe connectionProbe)
        : this(settings, applySettingsAsync, connectionProbe, null)
    {
    }

    internal ConnectionSettingsWindow(
        ViewerSettings settings,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync,
        IAgentConnectionProbe connectionProbe,
        ILocalAgentPreflight? localAgentPreflight)
    {
        InitializeComponent();
        _original = ViewerSettingsSanitizer.Copy(settings);
        _applySettingsAsync = applySettingsAsync;
        _connectionProbe = connectionProbe ?? throw new ArgumentNullException(nameof(connectionProbe));
        _localAgentPreflight = localAgentPreflight
                               ?? new LocalAgentPreflight(
                                   new SystemLocalIpv4Discovery(),
                                   _connectionProbe);
        DemoModeCheckBox.IsChecked = settings.DemoMode;
        ViewerSettingsSanitizer.SplitAgentUri(settings.AgentUri, out var address, out var port);
        AgentAddressTextBox.Text = address;
        _addressTextInitialized = true;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimizedToTray;
        if (ViewerSettingsSanitizer.IsLoopbackAgentUri(settings.AgentUri))
        {
            ValidationText.Text = ViewerSettingsSanitizer.LoopbackAgentAddressReason;
        }
        Loaded += (_, _) =>
        {
            FitToWorkingArea();
            if (DemoModeCheckBox.IsChecked == true) DemoModeCheckBox.Focus();
            else AgentAddressTextBox.Focus();
        };
        Closed += (_, _) => _lifetime.Cancel();
        UpdateLiveControls();
    }

    public ViewerSettings? Result { get; private set; }

    internal ViewerFieldDiagnosticSnapshot? FieldDiagnosticSnapshot => _lastFieldDiagnostic;

    private void FitToWorkingArea()
    {
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        Height = Math.Min(Height, MaxHeight);
    }

    private void DemoMode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateLiveControls();
        if (DemoModeCheckBox.IsChecked == true)
        {
            ResetProbeSteps();
            ConnectionProgressPanel.Visibility = Visibility.Collapsed;
            LocalPreflightResultPanel.Visibility = Visibility.Collapsed;
            _localPreflightCandidate = null;
            ClearFieldDiagnostic();
        }
    }

    private void UpdateLiveControls() => LiveSettingsPanel.IsEnabled = DemoModeCheckBox.IsChecked != true;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Foreground = MediaBrushes.Firebrick;
        ValidationText.Text = string.Empty;

        if (DemoModeCheckBox.IsChecked == true)
        {
            var demoCandidate = ViewerSettingsSanitizer.Copy(_original);
            demoCandidate.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
            demoCandidate.DemoMode = true;
            await ApplyAndCloseAsync(
                ViewerSettingsSanitizer.Sanitize(demoCandidate),
                probeConnection: false,
                keepOpenAfterApply: false);
            return;
        }

        if (!ViewerSettingsSanitizer.TryBuildAgentUri(
                AgentAddressTextBox.Text,
                ViewerSettingsSanitizer.DefaultAgentPort.ToString(),
                out var agentUri,
                out var reason))
        {
            ValidationText.Text = reason;
            return;
        }

        var usePreflightCandidate =
            _localPreflightCandidate is not null
            && string.Equals(
                ViewerSettingsSanitizer.NormalizeAgentUri(_localPreflightCandidate.AgentUri),
                agentUri,
                StringComparison.OrdinalIgnoreCase);
        var candidate = ViewerSettingsSanitizer.Copy(
            usePreflightCandidate ? _localPreflightCandidate! : _original);
        candidate.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
        candidate.DemoMode = false;
        candidate.AgentUri = agentUri;
        var clean = ViewerSettingsSanitizer.Sanitize(candidate);
        if (!ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out reason))
        {
            ValidationText.Text = reason;
            return;
        }

        if (usePreflightCandidate)
        {
            ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
            ValidationText.Text = "사전 테스트를 통과한 Agent 연결을 저장하고 있습니다.";
        }
        await ApplyAndCloseAsync(
            clean,
            probeConnection: !usePreflightCandidate,
            keepOpenAfterApply: true);
    }

    private async void LocalPreflight_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Foreground = MediaBrushes.Firebrick;
        ValidationText.Text = string.Empty;
        LocalPreflightResultPanel.Visibility = Visibility.Collapsed;
        _localPreflightCandidate = null;
        ClearFieldDiagnostic();
        ResetProbeSteps();
        ConnectionProgressPanel.Visibility = Visibility.Visible;
        ConnectionProgressTitleText.Text = "이 PC의 사설 IPv4를 확인하고 있습니다.";
        _localPreflightRunning = true;
        SetBusy(true);
        try
        {
            var candidate = ViewerSettingsSanitizer.Copy(_original);
            candidate.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
            candidate.DemoMode = false;
            var progress = new Progress<LocalAgentPreflightUpdate>(UpdateLocalPreflight);
            var result = await _localAgentPreflight.RunAsync(
                candidate,
                progress,
                _lifetime.Token);
            SetFieldDiagnostic("SAME_PC", result.ProbeResult, result.CandidateCount);
            if (!result.Succeeded || result.SuccessfulSettings is null)
            {
                ValidationText.Foreground = MediaBrushes.Firebrick;
                var attempted = result.CandidateCount == 0
                    ? string.Empty
                    : $"사설 IPv4 {result.CandidateCount}개 확인 · ";
                ValidationText.Text =
                    $"{attempted}{ProbeStageTitle(result.ProbeResult.FailedStage)} 단계 실패 · "
                    + $"{result.ProbeResult.Detail} ({result.ProbeResult.ErrorCode})";
                return;
            }

            _localPreflightCandidate = ViewerSettingsSanitizer.Copy(result.SuccessfulSettings);
            ViewerSettingsSanitizer.SplitAgentUri(
                _localPreflightCandidate.AgentUri,
                out var address,
                out _);
            _settingDiscoveredAddress = true;
            try
            {
                AgentAddressTextBox.Text = address;
            }
            finally
            {
                _settingDiscoveredAddress = false;
            }

            LocalAgentApiStatusText.Text =
                $"✓ Agent 실행 및 API 연결: 정상 ({address})";
            LocalPreflightResultPanel.Visibility = Visibility.Visible;
            ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
            ValidationText.Text =
                "동일 PC 사전 테스트를 통과했습니다. '연결 확인 및 저장'을 눌러 적용하세요.";
        }
        catch (OperationCanceledException)
        {
            // Closing the dialog cancels the bounded local preflight.
        }
        catch
        {
            var result = AgentConnectionProbeResult.Failure(
                AgentConnectionProbeStage.Address,
                "LOCAL_AGENT_PREFLIGHT_FAILED",
                ViewerConnectionMessages.ForCode("LOCAL_AGENT_PREFLIGHT_FAILED"));
            SetFieldDiagnostic("SAME_PC", result, 0);
            ValidationText.Foreground = MediaBrushes.Firebrick;
            ValidationText.Text =
                "이 PC 사전 테스트를 완료하지 못했습니다. 네트워크 어댑터와 Agent 서비스를 확인해 주세요. "
                + "(LOCAL_AGENT_PREFLIGHT_FAILED)";
        }
        finally
        {
            _localPreflightRunning = false;
            if (IsVisible) SetBusy(false);
        }
    }

    private void AgentAddress_Changed(object sender, TextChangedEventArgs e)
    {
        if (_settingDiscoveredAddress || !_addressTextInitialized)
        {
            return;
        }

        _localPreflightCandidate = null;
        _identityMismatchCandidate = null;
        RetrustButton.Visibility = Visibility.Collapsed;
        ConnectionProgressPanel.Visibility = Visibility.Collapsed;
        LocalPreflightResultPanel.Visibility = Visibility.Collapsed;
        ValidationText.Text = string.Empty;
        ClearFieldDiagnostic();
    }

    private void UpdateLocalPreflight(LocalAgentPreflightUpdate update)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !_lifetime.IsCancellationRequested)
            {
                _ = Dispatcher.BeginInvoke(() => UpdateLocalPreflight(update));
            }
            return;
        }
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (update.ProbeUpdate is null)
        {
            ResetProbeSteps();
            ConnectionProgressPanel.Visibility = Visibility.Visible;
            ConnectionProgressTitleText.Text =
                $"이 PC 주소 {update.CandidateNumber}/{update.CandidateCount} · {update.CandidateAddress}";
            return;
        }

        UpdateProbeStep(update.ProbeUpdate);
    }

    private async Task ApplyAndCloseAsync(
        ViewerSettings settings,
        bool probeConnection,
        bool keepOpenAfterApply)
    {
        SetBusy(true);
        try
        {
            if (probeConnection)
            {
                ClearFieldDiagnostic();
                ResetProbeSteps();
                ConnectionProgressPanel.Visibility = Visibility.Visible;
                var progress = new Progress<AgentConnectionProbeUpdate>(UpdateProbeStep);
                var probeResult = await _connectionProbe.ProbeAsync(
                    settings,
                    progress,
                    _lifetime.Token);
                SetFieldDiagnostic("NORMAL", probeResult, 1);
                if (!probeResult.Succeeded)
                {
                    ShowProbeFailure(settings, probeResult);
                    return;
                }

                ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                ValidationText.Text = "Agent 연결 확인 완료 · 설정을 저장하고 있습니다.";
            }

            await _applySettingsAsync(settings, _lifetime.Token);
            Result = settings;
            if (keepOpenAfterApply)
            {
                _settingsApplied = true;
                ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                ValidationText.Text =
                    "Agent 연결과 설정 저장이 완료되었습니다. 필요하면 익명 진단을 저장한 뒤 닫아 주세요.";
            }
            else
            {
                DialogResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown or an explicit dialog close cancels the operation.
        }
        catch (AgentClientException exception)
        {
            ReplaceSuccessfulDiagnosticWithApplyFailure(exception.ErrorCode);
            ValidationText.Foreground = MediaBrushes.Firebrick;
            ValidationText.Text =
                $"{ViewerConnectionMessages.ForCode(exception.ErrorCode)} ({exception.ErrorCode})";
            if (exception.ErrorCode == "AGENT_IDENTITY_CHANGED")
            {
                _identityMismatchCandidate = settings;
                RetrustButton.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            ReplaceSuccessfulDiagnosticWithApplyFailure("VIEWER_UNEXPECTED_ERROR");
            if (probeConnection && _lastFieldDiagnostic is null)
            {
                var result = AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Address,
                    "VIEWER_UNEXPECTED_ERROR",
                    ViewerConnectionMessages.ForCode("VIEWER_UNEXPECTED_ERROR"));
                SetFieldDiagnostic("NORMAL", result, 1);
            }
            ValidationText.Foreground = MediaBrushes.Firebrick;
            ValidationText.Text =
                "연결 설정을 적용하지 못했습니다. Agent 서비스와 네트워크 경로를 확인해 주세요. "
                + "(VIEWER_UNEXPECTED_ERROR)";
        }
        finally
        {
            if (IsVisible) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy && !_settingsApplied;
        CancelButton.IsEnabled = !busy;
        AgentAddressTextBox.IsEnabled = !busy && !_settingsApplied;
        DemoModeCheckBox.IsEnabled = !busy && !_settingsApplied;
        StartMinimizedCheckBox.IsEnabled = !busy && !_settingsApplied;
        RetrustButton.IsEnabled = !busy && !_settingsApplied;
        LocalPreflightButton.IsEnabled = !busy && !_settingsApplied;
        DiagnosticSaveButton.IsEnabled = !busy && _lastFieldDiagnostic is not null;
        SaveButton.Content = busy
            ? "연결 확인 중…"
            : _settingsApplied
                ? "저장 완료"
                : "연결 확인 및 저장";
        CancelButton.Content = _settingsApplied ? "닫기" : "취소";
        LocalPreflightButton.Content =
            busy && _localPreflightRunning
                ? "같은 PC 확인 중…"
                : "Agent와 Viewer가 같은 PC일 때 테스트";
    }

    private async void Retrust_Click(object sender, RoutedEventArgs e)
    {
        if (_identityMismatchCandidate is null) return;
        if (MessageBox.Show(
                this,
                "Agent PC가 실제로 교체되었거나 Agent가 다시 설치된 경우에만 진행하세요.",
                "이 Agent로 다시 연결",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        var candidate = ViewerSettingsSanitizer.Copy(_identityMismatchCandidate);
        candidate.RemoveAgentTrustPin();
        RetrustButton.Visibility = Visibility.Collapsed;
        _identityMismatchCandidate = null;
        await ApplyAndCloseAsync(
            candidate,
            probeConnection: true,
            keepOpenAfterApply: true);
    }

    private async void DiagnosticSave_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _lastFieldDiagnostic;
        if (snapshot is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "익명 Agent 연결 진단 저장",
            FileName = $"ssw-viewer-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            Filter = "텍스트 파일 (*.txt)|*.txt",
            OverwritePrompt = true
        };

        bool accepted;
        try
        {
            accepted = dialog.ShowDialog(this) == true;
        }
        catch
        {
            ShowDiagnosticWriteFailure();
            return;
        }

        if (!accepted)
        {
            return;
        }

        DiagnosticSaveButton.IsEnabled = false;
        var result = await _fieldDiagnosticWriter.WriteAsync(
            dialog.FileName,
            snapshot,
            _lifetime.Token);
        if (result.Succeeded)
        {
            ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
            ValidationText.Text = "익명 진단을 저장했습니다.";
        }
        else
        {
            ShowDiagnosticWriteFailure();
        }

        if (IsVisible)
        {
            DiagnosticSaveButton.IsEnabled = _lastFieldDiagnostic is not null;
        }
    }

    private void SetFieldDiagnostic(
        string mode,
        AgentConnectionProbeResult result,
        int candidateCount)
    {
        _lastFieldDiagnostic = ViewerFieldDiagnostic.Create(
            mode,
            result,
            candidateCount);
        _lastDiagnosticProbeResult = result;
        _lastDiagnosticMode = mode;
        _lastDiagnosticCandidateCount = candidateCount;
        DiagnosticSaveButton.Visibility = Visibility.Visible;
        DiagnosticSaveButton.IsEnabled = true;
    }

    private void ReplaceSuccessfulDiagnosticWithApplyFailure(string errorCode)
    {
        if (_lastFieldDiagnostic?.Result != "SUCCESS"
            || _lastDiagnosticProbeResult is null)
        {
            return;
        }

        _lastFieldDiagnostic = ViewerFieldDiagnostic.CreateApplyFailure(
            _lastDiagnosticMode,
            _lastDiagnosticProbeResult,
            _lastDiagnosticCandidateCount,
            errorCode);
        DiagnosticSaveButton.Visibility = Visibility.Visible;
        DiagnosticSaveButton.IsEnabled = true;
    }

    private void ClearFieldDiagnostic()
    {
        _lastFieldDiagnostic = null;
        _lastDiagnosticProbeResult = null;
        _lastDiagnosticMode = "NORMAL";
        _lastDiagnosticCandidateCount = 0;
        DiagnosticSaveButton.Visibility = Visibility.Collapsed;
        DiagnosticSaveButton.IsEnabled = false;
    }

    private void ShowDiagnosticWriteFailure()
    {
        ValidationText.Foreground = MediaBrushes.Firebrick;
        ValidationText.Text =
            "익명 진단을 저장하지 못했습니다. 저장 위치의 쓰기 권한과 디스크 여유 공간을 확인해 주세요. "
            + "(DIAGNOSTIC_WRITE_FAILED)";
    }

    private void ShowProbeFailure(
        ViewerSettings settings,
        AgentConnectionProbeResult result)
    {
        ValidationText.Foreground = MediaBrushes.Firebrick;
        ValidationText.Text =
            $"{ProbeStageTitle(result.FailedStage)} 단계 실패 · {result.Detail} ({result.ErrorCode})";
        if (result.ErrorCode == "AGENT_IDENTITY_CHANGED")
        {
            _identityMismatchCandidate = settings;
            RetrustButton.Visibility = Visibility.Visible;
        }
    }

    private void ResetProbeSteps()
    {
        RetrustButton.Visibility = Visibility.Collapsed;
        _identityMismatchCandidate = null;
        ConnectionProgressTitleText.Text = "연결 확인 단계";
        SetProbeText(AddressProbeText, "○", "1. 주소 형식", string.Empty, MediaBrushes.SlateGray);
        SetProbeText(DnsProbeText, "○", "2. DNS 또는 IPv4", string.Empty, MediaBrushes.SlateGray);
        SetProbeText(TcpProbeText, "○", "3. TCP/18443", string.Empty, MediaBrushes.SlateGray);
        SetProbeText(HttpsProbeText, "○", "4. HTTPS 보호", string.Empty, MediaBrushes.SlateGray);
        SetProbeText(IdentityProbeText, "○", "5. Agent API와 버전", string.Empty, MediaBrushes.SlateGray);
        ValidationText.Text = string.Empty;
    }

    private void UpdateProbeStep(AgentConnectionProbeUpdate update)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !_lifetime.IsCancellationRequested)
            {
                _ = Dispatcher.BeginInvoke(() => UpdateProbeStep(update));
            }
            return;
        }
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var block = update.Stage switch
        {
            AgentConnectionProbeStage.Address => AddressProbeText,
            AgentConnectionProbeStage.Dns => DnsProbeText,
            AgentConnectionProbeStage.Tcp => TcpProbeText,
            AgentConnectionProbeStage.Https => HttpsProbeText,
            AgentConnectionProbeStage.Identity => IdentityProbeText,
            _ => throw new ArgumentOutOfRangeException(nameof(update))
        };
        var (icon, brush) = update.State switch
        {
            AgentConnectionProbeState.Running => ("●", new SolidColorBrush(Color.FromRgb(37, 99, 235))),
            AgentConnectionProbeState.Succeeded => ("✓", new SolidColorBrush(Color.FromRgb(22, 101, 52))),
            AgentConnectionProbeState.Failed => ("!", MediaBrushes.Firebrick),
            _ => ("○", MediaBrushes.SlateGray)
        };
        SetProbeText(block, icon, ProbeStageNumberedTitle(update.Stage), update.Detail, brush);
    }

    private static void SetProbeText(
        TextBlock block,
        string icon,
        string title,
        string detail,
        System.Windows.Media.Brush foreground)
    {
        block.Foreground = foreground;
        block.Text = string.IsNullOrWhiteSpace(detail)
            ? $"{icon} {title}"
            : $"{icon} {title} · {detail}";
    }

    private static string ProbeStageNumberedTitle(AgentConnectionProbeStage stage) => stage switch
    {
        AgentConnectionProbeStage.Address => "1. 주소 형식",
        AgentConnectionProbeStage.Dns => "2. DNS 또는 IPv4",
        AgentConnectionProbeStage.Tcp => "3. TCP/18443",
        AgentConnectionProbeStage.Https => "4. HTTPS 보호",
        AgentConnectionProbeStage.Identity => "5. Agent API와 버전",
        _ => "연결 확인"
    };

    private static string ProbeStageTitle(AgentConnectionProbeStage? stage) => stage switch
    {
        AgentConnectionProbeStage.Address => "주소",
        AgentConnectionProbeStage.Dns => "DNS/IPv4",
        AgentConnectionProbeStage.Tcp => "TCP/18443",
        AgentConnectionProbeStage.Https => "HTTPS",
        AgentConnectionProbeStage.Identity => "Agent API/버전",
        _ => "연결 확인"
    };
}
