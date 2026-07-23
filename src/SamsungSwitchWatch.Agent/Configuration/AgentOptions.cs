using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Core.Profiles;

namespace SamsungSwitchWatch.Agent.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";
    public const int MaximumConcurrentExecutionLimit = 16;
    public const int MaximumCommandsPerRequestLimit = 8;
    public static readonly IReadOnlySet<string> SupportedModels = new HashSet<string>(
        ["IES4224GP", "IES4028XP", "IES4226XP"], StringComparer.OrdinalIgnoreCase);

    public string AgentId { get; set; } = "agent-poc-01";
    public string ListenUrl { get; set; } = "https://0.0.0.0:18443";
    public string DataDirectory { get; set; } = "data";
    public bool MockMode { get; set; }
    public List<string> AllowedTargetCidrs { get; set; } = [];
    public int MaxConcurrentExecutions { get; set; } = 2;
    public int RateLimitPerMinute { get; set; } = 60;
    public int MaxCommandsPerRequest { get; set; } = 8;
    public int MaxCommandLength { get; set; } = ReadOnlyQueryPolicy.MaximumCommandLength;
    public int MaxOutputBytes { get; set; } = ReadOnlyQueryPolicy.MaximumOutputBytes;
    public int MaxRequestBodyBytes { get; set; } = 32 * 1024;
    public TelnetSessionOptions Telnet { get; set; } = new();

    // Configuration binding ignores v0.7 polling, device, credential, and
    // retention keys during an in-place upgrade. They are intentionally not
    // represented here so no legacy state can enter the v0.9 runtime.
}

public sealed class TelnetSessionOptions
{
    public int MaxSessionSeconds { get; set; } = 240;
    public int ImmediateSessionCloseRetryCount { get; set; } = 1;
    public int ImmediateSessionCloseRetryDelaySeconds { get; set; } = 2;
}

public static class AgentOptionsValidator
{
    public static void ValidateAndNormalize(AgentOptions options, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsIdentifier(options.AgentId, 64))
        {
            throw new AgentConfigurationException(
                AgentErrorCodes.ConfigurationInvalid,
                "Agent id must contain only letters, digits, hyphen, or underscore.");
        }

        if (options.Telnet is null ||
            options.Telnet.MaxSessionSeconds is < 30 or > 240 ||
            options.Telnet.ImmediateSessionCloseRetryCount is < 0 or > 1 ||
            options.Telnet.ImmediateSessionCloseRetryDelaySeconds is < 1 or > 10)
        {
            throw new AgentConfigurationException(
                AgentErrorCodes.ConfigurationInvalid,
                "Telnet session settings are invalid.");
        }

        if (options.MaxConcurrentExecutions is < 1 or > AgentOptions.MaximumConcurrentExecutionLimit ||
            options.RateLimitPerMinute is < 1 or > 120 ||
            options.MaxCommandsPerRequest is < 1 or > AgentOptions.MaximumCommandsPerRequestLimit ||
            options.MaxCommandLength is < 16 or > ReadOnlyQueryPolicy.MaximumCommandLength ||
            options.MaxOutputBytes is < 1024 or > ReadOnlyQueryPolicy.MaximumOutputBytes ||
            options.MaxRequestBodyBytes is < 4096 or > 64 * 1024)
        {
            throw new AgentConfigurationException(
                AgentErrorCodes.ConfigurationInvalid,
                "Stateless execution limits are invalid.");
        }

        if (options.AllowedTargetCidrs is null || options.AllowedTargetCidrs.Count == 0)
        {
            throw new AgentConfigurationException(
                AgentErrorCodes.ConfigurationInvalid,
                "At least one allowed IPv4 target CIDR is required.");
        }

        if (options.AllowedTargetCidrs.Any(cidr => !Ipv4Cidr.TryParse(cidr, out _)))
        {
            throw new AgentConfigurationException(
                AgentErrorCodes.ConfigurationInvalid,
                "Allowed target CIDRs must use canonical IPv4 CIDR notation.");
        }

        if (!Uri.TryCreate(options.ListenUrl, UriKind.Absolute, out var listenUri) ||
            !string.IsNullOrEmpty(listenUri.UserInfo) ||
            !string.IsNullOrEmpty(listenUri.Query) ||
            !string.IsNullOrEmpty(listenUri.Fragment) ||
            listenUri.AbsolutePath != "/" ||
            (!options.MockMode && listenUri.Scheme != Uri.UriSchemeHttps) ||
            (options.MockMode &&
             listenUri.Scheme == Uri.UriSchemeHttp &&
             !IsLoopbackHost(listenUri.Host)) ||
            (listenUri.Scheme != Uri.UriSchemeHttps && listenUri.Scheme != Uri.UriSchemeHttp) ||
            (!options.MockMode && listenUri.Port != 18443))
        {
            throw new AgentConfigurationException(
                AgentErrorCodes.ConfigurationInvalid,
                "Agent must listen on HTTPS TCP/18443; mock HTTP is restricted to loopback.");
        }

        options.DataDirectory = Path.IsPathRooted(options.DataDirectory)
            ? Path.GetFullPath(options.DataDirectory)
            : Path.GetFullPath(Path.Combine(contentRoot, options.DataDirectory));
        Directory.CreateDirectory(options.DataDirectory);
    }

    private static bool IsIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

public readonly record struct Ipv4Cidr(uint Network, int PrefixLength)
{
    public static bool TryParse(string? value, out Ipv4Cidr cidr)
    {
        cidr = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var pieces = value.Split('/');
        if (pieces.Length != 2 ||
            !TryParseStrictAddress(pieces[0], out var address) ||
            !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
            prefix is < 0 or > 32)
        {
            return false;
        }

        var mask = PrefixMask(prefix);
        var network = ToUInt32(address) & mask;
        if (network != ToUInt32(address))
        {
            return false;
        }

        cidr = new Ipv4Cidr(network, prefix);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var mask = PrefixMask(PrefixLength);
        return (ToUInt32(address) & mask) == Network;
    }

    public static bool TryParseStrictAddress(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var pieces = value.Split('.');
        if (pieces.Length != 4)
        {
            return false;
        }

        var bytes = new byte[4];
        for (var index = 0; index < pieces.Length; index++)
        {
            var piece = pieces[index];
            if (piece.Length is < 1 or > 3 ||
                piece.Length > 1 && piece[0] == '0' ||
                piece.Any(ch => ch is < '0' or > '9') ||
                !byte.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[index]))
            {
                return false;
            }
        }

        address = new IPAddress(bytes);
        return true;
    }

    public static bool IsForbiddenTarget(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] >= 224;
    }

    private static uint PrefixMask(int prefix) =>
        prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }
}

public sealed class AgentConfigurationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
