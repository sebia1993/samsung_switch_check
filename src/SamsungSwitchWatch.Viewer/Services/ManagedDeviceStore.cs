using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public class ManagedDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private readonly IViewerSecretProtector _protector;

    public ManagedDeviceStore(string? path = null, IViewerSecretProtector? protector = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamsungSwitchWatch",
            "viewer-devices.json");
        _protector = protector ?? new CurrentUserSecretProtector();
    }

    public string Path => _path;

    public IReadOnlyList<ManagedDeviceProfile> Load()
    {
        lock (_sync)
        {
            return LoadUnsafe()
                .Select(item => item.Copy())
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
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
            catch (Exception exception) when (exception is not OutOfMemoryException)
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
            catch
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

    private IReadOnlyList<ManagedDeviceProfile> LoadUnsafe()
    {
        if (!File.Exists(_path)) return [];
        DeviceStoreEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DeviceStoreEnvelope>(
                File.ReadAllText(_path, Encoding.UTF8), JsonOptions);
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException
            or DecoderFallbackException)
        {
            QuarantineCorruptFile();
            return [];
        }

        var devices = envelope?.Devices ?? [];
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
        var valid = devices.Where(IsStructurallyValid).ToArray();
        var credentialStateChanged = false;
        foreach (var item in valid)
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
        if (migrated || credentialStateChanged) SaveUnsafe(valid);
        return valid;
    }

    private void SaveUnsafe(IEnumerable<ManagedDeviceProfile> devices)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var envelope = new DeviceStoreEnvelope
            {
                SchemaVersion = 1,
                Devices = devices.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(envelope, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private void QuarantineCorruptFile()
    {
        if (!File.Exists(_path)) return;
        try
        {
            File.Move(_path, _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}", false);
        }
        catch { }
    }

    private static bool IsStructurallyValid(ManagedDeviceProfile item) =>
        !string.IsNullOrWhiteSpace(item.Id)
        && !string.IsNullOrWhiteSpace(item.DisplayName)
        && SupportedSwitchModels.Contains(item.Model)
        && item.Port == 23
        && !string.IsNullOrWhiteSpace(item.ProtectedUsername)
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
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsCredentialCorrupt(ManagedDeviceProfile profile) =>
        profile.LastConnectionTestCode?.Equals(
            "VIEWER_CREDENTIAL_CORRUPT",
            StringComparison.Ordinal) == true;

    private sealed class DeviceStoreEnvelope
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ManagedDeviceProfile> Devices { get; set; } = [];
    }
}
