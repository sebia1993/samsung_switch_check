using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;

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
        Directory.CreateDirectory(_folder);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential, JsonDefaults.Serializer);
        try
        {
            var encrypted = protector.Protect(plaintext);
            await File.WriteAllBytesAsync(Path.Combine(_folder, $"{credentialId}.bin"), encrypted, cancellationToken);
            CryptographicOperations.ZeroMemory(encrypted);
        }
        finally
        {
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
        var plaintext = protector.Unprotect(encrypted);
        try
        {
            return JsonSerializer.Deserialize<SwitchCredential>(plaintext, JsonDefaults.Serializer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
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
        if (string.IsNullOrWhiteSpace(code) || !await store.ConsumePairingCodeAsync(Hash(code.Trim()), DateTimeOffset.UtcNow, cancellationToken))
        {
            throw new AgentOperationException(AgentErrorCodes.PairingInvalid, "Pairing code is invalid, expired, or already used.", 400);
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        await store.StoreTokenAsync(Hash(token), DateTimeOffset.UtcNow, cancellationToken);
        return token;
    }

    public Task<bool> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(token)
            ? Task.FromResult(false)
            : store.ValidateAndTouchTokenAsync(Hash(token), DateTimeOffset.UtcNow, cancellationToken);

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
