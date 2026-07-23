using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public class ManagedDeviceStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private readonly IViewerSecretProtector _protector;
    private readonly IManagedDevicePersistence _persistence;

    public ManagedDeviceStore(string? path = null, IViewerSecretProtector? protector = null)
        : this(path, protector, PhysicalManagedDevicePersistence.Instance)
    {
    }

    internal ManagedDeviceStore(
        string? path,
        IViewerSecretProtector? protector,
        IManagedDevicePersistence persistence)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamsungSwitchWatch",
            "viewer-devices.json");
        _protector = protector ?? new CurrentUserSecretProtector();
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    public string Path => _path;
    public ManagedDeviceLoadStatus LastLoadStatus { get; private set; } = ManagedDeviceLoadStatus.Ok;

    public IReadOnlyList<ManagedDeviceProfile> Load() => LoadWithStatus().Devices;

    internal ManagedDeviceLoadResult LoadWithStatus()
    {
        lock (_sync)
        {
            try
            {
                var devices = LoadUnsafe(allowMigrationWriteFailure: true)
                    .Select(item => item.Copy())
                    .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                return new ManagedDeviceLoadResult(devices, LastLoadStatus);
            }
            catch (DeviceStoreCorruptException)
            {
                return new ManagedDeviceLoadResult([], LastLoadStatus);
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                // A locked, read-only, or temporarily unavailable file is not
                // corrupt. Preserve it and expose a non-normal load state.
                LastLoadStatus = ManagedDeviceLoadStatus.StorageUnavailable;
                return new ManagedDeviceLoadResult([], LastLoadStatus);
            }
        }
    }

    public ManagedDeviceProfile Save(ManagedDeviceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_sync)
        {
            var devices = LoadUnsafe().ToList();
            var existing = string.IsNullOrWhiteSpace(draft.Id)
                ? null
                : devices.FirstOrDefault(item => item.Id.Equals(draft.Id, StringComparison.Ordinal));
            if (!ManagedDeviceValidator.TryValidate(draft, existing is null, out var reason))
            {
                throw new InvalidDataException(reason);
            }
            if (devices.Any(item =>
                    !ReferenceEquals(item, existing)
                    && item.Host.Equals(draft.Host.Trim(), StringComparison.Ordinal)))
            {
                throw new InvalidDataException("같은 장비 IP가 이미 등록되어 있습니다.");
            }

            var existingCredentialCorrupt = existing is not null && IsCredentialCorrupt(existing);
            if (existingCredentialCorrupt && string.IsNullOrEmpty(draft.Password))
            {
                throw new InvalidDataException(
                    "저장된 계정을 읽을 수 없습니다. 로그인 ID와 PW를 다시 입력해 주세요.");
            }
            var existingUsername = existingCredentialCorrupt || existing is null
                ? string.Empty
                : ReadUsername(existing);
            var connectionChanged = existing is null
                                    || existingCredentialCorrupt
                                    || !existing.Host.Equals(draft.Host.Trim(), StringComparison.Ordinal)
                                    || !existing.Model.Equals(draft.Model.Trim(), StringComparison.OrdinalIgnoreCase)
                                    || !existingUsername.Equals(draft.Username.Trim(), StringComparison.Ordinal)
                                    || !string.IsNullOrEmpty(draft.Password)
                                    || draft.ClearEnablePassword
                                    || !string.IsNullOrEmpty(draft.EnablePassword);
            var hasNewTestResult = draft.LastConnectionTestUtc.HasValue
                                   && (existing?.LastConnectionTestUtc is null
                                       || draft.LastConnectionTestUtc > existing.LastConnectionTestUtc);
            var verified = hasNewTestResult
                ? draft.ConnectionVerified
                : !connectionChanged && existing!.ConnectionVerified;
            var profile = new ManagedDeviceProfile
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                DisplayName = draft.DisplayName.Trim(),
                Model = SupportedSwitchModels.All.First(item =>
                    item.Equals(draft.Model.Trim(), StringComparison.OrdinalIgnoreCase)),
                Host = draft.Host.Trim(),
                Port = 23,
                ProtectedUsername = _protector.Protect(draft.Username.Trim()),
                ProtectedPassword = string.IsNullOrEmpty(draft.Password)
                    ? existing?.ProtectedPassword ?? throw new InvalidDataException("로그인 비밀번호를 입력해 주세요.")
                    : _protector.Protect(draft.Password),
                ProtectedEnablePassword = draft.ClearEnablePassword
                    ? null
                    : string.IsNullOrEmpty(draft.EnablePassword)
                        ? existingCredentialCorrupt ? null : existing?.ProtectedEnablePassword
                        : _protector.Protect(draft.EnablePassword),
                ConnectionVerified = verified,
                MonitoringEnabled = verified && draft.MonitoringEnabled,
                LastConnectionTestUtc = draft.LastConnectionTestUtc ?? existing?.LastConnectionTestUtc,
                LastConnectionTestCode = hasNewTestResult
                    ? draft.LastConnectionTestCode
                    : existingCredentialCorrupt
                        ? "VIEWER_CONNECTION_TEST_REQUIRED"
                        : draft.LastConnectionTestCode ?? existing?.LastConnectionTestCode,
                UpdatedUtc = DateTimeOffset.UtcNow
            };

            if (existing is null) devices.Add(profile);
            else devices[devices.IndexOf(existing)] = profile;
            SaveUnsafe(devices);
            return profile.Copy();
        }
    }

    public bool Delete(string id)
    {
        lock (_sync)
        {
            var devices = LoadUnsafe().ToList();
            var removed = devices.RemoveAll(item => item.Id.Equals(id, StringComparison.Ordinal)) > 0;
            if (removed) SaveUnsafe(devices);
            return removed;
        }
    }

    public ManagedDeviceSecrets GetSecrets(string id)
    {
        lock (_sync)
        {
            var profile = LoadUnsafe().FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))
                          ?? throw new KeyNotFoundException("VIEWER_DEVICE_NOT_FOUND");
            try
            {
                return new ManagedDeviceSecrets(
                    ReadUsername(profile),
                    _protector.Unprotect(profile.ProtectedPassword),
                    string.IsNullOrWhiteSpace(profile.ProtectedEnablePassword)
                        ? null
                        : _protector.Unprotect(profile.ProtectedEnablePassword));
            }
            catch (Exception exception) when (IsCredentialProtectionException(exception))
            {
                throw new InvalidDataException("VIEWER_CREDENTIAL_CORRUPT", exception);
            }
        }
    }

    public ManagedDeviceDraft ResolveDraftForOperation(ManagedDeviceDraft input)
    {
        lock (_sync)
        {
            var existing = string.IsNullOrWhiteSpace(input.Id)
                ? null
                : LoadUnsafe().FirstOrDefault(item => item.Id.Equals(input.Id, StringComparison.Ordinal));
            var result = new ManagedDeviceDraft
            {
                Id = input.Id,
                DisplayName = input.DisplayName,
                Model = input.Model,
                Host = input.Host,
                Username = input.Username,
                Password = input.Password,
                EnablePassword = input.EnablePassword,
                ClearEnablePassword = input.ClearEnablePassword,
                MonitoringEnabled = input.MonitoringEnabled,
                ConnectionVerified = input.ConnectionVerified,
                LastConnectionTestUtc = input.LastConnectionTestUtc,
                LastConnectionTestCode = input.LastConnectionTestCode
            };
            if (existing is not null)
            {
                var credentialCorrupt = IsCredentialCorrupt(existing);
                if (string.IsNullOrWhiteSpace(result.Username))
                {
                    if (credentialCorrupt) throw new InvalidDataException("VIEWER_CREDENTIAL_CORRUPT");
                    result.Username = ReadUsername(existing);
                }
                if (string.IsNullOrEmpty(result.Password))
                {
                    if (credentialCorrupt) throw new InvalidDataException("VIEWER_CREDENTIAL_CORRUPT");
                    result.Password = _protector.Unprotect(existing.ProtectedPassword);
                }
                if (!result.ClearEnablePassword && string.IsNullOrEmpty(result.EnablePassword)
                    && !string.IsNullOrWhiteSpace(existing.ProtectedEnablePassword)
                    && !credentialCorrupt)
                {
                    result.EnablePassword = _protector.Unprotect(existing.ProtectedEnablePassword);
                }
            }
            return result;
        }
    }

    public virtual ManagedDeviceProfile MarkConnectionTest(string id, bool success, string code)
    {
        lock (_sync)
        {
            var devices = LoadUnsafe().ToList();
            var profile = devices.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))
                          ?? throw new KeyNotFoundException("VIEWER_DEVICE_NOT_FOUND");
            profile.ConnectionVerified = success;
            profile.MonitoringEnabled = success && profile.MonitoringEnabled;
            profile.LastConnectionTestUtc = DateTimeOffset.UtcNow;
            profile.LastConnectionTestCode = code;
            profile.UpdatedUtc = DateTimeOffset.UtcNow;
            SaveUnsafe(devices);
            return profile.Copy();
        }
    }

    public ManagedDeviceDraft CreateEditDraft(string id)
    {
        lock (_sync)
        {
            var profile = LoadUnsafe().FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))
                          ?? throw new KeyNotFoundException("VIEWER_DEVICE_NOT_FOUND");
            string username;
            try
            {
                username = IsCredentialCorrupt(profile) ? string.Empty : ReadUsername(profile);
            }
            catch (Exception exception) when (IsCredentialProtectionException(exception))
            {
                username = string.Empty;
            }
            return new ManagedDeviceDraft
            {
                Id = profile.Id,
                DisplayName = profile.DisplayName,
                Model = profile.Model,
                Host = profile.Host,
                Username = username,
                MonitoringEnabled = !IsCredentialCorrupt(profile) && profile.MonitoringEnabled,
                ConnectionVerified = !IsCredentialCorrupt(profile) && profile.ConnectionVerified,
                LastConnectionTestUtc = profile.LastConnectionTestUtc,
                LastConnectionTestCode = profile.LastConnectionTestCode
            };
        }
    }

    public ManagedDeviceProfile SetMonitoring(string id, bool enabled)
    {
        lock (_sync)
        {
            var devices = LoadUnsafe().ToList();
            var profile = devices.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))
                          ?? throw new KeyNotFoundException("VIEWER_DEVICE_NOT_FOUND");
            if (enabled && !profile.ConnectionVerified)
            {
                throw new InvalidOperationException("VIEWER_CONNECTION_TEST_REQUIRED");
            }
            profile.MonitoringEnabled = enabled;
            profile.UpdatedUtc = DateTimeOffset.UtcNow;
            SaveUnsafe(devices);
            return profile.Copy();
        }
    }

    private IReadOnlyList<ManagedDeviceProfile> LoadUnsafe(bool allowMigrationWriteFailure = false)
    {
        var storedJson = _persistence.ReadIfExists(_path);
        if (storedJson is null)
        {
            LastLoadStatus = ManagedDeviceLoadStatus.Missing;
            return [];
        }

        DeviceStoreEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DeviceStoreEnvelope>(
                storedJson, JsonOptions);
            ValidateEnvelope(envelope);
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException
            or DecoderFallbackException
            or DeviceStoreFormatException)
        {
            TryQuarantineCorruptFile();
            LastLoadStatus = ManagedDeviceLoadStatus.Corrupt;
            throw new DeviceStoreCorruptException(exception);
        }

        var devices = envelope!.Devices!;
        var migrated = false;
        foreach (var item in devices)
        {
            if (string.IsNullOrWhiteSpace(item.ProtectedUsername)
                && !string.IsNullOrWhiteSpace(item.LegacyUsername))
            {
                item.ProtectedUsername = _protector.Protect(item.LegacyUsername);
                item.LegacyUsername = null;
                migrated = true;
            }
        }

        var credentialStateChanged = false;
        foreach (var item in devices)
        {
            if (CanDecryptCredentials(item)) continue;
            if (!IsCredentialCorrupt(item)
                || item.ConnectionVerified
                || item.MonitoringEnabled)
            {
                item.ConnectionVerified = false;
                item.MonitoringEnabled = false;
                item.LastConnectionTestUtc = DateTimeOffset.UtcNow;
                item.LastConnectionTestCode = "VIEWER_CREDENTIAL_CORRUPT";
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                credentialStateChanged = true;
            }
        }

        if (migrated || credentialStateChanged)
        {
            try
            {
                SaveUnsafe(devices);
            }
            catch (Exception exception) when (
                allowMigrationWriteFailure && IsStorageException(exception))
            {
                // Keep the validated in-memory data available for this session,
                // but do not report the migration as successfully persisted.
                LastLoadStatus = ManagedDeviceLoadStatus.StorageUnavailable;
                return devices;
            }
        }

        LastLoadStatus = ManagedDeviceLoadStatus.Ok;
        return devices;
    }

    private void SaveUnsafe(IEnumerable<ManagedDeviceProfile> devices)
    {
        var envelope = new DeviceStoreEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            Devices = devices.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
        };
        _persistence.WriteAtomically(_path, JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private void TryQuarantineCorruptFile()
    {
        var quarantine = _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            _persistence.Quarantine(_path, quarantine);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            // Keep the corrupt source in place if it cannot be moved. It must
            // never be deleted or treated as a normal empty device list.
        }
    }

    private static void ValidateEnvelope(DeviceStoreEnvelope? envelope)
    {
        if (envelope is null
            || envelope.SchemaVersion != CurrentSchemaVersion
            || envelope.Devices is null)
        {
            throw new DeviceStoreFormatException();
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in envelope.Devices)
        {
            if (!IsStructurallyValid(item)
                || !ids.Add(item.Id)
                || !hosts.Add(item.Host))
            {
                throw new DeviceStoreFormatException();
            }
        }
    }

    private static bool IsStructurallyValid(ManagedDeviceProfile? item) =>
        item is not null
        && !string.IsNullOrWhiteSpace(item.Id)
        && item.Id.Length <= 128
        && !string.IsNullOrWhiteSpace(item.DisplayName)
        && item.DisplayName.Trim().Length <= 80
        && SupportedSwitchModels.Contains(item.Model)
        && IPAddress.TryParse(item.Host, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork
        && item.Port == 23
        && (!string.IsNullOrWhiteSpace(item.ProtectedUsername)
            || !string.IsNullOrWhiteSpace(item.LegacyUsername))
        && !string.IsNullOrWhiteSpace(item.ProtectedPassword);

    private string ReadUsername(ManagedDeviceProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ProtectedUsername))
        {
            return _protector.Unprotect(profile.ProtectedUsername);
        }
        if (!string.IsNullOrWhiteSpace(profile.LegacyUsername))
        {
            return profile.LegacyUsername;
        }
        throw new InvalidDataException("VIEWER_CREDENTIAL_CORRUPT");
    }

    private bool CanDecryptCredentials(ManagedDeviceProfile profile)
    {
        try
        {
            _ = ReadUsername(profile);
            _ = _protector.Unprotect(profile.ProtectedPassword);
            if (!string.IsNullOrWhiteSpace(profile.ProtectedEnablePassword))
            {
                _ = _protector.Unprotect(profile.ProtectedEnablePassword);
            }
            return true;
        }
        catch (Exception exception) when (IsCredentialProtectionException(exception))
        {
            return false;
        }
    }

    private static bool IsCredentialCorrupt(ManagedDeviceProfile profile) =>
        profile.LastConnectionTestCode?.Equals(
            "VIEWER_CREDENTIAL_CORRUPT",
            StringComparison.Ordinal) == true;

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static bool IsCredentialProtectionException(Exception exception) =>
        exception is InvalidDataException
            or FormatException
            or ArgumentException
            or System.ComponentModel.Win32Exception
            or PlatformNotSupportedException;

    private sealed class DeviceStoreEnvelope
    {
        public int SchemaVersion { get; set; }
        public List<ManagedDeviceProfile>? Devices { get; set; }
    }

    private sealed class DeviceStoreFormatException : Exception
    {
    }

    private sealed class DeviceStoreCorruptException(Exception innerException)
        : Exception("VIEWER_DEVICE_STORE_CORRUPT", innerException)
    {
    }
}

public enum ManagedDeviceLoadStatus
{
    Ok,
    Missing,
    Corrupt,
    StorageUnavailable
}

internal sealed record ManagedDeviceLoadResult(
    IReadOnlyList<ManagedDeviceProfile> Devices,
    ManagedDeviceLoadStatus Status);

internal interface IManagedDevicePersistence
{
    string? ReadIfExists(string path);
    void WriteAtomically(string path, string content);
    void Quarantine(string path, string destination);
}

internal sealed class PhysicalManagedDevicePersistence : IManagedDevicePersistence
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PhysicalManagedDevicePersistence Instance { get; } = new();

    private PhysicalManagedDevicePersistence()
    {
    }

    public string? ReadIfExists(string path)
    {
        try
        {
            return File.ReadAllText(path, StrictUtf8);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void WriteAtomically(string path, string content)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))
                        ?? throw new InvalidOperationException("VIEWER_DEVICE_PATH_INVALID");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup only; do not mask the primary write result.
            }
            catch (UnauthorizedAccessException)
            {
                // See IOException comment above.
            }
        }
    }

    public void Quarantine(string path, string destination) =>
        File.Move(path, destination, false);
}
