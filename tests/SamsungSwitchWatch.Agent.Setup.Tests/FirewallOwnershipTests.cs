using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class FirewallOwnershipTests
{
    [Theory]
    [InlineData(
        "SamsungSwitchWatchAgent-Https",
        "Owned by SamsungSwitchWatchAgent native setup v1")]
    [InlineData(
        "SamsungSwitchWatchAgent-Https",
        "Owned by SamsungSwitchWatchAgent installer v3")]
    [InlineData(
        "SamsungSwitchWatchAgent-Https",
        "Owned by SamsungSwitchWatchAgent installer v1")]
    [InlineData(
        "SamsungSwitchWatchAgent-Http",
        "Owned by SamsungSwitchWatchAgent installer v2")]
    public void IsOwnedRule_AcceptsOnlyKnownInstallerContracts(
        string name,
        string description)
    {
        Assert.True(WindowsFirewallManager.IsOwnedRule(Rule(name, description)));
    }

    [Theory]
    [InlineData("SamsungSwitchWatchAgent-Https", "Company managed rule")]
    [InlineData("SamsungSwitchWatchAgent-Http", "")]
    [InlineData("DifferentRule", "Owned by SamsungSwitchWatchAgent installer v3")]
    public void IsOwnedRule_RejectsSameNameOrDescriptionWithoutFullContract(
        string name,
        string description)
    {
        Assert.False(WindowsFirewallManager.IsOwnedRule(Rule(name, description)));
    }

    [Fact]
    public void IsOwnedRule_RecognizesVerifiedLegacyPowerShellFriendlyRule()
    {
        var snapshot = Rule(
            WindowsFirewallManager.LegacyPowerShellHttpsFriendlyName,
            "Owned by SamsungSwitchWatchAgent installer v3") with
        {
            Grouping = WindowsFirewallManager.LegacyPowerShellGroup
        };

        Assert.True(WindowsFirewallManager.IsOwnedRule(snapshot));
        Assert.Equal(
            FirewallRuleDisposition.Owned,
            WindowsFirewallManager.ClassifyRuleForSecurity(
                snapshot,
                18443,
                3,
                AgentPath));
    }

    [Theory]
    [InlineData("", "", "18443")]
    [InlineData("Wrong Group", "", "18443")]
    [InlineData("Samsung Switch Watch", @"C:\Other\server.exe", "18443")]
    [InlineData("Samsung Switch Watch", "", "443")]
    public void IsOwnedRule_RejectsFriendlyRuleWithoutFullOwnershipSignature(
        string group,
        string application,
        string localPorts)
    {
        var snapshot = Rule(
            WindowsFirewallManager.LegacyPowerShellHttpsFriendlyName,
            "Owned by SamsungSwitchWatchAgent installer v3") with
        {
            Grouping = group,
            ApplicationName = application,
            LocalPorts = localPorts
        };

        Assert.False(WindowsFirewallManager.IsOwnedRule(snapshot));
    }

    [Fact]
    public void ClassifyRuleForSecurity_GenericOverlapReturnsWarningDisposition()
    {
        var snapshot = Rule(
            "Company TCP 18443",
            "Company managed rule");

        Assert.Equal(
            FirewallRuleDisposition.ExternalOverlap,
            WindowsFirewallManager.ClassifyRuleForSecurity(
                snapshot,
                18443,
                3,
                AgentPath));
    }

    [Fact]
    public void ClassifyRuleForSecurity_NonOwnedNativeNameIsHardCollision()
    {
        var snapshot = Rule(
            SetupConstants.FirewallRuleName,
            "Company managed rule") with
        {
            Enabled = false,
            Protocol = 17,
            LocalPorts = "53"
        };

        Assert.Equal(
            FirewallRuleDisposition.ProductNameCollision,
            WindowsFirewallManager.ClassifyRuleForSecurity(
                snapshot,
                18443,
                3,
                AgentPath));
    }

    [Fact]
    public void ClassifyRuleForSecurity_NonOwnedLegacyFriendlyNameIsHardCollision()
    {
        var snapshot = Rule(
            WindowsFirewallManager.LegacyPowerShellHttpsFriendlyName,
            "Company managed rule");

        Assert.Equal(
            FirewallRuleDisposition.ProductNameCollision,
            WindowsFirewallManager.ClassifyRuleForSecurity(
                snapshot,
                18443,
                3,
                AgentPath));
    }

    [Fact]
    public void AssertPolicySecurityGate_AcceptsDomainOrPrivateBlockPolicy()
    {
        WindowsFirewallManager.AssertPolicySecurityGate(
            firewallServiceRunning: true,
            activeProfiles: 3,
            [
                new FirewallProfileSecurityState(1, true, 0, true),
                new FirewallProfileSecurityState(2, true, 0, true)
            ]);
    }

    [Theory]
    [InlineData(false, 1, true, 0, true)]
    [InlineData(true, 4, true, 0, true)]
    [InlineData(true, 1, false, 0, true)]
    [InlineData(true, 1, true, 1, true)]
    [InlineData(true, 1, true, 0, false)]
    public void AssertPolicySecurityGate_PreservesHardFailureConditions(
        bool serviceRunning,
        int activeProfiles,
        bool firewallEnabled,
        int defaultInboundAction,
        bool allowsLocalRules)
    {
        var exception = Assert.Throws<SetupException>(() =>
            WindowsFirewallManager.AssertPolicySecurityGate(
                serviceRunning,
                activeProfiles,
                [
                    new FirewallProfileSecurityState(
                        activeProfiles,
                        firewallEnabled,
                        defaultInboundAction,
                        allowsLocalRules)
                ]));

        Assert.Equal(SetupErrorCodes.FirewallFailed, exception.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("Any")]
    [InlineData("18443")]
    [InlineData("80, 18443, 443")]
    [InlineData("18000-19000")]
    public void PortSpecificationIncludes_DetectsAnyExactListAndRange(string? value)
    {
        Assert.True(WindowsFirewallManager.PortSpecificationIncludes(value, 18443));
    }

    [Theory]
    [InlineData("443")]
    [InlineData("1-1024")]
    [InlineData("18444-19000")]
    public void PortSpecificationIncludes_RejectsNonOverlappingPorts(string value)
    {
        Assert.False(WindowsFirewallManager.PortSpecificationIncludes(value, 18443));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("*", "Any", true)]
    [InlineData(@"C:\Program Files\SamsungSwitchWatch\Agent\SamsungSwitchWatch.Agent.exe",
        "SamsungSwitchWatchAgent", true)]
    [InlineData(@"C:\Program Files\Other\server.exe", null, false)]
    [InlineData(null, "OtherService", false)]
    public void RuleMayApplyToAgent_RespectsProgramAndServiceScope(
        string? application,
        string? service,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsFirewallManager.RuleMayApplyToAgent(
                application,
                service,
                AgentPath,
                "SamsungSwitchWatchAgent"));
    }

    [Fact]
    public void RuleMayApplyToAgent_ExpandsEnvironmentVariablesInProgramScope()
    {
        var windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        var agentPath = Path.Combine(windowsDirectory, "System32", "agent.exe");

        Assert.True(WindowsFirewallManager.RuleMayApplyToAgent(
            @"%SystemRoot%\System32\agent.exe",
            null,
            agentPath,
            "SamsungSwitchWatchAgent"));
    }

    [Fact]
    public void RuleMayApplyToAgent_TreatsInvalidProgramScopeAsPotentialConflict()
    {
        Assert.True(WindowsFirewallManager.RuleMayApplyToAgent(
            "\0invalid",
            null,
            @"C:\Program Files\SamsungSwitchWatch\Agent\SamsungSwitchWatch.Agent.exe",
            "SamsungSwitchWatchAgent"));
    }

    private static FirewallRuleSnapshot Rule(string name, string description) =>
        new(
            true,
            name,
            description,
            true,
            1,
            1,
            6,
            "18443",
            "192.168.1.20/32",
            3,
            "All",
            false,
            string.Empty);

    private const string AgentPath =
        @"C:\Program Files\SamsungSwitchWatch\Agent\SamsungSwitchWatch.Agent.exe";
}
