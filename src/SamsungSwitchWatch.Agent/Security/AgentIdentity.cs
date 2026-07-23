using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;

namespace SamsungSwitchWatch.Agent.Security;

public sealed class AgentIdentity : IDisposable
{
    public AgentIdentity(string instanceId, X509Certificate2 certificate)
    {
        InstanceId = instanceId;
        Certificate = certificate;
        using var publicKey = certificate.GetECDsaPublicKey()
            ?? throw new AgentConfigurationException(
                AgentErrorCodes.TlsIdentityInvalid,
                "Agent HTTPS identity does not contain an ECDSA public key.");
        CertificatePublicKeySha256 =
            Convert.ToHexString(SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo()));
    }

    public string InstanceId { get; }
    public X509Certificate2 Certificate { get; }
    public string CertificatePublicKeySha256 { get; }

    public void Dispose() => Certificate.Dispose();
}

public static class AgentIdentityStore
{
    private const string MetadataFileName = "agent-identity.json";
    private const string CertificateFileName = "https-certificate.pfx.dpapi";
    private static readonly byte[] Entropy =
        SHA256.HashData("SamsungSwitchWatch.Agent.HttpsIdentity.v1"u8);

    public static AgentIdentity LoadOrCreate(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MockMode)
        {
            return new AgentIdentity(Guid.NewGuid().ToString("N"), CreateCertificate());
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new AgentConfigurationException(
                AgentErrorCodes.TlsIdentityInvalid,
                "Persistent Agent HTTPS identity requires Windows DPAPI.");
        }

        Directory.CreateDirectory(options.DataDirectory);
        var metadataPath = Path.Combine(options.DataDirectory, MetadataFileName);
        var certificatePath = Path.Combine(options.DataDirectory, CertificateFileName);
        if (File.Exists(metadataPath) || File.Exists(certificatePath))
        {
            return LoadExisting(metadataPath, certificatePath);
        }

        var instanceId = Guid.NewGuid().ToString("N");
        using var generated = CreateCertificate();
        var exported = generated.Export(X509ContentType.Pfx);
        try
        {
            var protectedBytes = ProtectedData.Protect(exported, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                WriteNewFile(certificatePath, protectedBytes);
                var metadata = JsonSerializer.SerializeToUtf8Bytes(
                    new IdentityMetadata(instanceId, CertificateFileName));
                WriteNewFile(metadataPath, metadata);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        catch
        {
            TryDeleteIncomplete(metadataPath);
            TryDeleteIncomplete(certificatePath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }

        return LoadExisting(metadataPath, certificatePath);
    }

    [SupportedOSPlatform("windows")]
    private static AgentIdentity LoadExisting(string metadataPath, string certificatePath)
    {
        try
        {
            if (!File.Exists(metadataPath) || !File.Exists(certificatePath))
            {
                throw new InvalidDataException("Agent identity files are incomplete.");
            }

            var metadata = JsonSerializer.Deserialize<IdentityMetadata>(
                File.ReadAllBytes(metadataPath));
            if (metadata is null ||
                !Guid.TryParseExact(metadata.InstanceId, "N", out _) ||
                !string.Equals(metadata.CertificateFile, CertificateFileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Agent identity metadata is invalid.");
            }

            var protectedBytes = File.ReadAllBytes(certificatePath);
            byte[]? exported = null;
            try
            {
                exported = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.LocalMachine);
                var certificate = X509CertificateLoader.LoadPkcs12(
                    exported,
                    password: null,
                    X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
                if (!certificate.HasPrivateKey ||
                    certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))
                {
                    certificate.Dispose();
                    throw new CryptographicException("Agent certificate is unusable or near expiry.");
                }

                return new AgentIdentity(metadata.InstanceId, certificate);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                if (exported is not null)
                {
                    CryptographicOperations.ZeroMemory(exported);
                }
            }
        }
        catch (AgentConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or InvalidDataException)
        {
            throw new AgentConfigurationException(
                AgentErrorCodes.TlsIdentityInvalid,
                "Agent HTTPS identity could not be loaded.");
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=Samsung Switch Watch Agent",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));
        var exported = certificate.Export(X509ContentType.Pfx);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                exported,
                password: null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteIncomplete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The original identity creation failure is the useful result.
        }
    }

    private sealed record IdentityMetadata(string InstanceId, string CertificateFile);
}
