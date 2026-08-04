namespace SamsungSwitchWatch.Agent.Setup.Deployment;

internal static class FirewallRuleMismatchCodes
{
    public const string Missing = "FIREWALL_RULE_MISSING";
    public const string Disabled = "FIREWALL_RULE_DISABLED";
    public const string Direction = "FIREWALL_DIRECTION_MISMATCH";
    public const string Action = "FIREWALL_ACTION_MISMATCH";
    public const string Protocol = "FIREWALL_PROTOCOL_MISMATCH";
    public const string LocalPort = "FIREWALL_PORT_MISMATCH";
    public const string RemoteAddress = "FIREWALL_REMOTE_ADDRESS_MISMATCH";
    public const string Profiles = "FIREWALL_PROFILE_MISMATCH";
    public const string EdgeTraversal = "FIREWALL_EDGE_TRAVERSAL_MISMATCH";
}

internal readonly record struct FirewallRuleVerificationResult(
    bool IsExact,
    string? MismatchCode)
{
    public static FirewallRuleVerificationResult Exact { get; } = new(true, null);

    public static FirewallRuleVerificationResult Mismatch(string code) =>
        new(false, code);
}

internal static class FirewallRuleVerifier
{
    private const int NetFwProfileDomainAndPrivate = 3;
    private const int NetFwRuleDirectionIn = 1;
    private const int NetFwActionAllow = 1;
    private const int TcpProtocol = 6;

    public static FirewallRuleVerificationResult Evaluate(
        FirewallRuleSnapshot snapshot,
        int port,
        string viewerIpv4)
    {
        if (!snapshot.Exists)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Missing);
        }

        if (!snapshot.Enabled)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Disabled);
        }

        if (snapshot.Direction != NetFwRuleDirectionIn)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Direction);
        }

        if (snapshot.Action != NetFwActionAllow)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Action);
        }

        if (snapshot.Protocol != TcpProtocol)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Protocol);
        }

        if (!string.Equals(
                snapshot.LocalPorts,
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.LocalPort);
        }

        if (!IsExactSingleViewerAddress(snapshot.RemoteAddresses, viewerIpv4))
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.RemoteAddress);
        }

        if (snapshot.Profiles != NetFwProfileDomainAndPrivate)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Profiles);
        }

        if (snapshot.EdgeTraversal)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.EdgeTraversal);
        }

        return FirewallRuleVerificationResult.Exact;
    }

    public static FirewallRuleVerificationResult EvaluatePrivateNetworks(
        FirewallRuleSnapshot snapshot,
        int port)
    {
        var structural = EvaluateStructure(snapshot, port);
        if (!structural.IsExact)
        {
            return structural;
        }

        if (!HasExactPrivateNetworkSet(snapshot.RemoteAddresses))
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.RemoteAddress);
        }

        return FirewallRuleVerificationResult.Exact;
    }

    private static FirewallRuleVerificationResult EvaluateStructure(
        FirewallRuleSnapshot snapshot,
        int port)
    {
        if (!snapshot.Exists)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Missing);
        }

        if (!snapshot.Enabled)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Disabled);
        }

        if (snapshot.Direction != NetFwRuleDirectionIn)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Direction);
        }

        if (snapshot.Action != NetFwActionAllow)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Action);
        }

        if (snapshot.Protocol != TcpProtocol)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Protocol);
        }

        if (!string.Equals(
                snapshot.LocalPorts,
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.LocalPort);
        }

        if (snapshot.Profiles != NetFwProfileDomainAndPrivate)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.Profiles);
        }

        if (snapshot.EdgeTraversal)
        {
            return FirewallRuleVerificationResult.Mismatch(
                FirewallRuleMismatchCodes.EdgeTraversal);
        }

        return FirewallRuleVerificationResult.Exact;
    }

    private static bool HasExactPrivateNetworkSet(string? remoteAddresses)
    {
        if (string.IsNullOrWhiteSpace(remoteAddresses))
        {
            return false;
        }

        var actual = remoteAddresses
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePrivateNetworkToken)
            .ToArray();
        return actual.All(value => value is not null) &&
               actual.Length == SetupConstants.PrivateNetworkTargetCidrs.Count &&
               actual!
                   .Cast<string>()
                   .ToHashSet(StringComparer.Ordinal)
                   .SetEquals(SetupConstants.PrivateNetworkTargetCidrs);
    }

    private static string? NormalizePrivateNetworkToken(string value)
    {
        var normalized = value switch
        {
            "10.0.0.0/255.0.0.0" => "10.0.0.0/8",
            "172.16.0.0/255.240.0.0" => "172.16.0.0/12",
            "192.168.0.0/255.255.0.0" => "192.168.0.0/16",
            _ => value
        };
        return Ipv4Input.TryNormalizePrivateCidr(normalized, out var canonical)
            ? canonical
            : null;
    }

    private static bool IsExactSingleViewerAddress(
        string? remoteAddresses,
        string viewerIpv4)
    {
        if (!Ipv4Input.TryParseStrict(viewerIpv4, out var expected) ||
            string.IsNullOrWhiteSpace(remoteAddresses))
        {
            return false;
        }

        var value = remoteAddresses.Trim();
        if (value.Contains(',') ||
            value.Contains(';') ||
            value.Contains('-'))
        {
            return false;
        }

        var pieces = value.Split('/');
        if (pieces.Length is < 1 or > 2 ||
            pieces[0].Length == 0 ||
            pieces[0] != pieces[0].Trim() ||
            !Ipv4Input.TryParseStrict(pieces[0], out var actual) ||
            !actual.Equals(expected))
        {
            return false;
        }

        if (pieces.Length == 1)
        {
            return true;
        }

        return pieces[1] == "32" ||
               pieces[1] == "255.255.255.255";
    }
}
