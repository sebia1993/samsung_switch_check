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
            return ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.LocalMachine);
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
            return ProtectedData.Unprotect(protectedBytes.ToArray(), Entropy, DataProtectionScope.LocalMachine);
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
    private static readonly TimeSpan TokenValidationCacheDuration = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _validatedTokens = new(StringComparer.Ordinal);

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
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(Math.Clamp(options.PairingCodeLifetimeMinutes, 1, 60));
        await store.StorePairingCodeAsync(Hash(code), now, expires, cancellationToken);
        return (code, expires);
    }

    public async Task<string> ExchangeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64 ||
            !await store.ConsumePairingCodeAsync(Hash(code.Trim()), DateTimeOffset.UtcNow, cancellationToken))
        {
            throw new AgentOperationException(AgentErrorCodes.PairingInvalid, "Pairing code is invalid, expired, or already used.", 400);
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await store.StoreTokenAsync(Hash(token), DateTimeOffset.UtcNow, cancellationToken);
        return token;
    }

    public async Task<bool> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
        {
            return false;
        }

        var tokenHash = Hash(token);
        var now = DateTimeOffset.UtcNow;
        if (_validatedTokens.TryGetValue(tokenHash, out var lastValidated) &&
            now - lastValidated < TokenValidationCacheDuration)
        {
            return true;
        }

        var valid = await store.ValidateAndTouchTokenAsync(tokenHash, now, cancellationToken);
        if (valid)
        {
            _validatedTokens[tokenHash] = now;
        }
        return valid;
    }

    private string Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(options.TokenPepper + "\0" + value);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class PairingAttemptLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaximumAttemptsPerWindow = 5;
    private readonly ConcurrentDictionary<string, AttemptBucket> _buckets = new(StringComparer.Ordinal);

    public bool TryAcquire(string clientKey, DateTimeOffset now, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientKey);
        var bucket = _buckets.GetOrAdd(clientKey, static _ => new AttemptBucket());
        lock (bucket.SyncRoot)
        {
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
            return true;
        }
    }

    private sealed class AttemptBucket
    {
        public object SyncRoot { get; } = new();

        public Queue<DateTimeOffset> Attempts { get; } = new();
    }
}

public sealed class CertificateStatusService(AgentOptions options, IWebHostEnvironment environment, ILogger<CertificateStatusService> logger) : IHostedService
{
    public CertificateStatus Status { get; private set; } = new(false, null);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Status = ReadStatus();
        if (Status.HttpsEnabled)
        {
            logger.LogInformation("Agent HTTPS certificate SHA-256 fingerprint: {Fingerprint}", Status.Sha256Fingerprint);
        }
        else
        {
            logger.LogWarning("Agent is running without HTTPS because mock/development mode is enabled.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private CertificateStatus ReadStatus()
    {
        if (!options.Https.Enabled)
        {
            return new CertificateStatus(false, null);
        }

        var path = Path.IsPathRooted(options.Https.CertificatePath)
            ? options.Https.CertificatePath
            : Path.Combine(environment.ContentRootPath, options.Https.CertificatePath);
        var password = Environment.GetEnvironmentVariable(options.Https.CertificatePasswordEnvironmentVariable);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        return new CertificateStatus(true, Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)));
    }
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
        if (string.IsNullOrEmpty(token) && context.Request.Path.StartsWithSegments("/hubs/events"))
        {
            // SignalR WebSocket handshakes may carry the access token in the query string.
            token = context.Request.Query["access_token"].ToString();
        }
        if (token.Length > 4096 || !await pairingService.ValidateTokenAsync(token, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = AgentErrorCodes.AuthFailed, message = "Authentication failed." }
            }, cancellationToken: context.RequestAborted);
            return;
        }

        context.Items["actor"] = "paired-viewer";
        await next(context);
    }
}
