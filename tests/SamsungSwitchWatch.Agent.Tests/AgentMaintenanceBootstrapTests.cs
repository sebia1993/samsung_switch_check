using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class AgentMaintenanceBootstrapTests
{
    [Fact]
    public async Task PairingCreate_LoadsProductionSettingsWithoutLoadingKestrelCertificate()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-MaintenanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new
            {
                Agent = new
                {
                    AgentId = "maintenance-test-agent",
                    DataDirectory = "runtime",
                    MockMode = true,
                    EnablePolling = false,
                    EnableSimulator = false,
                    PairingCodeLifetimeMinutes = 3,
                    TokenPepper = "synthetic-maintenance-test-pepper-that-is-not-a-secret",
                    Https = new
                    {
                        Enabled = true,
                        Port = 18443,
                        CertificatePath = "missing-agent-certificate.pfx",
                        CertificatePasswordEnvironmentVariable = "MISSING_SSW_CERT_PASSWORD_FOR_TEST",
                        CertificateStoreThumbprint = new string('A', 40)
                    },
                    Switches = new[]
                    {
                        new
                        {
                            Id = "TEST-SW-01",
                            DisplayName = "Synthetic switch",
                            Model = "IES4224GP",
                            Host = "192.0.2.10",
                            Port = 23,
                            CredentialId = "readonly",
                            UplinkPort = "24"
                        }
                    }
                }
            };
            await File.WriteAllTextAsync(
                Path.Combine(folder, "appsettings.Production.json"),
                JsonSerializer.Serialize(configuration));

            var options = AgentMaintenanceBootstrap.LoadOptions(folder, Environments.Production);
            var created = await AgentMaintenanceBootstrap.CreatePairingCodeAsync(options);

            Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", created.Code);
            Assert.InRange(created.ExpiresUtc - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));
            Assert.Equal(new string('A', 40), options.Https.CertificateStoreThumbprint);
            Assert.True(File.Exists(options.DatabasePath));

            var verificationStore = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await verificationStore.InitializeAsync();
            var exchangedToken = await new PairingService(options, verificationStore).ExchangeAsync(created.Code);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", exchangedToken);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void PairingCreate_RejectsUnsafeEnvironmentNameBeforeReadingConfiguration()
    {
        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentMaintenanceBootstrap.LoadOptions(Path.GetTempPath(), "..\\Production"));

        Assert.Equal("CONFIG_INVALID", exception.Code);
    }
}
