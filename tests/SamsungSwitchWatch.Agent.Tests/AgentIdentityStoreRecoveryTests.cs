using System.Text;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class AgentIdentityStoreRecoveryTests
{
    [Fact]
    public async Task ConcurrentInitialCreation_LateTransactionDoesNotRemoveCommittedIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        using var contenderReachedPreflight = new ManualResetEventSlim();
        using var releaseContender = new ManualResetEventSlim();
        AgentIdentity? contenderIdentity = null;
        try
        {
            var contender = Task.Run(() =>
                AgentIdentityStore.LoadOrCreate(
                    CreateOptions(folder),
                    () =>
                    {
                        contenderReachedPreflight.Set();
                        if (!releaseContender.Wait(TimeSpan.FromSeconds(10)))
                        {
                            throw new TimeoutException(
                                "The committed identity was not created before the contender resumed.");
                        }
                    }));

            Assert.True(
                contenderReachedPreflight.Wait(TimeSpan.FromSeconds(10)),
                "The contender did not reach the post-preflight creation boundary.");

            string committedInstance;
            string committedKey;
            try
            {
                using var committed = AgentIdentityStore.LoadOrCreate(CreateOptions(folder));
                committedInstance = committed.InstanceId;
                committedKey = committed.CertificatePublicKeySha256;
            }
            finally
            {
                releaseContender.Set();
            }

            contenderIdentity = await contender;
            Assert.Equal(committedInstance, contenderIdentity.InstanceId);
            Assert.Equal(committedKey, contenderIdentity.CertificatePublicKeySha256);

            using var restarted = AgentIdentityStore.LoadOrCreate(CreateOptions(folder));
            Assert.Equal(committedInstance, restarted.InstanceId);
            Assert.Equal(committedKey, restarted.CertificatePublicKeySha256);
            AssertFinalFilesOnly(folder);
        }
        finally
        {
            contenderIdentity?.Dispose();
            releaseContender.Set();
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("marker-only")]
    [InlineData("certificate-pending")]
    [InlineData("both-pending")]
    [InlineData("certificate-promoted")]
    public void MarkedInitialCreation_WithIncompleteArtifacts_RegeneratesAndIsStable(
        string stage)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            WriteCreationMarker(folder);
            ArrangeInterruptedStage(folder, stage);

            string recoveredInstance;
            string recoveredKey;
            using (var recovered = AgentIdentityStore.LoadOrCreate(CreateOptions(folder)))
            {
                recoveredInstance = recovered.InstanceId;
                recoveredKey = recovered.CertificatePublicKeySha256;
            }

            using var restarted = AgentIdentityStore.LoadOrCreate(CreateOptions(folder));

            Assert.Equal(recoveredInstance, restarted.InstanceId);
            Assert.Equal(recoveredKey, restarted.CertificatePublicKeySha256);
            AssertFinalFilesOnly(folder);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void TruncatedCreationMarker_WithoutIdentityArtifacts_RegeneratesAndIsStable(
        int markerLength)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            File.WriteAllText(
                Path.Combine(folder, AgentIdentityStore.CreationMarkerFileName),
                AgentIdentityStore.CreationMarkerValue[..markerLength],
                new UTF8Encoding(false));

            string recoveredInstance;
            string recoveredKey;
            using (var recovered = AgentIdentityStore.LoadOrCreate(CreateOptions(folder)))
            {
                recoveredInstance = recovered.InstanceId;
                recoveredKey = recovered.CertificatePublicKeySha256;
            }

            using var restarted = AgentIdentityStore.LoadOrCreate(CreateOptions(folder));

            Assert.Equal(recoveredInstance, restarted.InstanceId);
            Assert.Equal(recoveredKey, restarted.CertificatePublicKeySha256);
            AssertFinalFilesOnly(folder);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TruncatedCreationMarker_WithIdentityArtifact_FailsClosedAndPreservesEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            File.WriteAllBytes(
                Path.Combine(folder, AgentIdentityStore.CreationMarkerFileName),
                []);
            WriteArtifact(folder, AgentIdentityStore.CertificatePendingFileName);
            var before = CaptureFiles(folder);

            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentIdentityStore.LoadOrCreate(CreateOptions(folder)));

            Assert.Equal(AgentErrorCodes.TlsIdentityInvalid, exception.Code);
            AssertFileSnapshot(before, CaptureFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MarkedInitialCreation_WithBothValidFiles_PreservesIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            string originalInstance;
            string originalKey;
            using (var original = AgentIdentityStore.LoadOrCreate(CreateOptions(folder)))
            {
                originalInstance = original.InstanceId;
                originalKey = original.CertificatePublicKeySha256;
            }

            WriteCreationMarker(folder);
            using var recovered = AgentIdentityStore.LoadOrCreate(CreateOptions(folder));

            Assert.Equal(originalInstance, recovered.InstanceId);
            Assert.Equal(originalKey, recovered.CertificatePublicKeySha256);
            AssertFinalFilesOnly(folder);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("missing-metadata")]
    [InlineData("missing-certificate")]
    [InlineData("corrupt-metadata")]
    public void MarkerlessIdentityDamage_FailsClosedAndPreservesEvidence(string damage)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            using (AgentIdentityStore.LoadOrCreate(CreateOptions(folder)))
            {
            }

            ApplyMarkerlessDamage(folder, damage);
            var before = CaptureFiles(folder);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var exception = Assert.Throws<AgentConfigurationException>(() =>
                    AgentIdentityStore.LoadOrCreate(CreateOptions(folder)));

                Assert.Equal(AgentErrorCodes.TlsIdentityInvalid, exception.Code);
                AssertFileSnapshot(before, CaptureFiles(folder));
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MarkedInitialCreation_WithCorruptCommittedFiles_FailsClosedAndPreservesEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            WriteCreationMarker(folder);
            WriteArtifact(folder, AgentIdentityStore.CertificateFileName);
            WriteArtifact(folder, AgentIdentityStore.MetadataFileName);
            var before = CaptureFiles(folder);

            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentIdentityStore.LoadOrCreate(CreateOptions(folder)));

            Assert.Equal(AgentErrorCodes.TlsIdentityInvalid, exception.Code);
            AssertFileSnapshot(before, CaptureFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MarkerlessPendingArtifact_FailsClosedAndPreservesEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            var pendingPath = Path.Combine(
                folder,
                AgentIdentityStore.CertificatePendingFileName);
            File.WriteAllBytes(pendingPath, [0x01, 0x02, 0x03]);
            var before = CaptureFiles(folder);

            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentIdentityStore.LoadOrCreate(CreateOptions(folder)));

            Assert.Equal(AgentErrorCodes.TlsIdentityInvalid, exception.Code);
            AssertFileSnapshot(before, CaptureFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void LockedCreationMarker_FailsClosedWithoutDeletingActiveTransaction()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            var markerPath = Path.Combine(folder, AgentIdentityStore.CreationMarkerFileName);
            using var marker = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);
            marker.Write(Encoding.UTF8.GetBytes(AgentIdentityStore.CreationMarkerValue));
            marker.Flush(flushToDisk: true);

            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentIdentityStore.LoadOrCreate(CreateOptions(folder)));

            Assert.Equal(AgentErrorCodes.TlsIdentityInvalid, exception.Code);
            Assert.True(File.Exists(markerPath));
            Assert.Equal(AgentIdentityStore.CreationMarkerValue.Length, marker.Length);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MarkedValidIdentity_WithLockedPendingFile_FailsThenPreservesIdentityOnRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = NewTemporaryFolder();
        try
        {
            string originalInstance;
            string originalKey;
            using (var original = AgentIdentityStore.LoadOrCreate(CreateOptions(folder)))
            {
                originalInstance = original.InstanceId;
                originalKey = original.CertificatePublicKeySha256;
            }

            WriteCreationMarker(folder);
            var pendingPath = Path.Combine(
                folder,
                AgentIdentityStore.CertificatePendingFileName);
            using (var lockedPending = new FileStream(
                       pendingPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                lockedPending.Write([0x10, 0x20, 0x30]);
                lockedPending.Flush(flushToDisk: true);

                var exception = Assert.Throws<AgentConfigurationException>(() =>
                    AgentIdentityStore.LoadOrCreate(CreateOptions(folder)));

                Assert.Equal(AgentErrorCodes.TlsIdentityInvalid, exception.Code);
                Assert.True(File.Exists(Path.Combine(
                    folder,
                    AgentIdentityStore.CreationMarkerFileName)));
                Assert.True(File.Exists(Path.Combine(
                    folder,
                    AgentIdentityStore.MetadataFileName)));
                Assert.True(File.Exists(Path.Combine(
                    folder,
                    AgentIdentityStore.CertificateFileName)));
            }

            using var recovered = AgentIdentityStore.LoadOrCreate(CreateOptions(folder));

            Assert.Equal(originalInstance, recovered.InstanceId);
            Assert.Equal(originalKey, recovered.CertificatePublicKeySha256);
            AssertFinalFilesOnly(folder);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static AgentOptions CreateOptions(string folder)
    {
        var options = new AgentOptions
        {
            ListenUrl = "https://127.0.0.1:18443",
            DataDirectory = folder,
            AllowedTargetCidrs = ["192.0.2.0/24"]
        };
        AgentOptionsValidator.ValidateAndNormalize(options, folder);
        return options;
    }

    private static void WriteCreationMarker(string folder) =>
        File.WriteAllText(
            Path.Combine(folder, AgentIdentityStore.CreationMarkerFileName),
            AgentIdentityStore.CreationMarkerValue,
            new UTF8Encoding(false));

    private static void ArrangeInterruptedStage(string folder, string stage)
    {
        switch (stage)
        {
            case "marker-only":
                return;
            case "certificate-pending":
                WriteArtifact(folder, AgentIdentityStore.CertificatePendingFileName);
                return;
            case "both-pending":
                WriteArtifact(folder, AgentIdentityStore.CertificatePendingFileName);
                WriteArtifact(folder, AgentIdentityStore.MetadataPendingFileName);
                return;
            case "certificate-promoted":
                WriteArtifact(folder, AgentIdentityStore.CertificateFileName);
                WriteArtifact(folder, AgentIdentityStore.MetadataPendingFileName);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
        }
    }

    private static void ApplyMarkerlessDamage(string folder, string damage)
    {
        switch (damage)
        {
            case "missing-metadata":
                File.Delete(Path.Combine(folder, AgentIdentityStore.MetadataFileName));
                return;
            case "missing-certificate":
                File.Delete(Path.Combine(folder, AgentIdentityStore.CertificateFileName));
                return;
            case "corrupt-metadata":
                File.WriteAllText(
                    Path.Combine(folder, AgentIdentityStore.MetadataFileName),
                    "{not-json",
                    new UTF8Encoding(false));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }
    }

    private static void WriteArtifact(string folder, string fileName) =>
        File.WriteAllBytes(Path.Combine(folder, fileName), [0x10, 0x20, 0x30]);

    private static Dictionary<string, byte[]> CaptureFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .ToDictionary(
                path => Path.GetFileName(path),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static void AssertFileSnapshot(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (fileName, bytes) in expected)
        {
            Assert.Equal(bytes, actual[fileName]);
        }
    }

    private static void AssertFinalFilesOnly(string folder) =>
        Assert.Equal(
            [
                AgentIdentityStore.MetadataFileName,
                AgentIdentityStore.CertificateFileName
            ],
            Directory.EnumerateFiles(folder)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray());

    private static string NewTemporaryFolder()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-AgentIdentityRecoveryTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
