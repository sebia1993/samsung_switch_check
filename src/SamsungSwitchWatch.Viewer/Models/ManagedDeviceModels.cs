using System.Net;
using System.Net.Sockets;
using SamsungSwitchWatch.Core.Profiles;
using System.Text.Json.Serialization;

namespace SamsungSwitchWatch.Viewer.Models;

public static class SupportedSwitchModels
{
    public static IReadOnlyList<string> All { get; } =
    [
        "IES4224GP",
        "IES4028XP",
        "IES4226XP"
    ];

    public static bool Contains(string? model) =>
        All.Contains(model?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

public sealed class ManagedDeviceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string Model { get; set; } = SupportedSwitchModels.All[0];
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 23;
    public string ProtectedUsername { get; set; } = string.Empty;
    [JsonPropertyName("Username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyUsername { get; set; }
    public string ProtectedPassword { get; set; } = string.Empty;
    public string? ProtectedEnablePassword { get; set; }
    public bool MonitoringEnabled { get; set; }
    public bool ConnectionVerified { get; set; }
    public DateTimeOffset? LastConnectionTestUtc { get; set; }
    public string? LastConnectionTestCode { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool HasEnablePassword => !string.IsNullOrWhiteSpace(ProtectedEnablePassword);

    public ManagedDeviceProfile Copy() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Model = Model,
        Host = Host,
        Port = Port,
        ProtectedUsername = ProtectedUsername,
        ProtectedPassword = ProtectedPassword,
        ProtectedEnablePassword = ProtectedEnablePassword,
        MonitoringEnabled = MonitoringEnabled,
        ConnectionVerified = ConnectionVerified,
        LastConnectionTestUtc = LastConnectionTestUtc,
        LastConnectionTestCode = LastConnectionTestCode,
        UpdatedUtc = UpdatedUtc
    };
}

public sealed class ManagedDeviceDraft
{
    public string? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Model { get; set; } = SupportedSwitchModels.All[0];
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string EnablePassword { get; set; } = string.Empty;
    public bool ClearEnablePassword { get; set; }
    public bool MonitoringEnabled { get; set; }
    public bool ConnectionVerified { get; set; }
    public DateTimeOffset? LastConnectionTestUtc { get; set; }
    public string? LastConnectionTestCode { get; set; }
}

public sealed record ManagedDeviceSecrets(string Username, string Password, string? EnablePassword);

public sealed record TelnetTargetDto(
    string RequestId,
    string Host,
    int Port,
    string Model,
    string Username,
    string Password,
    string? EnablePassword,
    string Purpose);

public sealed record TelnetExecuteRequestDto(
    string RequestId,
    string Host,
    int Port,
    string Model,
    string Username,
    string Password,
    string? EnablePassword,
    string Purpose,
    IReadOnlyList<string> Commands);

public sealed record TelnetCommandOutputDto(
    string Command,
    string Output,
    bool Truncated,
    DateTimeOffset CollectedUtc);

public sealed record TelnetExecutionResultDto(
    int ApiVersion,
    string RequestId,
    bool Success,
    string Privilege,
    string PromptTerminator,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    long DurationMs,
    IReadOnlyList<TelnetCommandOutputDto> Commands)
{
    public int SessionCount { get; init; } = 1;

    public int ReconnectCount { get; init; }
}

public sealed record AgentIdentityDto(
    int ApiVersion,
    string AgentId,
    string InstanceId,
    string CertificatePublicKeySha256,
    string Protocol,
    int MaxCommandsPerRequest,
    int MaxOutputBytes);

public static class ManagedDeviceValidator
{
    public static bool TryValidate(ManagedDeviceDraft draft, bool passwordRequired, out string reason)
    {
        if (string.IsNullOrWhiteSpace(draft.DisplayName) || draft.DisplayName.Trim().Length > 80)
        {
            reason = "장비명은 1~80자로 입력해 주세요.";
            return false;
        }
        if (!SupportedSwitchModels.Contains(draft.Model))
        {
            reason = "지원되는 삼성 스위치 모델을 선택해 주세요.";
            return false;
        }
        if (!IPAddress.TryParse(draft.Host?.Trim(), out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            reason = "장비 주소는 IPv4 형식으로 입력해 주세요.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(draft.Username) || draft.Username.Trim().Length > 128)
        {
            reason = "계정 ID를 입력해 주세요.";
            return false;
        }
        if (passwordRequired && string.IsNullOrEmpty(draft.Password))
        {
            reason = "로그인 비밀번호를 입력해 주세요.";
            return false;
        }
        if (draft.Password.Length > 256 || draft.EnablePassword.Length > 256)
        {
            reason = "비밀번호 길이가 너무 깁니다.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool IsSingleShowCommand(string? command, int maxLength = 128) =>
        ReadOnlyQueryPolicy.Validate(command, Math.Clamp(maxLength, 1, ReadOnlyQueryPolicy.MaximumCommandLength))
            .IsAllowed;
}
