using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungSwitchWatch.Agent.Api;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class CertificateLifecycleTests
{
    [Theory]
    [InlineData(61, "valid")]
    [InlineData(45, "expiring-60")]
    [InlineData(20, "expiring-30")]
    [InlineData(5, "expiring-7")]
    public async Task HttpsCertificateStatus_ReportsRotationWindow(int daysRemaining, string expectedState)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-CertificateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var passwordVariable = "SSW_TEST_CERT_" + Guid.NewGuid().ToString("N");
        try
        {
            const string password = "synthetic-test-password";
            var certificatePath = Path.Combine(folder, "rotation.pfx");
            File.WriteAllBytes(certificatePath, CreateCertificate(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(daysRemaining), password));
            Environment.SetEnvironmentVariable(passwordVariable, password);
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                EnablePolling = false,
                Https = new HttpsOptions
                {
                    Enabled = true,
                    CertificatePath = certificatePath,
                    CertificatePasswordEnvironmentVariable = passwordVariable
                }
            };
            var service = new CertificateStatusService(options, new TestEnvironment(folder),
                NullLogger<CertificateStatusService>.Instance);

            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(expectedState, service.Status.State);
                Assert.InRange(service.Status.DaysRemaining!.Value, daysRemaining - 1, daysRemaining);
                var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
                await store.InitializeAsync();
                var readiness = new AgentReadinessService(options, store, service,
                    new EmptyCredentialVault(), new AgentRuntimeState(),
                    NullLogger<AgentReadinessService>.Instance);
                Assert.True((await readiness.CheckAsync()).Ready);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(13, 2)]
    [InlineData(15, 1)]
    [InlineData(-1, 1)]
    public async Task PreviousCertificatePin_IsAcceptedOnlyDuringBoundedOverlap(int overlapDays, int expectedPins)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-CertificateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var passwordVariable = "SSW_TEST_CERT_" + Guid.NewGuid().ToString("N");
        try
        {
            const string password = "synthetic-test-password";
            var certificatePath = Path.Combine(folder, "overlap.pfx");
            File.WriteAllBytes(certificatePath, CreateCertificate(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90), password));
            Environment.SetEnvironmentVariable(passwordVariable, password);
            var options = new AgentOptions
            {
                Https = new HttpsOptions
                {
                    Enabled = true,
                    CertificatePath = certificatePath,
                    CertificatePasswordEnvironmentVariable = passwordVariable,
                    PreviousCertificateSha256Fingerprint = new string('A', 64),
                    PreviousCertificateAcceptUntilUtc = DateTimeOffset.UtcNow.AddDays(overlapDays)
                }
            };
            var service = new CertificateStatusService(options, new TestEnvironment(folder),
                NullLogger<CertificateStatusService>.Instance);

            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(expectedPins, service.Status.AcceptedSha256Fingerprints!.Count);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void PfxWithoutPrivateKey_IsRejected()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-CertificateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var passwordVariable = "SSW_TEST_CERT_" + Guid.NewGuid().ToString("N");
        try
        {
            const string password = "synthetic-test-password";
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=SamsungSwitchWatch-PublicOnly", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var withKey = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));
            var certificatePath = Path.Combine(folder, "public-only.pfx");
            File.WriteAllBytes(certificatePath, publicOnly.Export(X509ContentType.Pfx, password));
            Environment.SetEnvironmentVariable(passwordVariable, password);
            var options = new HttpsOptions
            {
                Enabled = true,
                CertificatePath = certificatePath,
                CertificatePasswordEnvironmentVariable = passwordVariable
            };

            var exception = Assert.Throws<InvalidOperationException>(() =>
                AgentCertificateLoader.Load(options, folder));

            Assert.Equal("CERTIFICATE_PRIVATE_KEY_UNAVAILABLE", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MissingStoreCertificate_IsRejectedWithoutFallingBackToPfx()
    {
        var options = new HttpsOptions
        {
            Enabled = true,
            CertificateStoreThumbprint = new string('F', 40),
            CertificatePath = "must-not-be-used.pfx"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => AgentCertificateLoader.Load(options));

        Assert.Equal("CERTIFICATE_UNAVAILABLE", exception.Message);
    }

    [Fact]
    public async Task ExpiredHttpsCertificate_FailsReadinessWithStableCode()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-CertificateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var passwordVariable = "SSW_TEST_CERT_" + Guid.NewGuid().ToString("N");
        try
        {
            const string password = "synthetic-test-password";
            var certificatePath = Path.Combine(folder, "expired.pfx");
            File.WriteAllBytes(certificatePath, CreateCertificate(
                DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1), password));
            Environment.SetEnvironmentVariable(passwordVariable, password);
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                EnablePolling = false,
                Https = new HttpsOptions
                {
                    Enabled = true,
                    CertificatePath = certificatePath,
                    CertificatePasswordEnvironmentVariable = passwordVariable
                }
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var certificateStatus = new CertificateStatusService(options, new TestEnvironment(folder),
                NullLogger<CertificateStatusService>.Instance);
            await certificateStatus.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal("expired", certificateStatus.Status.State);
                Assert.Single(certificateStatus.Status.AcceptedSha256Fingerprints!);
                var readiness = new AgentReadinessService(options, store, certificateStatus,
                    new EmptyCredentialVault(), new AgentRuntimeState(), NullLogger<AgentReadinessService>.Instance);

                var result = await readiness.CheckAsync();

                Assert.False(result.Ready);
                Assert.Equal(AgentErrorCodes.CertificateExpired, result.Code);
            }
            finally
            {
                await certificateStatus.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task NotYetValidHttpsCertificate_FailsReadinessAsUnavailable()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-CertificateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var passwordVariable = "SSW_TEST_CERT_" + Guid.NewGuid().ToString("N");
        try
        {
            const string password = "synthetic-test-password";
            var certificatePath = Path.Combine(folder, "future.pfx");
            File.WriteAllBytes(certificatePath, CreateCertificate(
                DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30), password));
            Environment.SetEnvironmentVariable(passwordVariable, password);
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                EnablePolling = false,
                Https = new HttpsOptions
                {
                    Enabled = true,
                    CertificatePath = certificatePath,
                    CertificatePasswordEnvironmentVariable = passwordVariable
                }
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var certificateStatus = new CertificateStatusService(options, new TestEnvironment(folder),
                NullLogger<CertificateStatusService>.Instance);
            await certificateStatus.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal("unavailable", certificateStatus.Status.State);
                var readiness = new AgentReadinessService(options, store, certificateStatus,
                    new EmptyCredentialVault(), new AgentRuntimeState(), NullLogger<AgentReadinessService>.Instance);

                var result = await readiness.CheckAsync();

                Assert.False(result.Ready);
                Assert.Equal(AgentErrorCodes.CertificateUnavailable, result.Code);
            }
            finally
            {
                await certificateStatus.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    private static byte[] CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SamsungSwitchWatch-Test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return certificate.Export(X509ContentType.Pfx, password);
    }

    private sealed class EmptyCredentialVault : ICredentialVault
    {
        public Task StoreAsync(string credentialId, SwitchCredential credential,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SwitchCredential?> GetAsync(string credentialId,
            CancellationToken cancellationToken = default) => Task.FromResult<SwitchCredential?>(null);
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SamsungSwitchWatch.Agent.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
