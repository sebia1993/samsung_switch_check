using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Persistence;

public sealed class SqliteAgentStore(
    AgentOptions options,
    ILogger<SqliteAgentStore> logger,
    IRawOutputProtector? rawOutputProtector = null)
{
    private const int CurrentSchemaVersion = 5;
    private const int CurrentRawProtectionVersion = 1;
    private const string EventColumns = "sequence, event_id, device_id, severity, type, title, message, state, occurred_utc, acknowledged_utc, recovered_utc, condition_key, details_json, is_active_condition";
    private const string EventColumnsWithAlias = "e.sequence, e.event_id, e.device_id, e.severity, e.type, e.title, e.message, e.state, e.occurred_utc, e.acknowledged_utc, e.recovered_utc, e.condition_key, e.details_json, e.is_active_condition";
    private readonly string _databasePath = options.DatabasePath;
    private readonly IRawOutputProtector _rawOutputProtector = rawOutputProtector ?? new RawOutputProtector(options);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _integrityGate = new(1, 1);
    private volatile bool _initialized;
    private volatile string? _initializationErrorCode;
    private volatile string? _integrityErrorCode;
    private volatile int _schemaVersion;
    private long _lastIntegrityCheckUtcTicks;

    public bool IsInitialized => _initialized;
    public string? InitializationErrorCode => _initializationErrorCode;
    public bool WritesAllowed => _initialized && _integrityErrorCode is null &&
                                 _schemaVersion == CurrentSchemaVersion;
    public DateTimeOffset? LastIntegrityCheckUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastIntegrityCheckUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            var databaseExists = File.Exists(_databasePath) && new FileInfo(_databasePath).Length > 0;
            if (databaseExists)
            {
                await ValidateDatabaseAsync(cancellationToken);
                await BackupBeforeMigrationIfRequiredAsync(cancellationToken);
            }

            await using var connection = await OpenAsync(cancellationToken);
            await ApplyMigrationsAsync(connection, cancellationToken);
            await EnsureEventChangeSnapshotsAsync(connection, cancellationToken);
            await ValidateDatabaseAsync(connection, cancellationToken);
            _schemaVersion = await GetSchemaVersionAsync(connection, cancellationToken);
            if (_schemaVersion != CurrentSchemaVersion)
            {
                throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                    "Agent database schema is unavailable.", 503);
            }
            _initialized = true;
            _initializationErrorCode = null;
            _integrityErrorCode = null;
            MarkIntegrityChecked(DateTimeOffset.UtcNow);
        }
        catch (AgentOperationException)
        {
            _initializationErrorCode = AgentErrorCodes.StorageWriteFailed;
            _integrityErrorCode = AgentErrorCodes.StorageWriteFailed;
            MarkIntegrityChecked(DateTimeOffset.UtcNow);
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _initializationErrorCode = AgentErrorCodes.StorageWriteFailed;
            _integrityErrorCode = AgentErrorCodes.StorageWriteFailed;
            MarkIntegrityChecked(DateTimeOffset.UtcNow);
            logger.LogError("Agent storage initialization failed with {ErrorCode}.", AgentErrorCodes.StorageWriteFailed);
            throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed, "Agent storage is unavailable.", 503);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<DeviceSnapshot?> GetSnapshotAsync(string deviceId, string commandId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT captured_utc, data_json FROM snapshots WHERE device_id=$device AND command_id=$command";
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$command", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceSnapshot(
            deviceId,
            commandId,
            Parse(reader.GetString(0)),
            JsonNode.Parse(reader.GetString(1))?.AsObject() ?? []);
    }

    public async Task UpsertSnapshotAsync(DeviceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO snapshots(device_id, command_id, captured_utc, data_json)
                VALUES($device, $command, $captured, $json)
                ON CONFLICT(device_id, command_id) DO UPDATE SET
                    captured_utc=excluded.captured_utc,
                    data_json=excluded.data_json;
                """;
            command.Parameters.AddWithValue("$device", snapshot.DeviceId);
            command.Parameters.AddWithValue("$command", snapshot.CommandId);
            command.Parameters.AddWithValue("$captured", Format(snapshot.CapturedUtc));
            command.Parameters.AddWithValue("$json", snapshot.Data.ToJsonString(JsonDefaults.Serializer));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> GetAllSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var results = new List<DeviceSnapshot>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, command_id, captured_utc, data_json FROM snapshots ORDER BY device_id, command_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DeviceSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                Parse(reader.GetString(2)),
                JsonNode.Parse(reader.GetString(3))?.AsObject() ?? []));
        }

        return results;
    }

    public async Task<StructuredEvent> InsertEventAsync(NewEvent item, CancellationToken cancellationToken = default)
    {
        var change = await InsertEventChangeAsync(item, cancellationToken);
        return change.Event;
    }

    public async Task<EventChange> InsertEventChangeAsync(NewEvent item, CancellationToken cancellationToken = default)
    {
        EventChange? result = null;
        await ExecuteTransactionAsync(async (connection, transaction) =>
        {
            result = await InsertEventCoreAsync(connection, transaction, item, EventChangeKind.Created, cancellationToken);
        }, cancellationToken);
        return result!;
    }

    public async Task<IReadOnlyList<StructuredEvent>> GetEventsAfterAsync(long after, int limit = 500, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var results = new List<StructuredEvent>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EventColumns}
            FROM events WHERE sequence > $after ORDER BY sequence LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", Math.Max(0, after));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEvent(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<StructuredEvent>> GetRecentEventsAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var results = new List<StructuredEvent>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EventColumns}
            FROM events ORDER BY sequence DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEvent(reader));
        }
        return results;
    }

    public async Task<EventChangesPage> GetEventChangesAsync(long after, int limit = 500, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var cursor = Math.Max(0, after);
        var pageSize = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenAsync(cancellationToken);
        var highWatermark = await GetChangeHighWatermarkCoreAsync(connection, cancellationToken);
        var changes = new List<EventChange>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT c.change_sequence, c.change_kind, c.event_snapshot_json, {EventColumnsWithAlias}
            FROM event_changes c
            INNER JOIN events e ON e.event_id = c.event_id
            WHERE c.change_sequence > $after AND c.change_sequence <= $high
            ORDER BY c.change_sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", cursor);
        command.Parameters.AddWithValue("$high", highWatermark);
        command.Parameters.AddWithValue("$limit", pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var changeSequence = reader.GetInt64(0);
            var changeKind = Enum.Parse<EventChangeKind>(reader.GetString(1));
            var snapshot = reader.IsDBNull(2)
                ? ReadEvent(reader, 3)
                : JsonSerializer.Deserialize<StructuredEvent>(reader.GetString(2), JsonDefaults.Serializer)
                  ?? throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                      "Event change snapshot is unavailable.", 503);
            changes.Add(new EventChange(changeSequence, changeKind, snapshot));
        }

        var resetRequired = cursor > highWatermark;
        var expected = cursor;
        foreach (var change in changes)
        {
            if (expected == long.MaxValue || change.ChangeSequence != expected + 1)
            {
                resetRequired = true;
                break;
            }
            expected = change.ChangeSequence;
        }
        if (!resetRequired && changes.Count == 0 && cursor < highWatermark)
        {
            resetRequired = true;
        }
        if (resetRequired)
        {
            return new EventChangesPage(highWatermark, highWatermark, false, [], true, highWatermark);
        }

        var nextCursor = changes.Count == 0 ? cursor : changes[^1].ChangeSequence;
        return new EventChangesPage(highWatermark, nextCursor, nextCursor < highWatermark, changes, false, nextCursor);
    }

    public async Task<long> GetChangeHighWatermarkAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        return await GetChangeHighWatermarkCoreAsync(connection, cancellationToken);
    }

    public async Task<EventSummary> GetEventSummaryAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(MAX(sequence), 0),
                (SELECT COUNT(*) FROM (
                    SELECT 1 FROM events active_events
                    WHERE active_events.severity = $critical
                      AND active_events.is_active_condition = 1
                      AND active_events.recovered_utc IS NULL
                    GROUP BY active_events.device_id, active_events.condition_key)),
                COALESCE(SUM(CASE WHEN state = $new THEN 1 ELSE 0 END), 0)
            FROM events;
            """;
        command.Parameters.AddWithValue("$critical", EventSeverity.Critical.ToString());
        command.Parameters.AddWithValue("$new", EventState.New.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new EventSummary(0, 0, 0);
        }

        return new EventSummary(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task<StructuredEvent?> AcknowledgeEventAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var change = await AcknowledgeEventChangeAsync(id, at, cancellationToken);
        return change?.Event ?? await GetEventByIdAsync(id, cancellationToken);
    }

    public async Task<EventChange?> AcknowledgeEventChangeAsync(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        EventChange? change = null;
        await ExecuteTransactionAsync(async (connection, transaction) =>
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE events SET state=$state, acknowledged_utc=$at
                WHERE event_id=$id AND state=$new AND acknowledged_utc IS NULL;
                """;
            update.Parameters.AddWithValue("$state", EventState.Acknowledged.ToString());
            update.Parameters.AddWithValue("$at", Format(at));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$new", EventState.New.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                change = await InsertChangeForEventAsync(connection, transaction, id, EventChangeKind.Acknowledged, at, cancellationToken);
            }
        }, cancellationToken);
        return change;
    }

    public async Task<IReadOnlyList<EventChange>> MarkConditionRecoveredAsync(
        string deviceId,
        string conditionKey,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<EventChange>();
        await ExecuteTransactionAsync(async (connection, transaction) =>
        {
            var ids = new List<string>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT event_id FROM events
                    WHERE device_id=$device AND condition_key=$condition
                      AND is_active_condition=1 AND recovered_utc IS NULL;
                    """;
                select.Parameters.AddWithValue("$device", deviceId);
                select.Parameters.AddWithValue("$condition", conditionKey);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(reader.GetString(0));
                }
            }

            foreach (var id in ids)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE events SET state=$state, recovered_utc=$at WHERE event_id=$id;";
                update.Parameters.AddWithValue("$state", EventState.Recovered.ToString());
                update.Parameters.AddWithValue("$at", Format(at));
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken);
                changes.Add(await InsertChangeForEventAsync(connection, transaction, id, EventChangeKind.Recovered, at, cancellationToken));
            }
        }, cancellationToken);
        return changes;
    }

    public async Task<IReadOnlyList<EventChange>> RecoverConditionAndInsertEventAsync(
        string deviceId,
        string conditionKey,
        DateTimeOffset at,
        NewEvent recoveryEvent,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<EventChange>();
        await ExecuteTransactionAsync(async (connection, transaction) =>
        {
            changes.AddRange(await RecoverConditionCoreAsync(connection, transaction, deviceId,
                conditionKey, at, recoveryEvent, cancellationToken));
        }, cancellationToken);
        return changes;
    }

    public async Task InsertAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO audit(occurred_utc, action, actor, device_id, outcome, detail)
                VALUES($occurred, $action, $actor, $device, $outcome, $detail);
                """;
            command.Parameters.AddWithValue("$occurred", Format(entry.OccurredUtc));
            command.Parameters.AddWithValue("$action", entry.Action);
            command.Parameters.AddWithValue("$actor", entry.Actor);
            command.Parameters.AddWithValue("$device", (object?)entry.DeviceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$outcome", entry.Outcome);
            command.Parameters.AddWithValue("$detail", entry.Detail);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task InsertRawAsync(string deviceId, string commandId, DateTimeOffset captured, string raw, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        try
        {
            await InsertRawBytesAsync(deviceId, commandId, captured, bytes, cancellationToken);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task InsertRawBytesAsync(string deviceId, string commandId, DateTimeOffset captured, byte[] content, CancellationToken cancellationToken = default)
    {
        byte[]? protectedContent = null;
        try
        {
            protectedContent = _rawOutputProtector.Protect(content);
            await ExecuteWriteAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO raw_blobs(
                        device_id, command_id, captured_utc, content, size_bytes, protection_version)
                    VALUES($device, $command, $captured, $content, $size, $protectionVersion);
                    """;
                command.Parameters.AddWithValue("$device", deviceId);
                command.Parameters.AddWithValue("$command", commandId);
                command.Parameters.AddWithValue("$captured", Format(captured));
                command.Parameters.Add("$content", SqliteType.Blob).Value = protectedContent;
                command.Parameters.AddWithValue("$size", protectedContent.LongLength);
                command.Parameters.AddWithValue("$protectionVersion", CurrentRawProtectionVersion);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await EnforceRawLimitAsync(connection, cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            if (protectedContent is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(protectedContent);
            }
            // Raw evidence buffers are ownership-transferred into this method so
            // plaintext does not wait for a later GC cycle after persistence.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(content);
        }
    }

    public async Task<IReadOnlyList<EventChange>> CommitSuccessfulCollectionAsync(
        CollectedOutput output,
        AuditEntry audit,
        IReadOnlyList<NewEvent> newEvents,
        IReadOnlyList<ConditionRecoveryRequest> recoveries,
        CancellationToken cancellationToken = default) =>
        await CommitSuccessfulCollectionAsync(output, [], audit, newEvents, recoveries, cancellationToken);

    public async Task<IReadOnlyList<EventChange>> CommitSuccessfulCollectionAsync(
        CollectedOutput output,
        IReadOnlyList<DeviceSnapshot> stateSnapshots,
        AuditEntry audit,
        IReadOnlyList<NewEvent> newEvents,
        IReadOnlyList<ConditionRecoveryRequest> recoveries,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<EventChange>();
        await ExecuteTransactionAsync(async (connection, transaction) =>
        {
            await using (var raw = connection.CreateCommand())
            {
                raw.Transaction = transaction;
                raw.CommandText = """
                    INSERT INTO raw_blobs(
                        device_id, command_id, captured_utc, content, size_bytes, protection_version)
                    VALUES($device, $command, $captured, $content, $size, $protectionVersion);
                    """;
                var bytes = Encoding.UTF8.GetBytes(output.RawOutput);
                var protectedContent = _rawOutputProtector.Protect(bytes);
                try
                {
                    raw.Parameters.AddWithValue("$device", output.DeviceId);
                    raw.Parameters.AddWithValue("$command", output.CommandId);
                    raw.Parameters.AddWithValue("$captured", Format(output.CapturedUtc));
                    raw.Parameters.Add("$content", SqliteType.Blob).Value = protectedContent;
                    raw.Parameters.AddWithValue("$size", protectedContent.LongLength);
                    raw.Parameters.AddWithValue("$protectionVersion", CurrentRawProtectionVersion);
                    await raw.ExecuteNonQueryAsync(cancellationToken);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(protectedContent);
                }
            }

            foreach (var recovery in recoveries)
            {
                changes.AddRange(await RecoverConditionCoreAsync(connection, transaction,
                    recovery.DeviceId, recovery.ConditionKey, recovery.RecoveredUtc,
                    recovery.RecoveryEvent, cancellationToken));
            }
            foreach (var item in newEvents)
            {
                changes.Add(await InsertEventCoreAsync(connection, transaction, item,
                    EventChangeKind.Created, cancellationToken));
            }

            foreach (var stateSnapshot in stateSnapshots)
            {
                await UpsertSnapshotCoreAsync(connection, transaction, stateSnapshot, cancellationToken);
            }

            await UpsertSnapshotCoreAsync(connection, transaction,
                new DeviceSnapshot(output.DeviceId, output.CommandId, output.CapturedUtc, output.Structured),
                cancellationToken);

            await using (var auditCommand = connection.CreateCommand())
            {
                auditCommand.Transaction = transaction;
                auditCommand.CommandText = """
                    INSERT INTO audit(occurred_utc, action, actor, device_id, outcome, detail)
                    VALUES($occurred, $action, $actor, $device, $outcome, $detail);
                    """;
                auditCommand.Parameters.AddWithValue("$occurred", Format(audit.OccurredUtc));
                auditCommand.Parameters.AddWithValue("$action", audit.Action);
                auditCommand.Parameters.AddWithValue("$actor", audit.Actor);
                auditCommand.Parameters.AddWithValue("$device", (object?)audit.DeviceId ?? DBNull.Value);
                auditCommand.Parameters.AddWithValue("$outcome", audit.Outcome);
                auditCommand.Parameters.AddWithValue("$detail", audit.Detail);
                await auditCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await EnforceRawLimitAsync(connection, cancellationToken, transaction);
        }, cancellationToken);
        return changes;
    }

    public async Task<IReadOnlyList<EventChange>> CommitCollectorStateAsync(
        IReadOnlyList<DeviceSnapshot> stateSnapshots,
        AuditEntry audit,
        IReadOnlyList<NewEvent> newEvents,
        IReadOnlyList<ConditionRecoveryRequest> recoveries,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<EventChange>();
        await ExecuteTransactionAsync(async (connection, transaction) =>
        {
            foreach (var stateSnapshot in stateSnapshots)
            {
                await UpsertSnapshotCoreAsync(connection, transaction, stateSnapshot, cancellationToken);
            }
            foreach (var recovery in recoveries)
            {
                changes.AddRange(await RecoverConditionCoreAsync(connection, transaction,
                    recovery.DeviceId, recovery.ConditionKey, recovery.RecoveredUtc,
                    recovery.RecoveryEvent, cancellationToken));
            }
            foreach (var item in newEvents)
            {
                changes.Add(await InsertEventCoreAsync(connection, transaction, item,
                    EventChangeKind.Created, cancellationToken));
            }
            await InsertAuditCoreAsync(connection, transaction, audit, cancellationToken);
        }, cancellationToken);
        return changes;
    }

    public async Task RunRetentionAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM raw_blobs WHERE captured_utc < $rawCutoff;
                DELETE FROM events
                WHERE occurred_utc < $eventCutoff
                  AND NOT (is_active_condition = 1 AND recovered_utc IS NULL);
                DELETE FROM audit WHERE occurred_utc < $auditCutoff;
                """;
            command.Parameters.AddWithValue("$rawCutoff", Format(now.AddDays(-Math.Max(0, options.Retention.RawDays))));
            command.Parameters.AddWithValue("$eventCutoff", Format(now.AddDays(-Math.Max(0, options.Retention.EventDays))));
            command.Parameters.AddWithValue("$auditCutoff", Format(now.AddDays(-Math.Max(0, options.Retention.AuditDays))));
            await command.ExecuteNonQueryAsync(cancellationToken);

            await EnforceRawLimitAsync(connection, cancellationToken);
        }, cancellationToken);
    }

    public async Task<(long RawCount, long EventCount, long AuditCount)> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        static async Task<long> Count(SqliteConnection connection, string table, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        }

        return (await Count(connection, "raw_blobs", cancellationToken),
            await Count(connection, "events", cancellationToken),
            await Count(connection, "audit", cancellationToken));
    }

    public async Task<StructuredEvent?> GetEventByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {EventColumns}
            FROM events WHERE event_id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    private static StructuredEvent ReadEvent(SqliteDataReader reader, int offset = 0)
    {
        var details = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(offset + 12), JsonDefaults.Serializer)
            ?? new Dictionary<string, string>();
        return new StructuredEvent(
            reader.GetInt64(offset), reader.GetString(offset + 1), reader.GetString(offset + 2),
            Enum.Parse<EventSeverity>(reader.GetString(offset + 3)), reader.GetString(offset + 4), reader.GetString(offset + 5), reader.GetString(offset + 6),
            Enum.Parse<EventState>(reader.GetString(offset + 7)), Parse(reader.GetString(offset + 8)),
            reader.IsDBNull(offset + 9) ? null : Parse(reader.GetString(offset + 9)),
            reader.IsDBNull(offset + 10) ? null : Parse(reader.GetString(offset + 10)),
            reader.GetString(offset + 11), details, reader.GetInt64(offset + 13) != 0);
    }

    public Task<(bool Ready, string? ErrorCode, int SchemaVersion)> CheckReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized)
        {
            (bool Ready, string? ErrorCode, int SchemaVersion) result = (false,
                _initializationErrorCode ?? AgentErrorCodes.StorageWriteFailed,
                _schemaVersion);
            return Task.FromResult(result);
        }

        var ready = _integrityErrorCode is null && _schemaVersion == CurrentSchemaVersion;
        (bool Ready, string? ErrorCode, int SchemaVersion) readiness = (ready,
            ready ? null : _integrityErrorCode ?? AgentErrorCodes.StorageWriteFailed,
            _schemaVersion);
        return Task.FromResult(readiness);
    }

    public async Task<bool> RefreshIntegrityStatusAsync(CancellationToken cancellationToken = default)
    {
        await _integrityGate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                _integrityErrorCode = _initializationErrorCode ?? AgentErrorCodes.StorageWriteFailed;
                MarkIntegrityChecked(DateTimeOffset.UtcNow);
                return false;
            }

            await using var connection = await OpenAsync(cancellationToken);
            await ValidateDatabaseAsync(connection, cancellationToken);
            var schemaVersion = await GetSchemaVersionAsync(connection, cancellationToken);
            _schemaVersion = schemaVersion;
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                    "Agent database schema is unavailable.", 503);
            }
            _integrityErrorCode = null;
            MarkIntegrityChecked(DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is AgentOperationException or SqliteException or IOException or UnauthorizedAccessException)
        {
            _integrityErrorCode = AgentErrorCodes.StorageWriteFailed;
            MarkIntegrityChecked(DateTimeOffset.UtcNow);
            logger.LogWarning("Periodic Agent storage integrity check failed with {ErrorCode}.",
                AgentErrorCodes.StorageWriteFailed);
            return false;
        }
        finally
        {
            _integrityGate.Release();
        }
    }

    private async Task<EventChange> InsertEventCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewEvent item,
        EventChangeKind changeKind,
        CancellationToken cancellationToken)
    {
        var id = item.Id ?? Guid.NewGuid().ToString("N");
        var occurred = item.OccurredUtc ?? DateTimeOffset.UtcNow;
        var details = item.Details ?? new Dictionary<string, string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events(event_id, device_id, severity, type, title, message, state,
                occurred_utc, acknowledged_utc, recovered_utc, condition_key, details_json, is_active_condition)
            VALUES($id, $device, $severity, $type, $title, $message, $state,
                $occurred, $acknowledged, $recovered, $condition, $details, $active);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$device", item.DeviceId);
        command.Parameters.AddWithValue("$severity", item.Severity.ToString());
        command.Parameters.AddWithValue("$type", item.Type);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$message", item.Message);
        command.Parameters.AddWithValue("$state", item.State.ToString());
        command.Parameters.AddWithValue("$occurred", Format(occurred));
        command.Parameters.AddWithValue("$acknowledged", item.State == EventState.Acknowledged ? Format(occurred) : DBNull.Value);
        command.Parameters.AddWithValue("$recovered", item.State == EventState.Recovered ? Format(occurred) : DBNull.Value);
        command.Parameters.AddWithValue("$condition", item.ConditionKey);
        command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(details, JsonDefaults.Serializer));
        command.Parameters.AddWithValue("$active", item.IsActiveCondition ? 1 : 0);
        var eventSequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var created = new StructuredEvent(eventSequence, id, item.DeviceId, item.Severity, item.Type, item.Title,
            item.Message, item.State, occurred,
            item.State == EventState.Acknowledged ? occurred : null,
            item.State == EventState.Recovered ? occurred : null,
            item.ConditionKey, details, item.IsActiveCondition);
        var changeSequence = await InsertChangeCoreAsync(connection, transaction, created, changeKind, occurred,
            cancellationToken);
        return new EventChange(changeSequence, changeKind, created);
    }

    private static async Task UpsertSnapshotCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DeviceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshots(device_id, command_id, captured_utc, data_json)
            VALUES($device, $command, $captured, $json)
            ON CONFLICT(device_id, command_id) DO UPDATE SET
                captured_utc=excluded.captured_utc,
                data_json=excluded.data_json;
            """;
        command.Parameters.AddWithValue("$device", snapshot.DeviceId);
        command.Parameters.AddWithValue("$command", snapshot.CommandId);
        command.Parameters.AddWithValue("$captured", Format(snapshot.CapturedUtc));
        command.Parameters.AddWithValue("$json", snapshot.Data.ToJsonString(JsonDefaults.Serializer));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit(occurred_utc, action, actor, device_id, outcome, detail)
            VALUES($occurred, $action, $actor, $device, $outcome, $detail);
            """;
        command.Parameters.AddWithValue("$occurred", Format(audit.OccurredUtc));
        command.Parameters.AddWithValue("$action", audit.Action);
        command.Parameters.AddWithValue("$actor", audit.Actor);
        command.Parameters.AddWithValue("$device", (object?)audit.DeviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", audit.Outcome);
        command.Parameters.AddWithValue("$detail", audit.Detail);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<EventChange>> RecoverConditionCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string conditionKey,
        DateTimeOffset at,
        NewEvent recoveryEvent,
        CancellationToken cancellationToken)
    {
        var changes = new List<EventChange>();
        var ids = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT event_id FROM events
                WHERE device_id=$device AND condition_key=$condition
                  AND is_active_condition=1 AND recovered_utc IS NULL;
                """;
            select.Parameters.AddWithValue("$device", deviceId);
            select.Parameters.AddWithValue("$condition", conditionKey);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        foreach (var id in ids)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE events SET state=$state, recovered_utc=$at WHERE event_id=$id;";
            update.Parameters.AddWithValue("$state", EventState.Recovered.ToString());
            update.Parameters.AddWithValue("$at", Format(at));
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
            changes.Add(await InsertChangeForEventAsync(connection, transaction, id,
                EventChangeKind.Recovered, at, cancellationToken));
        }

        var normalizedRecovery = recoveryEvent with { OccurredUtc = recoveryEvent.OccurredUtc ?? at };
        changes.Add(await InsertEventCoreAsync(connection, transaction, normalizedRecovery,
            EventChangeKind.Created, cancellationToken));
        return changes;
    }

    private async Task<EventChange> InsertChangeForEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        EventChangeKind kind,
        DateTimeOffset occurred,
        CancellationToken cancellationToken)
    {
        StructuredEvent snapshot;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"SELECT {EventColumns} FROM events WHERE event_id=$id;";
            command.Parameters.AddWithValue("$id", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                    "Event change target is unavailable.", 503);
            }
            snapshot = ReadEvent(reader);
        }
        var changeSequence = await InsertChangeCoreAsync(connection, transaction, snapshot, kind, occurred,
            cancellationToken);
        return new EventChange(changeSequence, kind, snapshot);
    }

    private static async Task<long> InsertChangeCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StructuredEvent snapshot,
        EventChangeKind kind,
        DateTimeOffset occurred,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO event_changes(event_id, change_kind, occurred_utc, event_snapshot_json)
            VALUES($id, $kind, $occurred, $snapshot);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$occurred", Format(occurred));
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, JsonDefaults.Serializer));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<long> GetChangeHighWatermarkCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='event_changes'), 0);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken cancellationToken)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await action(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await pragmas.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                """;
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);
        }

        var version = await GetSchemaVersionAsync(connection, cancellationToken);
        if (version > CurrentSchemaVersion)
        {
            throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                "Agent database schema is newer than this application.", 503);
        }

        if (version < 1)
        {
            await ApplyMigrationAsync(connection, 1, """
                CREATE TABLE IF NOT EXISTS snapshots (
                    device_id TEXT NOT NULL, command_id TEXT NOT NULL, captured_utc TEXT NOT NULL,
                    data_json TEXT NOT NULL, PRIMARY KEY (device_id, command_id));
                CREATE TABLE IF NOT EXISTS events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE,
                    device_id TEXT NOT NULL, severity TEXT NOT NULL, type TEXT NOT NULL,
                    title TEXT NOT NULL, message TEXT NOT NULL, state TEXT NOT NULL,
                    occurred_utc TEXT NOT NULL, acknowledged_utc TEXT NULL, recovered_utc TEXT NULL,
                    condition_key TEXT NOT NULL, details_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_events_condition ON events(device_id, condition_key, state);
                CREATE INDEX IF NOT EXISTS ix_events_occurred ON events(occurred_utc);
                CREATE TABLE IF NOT EXISTS audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_utc TEXT NOT NULL,
                    action TEXT NOT NULL, actor TEXT NOT NULL, device_id TEXT NULL,
                    outcome TEXT NOT NULL, detail TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_audit_occurred ON audit(occurred_utc);
                CREATE TABLE IF NOT EXISTS raw_blobs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT NOT NULL,
                    command_id TEXT NOT NULL, captured_utc TEXT NOT NULL,
                    content BLOB NOT NULL, size_bytes INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_raw_captured ON raw_blobs(captured_utc);
                """, cancellationToken);
            version = 1;
        }

        if (version < 2)
        {
            await ApplyMigrationAsync(connection, 2, """
                ALTER TABLE events ADD COLUMN is_active_condition INTEGER NOT NULL DEFAULT 0;
                UPDATE events SET is_active_condition=1
                WHERE type IN ('collector-failed', 'uplink-down', 'simulated-down')
                  AND recovered_utc IS NULL;
                CREATE TABLE event_changes (
                    change_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL,
                    change_kind TEXT NOT NULL,
                    occurred_utc TEXT NOT NULL,
                    FOREIGN KEY(event_id) REFERENCES events(event_id) ON DELETE CASCADE);
                CREATE INDEX ix_event_changes_event ON event_changes(event_id, change_sequence);
                INSERT INTO event_changes(event_id, change_kind, occurred_utc)
                SELECT event_id, 'Created', occurred_utc FROM events ORDER BY sequence;
                """, cancellationToken);
            version = 2;
        }

        if (version < 3)
        {
            await ApplyMigrationAsync(connection, 3, """
                ALTER TABLE event_changes ADD COLUMN event_snapshot_json TEXT NULL;
                UPDATE events SET is_active_condition=0
                WHERE is_active_condition=1 AND recovered_utc IS NULL
                  AND sequence NOT IN (
                    SELECT MAX(sequence) FROM events
                    WHERE is_active_condition=1 AND recovered_utc IS NULL
                    GROUP BY device_id, condition_key
                  );
                CREATE UNIQUE INDEX ux_events_active_condition
                    ON events(device_id, condition_key)
                    WHERE is_active_condition=1 AND recovered_utc IS NULL;
                CREATE TABLE raw_storage_stats (
                    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                    total_bytes INTEGER NOT NULL CHECK(total_bytes >= 0));
                INSERT INTO raw_storage_stats(singleton, total_bytes)
                    VALUES(1, COALESCE((SELECT SUM(size_bytes) FROM raw_blobs), 0));
                CREATE TRIGGER raw_blobs_count_insert AFTER INSERT ON raw_blobs
                BEGIN
                    UPDATE raw_storage_stats SET total_bytes=total_bytes + NEW.size_bytes
                    WHERE singleton=1;
                END;
                CREATE TRIGGER raw_blobs_count_delete AFTER DELETE ON raw_blobs
                BEGIN
                    UPDATE raw_storage_stats SET total_bytes=MAX(0, total_bytes - OLD.size_bytes)
                    WHERE singleton=1;
                END;
                """, cancellationToken);
            version = 3;
        }

        if (version < 4)
        {
            await ApplyMigrationAsync(connection, 4, """
                PRAGMA secure_delete=ON;
                DROP TRIGGER IF EXISTS raw_blobs_count_insert;
                DROP TRIGGER IF EXISTS raw_blobs_count_delete;
                DROP INDEX IF EXISTS ix_raw_captured;
                DROP TABLE raw_blobs;
                CREATE TABLE raw_blobs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT NOT NULL,
                    command_id TEXT NOT NULL, captured_utc TEXT NOT NULL,
                    content BLOB NOT NULL, size_bytes INTEGER NOT NULL,
                    protection_version INTEGER NOT NULL CHECK(protection_version=1));
                CREATE INDEX ix_raw_captured ON raw_blobs(captured_utc);
                UPDATE raw_storage_stats SET total_bytes=0 WHERE singleton=1;
                CREATE TRIGGER raw_blobs_count_insert AFTER INSERT ON raw_blobs
                BEGIN
                    UPDATE raw_storage_stats SET total_bytes=total_bytes + NEW.size_bytes
                    WHERE singleton=1;
                END;
                CREATE TRIGGER raw_blobs_count_delete AFTER DELETE ON raw_blobs
                BEGIN
                    UPDATE raw_storage_stats SET total_bytes=MAX(0, total_bytes - OLD.size_bytes)
                    WHERE singleton=1;
                END;
                """, cancellationToken);
            version = 4;
        }

        if (version < 5)
        {
            await ApplyMigrationAsync(connection, 5, """
                PRAGMA secure_delete=ON;
                DROP TABLE IF EXISTS pairing_codes;
                DROP TABLE IF EXISTS api_tokens;
                """, cancellationToken);
        }
    }

    private static async Task EnsureEventChangeSnapshotsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var pending = new List<(long Sequence, StructuredEvent Snapshot)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"""
                SELECT c.change_sequence, {EventColumnsWithAlias}
                FROM event_changes c
                INNER JOIN events e ON e.event_id=c.event_id
                WHERE c.event_snapshot_json IS NULL;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pending.Add((reader.GetInt64(0), ReadEvent(reader, 1)));
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in pending)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE event_changes SET event_snapshot_json=$snapshot WHERE change_sequence=$sequence;";
            update.Parameters.AddWithValue("$snapshot",
                JsonSerializer.Serialize(item.Snapshot, JsonDefaults.Serializer));
            update.Parameters.AddWithValue("$sequence", item.Sequence);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int version,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = "INSERT INTO schema_migrations(version, applied_utc) VALUES($version, $now);";
        record.Parameters.AddWithValue("$version", version);
        record.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await record.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task BackupBeforeMigrationIfRequiredAsync(CancellationToken cancellationToken)
    {
        await using var source = await OpenAsync(cancellationToken);
        var version = await GetSchemaVersionAsync(source, cancellationToken);
        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        if (version < 4)
        {
            await SecurelyPurgeLegacyRawAsync(source, cancellationToken);
        }

        var backupPath = $"{_databasePath}.schema-v{version}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task SecurelyPurgeLegacyRawAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='raw_blobs';";
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) == 0)
            {
                return;
            }
        }

        await using (var purge = connection.CreateCommand())
        {
            purge.CommandText = "PRAGMA secure_delete=ON; DELETE FROM raw_blobs;";
            await purge.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ValidateDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ValidateDatabaseAsync(connection, cancellationToken);
    }

    private static async Task ValidateDatabaseAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                "Agent database integrity check failed.", 503);
        }
    }

    private void MarkIntegrityChecked(DateTimeOffset at) =>
        Interlocked.Exchange(ref _lastIntegrityCheckUtcTicks, at.UtcTicks);

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteWriteAsync(Func<SqliteConnection, Task> action, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        ThrowIfWritesBlocked();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfWritesBlocked();
            await using var connection = await OpenAsync(cancellationToken);
            await action(connection);
        }
        catch (AgentOperationException)
        {
            throw;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // A uniqueness or other constraint rejection is an application-level conflict,
            // not evidence that the database itself has lost integrity.
            throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                "Agent storage rejected a conflicting record.", 409);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _integrityErrorCode = AgentErrorCodes.StorageWriteFailed;
            logger.LogError("Agent storage write failed with {ErrorCode}.", AgentErrorCodes.StorageWriteFailed);
            throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed, "Agent storage write failed.", 503);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task EnforceRawLimitAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var maxBytes = Math.Max(0L, options.Retention.RawMaxMegabytes) * 1024L * 1024L;
        const int batchSize = 256;
        while (true)
        {
            var totalBytes = await GetRawBytesAsync(connection, transaction, cancellationToken);
            if (totalBytes <= maxBytes)
            {
                break;
            }

            var deleteCount = await GetRawTrimCountAsync(connection, transaction,
                totalBytes - maxBytes, batchSize, cancellationToken);
            if (deleteCount == 0)
            {
                break;
            }
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM raw_blobs
                WHERE id IN (
                    SELECT id FROM raw_blobs
                    ORDER BY captured_utc ASC, id ASC
                    LIMIT $deleteCount
                );
                """;
            delete.Parameters.AddWithValue("$deleteCount", deleteCount);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                break;
            }
        }
    }

    private static async Task<int> GetRawTrimCountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long bytesToFree,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT size_bytes FROM raw_blobs
            ORDER BY captured_utc ASC, id ASC
            LIMIT $batchSize;
            """;
        command.Parameters.AddWithValue("$batchSize", batchSize);
        long accumulated = 0;
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            accumulated += reader.GetInt64(0);
            count++;
            if (accumulated >= bytesToFree)
            {
                break;
            }
        }
        return count;
    }

    private static async Task<long> GetRawBytesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT total_bytes FROM raw_storage_stats WHERE singleton=1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private void ThrowIfWritesBlocked()
    {
        if (_integrityErrorCode is not null || _schemaVersion != CurrentSchemaVersion)
        {
            throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                "Agent storage writes are paused until integrity is restored.", 503);
        }
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken) => _initialized
        ? Task.CompletedTask
        : InitializeAsync(cancellationToken);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
