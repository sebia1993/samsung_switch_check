using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
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
                AgentErrorCodes.CredentialCorrupt,
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
