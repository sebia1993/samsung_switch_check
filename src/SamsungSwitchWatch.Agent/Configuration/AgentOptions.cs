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
    public string ListenUrl { get; set; } = "http://127.0.0.1:18443";
    public string DataDirectory { get; set; } = "data";
    public bool MockMode { get; set; } = true;
    public bool EnablePolling { get; set; } = true;
    public bool EnableSimulator { get; set; } = true;
    public int SchedulerTickSeconds { get; set; } = 1;
    public int MaxConcurrentDevices { get; set; } = 4;
    public int PairingCodeLifetimeMinutes { get; set; } = 10;
    public string TokenPepper { get; set; } = "change-this-local-secret-before-production";
    public TokenOptions Tokens { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public HttpsOptions Https { get; set; } = new();
    public List<SwitchOptions> Switches { get; set; } = [];

    public string DatabasePath => Path.Combine(Path.GetFullPath(DataDirectory), "switchwatch.db");
}

public sealed class TokenOptions
{
    public const int MaximumActiveTokenLimit = 5;

    public int MaximumActiveTokens { get; set; } = 5;
    public int AbsoluteLifetimeDays { get; set; } = 180;
    public int IdleLifetimeDays { get; set; } = 60;
}

public sealed class HttpsOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 18443;
    public string CertificatePath { get; set; } = "certs/agent.pfx";
    public string CertificatePasswordEnvironmentVariable { get; set; } = "SAMSUNG_SWITCH_WATCH_CERT_PASSWORD";
    public string? CertificateStoreThumbprint { get; set; }
    public string? PreviousCertificateSha256Fingerprint { get; set; }
    public DateTimeOffset? PreviousCertificateAcceptUntilUtc { get; set; }
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

        if (options.Switches is null || options.Tokens is null || options.Retention is null || options.Https is null ||
            string.IsNullOrEmpty(options.TokenPepper) || options.TokenPepper.Length > 1024)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Agent security settings are invalid.");
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

        if (options.PairingCodeLifetimeMinutes is < 1 or > 60)
        {
            throw new AgentConfigurationException("CONFIG_INVALID",
                "PairingCodeLifetimeMinutes must be between 1 and 60.");
        }

        if (options.Tokens.MaximumActiveTokens is < 1 or > TokenOptions.MaximumActiveTokenLimit ||
            options.Tokens.AbsoluteLifetimeDays is < 1 or > 365 ||
            options.Tokens.IdleLifetimeDays is < 1 or > 365 ||
            options.Tokens.IdleLifetimeDays > options.Tokens.AbsoluteLifetimeDays)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Token lifetime settings are invalid.");
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
            (listenUri.Scheme != Uri.UriSchemeHttp && listenUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Agent listen URL is invalid.");
        }

        if (!options.Https.Enabled && listenUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new AgentConfigurationException("CONFIG_INVALID",
                "ListenUrl must use HTTP when the explicit HTTPS endpoint is disabled.");
        }

        if (!options.MockMode && !options.Https.Enabled)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "HTTPS must be enabled outside mock mode.");
        }

        if (!options.MockMode &&
            (options.TokenPepper.Length < 32 ||
             string.Equals(options.TokenPepper, "replace-with-a-long-random-local-value", StringComparison.Ordinal) ||
             string.Equals(options.TokenPepper, "change-this-local-secret-before-production", StringComparison.Ordinal)))
        {
            throw new AgentConfigurationException("CONFIG_INVALID",
                "TokenPepper must be replaced with a unique random value of at least 32 characters outside mock mode.");
        }

        if (options.Https.Enabled)
        {
            if (options.Https.Port is < 1 or > 65535)
            {
                throw new AgentConfigurationException("CONFIG_INVALID", "HTTPS port is invalid.");
            }

            if (!string.IsNullOrWhiteSpace(options.Https.CertificateStoreThumbprint))
            {
                options.Https.CertificateStoreThumbprint = NormalizeHex(
                    options.Https.CertificateStoreThumbprint, 40, "HTTPS certificate store thumbprint");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(options.Https.CertificatePath) ||
                    string.IsNullOrWhiteSpace(options.Https.CertificatePasswordEnvironmentVariable) ||
                    options.Https.CertificatePasswordEnvironmentVariable.Length > 128 ||
                    options.Https.CertificatePasswordEnvironmentVariable.Any(character =>
                        character is '=' or '\0' || char.IsControl(character)))
                {
                    throw new AgentConfigurationException("CONFIG_INVALID",
                        "HTTPS certificate file settings are invalid.");
                }
                options.Https.CertificatePath = Path.IsPathRooted(options.Https.CertificatePath)
                    ? Path.GetFullPath(options.Https.CertificatePath)
                    : Path.GetFullPath(Path.Combine(contentRoot, options.Https.CertificatePath));
                if (!File.Exists(options.Https.CertificatePath))
                {
                    throw new AgentConfigurationException("CONFIG_INVALID", "HTTPS certificate file does not exist.");
                }
            }

            if (!string.IsNullOrWhiteSpace(options.Https.PreviousCertificateSha256Fingerprint))
            {
                options.Https.PreviousCertificateSha256Fingerprint = NormalizeHex(
                    options.Https.PreviousCertificateSha256Fingerprint, 64,
                    "Previous HTTPS certificate SHA-256 fingerprint");
                var now = DateTimeOffset.UtcNow;
                if (options.Https.PreviousCertificateAcceptUntilUtc is null ||
                    options.Https.PreviousCertificateAcceptUntilUtc <= now ||
                    options.Https.PreviousCertificateAcceptUntilUtc > now.AddDays(14))
                {
                    throw new AgentConfigurationException("CONFIG_INVALID",
                        "The previous certificate overlap must have an expiry no more than 14 days away.");
                }
            }
            else if (options.Https.PreviousCertificateAcceptUntilUtc is not null)
            {
                throw new AgentConfigurationException("CONFIG_INVALID",
                    "A previous certificate fingerprint is required for an overlap expiry.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(options.Https.PreviousCertificateSha256Fingerprint) ||
                 options.Https.PreviousCertificateAcceptUntilUtc is not null)
        {
            throw new AgentConfigurationException("CONFIG_INVALID",
                "Certificate pin overlap requires HTTPS to be enabled.");
        }

        options.DataDirectory = Path.IsPathRooted(options.DataDirectory)
            ? Path.GetFullPath(options.DataDirectory)
            : Path.GetFullPath(Path.Combine(contentRoot, options.DataDirectory));
        Directory.CreateDirectory(options.DataDirectory);
    }

    private static bool IsIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    private static string NormalizeHex(string value, int expectedLength, string field)
    {
        if (value.Any(character => !Uri.IsHexDigit(character) &&
                                   character is not ':' and not '-' && !char.IsWhiteSpace(character)))
        {
            throw new AgentConfigurationException("CONFIG_INVALID", $"{field} is invalid.");
        }
        var normalized = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length != expectedLength)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", $"{field} is invalid.");
        }
        return normalized;
    }
}

public sealed class AgentConfigurationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
