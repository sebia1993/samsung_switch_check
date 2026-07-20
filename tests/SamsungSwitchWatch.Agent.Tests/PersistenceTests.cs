using Microsoft.Extensions.Logging.Abstractions;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void RegisteredCommandPolicyUsesApprovedReadOnlySchedule()
    {
        Assert.Equal(TimeSpan.FromHours(1), CommandCatalog.Registered["version"].Interval);
        Assert.Equal(TimeSpan.FromMinutes(5), CommandCatalog.Registered["system"].Interval);
        Assert.Equal(TimeSpan.FromMinutes(1), CommandCatalog.Registered["log_ram"].Interval);
        Assert.Equal(TimeSpan.FromMinutes(1), CommandCatalog.Registered["interface_status"].Interval);
        Assert.All(CommandCatalog.Registered.Values,
            command => Assert.StartsWith("show ", command.Cli, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StorePersistsEventsAndAppliesAgeAndSizeRetention()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-StoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                Retention = new RetentionOptions
                {
                    RawDays = 7,
                    RawMaxMegabytes = 1,
                    EventDays = 90,
                    AuditDays = 180
                }
            };
            var now = DateTimeOffset.UtcNow;
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();

            await store.InsertRawBytesAsync("TEST-SW-01", "version", now.AddDays(-8), new byte[128]);
            await store.InsertRawBytesAsync("TEST-SW-01", "version", now.AddMinutes(-2), new byte[700 * 1024]);
            await store.InsertRawBytesAsync("TEST-SW-01", "version", now.AddMinutes(-1), new byte[700 * 1024]);
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info, "old", "Old", "Old", EventState.New,
                "old", OccurredUtc: now.AddDays(-91)));
            var activeCritical = await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Critical, "active-old",
                "Active critical", "Active critical", EventState.Acknowledged, "active-old", OccurredUtc: now.AddDays(-91)));
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Critical, "recovered-old",
                "Recovered critical", "Recovered critical", EventState.Recovered, "recovered-old", OccurredUtc: now.AddDays(-91)));
            var retained = await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Warning, "new", "New", "New",
                EventState.New, "new", OccurredUtc: now));
            await store.InsertAuditAsync(new AuditEntry(now.AddDays(-181), "old", "test", null, "success", "old"));
            await store.InsertAuditAsync(new AuditEntry(now, "new", "test", null, "success", "new"));

            await store.RunRetentionAsync(now);

            var counts = await store.GetCountsAsync();
            Assert.Equal(1, counts.RawCount);
            Assert.Equal(2, counts.EventCount);
            Assert.Equal(1, counts.AuditCount);

            var reopened = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await reopened.InitializeAsync();
            var events = await reopened.GetEventsAfterAsync(0);
            Assert.Equal(2, events.Count);
            Assert.Contains(events, item => item.Id == activeCritical.Id);
            Assert.Contains(events, item => item.Id == retained.Id);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
                // Best-effort temporary cleanup on Windows.
            }
        }
    }

    [Fact]
    public void PocConfigurationRejectsUnsupportedModel()
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.GetTempPath(),
            Switches = [new SwitchOptions { Model = "IES4028XP" }]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));
        Assert.Equal("CONFIG_INVALID", exception.Code);
    }

    [Fact]
    public void ProductionConfigurationRejectsPlaceholderTokenPepper()
    {
        var options = new AgentOptions
        {
            MockMode = false,
            DataDirectory = Path.GetTempPath(),
            TokenPepper = "replace-with-a-long-random-local-value",
            Https = new HttpsOptions { Enabled = true, CertificatePath = "missing.pfx" },
            Switches = [new SwitchOptions()]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));
        Assert.Contains("TokenPepper", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionConfigurationRejectsMissingHttpsCertificateEarly()
    {
        var options = new AgentOptions
        {
            MockMode = false,
            DataDirectory = Path.GetTempPath(),
            TokenPepper = "A-unique-production-pepper-that-is-longer-than-32-characters",
            Https = new HttpsOptions { Enabled = true, CertificatePath = $"missing-{Guid.NewGuid():N}.pfx" },
            Switches = [new SwitchOptions()]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));
        Assert.Contains("certificate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
