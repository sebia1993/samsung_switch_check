using System.Net;
using System.Text.Json.Nodes;
using System.Windows.Media;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class ConfigurationAndInputTests
{
    [Fact]
    public void NetworkSelectionItem_PreservesExistingCandidateProperties()
    {
        var candidate = new NetworkCandidate(
            "adapter-1",
            "Ethernet",
            "10.20.30.10",
            "10.20.30.0/24",
            "management adapter");

        var item = new NetworkSelectionItem(candidate);

        Assert.Equal(candidate.InterfaceName, item.InterfaceName);
        Assert.Equal(candidate.Address, item.Address);
        Assert.Equal(candidate.Cidr, item.Cidr);
        Assert.Equal(candidate.Description, item.Description);
    }

    [Fact]
    public void ResultRow_WarningUsesDistinctYellowIndicator()
    {
        var row = ResultRow.From(new SetupStepResult(
            "FIREWALL_OVERLAP_PROTECTED",
            "방화벽 중복 규칙",
            SetupStepState.Warning,
            "warning"));

        Assert.Equal("▲", row.Symbol);
        Assert.Equal(Brushes.DarkGoldenrod, row.Brush);
    }

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
                "AllowedViewerIpv4": "192.168.1.99",
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
            "192.168.1.20",
            existing);
        var agent = JsonNode.Parse(created)!["Agent"]!;

        Assert.Equal("existing-agent", agent["AgentId"]!.GetValue<string>());
        Assert.Equal("https://0.0.0.0:18443", agent["ListenUrl"]!.GetValue<string>());
        Assert.False(agent["MockMode"]!.GetValue<bool>());
        Assert.Equal(
            "192.168.1.20",
            agent["AllowedViewerIpv4"]!.GetValue<string>());
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
            "10.1.1.20",
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
                "10.1.1.20",
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

    [Theory]
    [InlineData("10.42.55.99/8", "10.0.0.0/8")]
    [InlineData("10.42.55.99/24", "10.42.55.0/24")]
    [InlineData("10.42.55.99/32", "10.42.55.99/32")]
    [InlineData("172.31.44.10/12", "172.16.0.0/12")]
    [InlineData("172.20.44.10/16", "172.20.0.0/16")]
    [InlineData("192.168.50.201/24", "192.168.50.0/24")]
    [InlineData(" 192.168.50.201/24 ", "192.168.50.0/24")]
    public void TryNormalizePrivateCidr_NormalizesPrivateHostAddress(
        string input,
        string expected)
    {
        var succeeded = Ipv4Input.TryNormalizePrivateCidr(input, out var actual);

        Assert.True(succeeded);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.1.1.1")]
    [InlineData("10.1.1.1/")]
    [InlineData("10.1.1.1/24/1")]
    [InlineData("010.1.1.1/8")]
    [InlineData("10.1.1.1 /24")]
    [InlineData("10.1.1.1/ 24")]
    [InlineData("10.1.1.1/024")]
    [InlineData("10.1.1.1/+24")]
    [InlineData("10.1.1.1/-1")]
    [InlineData("10.1.1.1/33")]
    [InlineData("10.1.1.1/7")]
    [InlineData("172.16.1.1/11")]
    [InlineData("192.168.1.1/15")]
    [InlineData("8.8.8.8/32")]
    [InlineData("127.0.0.1/32")]
    [InlineData("169.254.1.1/32")]
    [InlineData("2001:db8::1/128")]
    public void TryNormalizePrivateCidr_RejectsInvalidOrNonPrivateInput(string? input)
    {
        var succeeded = Ipv4Input.TryNormalizePrivateCidr(input, out var actual);

        Assert.False(succeeded);
        Assert.Equal(string.Empty, actual);
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("10.42.55.0/24")]
    [InlineData("10.42.55.99/32")]
    [InlineData("172.16.0.0/12")]
    [InlineData("172.20.0.0/16")]
    [InlineData("192.168.50.0/24")]
    public void IsCanonicalPrivateCidr_AcceptsCanonicalPrivateNetwork(string input)
    {
        Assert.True(Ipv4Input.IsCanonicalPrivateCidr(input));
    }

    [Theory]
    [InlineData("10.42.55.99/8")]
    [InlineData("172.31.44.10/12")]
    [InlineData("192.168.50.201/24")]
    [InlineData(" 192.168.50.0/24")]
    [InlineData("192.168.50.0/24 ")]
    [InlineData("192.168.50.0/024")]
    [InlineData("8.8.8.0/24")]
    public void IsCanonicalPrivateCidr_RejectsNonCanonicalOrNonPrivateNetwork(string input)
    {
        Assert.False(Ipv4Input.IsCanonicalPrivateCidr(input));
    }

    [Theory]
    [InlineData("unauthorized", SetupErrorCodes.PathNotWritable)]
    [InlineData("security", SetupErrorCodes.PathNotWritable)]
    [InlineData("io", SetupErrorCodes.PathNotWritable)]
    [InlineData("argument", SetupErrorCodes.PathInvalid)]
    [InlineData("unsupported", SetupErrorCodes.PathInvalid)]
    [InlineData("too-long", SetupErrorCodes.PathInvalid)]
    public void DeploymentPathValidation_MapsExpectedEnvironmentFailures(
        string failure,
        string expectedCode)
    {
        var fileSystem = new TestFileSystem
        {
            PathValidationException = failure switch
            {
                "unauthorized" => new UnauthorizedAccessException("private"),
                "security" => new System.Security.SecurityException("private"),
                "io" => new IOException("private"),
                "argument" => new ArgumentException("private"),
                "unsupported" => new NotSupportedException("private"),
                "too-long" => new PathTooLongException("private"),
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            }
        };
        var paths = new DeploymentPaths("package", "install", "data", "operations");

        var exception = Assert.Throws<SetupException>(() =>
            SetupDiagnosticsService.ValidateDeploymentPathsForInstall(
                fileSystem,
                paths,
                ServiceSnapshot.Missing,
                []));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("unsupported")]
    [InlineData("too-long")]
    public void DeploymentParentProbe_PreservesInvalidPathClassification(string failure)
    {
        var fileSystem = new TestFileSystem
        {
            CanCreateException = failure switch
            {
                "argument" => new ArgumentException("private"),
                "unsupported" => new NotSupportedException("private"),
                "too-long" => new PathTooLongException("private"),
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            }
        };
        var paths = new DeploymentPaths("package", "install", "data", "operations");

        var exception = Assert.Throws<SetupException>(() =>
            SetupDiagnosticsService.ValidateDeploymentPathsForInstall(
                fileSystem,
                paths,
                ServiceSnapshot.Missing,
                []));

        Assert.Equal(SetupErrorCodes.PathInvalid, exception.Code);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
