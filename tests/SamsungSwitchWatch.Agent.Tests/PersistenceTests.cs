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
            await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT total_bytes FROM raw_storage_stats WHERE singleton=1;";
                var protectedBytes = Convert.ToInt64(await command.ExecuteScalarAsync());
                Assert.InRange(protectedBytes, 700L * 1024L, 1024L * 1024L);
            }

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
            Assert.Equal(EventState.New, firstPage.Changes[0].Event.State);
            Assert.Null(firstPage.Changes[0].Event.AcknowledgedUtc);
            Assert.Equal(EventState.Acknowledged, firstPage.Changes[1].Event.State);
            Assert.NotNull(firstPage.Changes[1].Event.AcknowledgedUtc);
            Assert.Null(firstPage.Changes[1].Event.RecoveredUtc);

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
    public async Task VersionOneDatabaseIsBackedUpMigratedToVersionFiveAndLegacyRawIsPurged()
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
                    CREATE TABLE raw_blobs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT NOT NULL,
                        command_id TEXT NOT NULL, captured_utc TEXT NOT NULL,
                        content BLOB NOT NULL, size_bytes INTEGER NOT NULL);
                    INSERT INTO events(event_id, device_id, severity, type, title, message, state,
                        occurred_utc, condition_key, details_json)
                    VALUES('legacy-event', 'TEST-SW-01', 'Warning', 'legacy', 'Legacy', 'Sanitized', 'New',
                        '2026-01-01T00:00:00.0000000Z', 'legacy:1', '{}');
                    INSERT INTO raw_blobs(device_id, command_id, captured_utc, content, size_bytes)
                    VALUES('TEST-SW-01', 'system', '2026-01-01T00:00:00.0000000Z',
                        CAST('legacy plaintext evidence' AS BLOB), 25);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteAgentStore(new AgentOptions { DataDirectory = folder },
                NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var readiness = await store.CheckReadinessAsync();
            var page = await store.GetEventChangesAsync(0);

            Assert.True(readiness.Ready);
            Assert.Equal(5, readiness.SchemaVersion);
            var backupPath = Assert.Single(Directory.GetFiles(folder, "switchwatch.db.schema-v1-*.bak"));
            await using (var current = new SqliteConnection($"Data Source={path}"))
            {
                await current.OpenAsync();
                await using (var rawCount = current.CreateCommand())
                {
                    rawCount.CommandText = "SELECT COUNT(*) FROM raw_blobs;";
                    Assert.Equal(0L, Convert.ToInt64(await rawCount.ExecuteScalarAsync()));
                }
                await using (var schema = current.CreateCommand())
                {
                    schema.CommandText =
                        "SELECT COUNT(*) FROM pragma_table_info('raw_blobs') WHERE name='protection_version';";
                    Assert.Equal(1L, Convert.ToInt64(await schema.ExecuteScalarAsync()));
                }
            }
            await using (var backup = new SqliteConnection($"Data Source={backupPath}"))
            {
                await backup.OpenAsync();
                await using var rawCount = backup.CreateCommand();
                rawCount.CommandText = "SELECT COUNT(*) FROM raw_blobs;";
                Assert.Equal(0L, Convert.ToInt64(await rawCount.ExecuteScalarAsync()));
            }
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", backupPath, backupPath + "-wal" }
                         .Where(File.Exists))
            {
                Assert.DoesNotContain("legacy plaintext evidence",
                    System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(candidate)),
                    StringComparison.Ordinal);
            }
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
    public async Task VersionThreeDatabaseUpgradePurgesLegacyRawBeforeCreatingBackup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-V3RawMigrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "switchwatch.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL);
                    INSERT INTO schema_migrations VALUES(1, '2026-01-01T00:00:00.0000000Z');
                    INSERT INTO schema_migrations VALUES(2, '2026-01-02T00:00:00.0000000Z');
                    INSERT INTO schema_migrations VALUES(3, '2026-01-03T00:00:00.0000000Z');
                    CREATE TABLE snapshots (
                        device_id TEXT NOT NULL, command_id TEXT NOT NULL, captured_utc TEXT NOT NULL,
                        data_json TEXT NOT NULL, PRIMARY KEY (device_id, command_id));
                    CREATE TABLE events (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE,
                        device_id TEXT NOT NULL, severity TEXT NOT NULL, type TEXT NOT NULL,
                        title TEXT NOT NULL, message TEXT NOT NULL, state TEXT NOT NULL,
                        occurred_utc TEXT NOT NULL, acknowledged_utc TEXT NULL, recovered_utc TEXT NULL,
                        condition_key TEXT NOT NULL, details_json TEXT NOT NULL,
                        is_active_condition INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE event_changes (
                        change_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        event_id TEXT NOT NULL, change_kind TEXT NOT NULL, occurred_utc TEXT NOT NULL,
                        event_snapshot_json TEXT NULL,
                        FOREIGN KEY(event_id) REFERENCES events(event_id) ON DELETE CASCADE);
                    CREATE TABLE audit (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_utc TEXT NOT NULL,
                        action TEXT NOT NULL, actor TEXT NOT NULL, device_id TEXT NULL,
                        outcome TEXT NOT NULL, detail TEXT NOT NULL);
                    CREATE TABLE pairing_codes (
                        code_hash TEXT PRIMARY KEY, created_utc TEXT NOT NULL,
                        expires_utc TEXT NOT NULL, used_utc TEXT NULL);
                    CREATE TABLE api_tokens (
                        token_hash TEXT PRIMARY KEY, created_utc TEXT NOT NULL,
                        last_used_utc TEXT NULL, revoked_utc TEXT NULL);
                    CREATE TABLE raw_blobs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT NOT NULL,
                        command_id TEXT NOT NULL, captured_utc TEXT NOT NULL,
                        content BLOB NOT NULL, size_bytes INTEGER NOT NULL);
                    CREATE INDEX ix_raw_captured ON raw_blobs(captured_utc);
                    CREATE TABLE raw_storage_stats (
                        singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                        total_bytes INTEGER NOT NULL CHECK(total_bytes >= 0));
                    INSERT INTO raw_storage_stats VALUES(1, 0);
                    CREATE TRIGGER raw_blobs_count_insert AFTER INSERT ON raw_blobs
                    BEGIN
                        UPDATE raw_storage_stats SET total_bytes=total_bytes + NEW.size_bytes WHERE singleton=1;
                    END;
                    CREATE TRIGGER raw_blobs_count_delete AFTER DELETE ON raw_blobs
                    BEGIN
                        UPDATE raw_storage_stats SET total_bytes=MAX(0, total_bytes - OLD.size_bytes) WHERE singleton=1;
                    END;
                    INSERT INTO raw_blobs(device_id, command_id, captured_utc, content, size_bytes)
                    VALUES('TEST-SW-01', 'system', '2026-01-03T00:00:00.0000000Z',
                        CAST('schema3 plaintext evidence' AS BLOB), 26);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteAgentStore(new AgentOptions { DataDirectory = folder },
                NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();

            Assert.Equal(5, (await store.CheckReadinessAsync()).SchemaVersion);
            var backupPath = Assert.Single(Directory.GetFiles(folder, "switchwatch.db.schema-v3-*.bak"));
            await using (var current = new SqliteConnection($"Data Source={path}"))
            {
                await current.OpenAsync();
                await using var command = current.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*), COALESCE(MAX(protection_version), 1) FROM raw_blobs;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt32(0));
            }
            await using (var backup = new SqliteConnection($"Data Source={backupPath}"))
            {
                await backup.OpenAsync();
                await using var command = backup.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM raw_blobs;";
                Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { path, path + "-wal", backupPath, backupPath + "-wal" }
                         .Where(File.Exists))
            {
                Assert.DoesNotContain("schema3 plaintext evidence",
                    System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(candidate)),
                    StringComparison.Ordinal);
            }
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
    public async Task VersionFourMigrationBacksUpThenDropsAuthTablesAndPreservesMonitoringData()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-V4MigrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var options = new AgentOptions { DataDirectory = folder };
        var path = options.DatabasePath;
        var credentialPath = Path.Combine(folder, "credentials", "readonly.bin");
        try
        {
            var seed = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await seed.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            await seed.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01", "system", now,
                new System.Text.Json.Nodes.JsonObject { ["uptime"] = "12 days" }));
            await seed.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Warning,
                "migration-fixture", "Migration fixture", "Sanitized", EventState.New, "migration:fixture"));
            await seed.InsertAuditAsync(new AuditEntry(now, "migration-fixture", "test", "TEST-SW-01",
                "success", "Sanitized"));
            await seed.InsertRawBytesAsync("TEST-SW-01", "system", now, "protected fixture"u8.ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
            var credentialEnvelope = "synthetic-dpapi-envelope"u8.ToArray();
            await File.WriteAllBytesAsync(credentialPath, credentialEnvelope);

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM schema_migrations WHERE version=5;
                    CREATE TABLE pairing_codes (
                        code_hash TEXT PRIMARY KEY, created_utc TEXT NOT NULL,
                        expires_utc TEXT NOT NULL, used_utc TEXT NULL);
                    CREATE TABLE api_tokens (
                        token_hash TEXT PRIMARY KEY, created_utc TEXT NOT NULL,
                        last_used_utc TEXT NULL, revoked_utc TEXT NULL);
                    INSERT INTO pairing_codes VALUES('PAIRING_HASH', '2026-01-01', '2026-01-02', NULL);
                    INSERT INTO api_tokens VALUES('TOKEN_HASH', '2026-01-01', NULL, NULL);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            var migrated = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await migrated.InitializeAsync();

            Assert.Equal(5, (await migrated.CheckReadinessAsync()).SchemaVersion);
            Assert.NotNull(await migrated.GetSnapshotAsync("TEST-SW-01", "system"));
            Assert.Single(await migrated.GetEventsAfterAsync(0));
            var counts = await migrated.GetCountsAsync();
            Assert.Equal(1, counts.RawCount);
            Assert.Equal(1, counts.EventCount);
            Assert.Equal(1, counts.AuditCount);
            Assert.Equal(credentialEnvelope, await File.ReadAllBytesAsync(credentialPath));

            var backupPath = Assert.Single(Directory.GetFiles(folder, "switchwatch.db.schema-v4-*.bak"));
            await using (var current = new SqliteConnection($"Data Source={path}"))
            {
                await current.OpenAsync();
                await using var command = current.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type='table' AND name IN ('pairing_codes', 'api_tokens');
                    """;
                Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
            await using (var backup = new SqliteConnection($"Data Source={backupPath}"))
            {
                await backup.OpenAsync();
                await using var command = backup.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type='table' AND name IN ('pairing_codes', 'api_tokens');
                    """;
                Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
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
                Assert.Equal(5, readiness.SchemaVersion);
            }
            Assert.Equal(initialCheck, store.LastIntegrityCheckUtc);

            await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM schema_migrations WHERE version=5;";
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
            Assert.Equal(4, refreshed.SchemaVersion);
            Assert.True(store.LastIntegrityCheckUtc > initialCheck);

            var writeFailure = await Assert.ThrowsAsync<AgentOperationException>(() =>
                store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01", "blocked-write",
                    DateTimeOffset.UtcNow, new System.Text.Json.Nodes.JsonObject { ["value"] = 1 })));
            Assert.Equal(AgentErrorCodes.StorageWriteFailed, writeFailure.Code);
            Assert.Null(await store.GetSnapshotAsync("TEST-SW-01", "blocked-write"));
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
    public async Task DatabaseEnforcesOneUnrecoveredActiveEventPerCondition()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ActiveConditionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var options = new AgentOptions { DataDirectory = folder };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Critical,
                "uplink-down", "Uplink down", "Sanitized", EventState.New, "uplink:24",
                IsActiveCondition: true));

            var conflict = await Assert.ThrowsAsync<AgentOperationException>(() => store.InsertEventAsync(
                new NewEvent("TEST-SW-01", EventSeverity.Critical, "uplink-down", "Duplicate",
                    "Sanitized", EventState.New, "uplink:24", IsActiveCondition: true)));
            Assert.Equal(409, conflict.StatusCode);

            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info,
                "independent", "Independent", "Sanitized", EventState.New, "info:1"));
            Assert.Equal(1, (await store.GetEventSummaryAsync()).ActiveCritical);
            Assert.True((await store.CheckReadinessAsync()).Ready);
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

    [Theory]
    [InlineData("IES4224GP")]
    [InlineData("IES4028XP")]
    [InlineData("IES4226XP")]
    public void ConfigurationAcceptsSupportedSamsungModels(string model)
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.GetTempPath(),
            Switches = [new SwitchOptions { Model = model }]
        };

        AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath());

        Assert.Equal(model, options.Switches[0].Model);
    }

    [Fact]
    public void ConfigurationRejectsUnsupportedModel()
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.GetTempPath(),
            Switches = [new SwitchOptions { Model = "UNSUPPORTED-SWITCH" }]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));
        Assert.Equal("CONFIG_INVALID", exception.Code);
    }

    [Fact]
    public void ConfigurationAcceptsMultipleUniqueDevicesAndRejectsDuplicateIds()
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.GetTempPath(),
            MaxConcurrentDevices = 2,
            Switches =
            [
                new SwitchOptions { Id = "SW-01", Model = "IES4224GP", Host = "192.0.2.10" },
                new SwitchOptions { Id = "SW-02", Model = "IES4028XP", Host = "192.0.2.11" },
                new SwitchOptions { Id = "SW-03", Model = "IES4226XP", Host = "192.0.2.12" }
            ]
        };

        AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath());
        Assert.Equal(3, options.Switches.Count);

        options.Switches[2].Id = "sw-01";
        var duplicate = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));
        Assert.Equal("CONFIG_INVALID", duplicate.Code);
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
    public void ProductionConfigurationAcceptsHttpWithoutCertificateOrApplicationToken()
    {
        var options = new AgentOptions
        {
            MockMode = false,
            DataDirectory = Path.GetTempPath(),
            ListenUrl = "http://0.0.0.0:18443",
            Switches = [new SwitchOptions()]
        };

        AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath());

        Assert.Equal("http://0.0.0.0:18443", options.ListenUrl);
    }

    [Fact]
    public void ConfigurationRejectsHttpsListenUrl()
    {
        var options = new AgentOptions
        {
            MockMode = false,
            DataDirectory = Path.GetTempPath(),
            ListenUrl = "https://0.0.0.0:18443",
            Switches = [new SwitchOptions()]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));
        Assert.Contains("HTTP", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionConfigurationRejectsEphemeralListenPort()
    {
        var options = new AgentOptions
        {
            MockMode = false,
            DataDirectory = Path.GetTempPath(),
            ListenUrl = "http://127.0.0.1:0",
            Switches = [new SwitchOptions()]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));

        Assert.Equal("CONFIG_INVALID", exception.Code);
        Assert.Contains("HTTP", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
