using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
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

            Assert.Equal("CREDENTIAL_CORRUPT", exception.Code);
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
    public void PairingAttemptLimiter_BoundsAttemptsAndRecoversAfterWindow()
    {
        var limiter = new PairingAttemptLimiter();
        var now = DateTimeOffset.Parse("2026-07-20T12:00:00Z");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.True(limiter.TryAcquire("loopback", now, out _));
        }

        Assert.False(limiter.TryAcquire("loopback", now, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
        Assert.True(limiter.TryAcquire("loopback", now.AddMinutes(1), out _));
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
}
