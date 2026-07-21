using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent.Security;

public interface ICredentialProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
}

public sealed class DpapiCredentialProtector(AgentOptions options) : ICredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SamsungSwitchWatch.Agent.Credentials.v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (OperatingSystem.IsWindows())
        {
            var copy = plaintext.ToArray();
            try
            {
                return ProtectedData.Protect(copy, Entropy, DataProtectionScope.LocalMachine);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(copy);
            }
        }

        if (!options.MockMode)
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required outside mock mode.");
        }

        return MockProtect(plaintext);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
    {
        if (OperatingSystem.IsWindows())
        {
            var copy = protectedBytes.ToArray();
            try
            {
                return ProtectedData.Unprotect(copy, Entropy, DataProtectionScope.LocalMachine);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(copy);
            }
        }

        if (!options.MockMode)
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required outside mock mode.");
        }

        return MockUnprotect(protectedBytes);
    }

    private static byte[] MockProtect(ReadOnlySpan<byte> bytes)
    {
        var result = new byte[bytes.Length + 5];
        Encoding.ASCII.GetBytes("MOCK:").CopyTo(result, 0);
        bytes.CopyTo(result.AsSpan(5));
        return result;
    }

    private static byte[] MockUnprotect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5 || !bytes[..5].SequenceEqual("MOCK:"u8))
        {
            throw new CryptographicException("Mock credential envelope is invalid.");
        }

        return bytes[5..].ToArray();
    }
}

public sealed record SwitchCredential(string Username, string Password);

public interface ICredentialVault
{
    Task StoreAsync(string credentialId, SwitchCredential credential, CancellationToken cancellationToken = default);
    Task<SwitchCredential?> GetAsync(string credentialId, CancellationToken cancellationToken = default);
}

public sealed class FileCredentialVault(AgentOptions options, ICredentialProtector protector) : ICredentialVault
{
    private readonly string _folder = Path.Combine(options.DataDirectory, "credentials");

    public async Task StoreAsync(string credentialId, SwitchCredential credential, CancellationToken cancellationToken = default)
    {
        ValidateId(credentialId);
        _ = new TelnetCredentials(credential.Username, credential.Password);

        Directory.CreateDirectory(_folder);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential, JsonDefaults.Serializer);
        byte[]? encrypted = null;
        var path = Path.Combine(_folder, $"{credentialId}.bin");
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            encrypted = protector.Protect(plaintext);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // A stale uniquely named temp file is safer than hiding the
                // original credential write failure.
            }

            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }

            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<SwitchCredential?> GetAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        ValidateId(credentialId);
        var path = Path.Combine(_folder, $"{credentialId}.bin");
        if (!File.Exists(path))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[]? plaintext = null;
        try
        {
            plaintext = protector.Unprotect(encrypted);
            return JsonSerializer.Deserialize<SwitchCredential>(plaintext, JsonDefaults.Serializer)
                ?? throw new JsonException("Credential payload was empty.");
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw new AgentOperationException(
                "CREDENTIAL_CORRUPT",
                "The stored switch credential cannot be read safely.",
                StatusCodes.Status500InternalServerError);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            throw new ArgumentException("Credential id is invalid.", nameof(id));
        }
    }
}

public sealed class PairingService(AgentOptions options, SqliteAgentStore store)
{
    private readonly ConcurrentDictionary<string, RealtimeConnectionBucket> _realtimeConnections =
        new(StringComparer.OrdinalIgnoreCase);
    private long _nextRealtimeConnectionId;

    public async Task<(string Code, DateTimeOffset ExpiresUtc)> CreateCodeAsync(CancellationToken cancellationToken = default)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> random = stackalloc byte[12];
        RandomNumberGenerator.Fill(random);
        Span<char> characters = stackalloc char[14];
        var target = 0;
        for (var i = 0; i < random.Length; i++)
        {
            if (i is 4 or 8)
            {
                characters[target++] = '-';
            }
            characters[target++] = alphabet[random[i] % alphabet.Length];
        }

        var code = new string(characters);
        CryptographicOperations.ZeroMemory(random);
        characters.Clear();
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(Math.Clamp(options.PairingCodeLifetimeMinutes, 1, 60));
        await store.StorePairingCodeAsync(Hash(code), now, expires, cancellationToken);
        return (code, expires);
    }

    public async Task<string> ExchangeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
        {
            throw new AgentOperationException(AgentErrorCodes.PairingInvalid, "Pairing code is invalid, expired, or already used.", 400);
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = DateTimeOffset.UtcNow;
        if (!await store.ConsumePairingCodeAndStoreTokenAsync(
                Hash(code.Trim()), Hash(token), now, cancellationToken))
        {
            throw new AgentOperationException(AgentErrorCodes.PairingInvalid,
                "Pairing code is invalid, expired, or already used.", 400);
        }
        return token;
    }

    public async Task<bool> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 43 ||
            token.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        return await store.ValidateAndTouchTokenAsync(Hash(token), DateTimeOffset.UtcNow, cancellationToken);
    }

    public Task<IReadOnlyList<ApiTokenInfo>> ListTokensAsync(CancellationToken cancellationToken = default) =>
        store.GetApiTokensAsync(DateTimeOffset.UtcNow, cancellationToken);

    public async Task RevokeTokenAsync(string tokenId, CancellationToken cancellationToken = default)
    {
        if (!await store.RevokeTokenAsync(tokenId, DateTimeOffset.UtcNow, cancellationToken))
        {
            throw new AgentOperationException(AgentErrorCodes.TokenNotFound,
                "The Viewer token id was not found.", 404);
        }
        AbortRealtimeConnections(tokenId);
    }

    public async Task<string> RotateTokenAsync(string tokenId, CancellationToken cancellationToken = default)
    {
        var replacement = Base64Url(RandomNumberGenerator.GetBytes(32));
        if (!await store.RotateTokenAsync(tokenId, Hash(replacement), DateTimeOffset.UtcNow, cancellationToken))
        {
            throw new AgentOperationException(AgentErrorCodes.TokenNotFound,
                "The Viewer token id was not found.", 404);
        }
        AbortRealtimeConnections(tokenId);
        return replacement;
    }

    public async Task<IDisposable?> TryRegisterRealtimeConnectionAsync(
        string token,
        Action abort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(abort);
        if (!await ValidateTokenAsync(token, cancellationToken))
        {
            return null;
        }

        var tokenId = Hash(token)[..16];
        var connectionId = Interlocked.Increment(ref _nextRealtimeConnectionId);
        var entry = new RealtimeConnectionEntry(abort);
        RealtimeConnectionBucket bucket;
        while (true)
        {
            bucket = _realtimeConnections.GetOrAdd(tokenId, static _ => new RealtimeConnectionBucket());
            if (bucket.TryAdd(connectionId, entry))
            {
                break;
            }
            RemoveExactBucket(tokenId, bucket);
        }

        var lease = new RealtimeConnectionLease(() =>
            RemoveRealtimeConnection(tokenId, connectionId, bucket));
        try
        {
            // Revalidate after registration. This closes the race where revoke
            // commits after the first validation but before the connection is visible.
            if (await ValidateTokenAsync(token, cancellationToken))
            {
                return lease;
            }

            lease.Dispose();
            entry.AbortOnce();
            return null;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void AbortRealtimeConnections(string tokenId)
    {
        if (_realtimeConnections.TryRemove(tokenId.ToUpperInvariant(), out var bucket))
        {
            bucket.AbortAll();
        }
    }

    private void RemoveRealtimeConnection(
        string tokenId,
        long connectionId,
        RealtimeConnectionBucket bucket)
    {
        bucket.Remove(connectionId);
        if (bucket.TryRetireIfEmpty())
        {
            RemoveExactBucket(tokenId, bucket);
        }
    }

    private void RemoveExactBucket(string tokenId, RealtimeConnectionBucket bucket) =>
        ((ICollection<KeyValuePair<string, RealtimeConnectionBucket>>)_realtimeConnections)
        .Remove(new KeyValuePair<string, RealtimeConnectionBucket>(tokenId, bucket));

    private string Hash(string value)
    {
        var pepperLength = Encoding.UTF8.GetByteCount(options.TokenPepper);
        var valueLength = Encoding.UTF8.GetByteCount(value);
        var bytes = new byte[pepperLength + 1 + valueLength];
        byte[]? digest = null;
        try
        {
            Encoding.UTF8.GetBytes(options.TokenPepper.AsSpan(), bytes.AsSpan(0, pepperLength));
            bytes[pepperLength] = 0;
            Encoding.UTF8.GetBytes(value.AsSpan(), bytes.AsSpan(pepperLength + 1, valueLength));
            digest = SHA256.HashData(bytes);
            return Convert.ToHexString(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    private static string Base64Url(byte[] value)
    {
        try
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private sealed class RealtimeConnectionEntry(Action abort)
    {
        private int _aborted;

        public void AbortOnce()
        {
            if (Interlocked.Exchange(ref _aborted, 1) != 0)
            {
                return;
            }
            try
            {
                abort();
            }
            catch
            {
                // One broken transport must not prevent the remaining sessions
                // for the revoked token from being terminated.
            }
        }
    }

    private sealed class RealtimeConnectionBucket
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, RealtimeConnectionEntry> _entries = [];
        private bool _closed;

        public bool TryAdd(long connectionId, RealtimeConnectionEntry entry)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }
                _entries.Add(connectionId, entry);
                return true;
            }
        }

        public void Remove(long connectionId)
        {
            lock (_gate)
            {
                _entries.Remove(connectionId);
            }
        }

        public bool TryRetireIfEmpty()
        {
            lock (_gate)
            {
                if (_closed || _entries.Count != 0)
                {
                    return false;
                }
                _closed = true;
                return true;
            }
        }

        public void AbortAll()
        {
            RealtimeConnectionEntry[] entries;
            lock (_gate)
            {
                _closed = true;
                entries = _entries.Values.ToArray();
                _entries.Clear();
            }
            foreach (var entry in entries)
            {
                entry.AbortOnce();
            }
        }
    }

    private sealed class RealtimeConnectionLease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

public sealed class PairingAttemptLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BucketRetention = TimeSpan.FromMinutes(10);
    private const int MaximumAttemptsPerWindow = 5;
    private readonly ConcurrentDictionary<string, AttemptBucket> _buckets = new(StringComparer.Ordinal);
    private int _operations;

    public bool TryAcquire(string clientKey, DateTimeOffset now, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);
        var bucket = _buckets.GetOrAdd(clientKey, static _ => new AttemptBucket());
        lock (bucket.SyncRoot)
        {
            bucket.LastSeenUtc = now;
            while (bucket.Attempts.TryPeek(out var oldest) && now - oldest >= Window)
            {
                bucket.Attempts.Dequeue();
            }

            if (bucket.Attempts.Count >= MaximumAttemptsPerWindow)
            {
                retryAfter = Window - (now - bucket.Attempts.Peek());
                return false;
            }

            bucket.Attempts.Enqueue(now);
            retryAfter = TimeSpan.Zero;
        }

        MaybeCleanup(now);
        return true;
    }

    private void MaybeCleanup(DateTimeOffset now)
    {
        if ((Interlocked.Increment(ref _operations) & 0xff) != 0 && _buckets.Count <= 1024)
        {
            return;
        }

        foreach (var pair in _buckets)
        {
            var stale = false;
            lock (pair.Value.SyncRoot)
            {
                stale = now - pair.Value.LastSeenUtc >= BucketRetention;
            }
            if (stale)
            {
                _buckets.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class AttemptBucket
    {
        public object SyncRoot { get; } = new();

        public Queue<DateTimeOffset> Attempts { get; } = new();

        public DateTimeOffset LastSeenUtc { get; set; }
    }
}

public sealed class CertificateStatusService(AgentOptions options, IWebHostEnvironment environment, ILogger<CertificateStatusService> logger) : IHostedService
{
    public CertificateStatus Status { get; private set; } = new(false, null);
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Status = ReadStatus();
        }
        catch (Exception exception) when (IsCertificateAvailabilityFailure(exception))
        {
            logger.LogError("Agent HTTPS certificate status load failed with {ErrorCode}.",
                AgentErrorCodes.CertificateUnavailable);
            Status = new CertificateStatus(options.Https.Enabled, null, State: "unavailable");
        }
        if (Status.HttpsEnabled)
        {
            logger.LogInformation(
                "Agent HTTPS certificate state {CertificateState}; expires {NotAfterUtc}; accepted pins {AcceptedPinCount}.",
                Status.State, Status.NotAfterUtc, Status.AcceptedSha256Fingerprints?.Count ?? 0);
            if (Status.State.StartsWith("expiring", StringComparison.Ordinal))
            {
                logger.LogWarning("Agent HTTPS certificate expires in {DaysRemaining} days.", Status.DaysRemaining);
            }
            else if (string.Equals(Status.State, "expired", StringComparison.Ordinal))
            {
                logger.LogError("Agent HTTPS certificate is expired.");
            }
        }
        else
        {
            logger.LogWarning("Agent is running without HTTPS because mock/development mode is enabled.");
        }
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _refreshTask = RefreshLoopAsync(_refreshCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_refreshCancellation is null || _refreshTask is null)
        {
            return;
        }
        await _refreshCancellation.CancelAsync();
        try
        {
            await _refreshTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected during service shutdown.
        }
        finally
        {
            _refreshCancellation.Dispose();
            _refreshCancellation = null;
            _refreshTask = null;
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                Status = ReadStatus();
            }
            catch (Exception exception) when (IsCertificateAvailabilityFailure(exception))
            {
                logger.LogError("Agent HTTPS certificate status refresh failed with {ErrorCode}.",
                    AgentErrorCodes.CertificateUnavailable);
                Status = new CertificateStatus(true, null, State: "unavailable");
            }
        }
    }

    private CertificateStatus ReadStatus()
    {
        if (!options.Https.Enabled)
        {
            return new CertificateStatus(false, null);
        }

        using var certificate = AgentCertificateLoader.Load(options.Https, environment.ContentRootPath);
        var now = DateTimeOffset.UtcNow;
        var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var remaining = notAfter - now;
        var daysRemaining = (int)Math.Floor(remaining.TotalDays);
        var state = now < notBefore ? "unavailable" : remaining <= TimeSpan.Zero ? "expired" : remaining switch
        {
            var value when value <= TimeSpan.FromDays(7) => "expiring-7",
            var value when value <= TimeSpan.FromDays(30) => "expiring-30",
            var value when value <= TimeSpan.FromDays(60) => "expiring-60",
            _ => "valid"
        };
        var current = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
        var accepted = new List<string> { current };
        if (!string.IsNullOrWhiteSpace(options.Https.PreviousCertificateSha256Fingerprint) &&
            options.Https.PreviousCertificateAcceptUntilUtc > now &&
            options.Https.PreviousCertificateAcceptUntilUtc <= now.AddDays(14) &&
            !string.Equals(options.Https.PreviousCertificateSha256Fingerprint, current,
                StringComparison.OrdinalIgnoreCase))
        {
            accepted.Add(options.Https.PreviousCertificateSha256Fingerprint);
        }
        return new CertificateStatus(true, current, notAfter, daysRemaining, state, accepted);
    }

    private static bool IsCertificateAvailabilityFailure(Exception exception) =>
        exception is CryptographicException or IOException or UnauthorizedAccessException or
            InvalidOperationException or PlatformNotSupportedException;
}

public sealed class BearerTokenMiddleware(RequestDelegate next)
{
    private static readonly string[] AnonymousPaths =
    [
        "/health",
        "/health/live",
        "/health/ready",
        "/api/v1/pairing/bootstrap",
        "/api/v1/pairing/exchange",
        "/api/v1/certificate/fingerprint"
    ];

    public async Task InvokeAsync(HttpContext context, PairingService pairingService)
    {
        if (AnonymousPaths.Any(path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : string.Empty;
        var isRealtimeEndpoint = context.Request.Path.StartsWithSegments("/hubs/events");
        if (string.IsNullOrEmpty(token) && isRealtimeEndpoint)
        {
            // SignalR WebSocket handshakes may carry the access token in the query string.
            token = context.Request.Query["access_token"].ToString();
        }
        IDisposable? realtimeLease = null;
        var authenticated = false;
        if (token.Length <= 4096)
        {
            if (isRealtimeEndpoint)
            {
                realtimeLease = await pairingService.TryRegisterRealtimeConnectionAsync(
                    token,
                    context.Abort,
                    context.RequestAborted);
                authenticated = realtimeLease is not null;
            }
            else
            {
                authenticated = await pairingService.ValidateTokenAsync(token, context.RequestAborted);
            }
        }
        if (!authenticated)
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = AgentErrorCodes.AuthFailed, message = "Authentication failed." }
            }, cancellationToken: context.RequestAborted);
            return;
        }

        context.Items["actor"] = "paired-viewer";
        try
        {
            await next(context);
        }
        finally
        {
            realtimeLease?.Dispose();
        }
    }
}
