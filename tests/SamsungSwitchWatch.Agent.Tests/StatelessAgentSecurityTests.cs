using System.Text;
using System.Net;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Execution;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class StatelessAgentSecurityTests
{
    [Theory]
    [InlineData("192.0.2.0/24", true)]
    [InlineData("192.0.2.10/24", false)]
    [InlineData("192.0.002.0/24", false)]
    [InlineData("2001:db8::/64", false)]
    [InlineData("192.0.2.0/33", false)]
    public void CidrParser_RequiresCanonicalIpv4Network(string value, bool expected) =>
        Assert.Equal(expected, Ipv4Cidr.TryParse(value, out _));

    [Fact]
    public void TargetPolicy_AllowsOnlyConfiguredNetworkAndPort23()
    {
        var options = new AgentOptions
        {
            AllowedTargetCidrs = ["10.10.20.0/24"]
        };
        var policy = new TargetNetworkPolicy(options);

        Assert.True(policy.TryValidate("10.10.20.25", 23, out var address));
        Assert.Equal("10.10.20.25", address.ToString());
        Assert.False(policy.TryValidate("10.10.21.25", 23, out _));
        Assert.False(policy.TryValidate("10.10.20.25", 2323, out _));
        Assert.False(policy.TryValidate("010.10.20.25", 23, out _));
    }

    [Theory]
    [InlineData("0.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    public void TargetPolicy_RejectsSpecialUseTargetsEvenWithCatchAllCidr(
        string target)
    {
        var policy = new TargetNetworkPolicy(new AgentOptions
        {
            AllowedTargetCidrs = ["0.0.0.0/0"]
        });

        Assert.False(policy.TryValidate(target, 23, out _));
    }

    [Fact]
    public async Task Admission_AllowsOnlyOneActiveSessionPerTarget()
    {
        var admission = new TelnetExecutionAdmission(new AgentOptions
        {
            AllowedTargetCidrs = ["10.0.0.0/8"],
            MaxConcurrentExecutions = 2,
            RateLimitPerMinute = 60
        });
        var firstTarget = IPAddress.Parse("10.40.0.10");
        var otherTarget = IPAddress.Parse("10.40.0.11");

        var first = await admission.EnterAsync("client-1", firstTarget, CancellationToken.None);
        var sameTargetFailure = await Assert.ThrowsAsync<AgentOperationException>(async () =>
            await admission.EnterAsync("client-2", firstTarget, CancellationToken.None));
        await using var differentTarget =
            await admission.EnterAsync("client-2", otherTarget, CancellationToken.None);

        Assert.Equal(AgentErrorCodes.AgentBusy, sameTargetFailure.Code);

        await first.DisposeAsync();
        await using var afterRelease =
            await admission.EnterAsync("client-3", firstTarget, CancellationToken.None);
    }

    [Fact]
    public void Configuration_RequiresHttpsOutsideLoopbackMockMode()
    {
        var folder = NewTemporaryFolder();
        try
        {
            var options = new AgentOptions
            {
                ListenUrl = "http://0.0.0.0:18443",
                DataDirectory = folder,
                AllowedTargetCidrs = ["192.0.2.0/24"]
            };

            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentOptionsValidator.ValidateAndNormalize(options, folder));

            Assert.Equal(AgentErrorCodes.ConfigurationInvalid, exception.Code);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void OutputLimiter_TruncatesAtUnicodeScalarBoundary()
    {
        var value = string.Concat(Enumerable.Repeat("가😀", 500));

        var result = Utf8OutputLimiter.Limit(value, 1024);

        Assert.True(result.Truncated);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Value), 1, 1024);
        Assert.False(char.IsHighSurrogate(result.Value[^1]));
    }

    [Fact]
    public async Task DirectNoArgumentProgramInvocation_ExitsWithoutStartingHost()
    {
        var completed = Program.Main([]);

        await completed.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RemovedBackgroundRuntimeMode_ExitsWithoutStartingHost()
    {
        var completed = Program.Main(["--background"]);

        await completed.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RequestDiagnostics_RedactEndpointCredentialsCommandsAndOutput()
    {
        var request = new StatelessTelnetRequest(
            "request-1",
            IPAddress.Parse("192.0.2.10"),
            "IES4224GP",
            new("operator-private", "login-private", "enable-private"),
            ["show running-config"],
            "manual");
        var output = new TelnetApiCommandResult(
            "show running-config",
            "private device output",
            false,
            DateTimeOffset.UtcNow);

        var diagnostic = request + " " + output;

        Assert.DoesNotContain("192.0.2.10", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-private", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("login-private", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("enable-private", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("show running-config", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private device output", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistentHttpsIdentity_ReusesInstanceAndPublicKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            var options = new AgentOptions
            {
                ListenUrl = "https://127.0.0.1:18443",
                DataDirectory = folder,
                AllowedTargetCidrs = ["192.0.2.0/24"]
            };
            AgentOptionsValidator.ValidateAndNormalize(options, folder);

            string firstInstance;
            string firstKey;
            using (var first = AgentIdentityStore.LoadOrCreate(options))
            {
                firstInstance = first.InstanceId;
                firstKey = first.CertificatePublicKeySha256;
            }
            using var second = AgentIdentityStore.LoadOrCreate(options);

            Assert.Equal(firstInstance, second.InstanceId);
            Assert.Equal(firstKey, second.CertificatePublicKeySha256);
            Assert.Matches("^[0-9A-F]{64}$", second.CertificatePublicKeySha256);
            Assert.Equal(
                ["agent-identity.json", "https-certificate.pfx.dpapi"],
                Directory.EnumerateFiles(folder)
                    .Select(path => Path.GetFileName(path)!)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static string NewTemporaryFolder()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-AgentSecurityTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
