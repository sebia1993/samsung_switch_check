using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Queries;
using SamsungSwitchWatch.Agent.Security;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class ReadOnlyQueryCollectorTests
{
    [Fact]
    public async Task UnsafeCommandIsRejectedBeforeCredentialsOrTelnet()
    {
        var telnet = new RecordingTelnetClient();
        var credentials = new RecordingCredentialVault();
        var collector = new CoreTelnetReadOnlyQueryCollector(telnet, Profiles(), credentials);

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => collector.ExecuteAsync(
            Device(),
            "show running-config"));

        Assert.Equal(AgentErrorCodes.QueryCommandBlocked, exception.Code);
        Assert.Equal(0, credentials.GetCalls);
        Assert.Equal(0, telnet.Calls);
    }

    [Fact]
    public async Task ApprovedCommandUsesOneTemporaryRegisteredCommand()
    {
        var telnet = new RecordingTelnetClient();
        var credentials = new RecordingCredentialVault();
        var collector = new CoreTelnetReadOnlyQueryCollector(telnet, Profiles(), credentials);

        var result = await collector.ExecuteAsync(Device(), "show port status");

        Assert.Equal(1, credentials.GetCalls);
        Assert.Equal(1, telnet.Calls);
        Assert.Equal("show port status", telnet.ExecutedCommand);
        Assert.Equal("Port 24 Up", result.Output);
        Assert.Equal(1, result.SessionCount);
    }

    private static SwitchOptions Device() => new()
    {
        Id = "TEST-SW-01",
        DisplayName = "Synthetic switch",
        Model = "IES4224GP",
        Host = "192.0.2.10",
        CredentialId = "test-switch-readonly",
        UplinkPort = "24"
    };

    private static DeviceProfileRegistry Profiles() => new([Ies4224GpProfile.Create()]);

    private sealed class RecordingCredentialVault : ICredentialVault
    {
        public int GetCalls { get; private set; }

        public Task StoreAsync(
            string credentialId,
            SwitchCredential credential,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SwitchCredential?> GetAsync(
            string credentialId,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult<SwitchCredential?>(new SwitchCredential("monitor", "synthetic-password"));
        }
    }

    private sealed class RecordingTelnetClient : ITelnetClient
    {
        public int Calls { get; private set; }
        public string? ExecutedCommand { get; private set; }

        public Task<TelnetSessionResult> ExecuteRegisteredAsync(
            TelnetEndpoint endpoint,
            TelnetCredentials credentials,
            DeviceCommandProfile profile,
            IReadOnlyCollection<string> commandIds,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var commandId = Assert.Single(commandIds);
            var definition = profile.GetRequiredCommand(commandId);
            ExecutedCommand = definition.Command;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new TelnetSessionResult(
                profile.Model,
                [new CommandOutput(commandId, definition.Command, "raw", "Port 24 Up", now)],
                now,
                now));
        }
    }
}
