using System.Runtime.InteropServices;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class FirewallRuleVerifierTests
{
    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("192.168.1.20/32")]
    [InlineData("192.168.1.20/255.255.255.255")]
    public void Evaluate_AcceptsOnlyEquivalentSingleHostRepresentations(
        string remoteAddresses)
    {
        var result = FirewallRuleVerifier.Evaluate(
            ExactRule(remoteAddresses),
            SetupConstants.HttpsPort,
            "192.168.1.20");

        Assert.True(result.IsExact);
        Assert.Null(result.MismatchCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Any")]
    [InlineData("LocalSubnet")]
    [InlineData("192.168.1.20/31")]
    [InlineData("192.168.1.0/24")]
    [InlineData("192.168.1.20/255.255.255.0")]
    [InlineData("192.168.1.20,192.168.1.21")]
    [InlineData("192.168.1.20-192.168.1.21")]
    [InlineData("192.168.1.20/32,10.0.0.1/32")]
    [InlineData("2001:db8::20/128")]
    [InlineData("192.168.1.21")]
    [InlineData("192.168.1.20/032")]
    [InlineData("192.168.1.20 /32")]
    [InlineData("192.168.1.20/ 32")]
    public void Evaluate_RejectsBroaderMultipleInvalidAndDifferentScopes(
        string remoteAddresses)
    {
        var result = FirewallRuleVerifier.Evaluate(
            ExactRule(remoteAddresses),
            SetupConstants.HttpsPort,
            "192.168.1.20");

        Assert.False(result.IsExact);
        Assert.Equal(
            FirewallRuleMismatchCodes.RemoteAddress,
            result.MismatchCode);
    }

    public static TheoryData<FirewallRuleSnapshot, string> OtherFieldMismatches =>
        new()
        {
            {
                ExactRule("192.168.1.20/32") with { Exists = false },
                FirewallRuleMismatchCodes.Missing
            },
            {
                ExactRule("192.168.1.20/32") with { Enabled = false },
                FirewallRuleMismatchCodes.Disabled
            },
            {
                ExactRule("192.168.1.20/32") with { Direction = 2 },
                FirewallRuleMismatchCodes.Direction
            },
            {
                ExactRule("192.168.1.20/32") with { Action = 0 },
                FirewallRuleMismatchCodes.Action
            },
            {
                ExactRule("192.168.1.20/32") with { Protocol = 17 },
                FirewallRuleMismatchCodes.Protocol
            },
            {
                ExactRule("192.168.1.20/32") with { LocalPorts = "18443,18444" },
                FirewallRuleMismatchCodes.LocalPort
            },
            {
                ExactRule("192.168.1.20/32") with { Profiles = 7 },
                FirewallRuleMismatchCodes.Profiles
            },
            {
                ExactRule("192.168.1.20/32") with { EdgeTraversal = true },
                FirewallRuleMismatchCodes.EdgeTraversal
            }
        };

    [Theory]
    [MemberData(nameof(OtherFieldMismatches))]
    public void Evaluate_KeepsAllOtherSecurityFieldsStrict(
        FirewallRuleSnapshot snapshot,
        string expectedMismatch)
    {
        var result = FirewallRuleVerifier.Evaluate(
            snapshot,
            SetupConstants.HttpsPort,
            "192.168.1.20");

        Assert.False(result.IsExact);
        Assert.Equal(expectedMismatch, result.MismatchCode);
    }

    [Fact]
    public void Evaluate_AcceptsWindowsFirewallComDottedMaskReadbackWithoutRegisteringRule()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
        Assert.NotNull(ruleType);
        object? ruleObject = null;
        try
        {
            ruleObject = Activator.CreateInstance(ruleType);
            Assert.NotNull(ruleObject);
            dynamic rule = ruleObject;
            rule.RemoteAddresses = "192.168.1.20/32";
            var readback = (string)rule.RemoteAddresses;

            Assert.Equal(
                "192.168.1.20/255.255.255.255",
                readback);
            Assert.True(
                FirewallRuleVerifier.Evaluate(
                    ExactRule(readback),
                    SetupConstants.HttpsPort,
                    "192.168.1.20").IsExact);
        }
        finally
        {
            if (ruleObject is not null && Marshal.IsComObject(ruleObject))
            {
                Marshal.FinalReleaseComObject(ruleObject);
            }
        }
    }

    private static FirewallRuleSnapshot ExactRule(string remoteAddresses) =>
        new(
            true,
            SetupConstants.FirewallRuleName,
            "Owned by SamsungSwitchWatchAgent native setup v1",
            true,
            1,
            1,
            6,
            SetupConstants.HttpsPort.ToString(),
            remoteAddresses,
            3,
            "All",
            false,
            string.Empty);
}
