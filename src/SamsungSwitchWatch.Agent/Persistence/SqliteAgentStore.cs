using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;

namespace SamsungSwitchWatch.Agent.Persistence;

public sealed class SqliteAgentStore(AgentOptions options, ILogger<SqliteAgentStore> logger)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _initialized;

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

            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS snapshots (
                    device_id TEXT NOT NULL,
                    command_id TEXT NOT NULL,
                    captured_utc TEXT NOT NULL,
                    data_json TEXT NOT NULL,
                    PRIMARY KEY (device_id, command_id)
                );
                CREATE TABLE IF NOT EXISTS events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL UNIQUE,
                    device_id TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    type TEXT NOT NULL,
                    title TEXT NOT NULL,
                    message TEXT NOT NULL,
                    state TEXT NOT NULL,
                    occurred_utc TEXT NOT NULL,
                    acknowledged_utc TEXT NULL,
                    recovered_utc TEXT NULL,
                    condition_key TEXT NOT NULL,
                    details_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_events_condition ON events(device_id, condition_key, state);
                CREATE INDEX IF NOT EXISTS ix_events_occurred ON events(occurred_utc);
                CREATE TABLE IF NOT EXISTS audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_utc TEXT NOT NULL,
                    action TEXT NOT NULL,
                    actor TEXT NOT NULL,
                    device_id TEXT NULL,
                    outcome TEXT NOT NULL,
                    detail TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_audit_occurred ON audit(occurred_utc);
                CREATE TABLE IF NOT EXISTS raw_blobs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    device_id TEXT NOT NULL,
                    command_id TEXT NOT NULL,
                    captured_utc TEXT NOT NULL,
                    content BLOB NOT NULL,
                    size_bytes INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_raw_captured ON raw_blobs(captured_utc);
                CREATE TABLE IF NOT EXISTS pairing_codes (
                    code_hash TEXT PRIMARY KEY,
                    created_utc TEXT NOT NULL,
                    expires_utc TEXT NOT NULL,
                    used_utc TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS api_tokens (
                    token_hash TEXT PRIMARY KEY,
                    created_utc TEXT NOT NULL,
                    last_used_utc TEXT NULL,
                    revoked_utc TEXT NULL
                );
                INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(1, $now);
                """;
            command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
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
        var id = item.Id ?? Guid.NewGuid().ToString("N");
        var occurred = item.OccurredUtc ?? DateTimeOffset.UtcNow;
        var details = item.Details ?? new Dictionary<string, string>();
        long sequence = 0;
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO events(event_id, device_id, severity, type, title, message, state,
                    occurred_utc, acknowledged_utc, recovered_utc, condition_key, details_json)
                VALUES($id, $device, $severity, $type, $title, $message, $state,
                    $occurred, NULL, $recovered, $condition, $details);
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
            command.Parameters.AddWithValue("$recovered", item.State == EventState.Recovered ? Format(occurred) : DBNull.Value);
            command.Parameters.AddWithValue("$condition", item.ConditionKey);
            command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(details, JsonDefaults.Serializer));
            sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);

        return new StructuredEvent(sequence, id, item.DeviceId, item.Severity, item.Type, item.Title,
            item.Message, item.State, occurred, null,
            item.State == EventState.Recovered ? occurred : null, item.ConditionKey, details);
    }

    public async Task<IReadOnlyList<StructuredEvent>> GetEventsAfterAsync(long after, int limit = 500, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var results = new List<StructuredEvent>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, event_id, device_id, severity, type, title, message, state,
                   occurred_utc, acknowledged_utc, recovered_utc, condition_key, details_json
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

    public async Task<EventSummary> GetEventSummaryAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(MAX(sequence), 0),
                COALESCE(SUM(CASE WHEN severity = $critical AND state <> $recovered THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN state = $new THEN 1 ELSE 0 END), 0)
            FROM events;
            """;
        command.Parameters.AddWithValue("$critical", EventSeverity.Critical.ToString());
        command.Parameters.AddWithValue("$recovered", EventState.Recovered.ToString());
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
        await ExecuteWriteAsync(async connection =>
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE events SET state=$state, acknowledged_utc=$at
                WHERE event_id=$id AND state=$new;
                """;
            update.Parameters.AddWithValue("$state", EventState.Acknowledged.ToString());
            update.Parameters.AddWithValue("$at", Format(at));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$new", EventState.New.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

        return await GetEventByIdAsync(id, cancellationToken);
    }

    public async Task MarkConditionRecoveredAsync(string deviceId, string conditionKey, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE events SET state=$recovered, recovered_utc=$at
                WHERE device_id=$device AND condition_key=$condition AND state IN ($new, $ack);
                """;
            command.Parameters.AddWithValue("$recovered", EventState.Recovered.ToString());
            command.Parameters.AddWithValue("$at", Format(at));
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$condition", conditionKey);
            command.Parameters.AddWithValue("$new", EventState.New.ToString());
            command.Parameters.AddWithValue("$ack", EventState.Acknowledged.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
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

    public Task InsertRawAsync(string deviceId, string commandId, DateTimeOffset captured, string raw, CancellationToken cancellationToken = default) =>
        InsertRawBytesAsync(deviceId, commandId, captured, Encoding.UTF8.GetBytes(raw), cancellationToken);

    public async Task InsertRawBytesAsync(string deviceId, string commandId, DateTimeOffset captured, byte[] content, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO raw_blobs(device_id, command_id, captured_utc, content, size_bytes)
                VALUES($device, $command, $captured, $content, $size);
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$command", commandId);
            command.Parameters.AddWithValue("$captured", Format(captured));
            command.Parameters.Add("$content", SqliteType.Blob).Value = content;
            command.Parameters.AddWithValue("$size", content.LongLength);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnforceRawLimitAsync(connection, cancellationToken);
        }, cancellationToken);
    }

    public async Task StorePairingCodeAsync(string hash, DateTimeOffset created, DateTimeOffset expires, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO pairing_codes(code_hash, created_utc, expires_utc, used_utc)
                VALUES($hash, $created, $expires, NULL)
                ON CONFLICT(code_hash) DO UPDATE SET created_utc=$created, expires_utc=$expires, used_utc=NULL;
                """;
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$created", Format(created));
            command.Parameters.AddWithValue("$expires", Format(expires));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<bool> ConsumePairingCodeAsync(string hash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var consumed = false;
        await ExecuteWriteAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE pairing_codes SET used_utc=$now
                WHERE code_hash=$hash AND used_utc IS NULL AND expires_utc >= $now;
                """;
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$hash", hash);
            consumed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);
        return consumed;
    }

    public async Task StoreTokenAsync(string hash, DateTimeOffset created, CancellationToken cancellationToken = default)
    {
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO api_tokens(token_hash, created_utc, last_used_utc, revoked_utc)
                VALUES($hash, $created, NULL, NULL);
                """;
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$created", Format(created));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<bool> ValidateAndTouchTokenAsync(string hash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var valid = false;
        await ExecuteWriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE api_tokens SET last_used_utc=$now
                WHERE token_hash=$hash AND revoked_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$hash", hash);
            valid = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, cancellationToken);
        return valid;
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
                  AND NOT (severity = $critical AND state <> $recovered);
                DELETE FROM audit WHERE occurred_utc < $auditCutoff;
                DELETE FROM pairing_codes WHERE expires_utc < $pairingCutoff;
                """;
            command.Parameters.AddWithValue("$rawCutoff", Format(now.AddDays(-Math.Max(0, options.Retention.RawDays))));
            command.Parameters.AddWithValue("$eventCutoff", Format(now.AddDays(-Math.Max(0, options.Retention.EventDays))));
            command.Parameters.AddWithValue("$auditCutoff", Format(now.AddDays(-Math.Max(0, options.Retention.AuditDays))));
            command.Parameters.AddWithValue("$pairingCutoff", Format(now.AddDays(-1)));
            command.Parameters.AddWithValue("$critical", EventSeverity.Critical.ToString());
            command.Parameters.AddWithValue("$recovered", EventState.Recovered.ToString());
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

    private async Task<StructuredEvent?> GetEventByIdAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, event_id, device_id, severity, type, title, message, state,
                   occurred_utc, acknowledged_utc, recovered_utc, condition_key, details_json
            FROM events WHERE event_id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    private static StructuredEvent ReadEvent(SqliteDataReader reader)
    {
        var details = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(12), JsonDefaults.Serializer)
            ?? new Dictionary<string, string>();
        return new StructuredEvent(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            Enum.Parse<EventSeverity>(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            Enum.Parse<EventState>(reader.GetString(7)), Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : Parse(reader.GetString(10)),
            reader.GetString(11), details);
    }

    private async Task ExecuteWriteAsync(Func<SqliteConnection, Task> action, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await action(connection);
        }
        catch (AgentOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogError("Agent storage write failed with {ErrorCode}.", AgentErrorCodes.StorageWriteFailed);
            throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed, "Agent storage write failed.", 503);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task EnforceRawLimitAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var maxBytes = Math.Max(0L, options.Retention.RawMaxMegabytes) * 1024L * 1024L;
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, size_bytes FROM raw_blobs ORDER BY captured_utc DESC, id DESC";
        var deleteIds = new List<long>();
        long retained = 0;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var size = reader.GetInt64(1);
                if (retained + size > maxBytes)
                {
                    deleteIds.Add(id);
                }
                else
                {
                    retained += size;
                }
            }
        }

        foreach (var id in deleteIds)
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM raw_blobs WHERE id=$id";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken) => _initialized
        ? Task.CompletedTask
        : InitializeAsync(cancellationToken);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
