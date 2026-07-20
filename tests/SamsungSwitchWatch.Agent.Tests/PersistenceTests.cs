using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
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
        Assert.Equal(TimeSpan.FromMinutes(1), CommandCatalog.Registered["system"].Interval);
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
            var activeCondition = await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Warning, "active-old",
                "Active condition", "Active condition", EventState.Acknowledged, "active-old", OccurredUtc: now.AddDays(-91),
                IsActiveCondition: true));
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
            Assert.Contains(events, item => item.Id == activeCondition.Id);
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
    public async Task EventChangeFeedAppendsCreateAcknowledgeAndRecoveryWithContiguousCursor()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-StoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new SqliteAgentStore(new AgentOptions { DataDirectory = folder },
                NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var created = await store.InsertEventChangeAsync(new NewEvent("TEST-SW-01", EventSeverity.Critical,
                "uplink-down", "Uplink down", "Sanitized message.", EventState.New, "uplink:24",
                IsActiveCondition: true));
            var acknowledged = await store.AcknowledgeEventChangeAsync(created.Event.Id, DateTimeOffset.UtcNow);
            var recovered = await store.MarkConditionRecoveredAsync("TEST-SW-01", "uplink:24", DateTimeOffset.UtcNow);

            Assert.NotNull(acknowledged);
            Assert.Single(recovered);
            var firstPage = await store.GetEventChangesAsync(0, 2);
            Assert.Equal(3, firstPage.HighWatermark);
            Assert.Equal(2, firstPage.NextCursor);
            Assert.True(firstPage.HasMore);
            Assert.Equal([EventChangeKind.Created, EventChangeKind.Acknowledged],
                firstPage.Changes.Select(item => item.ChangeKind));

            var secondPage = await store.GetEventChangesAsync(firstPage.NextCursor, 2);
            var recovery = Assert.Single(secondPage.Changes);
            Assert.Equal(EventChangeKind.Recovered, recovery.ChangeKind);
            Assert.Equal(EventState.Recovered, recovery.Event.State);
            Assert.NotNull(recovery.Event.AcknowledgedUtc);
            Assert.NotNull(recovery.Event.RecoveredUtc);
            Assert.True(recovery.Event.IsActiveCondition);
            Assert.False(secondPage.HasMore);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
    public async Task VersionOneDatabaseIsBackedUpAndMigratedToVersionTwo()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-MigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "switchwatch.db");
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                    INSERT INTO schema_migrations VALUES(1, '2026-01-01T00:00:00.0000000Z');
                    CREATE TABLE events (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE,
                        device_id TEXT NOT NULL, severity TEXT NOT NULL, type TEXT NOT NULL,
                        title TEXT NOT NULL, message TEXT NOT NULL, state TEXT NOT NULL,
                        occurred_utc TEXT NOT NULL, acknowledged_utc TEXT NULL, recovered_utc TEXT NULL,
                        condition_key TEXT NOT NULL, details_json TEXT NOT NULL);
                    INSERT INTO events(event_id, device_id, severity, type, title, message, state,
                        occurred_utc, condition_key, details_json)
                    VALUES('legacy-event', 'TEST-SW-01', 'Warning', 'legacy', 'Legacy', 'Sanitized', 'New',
                        '2026-01-01T00:00:00.0000000Z', 'legacy:1', '{}');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteAgentStore(new AgentOptions { DataDirectory = folder },
                NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var readiness = await store.CheckReadinessAsync();
            var page = await store.GetEventChangesAsync(0);

            Assert.True(readiness.Ready);
            Assert.Equal(2, readiness.SchemaVersion);
            Assert.Single(Directory.GetFiles(folder, "switchwatch.db.schema-v1-*.bak"));
            var migrated = Assert.Single(page.Changes);
            Assert.Equal("legacy-event", migrated.Event.Id);
            Assert.Equal(EventChangeKind.Created, migrated.ChangeKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
    public async Task CorruptDatabaseIsReportedNotReadyAndPreserved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-CorruptStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "switchwatch.db");
        var original = "not-a-sqlite-database"u8.ToArray();
        await File.WriteAllBytesAsync(path, original);
        try
        {
            var store = new SqliteAgentStore(new AgentOptions { DataDirectory = folder },
                NullLogger<SqliteAgentStore>.Instance);

            var exception = await Assert.ThrowsAsync<AgentOperationException>(() => store.InitializeAsync());
            var readiness = await store.CheckReadinessAsync();

            Assert.Equal(AgentErrorCodes.StorageWriteFailed, exception.Code);
            Assert.False(readiness.Ready);
            Assert.Equal(AgentErrorCodes.StorageWriteFailed, readiness.ErrorCode);
            SqliteConnection.ClearAllPools();
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
    public async Task ReadinessUsesCachedIntegrityAndPeriodicRefreshDetectsSchemaDrift()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-IntegrityCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var options = new AgentOptions { DataDirectory = folder };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var initialCheck = store.LastIntegrityCheckUtc;
            Assert.NotNull(initialCheck);

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var readiness = await store.CheckReadinessAsync();
                Assert.True(readiness.Ready);
                Assert.Equal(2, readiness.SchemaVersion);
            }
            Assert.Equal(initialCheck, store.LastIntegrityCheckUtc);

            await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM schema_migrations WHERE version=2;";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var cached = await store.CheckReadinessAsync();
            Assert.True(cached.Ready);
            Assert.Equal(initialCheck, store.LastIntegrityCheckUtc);

            await Task.Delay(20);
            Assert.False(await store.RefreshIntegrityStatusAsync());
            var refreshed = await store.CheckReadinessAsync();
            Assert.False(refreshed.Ready);
            Assert.Equal(AgentErrorCodes.StorageWriteFailed, refreshed.ErrorCode);
            Assert.Equal(1, refreshed.SchemaVersion);
            Assert.True(store.LastIntegrityCheckUtc > initialCheck);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
    public async Task FailedCollectionCommitRollsBackRawSnapshotEventAndAuditTogether()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-AtomicStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new SqliteAgentStore(new AgentOptions { DataDirectory = folder },
                NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info, "existing",
                "Existing", "Sanitized", EventState.New, "existing", Id: "duplicate-id"));
            var captured = DateTimeOffset.UtcNow;
            var output = new CollectedOutput("TEST-SW-01", "version", captured,
                new System.Text.Json.Nodes.JsonObject { ["softwareVersion"] = "MUST-ROLL-BACK" },
                "Sanitized raw fixture");

            var exception = await Assert.ThrowsAsync<AgentOperationException>(() => store.CommitSuccessfulCollectionAsync(
                output,
                new AuditEntry(captured, "command", "test", "TEST-SW-01", "success", "must roll back"),
                [new NewEvent("TEST-SW-01", EventSeverity.Warning, "duplicate", "Duplicate", "Sanitized",
                    EventState.New, "duplicate", Id: "duplicate-id")],
                [],
                CancellationToken.None));

            Assert.Equal(AgentErrorCodes.StorageWriteFailed, exception.Code);
            Assert.Null(await store.GetSnapshotAsync("TEST-SW-01", "version"));
            var counts = await store.GetCountsAsync();
            Assert.Equal(0, counts.RawCount);
            Assert.Equal(1, counts.EventCount);
            Assert.Equal(0, counts.AuditCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
    public async Task FailedCollectorStateCommitRollsBackHealthEventAndAuditTogether()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-AtomicStateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new SqliteAgentStore(new AgentOptions { DataDirectory = folder },
                NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info, "existing",
                "Existing", "Sanitized", EventState.New, "existing", Id: "duplicate-state-id"));
            var captured = DateTimeOffset.UtcNow;
            var health = new DeviceSnapshot("TEST-SW-01", CommandCatalog.CollectorHealthSnapshotIdFor("version"),
                captured, new System.Text.Json.Nodes.JsonObject
                {
                    ["errorCode"] = AgentErrorCodes.IncompleteOutput,
                    ["consecutiveFailures"] = 3,
                    ["state"] = "Failed"
                });

            var exception = await Assert.ThrowsAsync<AgentOperationException>(() => store.CommitCollectorStateAsync(
                [health],
                new AuditEntry(captured, "command", "test", "TEST-SW-01", "failed", "must roll back"),
                [new NewEvent("TEST-SW-01", EventSeverity.Critical, "duplicate", "Duplicate", "Sanitized",
                    EventState.New, "duplicate", Id: "duplicate-state-id", IsActiveCondition: true)],
                [],
                CancellationToken.None));

            Assert.Equal(AgentErrorCodes.StorageWriteFailed, exception.Code);
            Assert.Null(await store.GetSnapshotAsync("TEST-SW-01",
                CommandCatalog.CollectorHealthSnapshotIdFor("version")));
            var counts = await store.GetCountsAsync();
            Assert.Equal(1, counts.EventCount);
            Assert.Equal(0, counts.AuditCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
    public async Task RetentionGapRequiresViewerCursorResetToHighWatermark()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-FeedResetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                Retention = new RetentionOptions { EventDays = 1 }
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info, "expired",
                "Expired", "Expired", EventState.New, "expired", OccurredUtc: now.AddDays(-2)));
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Critical, "active",
                "Active", "Active", EventState.New, "active", OccurredUtc: now.AddDays(-2),
                IsActiveCondition: true));
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Warning, "recent",
                "Recent", "Recent", EventState.New, "recent", OccurredUtc: now));

            await store.RunRetentionAsync(now);
            var page = await store.GetEventChangesAsync(0, 500);

            Assert.True(page.ResetRequired);
            Assert.Equal(3, page.HighWatermark);
            Assert.Equal(page.HighWatermark, page.ResetCursor);
            Assert.Equal(page.ResetCursor, page.NextCursor);
            Assert.False(page.HasMore);
            Assert.Empty(page.Changes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
    public void PocConfigurationRejectsIpv6WithStableCode()
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.GetTempPath(),
            Switches = [new SwitchOptions { Host = "2001:db8::10" }]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));
        Assert.Equal(AgentErrorCodes.Ipv6Unsupported, exception.Code);
    }

    [Fact]
    public void PocConfigurationRejectsHostnames()
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.GetTempPath(),
            Switches = [new SwitchOptions { Host = "switch.internal.example" }]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));
        Assert.Equal("CONFIG_INVALID", exception.Code);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("credential")]
    [InlineData("uplink")]
    [InlineData("display")]
    public void PocConfigurationRejectsUnsafeOperatorIdentifiers(string field)
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.GetTempPath(),
            Switches = [new SwitchOptions()]
        };
        switch (field)
        {
            case "agent": options.AgentId = "agent/unsafe"; break;
            case "credential": options.Switches[0].CredentialId = "credential/unsafe"; break;
            case "uplink": options.Switches[0].UplinkPort = "24;reload"; break;
            case "display": options.Switches[0].DisplayName = "unsafe\r\nname"; break;
        }

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
