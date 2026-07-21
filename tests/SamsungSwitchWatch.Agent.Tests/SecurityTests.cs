using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task CredentialVault_ReplacesAtomicallyAndLeavesNoTemporaryFiles()
    {
        var folder = CreateFolder();
        try
        {
            var vault = CreateVault(folder);
            await vault.StoreAsync("readonly", new SwitchCredential("operator", "first-secret"));
            await vault.StoreAsync("readonly", new SwitchCredential("operator", "second-secret"));

            var stored = await vault.GetAsync("readonly");

            Assert.NotNull(stored);
            Assert.Equal("operator", stored.Username);
            Assert.Equal("second-secret", stored.Password);
            Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task CredentialVault_MapsCorruptPayloadToStableSafeCode()
    {
        var folder = CreateFolder();
        try
        {
            var vault = CreateVault(folder);
            await vault.StoreAsync("readonly", new SwitchCredential("operator", "synthetic-secret"));
            var path = Path.Combine(folder, "credentials", "readonly.bin");
            await File.WriteAllTextAsync(path, "not-json");

            var exception = await Assert.ThrowsAsync<AgentOperationException>(() => vault.GetAsync("readonly"));

            Assert.Equal(AgentErrorCodes.CredentialCorrupt, exception.Code);
            Assert.DoesNotContain("not-json", exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("operator\rshow system", "secret")]
    [InlineData("operator", "secret\nnext")]
    [InlineData("operator\0suffix", "secret")]
    public async Task CredentialVault_RejectsUnsafeTelnetPayloadBeforeWriting(string username, string password)
    {
        var folder = CreateFolder();
        try
        {
            var vault = CreateVault(folder);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                vault.StoreAsync("readonly", new SwitchCredential(username, password)));

            Assert.False(Directory.Exists(Path.Combine(folder, "credentials")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task RawOutput_IsProtectedAtRestAndCanBeRecoveredOnlyByAgentProtector()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions { DataDirectory = folder, MockMode = true };
            var protector = new RawOutputProtector(options);
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance, protector);
            await store.InitializeAsync();
            const string raw = "show system\r\nprivate synthetic output\r\n";
            await store.InsertRawAsync("TEST-SW-01", "system", DateTimeOffset.UtcNow, raw);

            await using var connection = new SqliteConnection($"Data Source={options.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT content, protection_version FROM raw_blobs LIMIT 1;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var envelope = (byte[])reader[0];
            Assert.Equal(1, reader.GetInt32(1));

            Assert.DoesNotContain("private synthetic output", System.Text.Encoding.UTF8.GetString(envelope),
                StringComparison.Ordinal);
            Assert.Equal(raw, System.Text.Encoding.UTF8.GetString(protector.Unprotect(envelope)));
            var tampered = envelope.ToArray();
            tampered[^1] ^= 0x01;
            Assert.Throws<CryptographicException>(() => protector.Unprotect(tampered));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task RawStoreClearsProtectedWorkingBufferAfterDatabaseWrite()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions { DataDirectory = folder, MockMode = true };
            var protector = new TrackingRawOutputProtector();
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance, protector);
            await store.InitializeAsync();

            var plaintext = "synthetic raw"u8.ToArray();
            await store.InsertRawBytesAsync("TEST-SW-01", "system", DateTimeOffset.UtcNow, plaintext);

            Assert.NotNull(protector.ProtectedBuffer);
            Assert.All(protector.ProtectedBuffer, value => Assert.Equal(0, value));
            Assert.All(plaintext, value => Assert.Equal(0, value));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    private static FileCredentialVault CreateVault(string folder) =>
        new(new AgentOptions { DataDirectory = folder, MockMode = true }, new PassthroughProtector());

    private static string CreateFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-SecurityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class PassthroughProtector : ICredentialProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => protectedBytes.ToArray();
    }

    private sealed class TrackingRawOutputProtector : IRawOutputProtector
    {
        public byte[]? ProtectedBuffer { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            ProtectedBuffer = plaintext.ToArray();
            return ProtectedBuffer;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => protectedBytes.ToArray();
    }
}
