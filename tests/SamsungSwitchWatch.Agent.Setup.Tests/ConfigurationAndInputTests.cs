using System.Net;
using System.Text.Json.Nodes;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class ConfigurationAndInputTests
{
    [Fact]
    public void Create_PreservesValidatedV09SettingsAndChangesOnlyDeploymentOwnedValues()
    {
        const string existing = """
            {
              "Agent": {
                "AgentId": "existing-agent",
                "ListenUrl": "https://127.0.0.1:18443",
                "DataDirectory": "old",
                "MockMode": true,
                "AllowedTargetCidrs": [ "10.9.0.0/16" ],
                "MaxConcurrentExecutions": 7,
                "RateLimitPerMinute": 75,
                "MaxRequestBodyBytes": 45000,
                "MaxCommandsPerRequest": 6,
                "MaxCommandLength": 100,
                "MaxOutputBytes": 55000,
                "Telnet": {
                  "MaxSessionSeconds": 180,
                  "ImmediateSessionCloseRetryCount": 0,
                  "ImmediateSessionCloseRetryDelaySeconds": 7
                }
              }
            }
            """;

        var created = AgentConfigurationFactory.Create(
            @"C:\ProgramData\SamsungSwitchWatch",
            ["192.168.30.0/24"],
            existing);
        var agent = JsonNode.Parse(created)!["Agent"]!;

        Assert.Equal("existing-agent", agent["AgentId"]!.GetValue<string>());
        Assert.Equal("https://0.0.0.0:18443", agent["ListenUrl"]!.GetValue<string>());
        Assert.False(agent["MockMode"]!.GetValue<bool>());
        Assert.Equal(7, agent["MaxConcurrentExecutions"]!.GetValue<int>());
        Assert.Equal(75, agent["RateLimitPerMinute"]!.GetValue<int>());
        Assert.Equal(180, agent["Telnet"]!["MaxSessionSeconds"]!.GetValue<int>());
        Assert.Equal(0, agent["Telnet"]!["ImmediateSessionCloseRetryCount"]!.GetValue<int>());
        Assert.Equal(7, agent["Telnet"]!["ImmediateSessionCloseRetryDelaySeconds"]!.GetValue<int>());
        Assert.Equal(
            "192.168.30.0/24",
            agent["AllowedTargetCidrs"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Create_ReplacesOutOfRangeLegacySettingsWithSafeDefaults()
    {
        const string existing = """
            {
              "Agent": {
                "AgentId": "../bad",
                "MaxConcurrentExecutions": 999,
                "RateLimitPerMinute": 0,
                "Telnet": {
                  "MaxSessionSeconds": 999,
                  "ImmediateSessionCloseRetryCount": 4
                }
              }
            }
            """;

        var agent = JsonNode.Parse(AgentConfigurationFactory.Create(
            @"C:\ProgramData\SamsungSwitchWatch",
            ["10.20.0.0/16"],
            existing))!["Agent"]!;

        Assert.NotEqual("../bad", agent["AgentId"]!.GetValue<string>());
        Assert.Equal(2, agent["MaxConcurrentExecutions"]!.GetValue<int>());
        Assert.Equal(60, agent["RateLimitPerMinute"]!.GetValue<int>());
        Assert.Equal(240, agent["Telnet"]!["MaxSessionSeconds"]!.GetValue<int>());
        Assert.Equal(1, agent["Telnet"]!["ImmediateSessionCloseRetryCount"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not-json")]
    [InlineData("{}")]
    [InlineData("""{"Agent":"wrong-shape"}""")]
    public void Create_RejectsMalformedExistingConfiguration(string existing)
    {
        var exception = Assert.Throws<SetupException>(() =>
            AgentConfigurationFactory.Create(
                @"C:\ProgramData\SamsungSwitchWatch",
                ["10.20.0.0/16"],
                existing));

        Assert.Equal(SetupErrorCodes.ConfigurationInvalid, exception.Code);
    }

    [Theory]
    [InlineData("10.0.0.0/7")]
    [InlineData("172.16.0.0/11")]
    [InlineData("192.168.0.0/15")]
    [InlineData("8.8.8.0/24")]
    public void ValidateInput_RejectsNetworkThatIncludesNonPrivateSpace(string cidr)
    {
        var exception = Assert.Throws<SetupException>(() =>
            SetupDiagnosticsService.ValidateInput(
                new SetupRequest("192.168.1.20", [cidr])));

        Assert.Equal(SetupErrorCodes.NetworkSelectionInvalid, exception.Code);
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("172.16.0.0/12")]
    [InlineData("192.168.0.0/16")]
    public void ValidateInput_AcceptsNetworkWhollyInsidePrivateSpace(string cidr)
    {
        SetupDiagnosticsService.ValidateInput(
            new SetupRequest("192.168.1.20", [cidr]));
    }

    [Fact]
    public void NetworkDiscovery_DropsPrivateAddressWithSupernetMask()
    {
        var candidates = WindowsNetworkDiscovery.BuildCandidates(
        [
            new NetworkAddress(
                "bad",
                "Ethernet",
                "bad-supernet",
                IPAddress.Parse("192.168.1.10"),
                IPAddress.Parse("255.254.0.0")),
            new NetworkAddress(
                "good",
                "Ethernet",
                "management",
                IPAddress.Parse("192.168.20.10"),
                IPAddress.Parse("255.255.255.0"))
        ]);

        var candidate = Assert.Single(candidates);
        Assert.Equal("192.168.20.0/24", candidate.Cidr);
    }
}
