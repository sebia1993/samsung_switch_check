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
                @"C:\Program Files\SamsungSwitchWatch\Agent\SamsungSwitchWatch.Agent.exe",
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
}
