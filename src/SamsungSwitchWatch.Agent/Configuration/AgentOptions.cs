using System.Net;

namespace SamsungSwitchWatch.Agent.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string AgentId { get; set; } = "agent-poc-01";
    public string ListenUrl { get; set; } = "http://127.0.0.1:18443";
    public string DataDirectory { get; set; } = "data";
    public bool MockMode { get; set; } = true;
    public bool EnablePolling { get; set; } = true;
    public bool EnableSimulator { get; set; } = true;
    public bool AllowRemotePairingBootstrap { get; set; }
    public int SchedulerTickSeconds { get; set; } = 1;
    public int PairingCodeLifetimeMinutes { get; set; } = 10;
    public string TokenPepper { get; set; } = "change-this-local-secret-before-production";
    public RetentionOptions Retention { get; set; } = new();
    public HttpsOptions Https { get; set; } = new();
    public List<SwitchOptions> Switches { get; set; } = [];

    public string DatabasePath => Path.Combine(Path.GetFullPath(DataDirectory), "switchwatch.db");
}

public sealed class HttpsOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 18443;
    public string CertificatePath { get; set; } = "certs/agent.pfx";
    public string CertificatePasswordEnvironmentVariable { get; set; } = "SAMSUNG_SWITCH_WATCH_CERT_PASSWORD";
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

        if (options.Switches.Count != 1)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "The POC requires exactly one configured switch.");
        }

        var device = options.Switches[0];
        if (!string.Equals(device.Model, "IES4224GP", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "The POC supports only model IES4224GP.");
        }

        if (string.IsNullOrWhiteSpace(device.Id) || device.Id.Length > 64 ||
            device.Id.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Device id must contain only letters, digits, hyphen, or underscore.");
        }

        if ((!IPAddress.TryParse(device.Host, out _) && Uri.CheckHostName(device.Host) == UriHostNameType.Unknown) ||
            device.Port is < 1 or > 65535)
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "The configured switch endpoint is invalid.");
        }

        if (!Uri.TryCreate(options.ListenUrl, UriKind.Absolute, out var listenUri) ||
            (listenUri.Scheme != Uri.UriSchemeHttp && listenUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AgentConfigurationException("CONFIG_INVALID", "Agent listen URL is invalid.");
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

            options.Https.CertificatePath = Path.IsPathRooted(options.Https.CertificatePath)
                ? Path.GetFullPath(options.Https.CertificatePath)
                : Path.GetFullPath(Path.Combine(contentRoot, options.Https.CertificatePath));
            if (!File.Exists(options.Https.CertificatePath))
            {
                throw new AgentConfigurationException("CONFIG_INVALID", "HTTPS certificate file does not exist.");
            }
        }

        options.DataDirectory = Path.IsPathRooted(options.DataDirectory)
            ? Path.GetFullPath(options.DataDirectory)
            : Path.GetFullPath(Path.Combine(contentRoot, options.DataDirectory));
        Directory.CreateDirectory(options.DataDirectory);
    }
}

public sealed class AgentConfigurationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
