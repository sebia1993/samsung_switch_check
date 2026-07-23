using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class DeviceManagementWindow : Window
{
    private readonly DashboardViewModel _dashboard;
    private readonly ObservableCollection<ManagedDeviceProfile> _devices = [];
    private string? _editingId;
    private string? _successfulTestSignature;
    private string? _failedTestSignature;
    private string? _lastTestCode;
    private DateTimeOffset? _lastTestUtc;
    private bool _busy;
    private bool _suppressSelectionChange;

    public DeviceManagementWindow(DashboardViewModel dashboard)
    {
        InitializeComponent();
        _dashboard = dashboard;
        ModelComboBox.ItemsSource = SupportedSwitchModels.All;
        DeviceList.ItemsSource = _devices;
        Loaded += (_, _) => InitializeWindow();
        Closed += (_, _) => RefreshDashboardAfterClose();
    }

    private void FitToWorkingArea()
    {
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        Height = Math.Min(Height, MaxHeight);
    }

    private void InitializeWindow()
    {
        try
        {
            FitToWorkingArea();
            Reload();
            if (_devices.Count == 0) BeginNewDevice();
            else DeviceList.SelectedIndex = 0;
        }
        catch (Exception exception)
        {
            if (_devices.Count == 0) BeginNewDevice();
            ShowOperationFailure(
                "device-management-load",
                exception,
                DeviceManagementOperation.Load);
        }
    }

    private void RefreshDashboardAfterClose()
    {
        try
        {
            _dashboard.ReloadManagedDevices(_editingId);
        }
        catch (Exception exception)
        {
            var code = DeviceManagementFailureMapper.ToErrorCode(
                exception,
                DeviceManagementOperation.Close);
            _dashboard.ReportDeviceManagementFailure(
                "device-management-close",
                code);
        }
    }

    internal void Reload(string? preferredId = null)
    {
        var selectedId = preferredId ?? _editingId;
        var loaded = _dashboard.GetManagedDevices().ToArray();
        _devices.Clear();
        foreach (var item in loaded) _devices.Add(item);
        DeviceCountText.Text = $"{_devices.Count}대";
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            DeviceList.SelectedItem = _devices.FirstOrDefault(item => item.Id == selectedId);
        }
    }

    private void NewDevice_Click(object sender, RoutedEventArgs e) => BeginNewDevice();

    private void BeginNewDevice()
    {
        DeviceList.SelectedItem = null;
        _editingId = null;
        _successfulTestSignature = null;
        _failedTestSignature = null;
        _lastTestCode = null;
        _lastTestUtc = null;
        FormTitleText.Text = "새 장비 등록";
        DisplayNameTextBox.Text = SuggestName();
        ModelComboBox.SelectedItem = SupportedSwitchModels.All[0];
        HostTextBox.Clear();
        UsernameTextBox.Clear();
        PasswordBox.Clear();
        EnablePasswordBox.Clear();
        ClearEnablePasswordCheckBox.IsChecked = false;
        ClearEnablePasswordCheckBox.IsEnabled = false;
        MonitoringCheckBox.IsChecked = false;
        MonitoringCheckBox.IsEnabled = false;
        PasswordHintText.Text = "새 장비는 로그인 PW가 필요합니다.";
        ResultText.Text = "장비 IP와 계정을 입력한 뒤 접속 시험 또는 저장을 선택하세요.";
        DeleteButton.IsEnabled = false;
        DisplayNameTextBox.Focus();
    }

    private string SuggestName()
    {
        var used = _devices.Select(item => item.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < 1000; index++)
        {
            var value = $"SW-{index:00}";
            if (!used.Contains(value)) return value;
        }
        return "SWITCH";
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChange) return;
        if (DeviceList.SelectedItem is not ManagedDeviceProfile profile) return;
        var previousId = _editingId;
        ManagedDeviceDraft editDraft;
        try
        {
            editDraft = _dashboard.GetManagedDeviceDraft(profile.Id);
        }
        catch (Exception exception)
        {
            RestoreSelection(previousId);
            ShowOperationFailure(
                "device-management-load",
                exception,
                DeviceManagementOperation.Load);
            return;
        }

        _editingId = profile.Id;
        _successfulTestSignature = null;
        _failedTestSignature = null;
        _lastTestCode = profile.LastConnectionTestCode;
        _lastTestUtc = profile.LastConnectionTestUtc;
        FormTitleText.Text = "장비 수정";
        DisplayNameTextBox.Text = profile.DisplayName;
        ModelComboBox.SelectedItem = profile.Model;
        HostTextBox.Text = profile.Host;
        UsernameTextBox.Text = editDraft.Username;
        PasswordBox.Clear();
        EnablePasswordBox.Clear();
        ClearEnablePasswordCheckBox.IsChecked = false;
        ClearEnablePasswordCheckBox.IsEnabled = profile.HasEnablePassword;
        MonitoringCheckBox.IsChecked = profile.MonitoringEnabled;
        var credentialCorrupt = profile.LastConnectionTestCode == "VIEWER_CREDENTIAL_CORRUPT";
        MonitoringCheckBox.IsEnabled = profile.ConnectionVerified && !credentialCorrupt;
        PasswordHintText.Text = credentialCorrupt
            ? "저장된 계정을 읽을 수 없습니다. 로그인 ID와 PW를 다시 입력해 주세요."
            : "변경하지 않으면 저장된 로그인 PW를 유지합니다.";
        ResultText.Text = credentialCorrupt
            ? "저장된 계정 보호 데이터가 손상되었거나 다른 Windows 사용자에게서 복사되었습니다. 계정을 다시 입력하고 접속 시험해 주세요."
            : profile.ConnectionVerified
            ? $"마지막 접속 시험 성공 · {profile.LastConnectionTestUtc?.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
            : $"접속 미확인 · {profile.LastConnectionTestCode ?? "시험 필요"}";
        DeleteButton.IsEnabled = true;
    }

    private void RestoreSelection(string? previousId)
    {
        _suppressSelectionChange = true;
        try
        {
            DeviceList.SelectedItem = string.IsNullOrWhiteSpace(previousId)
                ? null
                : _devices.FirstOrDefault(item => item.Id == previousId);
        }
        finally
        {
            _suppressSelectionChange = false;
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var draft = BuildDraft();
        if (!TryValidateForOperation(draft, out var reason))
        {
            ShowResult(reason, success: false);
            return;
        }

        SetBusy(true, "접속 시험 중…");
        try
        {
            var result = await _dashboard.TestManagedDeviceAsync(draft);
            draft.ConnectionVerified = result.Success;
            MonitoringCheckBox.IsEnabled = result.Success;
            if (!result.Success)
            {
                _successfulTestSignature = null;
                _failedTestSignature = BuildConnectionSignature(draft);
                _lastTestCode = "CONNECTION_TEST_FAILED";
                _lastTestUtc = DateTimeOffset.UtcNow;
                MonitoringCheckBox.IsChecked = false;
                ShowResult("접속 시험이 실패했습니다. 장비는 저장할 수 있지만 감시는 꺼집니다.", false);
                return;
            }
            _successfulTestSignature = BuildConnectionSignature(draft);
            _failedTestSignature = null;
            _lastTestCode = "OK";
            _lastTestUtc = DateTimeOffset.UtcNow;
            ShowResult($"접속 성공 · 권한 {result.Privilege} · {result.DurationMs:N0}ms", true);
        }
        catch (Exception exception)
        {
            _successfulTestSignature = null;
            _failedTestSignature = BuildConnectionSignature(draft);
            MonitoringCheckBox.IsChecked = false;
            MonitoringCheckBox.IsEnabled = false;
            var code = exception switch
            {
                AgentClientException typed => typed.ErrorCode,
                InvalidDataException invalid when invalid.Message == "VIEWER_CREDENTIAL_CORRUPT" =>
                    "VIEWER_CREDENTIAL_CORRUPT",
                _ => "CONNECTION_TEST_FAILED"
            };
            _lastTestCode = code;
            _lastTestUtc = DateTimeOffset.UtcNow;
            ShowResult($"접속 실패 · {ViewerConnectionMessages.ForCode(code)} ({code})", false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SaveDevice_Click(object sender, RoutedEventArgs e)
    {
        var draft = BuildDraft();
        var passwordRequired = string.IsNullOrWhiteSpace(_editingId);
        if (!ManagedDeviceValidator.TryValidate(draft, passwordRequired, out var reason))
        {
            ShowResult(reason, false);
            return;
        }

        try
        {
            var testMatches = _successfulTestSignature is not null
                              && FixedTimeEquals(_successfulTestSignature, BuildConnectionSignature(draft));
            var failedTestMatches = _failedTestSignature is not null
                                    && FixedTimeEquals(_failedTestSignature, BuildConnectionSignature(draft));
            if (testMatches)
            {
                draft.ConnectionVerified = true;
                draft.LastConnectionTestUtc = _lastTestUtc;
                draft.LastConnectionTestCode = _lastTestCode;
            }
            else if (failedTestMatches)
            {
                draft.ConnectionVerified = false;
                draft.LastConnectionTestUtc = _lastTestUtc;
                draft.LastConnectionTestCode = _lastTestCode;
                draft.MonitoringEnabled = false;
            }
            else if (_editingId is not null)
            {
                var existing = _devices.FirstOrDefault(item => item.Id == _editingId);
                string existingUsername;
                try
                {
                    existingUsername = existing is null
                        ? string.Empty
                        : _dashboard.GetManagedDeviceDraft(existing.Id).Username;
                }
                catch (Exception exception)
                {
                    ShowOperationFailure(
                        "device-management-load",
                        exception,
                        DeviceManagementOperation.Load);
                    return;
                }
                var connectionChanged = existing is null
                                        || !existing.Host.Equals(draft.Host.Trim(), StringComparison.Ordinal)
                                        || !existing.Model.Equals(draft.Model.Trim(), StringComparison.OrdinalIgnoreCase)
                                        || !existingUsername.Equals(draft.Username.Trim(), StringComparison.Ordinal)
                                        || !string.IsNullOrEmpty(draft.Password)
                                        || !string.IsNullOrEmpty(draft.EnablePassword)
                                        || draft.ClearEnablePassword;
                draft.ConnectionVerified = !connectionChanged && existing?.ConnectionVerified == true;
                draft.LastConnectionTestUtc = existing?.LastConnectionTestUtc;
                draft.LastConnectionTestCode = existing?.LastConnectionTestCode;
            }
            draft.MonitoringEnabled = draft.ConnectionVerified && MonitoringCheckBox.IsChecked == true;

            var saved = _dashboard.SaveManagedDevice(draft, out var warningCode);
            _editingId = saved.Id;
            try
            {
                Reload(saved.Id);
            }
            catch (Exception exception)
            {
                var code = DeviceManagementFailureMapper.ToErrorCode(
                    exception,
                    DeviceManagementOperation.Load);
                _dashboard.ReportDeviceManagementFailure(
                    "device-management-load",
                    code);
                ShowResult(
                    $"장비는 저장했지만 목록을 다시 불러오지 못했습니다. "
                    + $"{ViewerConnectionMessages.ForCode(code)} ({code})",
                    false);
                return;
            }

            if (warningCode is not null)
            {
                ShowResult(
                    $"장비를 저장했습니다. "
                    + $"{ViewerConnectionMessages.ForCode(warningCode)} ({warningCode})",
                    false);
                return;
            }

            ShowResult(
                saved.ConnectionVerified
                    ? "장비를 저장했습니다."
                    : "장비를 저장했습니다. 접속 시험 전까지 주기 감시는 꺼집니다.",
                true);
        }
        catch (Exception exception)
        {
            ShowOperationFailure(
                "device-management-save",
                exception,
                DeviceManagementOperation.Save);
        }
    }

    private void DeleteDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_editingId is null) return;
        var profile = _devices.FirstOrDefault(item => item.Id == _editingId);
        if (profile is null) return;
        if (MessageBox.Show(
                this,
                $"{profile.DisplayName} 장비와 저장된 계정을 삭제하시겠습니까?\n기존 변경 이력은 보존됩니다.",
                "장비 삭제",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        DeleteConfirmed(profile);
    }

    internal bool DeleteConfirmed(ManagedDeviceProfile profile)
    {
        try
        {
            if (!_dashboard.DeleteManagedDevice(profile.Id))
            {
                ShowOperationFailure(
                    "device-management-delete",
                    new KeyNotFoundException("VIEWER_DEVICE_NOT_FOUND"),
                    DeviceManagementOperation.Delete);
                return false;
            }

            Reload();
            BeginNewDevice();
            return true;
        }
        catch (Exception exception)
        {
            ShowOperationFailure(
                "device-management-delete",
                exception,
                DeviceManagementOperation.Delete);
            return false;
        }
    }

    private ManagedDeviceDraft BuildDraft() => new()
    {
        Id = _editingId,
        DisplayName = DisplayNameTextBox.Text,
        Model = ModelComboBox.SelectedItem as string ?? string.Empty,
        Host = HostTextBox.Text,
        Username = UsernameTextBox.Text,
        Password = PasswordBox.Password,
        EnablePassword = EnablePasswordBox.Password,
        ClearEnablePassword = ClearEnablePasswordCheckBox.IsChecked == true,
        MonitoringEnabled = MonitoringCheckBox.IsChecked == true
    };

    private bool TryValidateForOperation(ManagedDeviceDraft draft, out string reason)
    {
        try
        {
            var existing = _editingId is not null
                           && _devices.Any(item => item.Id == _editingId);
            return ManagedDeviceValidator.TryValidate(draft, !existing, out reason);
        }
        catch
        {
            reason = "입력값을 확인해 주세요.";
            return false;
        }
    }

    private static string BuildConnectionSignature(ManagedDeviceDraft draft)
    {
        var material = string.Join('\n',
            draft.Host.Trim(),
            draft.Model.Trim().ToUpperInvariant(),
            draft.Username.Trim(),
            draft.Password,
            draft.EnablePassword,
            draft.ClearEnablePassword);
        var bytes = Encoding.UTF8.GetBytes(material);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        TestButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy && _editingId is not null;
        DeviceList.IsEnabled = !busy;
        if (text is not null) ResultText.Text = text;
    }

    private void ShowResult(string message, bool success)
    {
        ResultText.Text = message;
        ResultText.Foreground = success
            ? System.Windows.Media.Brushes.DarkGreen
            : System.Windows.Media.Brushes.Firebrick;
    }

    private void ShowOperationFailure(
        string stage,
        Exception exception,
        DeviceManagementOperation operation)
    {
        if (DeviceManagementFailureMapper.TryGetValidationMessage(
                exception,
                out var validationMessage))
        {
            ShowResult(validationMessage, false);
            return;
        }

        var code = DeviceManagementFailureMapper.ToErrorCode(exception, operation);
        var message = $"{ViewerConnectionMessages.ForCode(code)} ({code})";
        _dashboard.ReportDeviceManagementFailure(stage, code);
        ShowResult(message, false);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
