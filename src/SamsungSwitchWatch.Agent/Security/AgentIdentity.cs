using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using System.Text;
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
    internal const string MetadataFileName = "agent-identity.json";
    internal const string CertificateFileName = "https-certificate.pfx.dpapi";
    internal const string MetadataPendingFileName = "agent-identity.json.pending";
    internal const string CertificatePendingFileName = "https-certificate.pfx.dpapi.pending";
    internal const string CreationMarkerFileName = "agent-identity.creation-in-progress";
    internal const string CreationMarkerValue =
        "SamsungSwitchWatch.Agent.HttpsIdentity.Initializing.v1";
    private static readonly byte[] Entropy =
        SHA256.HashData("SamsungSwitchWatch.Agent.HttpsIdentity.v1"u8);
    private static readonly byte[] CreationMarkerBytes =
        Encoding.UTF8.GetBytes(CreationMarkerValue);

    public static AgentIdentity LoadOrCreate(AgentOptions options) =>
        LoadOrCreate(options, static () => { });

    internal static AgentIdentity LoadOrCreate(
        AgentOptions options,
        Action beforeCreationMarkerAcquire)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(beforeCreationMarkerAcquire);
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
        var paths = IdentityPaths.Create(options.DataDirectory);
        if (File.Exists(paths.CreationMarker))
        {
            var recovered = RecoverInterruptedCreation(paths);
            if (recovered is not null)
            {
                return recovered;
            }
        }

        if (File.Exists(paths.MetadataPending) || File.Exists(paths.CertificatePending))
        {
            throw InvalidIdentity();
        }

        if (File.Exists(paths.Metadata) || File.Exists(paths.Certificate))
        {
            return LoadExisting(paths.Metadata, paths.Certificate);
        }

        FileStream? marker = null;
        AgentIdentity? createdIdentity = null;
        try
        {
            beforeCreationMarkerAcquire();
            // The marker is held exclusively until both durable pending files
            // have been promoted and the final pair has passed LoadExisting.
            marker = CreateCreationMarker(paths.CreationMarker);
            createdIdentity = LoadIdentityCreatedWhileWaiting(paths);
            if (createdIdentity is not null)
            {
                marker.Dispose();
                marker = null;
                DeleteFile(paths.CreationMarker);
                return createdIdentity;
            }

            var instanceId = Guid.NewGuid().ToString("N");
            using var generated = CreateCertificate();
            var exported = generated.Export(X509ContentType.Pfx);
            try
            {
                var protectedBytes = ProtectedData.Protect(
                    exported,
                    Entropy,
                    DataProtectionScope.LocalMachine);
                try
                {
                    WriteNewFile(paths.CertificatePending, protectedBytes);
                    var metadata = JsonSerializer.SerializeToUtf8Bytes(
                        new IdentityMetadata(instanceId, CertificateFileName));
                    WriteNewFile(paths.MetadataPending, metadata);
                    File.Move(paths.CertificatePending, paths.Certificate);
                    File.Move(paths.MetadataPending, paths.Metadata);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(exported);
            }

            createdIdentity = LoadExisting(paths.Metadata, paths.Certificate);
            marker.Dispose();
            marker = null;
            DeleteFile(paths.CreationMarker);
            return createdIdentity;
        }
        catch (Exception exception)
        {
            createdIdentity?.Dispose();
            marker?.Dispose();

            if (exception is AgentConfigurationException)
            {
                throw;
            }

            if (exception is IOException or UnauthorizedAccessException or
                CryptographicException or JsonException or InvalidDataException)
            {
                throw InvalidIdentity();
            }

            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static AgentIdentity? LoadIdentityCreatedWhileWaiting(IdentityPaths paths)
    {
        if (File.Exists(paths.MetadataPending) || File.Exists(paths.CertificatePending))
        {
            throw InvalidIdentity();
        }

        if (!File.Exists(paths.Metadata) && !File.Exists(paths.Certificate))
        {
            return null;
        }

        return LoadExisting(paths.Metadata, paths.Certificate);
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

    [SupportedOSPlatform("windows")]
    private static AgentIdentity? RecoverInterruptedCreation(IdentityPaths paths)
    {
        FileStream? marker = null;
        AgentIdentity? identity = null;
        try
        {
            marker = new FileStream(
                paths.CreationMarker,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            try
            {
                ValidateCreationMarker(marker);
            }
            catch (InvalidDataException) when (!HasIdentityArtifacts(paths))
            {
                // A crash may interrupt the marker write itself. With no
                // identity artifacts beside it, no key could have been exposed.
                marker.Dispose();
                marker = null;
                DeleteFile(paths.CreationMarker);
                return null;
            }

            if (File.Exists(paths.Metadata) && File.Exists(paths.Certificate))
            {
                // Atomic same-directory moves cannot tear both final files.
                // If the pair exists it must validate; never rotate it silently.
                identity = LoadExisting(paths.Metadata, paths.Certificate);
            }

            if (identity is not null)
            {
                DeleteFile(paths.MetadataPending);
                DeleteFile(paths.CertificatePending);
                marker.Dispose();
                marker = null;
                DeleteFile(paths.CreationMarker);
                return identity;
            }

            DeleteFile(paths.MetadataPending);
            DeleteFile(paths.CertificatePending);
            DeleteFile(paths.Metadata);
            DeleteFile(paths.Certificate);
            marker.Dispose();
            marker = null;
            DeleteFile(paths.CreationMarker);
            return null;
        }
        catch (AgentConfigurationException)
        {
            identity?.Dispose();
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            identity?.Dispose();
            throw InvalidIdentity();
        }
        finally
        {
            marker?.Dispose();
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

    private static FileStream CreateCreationMarker(string path)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(CreationMarkerBytes);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            var created = stream is not null;
            stream?.Dispose();
            if (created)
            {
                TryDeleteIncomplete(path);
            }

            throw;
        }
    }

    private static void ValidateCreationMarker(FileStream stream)
    {
        if (stream.Length != CreationMarkerBytes.Length)
        {
            throw new InvalidDataException("Agent identity creation marker is invalid.");
        }

        Span<byte> actual = stackalloc byte[CreationMarkerBytes.Length];
        stream.ReadExactly(actual);
        if (!actual.SequenceEqual(CreationMarkerBytes))
        {
            throw new InvalidDataException("Agent identity creation marker is invalid.");
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

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (File.Exists(path))
        {
            throw new IOException("Agent identity transaction artifact could not be deleted.");
        }
    }

    private static bool HasIdentityArtifacts(IdentityPaths paths) =>
        File.Exists(paths.MetadataPending) ||
        File.Exists(paths.CertificatePending) ||
        File.Exists(paths.Metadata) ||
        File.Exists(paths.Certificate);

    private static bool TryDeleteIncomplete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch
        {
            // The original identity creation failure is the useful result.
            return false;
        }
    }

    private static AgentConfigurationException InvalidIdentity() =>
        new(
            AgentErrorCodes.TlsIdentityInvalid,
            "Agent HTTPS identity could not be loaded.");

    private sealed record IdentityMetadata(string InstanceId, string CertificateFile);

    private readonly record struct IdentityPaths(
        string Metadata,
        string Certificate,
        string MetadataPending,
        string CertificatePending,
        string CreationMarker)
    {
        public static IdentityPaths Create(string dataDirectory) =>
            new(
                Path.Combine(dataDirectory, MetadataFileName),
                Path.Combine(dataDirectory, CertificateFileName),
                Path.Combine(dataDirectory, MetadataPendingFileName),
                Path.Combine(dataDirectory, CertificatePendingFileName),
                Path.Combine(dataDirectory, CreationMarkerFileName));
    }
}
