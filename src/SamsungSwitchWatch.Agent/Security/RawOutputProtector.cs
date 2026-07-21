using System.Security.Cryptography;
using System.Text;
using SamsungSwitchWatch.Agent.Configuration;

namespace SamsungSwitchWatch.Agent.Security;

public interface IRawOutputProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedBytes);
}

/// <summary>
/// Protects Agent-only Telnet evidence at rest. Production Windows hosts use
/// machine-scoped DPAPI so copying the database does not expose command output.
/// The authenticated AES envelope exists only for mock/test hosts where DPAPI
/// is unavailable.
/// </summary>
public sealed class RawOutputProtector(AgentOptions options) : IRawOutputProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SamsungSwitchWatch.Agent.RawOutput.v1");
    private static readonly byte[] MockHeader = "SSWMOCK1"u8.ToArray();

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

        EnsureMockMode();
        var key = DeriveMockKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, MockHeader);
            var result = new byte[MockHeader.Length + nonce.Length + tag.Length + ciphertext.Length];
            MockHeader.CopyTo(result, 0);
            nonce.CopyTo(result, MockHeader.Length);
            tag.CopyTo(result, MockHeader.Length + nonce.Length);
            ciphertext.CopyTo(result, MockHeader.Length + nonce.Length + tag.Length);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
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

        EnsureMockMode();
        var minimumLength = MockHeader.Length + 12 + 16;
        if (protectedBytes.Length < minimumLength ||
            !protectedBytes[..MockHeader.Length].SequenceEqual(MockHeader))
        {
            throw new CryptographicException("Raw output envelope is invalid.");
        }

        var nonce = protectedBytes.Slice(MockHeader.Length, 12);
        var tag = protectedBytes.Slice(MockHeader.Length + 12, 16);
        var ciphertext = protectedBytes[(MockHeader.Length + 28)..];
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveMockKey();
        var succeeded = false;
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, MockHeader);
            succeeded = true;
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (!succeeded)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static byte[] DeriveMockKey() =>
        SHA256.HashData("SamsungSwitchWatch.Agent.RawOutput.MockOnly.v1"u8);

    private void EnsureMockMode()
    {
        if (!options.MockMode)
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required to protect raw output.");
        }
    }
}
