using System.Net;
using SamsungSwitchWatch.Agent.Domain;

namespace SamsungSwitchWatch.Agent.Configuration;

public sealed class AgentOptions
{
    public const int MaximumSwitches = 256;
    public const int MaximumConcurrentDeviceLimit = 16;
    public static readonly IReadOnlySet<string> SupportedModels = new HashSet<string>(
        ["IES4224GP", "IES4028XP", "IES4226XP"], StringComparer.OrdinalIgnoreCase);

    public const string SectionName = "Agent";

    public string AgentId { get; set; } = "agent-poc-01";
    public string ListenUrl { get; set; } = "http://0.0.0.0:18443";
    public string DataDirectory { get; set; } = "data";
    public bool MockMode { get; set; } = true;
    public bool EnablePolling { get; set; } = true;
    public bool EnableSimulator { get; set; } = true;
    public int SchedulerTickSeconds { get; set; } = 1;
    public int MaxConcurrentDevices { get; set; } = 4;
    public TelnetSessionOptions Telnet { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public List<SwitchOptions> Switches { get; set; } = [];

    public string DatabasePath => Path.Combine(Path.GetFullPath(DataDirectory), "switchwatch.db");
}

public sealed class TelnetSessionOptions
{
    public int MaxSessionSeconds { get; set; } = 240;

    public int ImmediateSessionCloseRetryCount { get; set; } = 1;

    public int ImmediateSessionCloseRetryDelaySeconds { get; set; } = 2;
}

public sealed class RetentionOptions
{
    public int RawDays { get; set; } = 7;
    public int RawMaxMegabytes { get; set; } = 500;
    public int EventDays { get; set; } = 90;
    public int AuditDays { get; set; } = 180;
}

public sealed class SwitchOptions
{
    public string Id { get; set; } = "TEST-SW-01";
    public string DisplayName { get; set; } = "Samsung access switch POC";
    public string Model { get; set; } = "IES4224GP";
    public string Host { get; set; } = "192.0.2.10";
    public int Port { get; set; } = 23;
    public string CredentialId { get; set; } = "test-switch-readonly";
    public string UplinkPort { get; set; } = "24";
}

public static class AgentOptionsValidator
{
    public static void ValidateAndNormalize(AgentOptions options, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Switches is null || options.Telnet is null || options.Retention is null)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Agent settings are invalid.");
        }

        if (options.Switches.Count is < 1 or > AgentOptions.MaximumSwitches)
        {
            throw new AgentConfigurationException("CONFIG_INVALID",
                $"Between 1 and {AgentOptions.MaximumSwitches} switches must be configured.");
        }

        if (!IsIdentifier(options.AgentId, 64))
        {
            throw new AgentConfigurationException("CONFIG_INVALID",
                "Agent id must contain only letters, digits, hyphen, or underscore.");
        }

        if (options.MaxConcurrentDevices is < 1 or > AgentOptions.MaximumConcurrentDeviceLimit)
        {
            throw new AgentConfigurationException("CONFIG_INVALID",
                $"MaxConcurrentDevices must be between 1 and {AgentOptions.MaximumConcurrentDeviceLimit}.");
        }

        if (options.Telnet.MaxSessionSeconds is < 120 or > 240 ||
            options.Telnet.ImmediateSessionCloseRetryCount is < 0 or > 1 ||
            options.Telnet.ImmediateSessionCloseRetryDelaySeconds is < 1 or > 10)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Telnet session recovery settings are invalid.");
        }

        if (options.Retention.RawDays is < 1 or > 30 ||
            options.Retention.RawMaxMegabytes is < 10 or > 10_240 ||
            options.Retention.EventDays is < 1 or > 3650 ||
            options.Retention.AuditDays is < 1 or > 3650)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Retention settings are invalid.");
        }

        var duplicateId = options.Switches
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId is not null)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Switch device ids must be unique.");
        }

        foreach (var device in options.Switches)
        {
            if (!AgentOptions.SupportedModels.Contains(device.Model))
            {
                throw new AgentConfigurationException("CONFIG_INVALID",
                    $"Supported switch models are: {string.Join(", ", AgentOptions.SupportedModels.Order())}.");
            }

            if (!IsIdentifier(device.Id, 64))
            {
                throw new AgentConfigurationException("CONFIG_INVALID",
                    "Device id must contain only letters, digits, hyphen, or underscore.");
            }

            if (!IsIdentifier(device.CredentialId, 64))
            {
                throw new AgentConfigurationException("CONFIG_INVALID",
                    "Credential id must contain only letters, digits, hyphen, or underscore.");
            }

            if (string.IsNullOrWhiteSpace(device.DisplayName) || device.DisplayName.Length > 128 ||
                device.DisplayName.Any(char.IsControl))
            {
                throw new AgentConfigurationException("CONFIG_INVALID", "Switch display name is invalid.");
            }

            if (string.IsNullOrWhiteSpace(device.UplinkPort) || device.UplinkPort.Length > 32 ||
                device.UplinkPort.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '/' and not '.'))
            {
                throw new AgentConfigurationException("CONFIG_INVALID", "Uplink port identifier is invalid.");
            }

            if (IPAddress.TryParse(device.Host, out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                throw new AgentConfigurationException(AgentErrorCodes.Ipv6Unsupported,
                    "Only IPv4 switch addresses are supported.");
            }

            if (address is null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                device.Port is < 1 or > 65535)
            {
                throw new AgentConfigurationException("CONFIG_INVALID", "A valid IPv4 switch endpoint is required.");
            }
        }

        if (!Uri.TryCreate(options.ListenUrl, UriKind.Absolute, out var listenUri) ||
            listenUri.Scheme != Uri.UriSchemeHttp ||
            (!options.MockMode && listenUri.Port == 0) ||
            !string.IsNullOrEmpty(listenUri.UserInfo) ||
            !string.IsNullOrEmpty(listenUri.Query) ||
            !string.IsNullOrEmpty(listenUri.Fragment) ||
            listenUri.AbsolutePath != "/")
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Agent listen URL must be an HTTP origin.");
        }

        options.DataDirectory = Path.IsPathRooted(options.DataDirectory)
            ? Path.GetFullPath(options.DataDirectory)
            : Path.GetFullPath(Path.Combine(contentRoot, options.DataDirectory));
        Directory.CreateDirectory(options.DataDirectory);
    }

    private static bool IsIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

}

public sealed class AgentConfigurationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
