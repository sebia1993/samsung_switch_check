using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Security;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;

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
    public async Task PairingTokenLimit_IsAtomicAndRevocationAllowsSameCodeToRetry()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                TokenPepper = new string('p', 48),
                Tokens = new TokenOptions { MaximumActiveTokens = 1, AbsoluteLifetimeDays = 180, IdleLifetimeDays = 60 }
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var pairing = new PairingService(options, store);

            var firstCode = await pairing.CreateCodeAsync();
            var firstToken = await pairing.ExchangeAsync(firstCode.Code);
            var secondCode = await pairing.CreateCodeAsync();
            var limit = await Assert.ThrowsAsync<AgentOperationException>(() => pairing.ExchangeAsync(secondCode.Code));
            Assert.Equal(AgentErrorCodes.TokenLimitReached, limit.Code);

            var firstInfo = Assert.Single(await pairing.ListTokensAsync());
            await pairing.RevokeTokenAsync(firstInfo.Id);
            var secondToken = await pairing.ExchangeAsync(secondCode.Code);

            Assert.False(await pairing.ValidateTokenAsync(firstToken));
            Assert.True(await pairing.ValidateTokenAsync(secondToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task TokenRotation_RevokesOldTokenImmediatelyAndReturnsOneReplacement()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                TokenPepper = new string('r', 48)
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var pairing = new PairingService(options, store);
            var code = await pairing.CreateCodeAsync();
            var oldToken = await pairing.ExchangeAsync(code.Code);
            var tokenId = Assert.Single(await pairing.ListTokensAsync()).Id;

            var replacement = await pairing.RotateTokenAsync(tokenId);

            Assert.False(await pairing.ValidateTokenAsync(oldToken));
            Assert.True(await pairing.ValidateTokenAsync(replacement));
            Assert.Equal(2, (await pairing.ListTokensAsync()).Count);
            Assert.Single(await pairing.ListTokensAsync(), item => !item.Expired);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task TokenRevocation_AbortsOnlyItsRealtimeSessionsAndDisposedLeaseIsCleaned()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                TokenPepper = new string('s', 48)
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var pairing = new PairingService(options, store);
            var firstCode = await pairing.CreateCodeAsync();
            var firstToken = await pairing.ExchangeAsync(firstCode.Code);
            var firstTokenId = Assert.Single(await pairing.ListTokensAsync()).Id;
            var secondCode = await pairing.CreateCodeAsync();
            var secondToken = await pairing.ExchangeAsync(secondCode.Code);
            var firstAbort = 0;
            var throwingAbort = 0;
            var secondAbort = 0;
            var firstLease = await pairing.TryRegisterRealtimeConnectionAsync(firstToken,
                () => Interlocked.Increment(ref firstAbort));
            var throwingLease = await pairing.TryRegisterRealtimeConnectionAsync(firstToken, () =>
            {
                Interlocked.Increment(ref throwingAbort);
                throw new InvalidOperationException("synthetic transport failure");
            });
            var secondLease = await pairing.TryRegisterRealtimeConnectionAsync(secondToken,
                () => Interlocked.Increment(ref secondAbort));
            Assert.NotNull(firstLease);
            Assert.NotNull(throwingLease);
            Assert.NotNull(secondLease);

            await pairing.RevokeTokenAsync(firstTokenId);

            Assert.Equal(1, firstAbort);
            Assert.Equal(1, throwingAbort);
            Assert.Equal(0, secondAbort);
            Assert.False(await pairing.ValidateTokenAsync(firstToken));
            Assert.True(await pairing.ValidateTokenAsync(secondToken));

            secondLease.Dispose();
            var secondTokenInfo = Assert.Single(await pairing.ListTokensAsync(), token => !token.Expired);
            await pairing.RevokeTokenAsync(secondTokenInfo.Id);
            Assert.Equal(0, secondAbort);
            firstLease.Dispose();
            throwingLease.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task TokenRotation_AbortsOldRealtimeSessionAndReplacementCanRegister()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                TokenPepper = new string('u', 48)
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var pairing = new PairingService(options, store);
            var code = await pairing.CreateCodeAsync();
            var oldToken = await pairing.ExchangeAsync(code.Code);
            var oldTokenId = Assert.Single(await pairing.ListTokensAsync()).Id;
            var aborts = 0;
            var oldLease = await pairing.TryRegisterRealtimeConnectionAsync(oldToken,
                () => Interlocked.Increment(ref aborts));
            Assert.NotNull(oldLease);

            var replacement = await pairing.RotateTokenAsync(oldTokenId);

            Assert.Equal(1, aborts);
            Assert.Null(await pairing.TryRegisterRealtimeConnectionAsync(oldToken, () => { }));
            var replacementLease = await pairing.TryRegisterRealtimeConnectionAsync(replacement, () => { });
            Assert.NotNull(replacementLease);
            replacementLease.Dispose();
            oldLease.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ConcurrentRealtimeDisposeAndRevoke_AbortsEachLiveSessionAtMostOnce()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                TokenPepper = new string('v', 48)
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var pairing = new PairingService(options, store);
            var code = await pairing.CreateCodeAsync();
            var token = await pairing.ExchangeAsync(code.Code);
            var tokenId = Assert.Single(await pairing.ListTokensAsync()).Id;
            var aborts = new int[32];
            var registrations = await Task.WhenAll(Enumerable.Range(0, aborts.Length).Select(async index =>
                await pairing.TryRegisterRealtimeConnectionAsync(token,
                    () => Interlocked.Increment(ref aborts[index]))));
            var leases = registrations.Select(lease => Assert.IsAssignableFrom<IDisposable>(lease)).ToArray();

            var disposeTask = Task.Run(() =>
            {
                for (var index = 0; index < leases.Length; index += 2)
                {
                    leases[index].Dispose();
                }
            });
            var revokeTask = pairing.RevokeTokenAsync(tokenId);
            await Task.WhenAll(disposeTask, revokeTask);

            for (var index = 0; index < aborts.Length; index++)
            {
                Assert.InRange(aborts[index], 0, 1);
                if (index % 2 != 0)
                {
                    Assert.Equal(1, aborts[index]);
                }
                leases[index].Dispose();
            }
            Assert.Null(await pairing.TryRegisterRealtimeConnectionAsync(token, () => { }));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task BearerMiddleware_RevokeAbortsActiveRealtimeRequest()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                TokenPepper = new string('w', 48)
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var pairing = new PairingService(options, store);
            var code = await pairing.CreateCodeAsync();
            var token = await pairing.ExchangeAsync(code.Code);
            var tokenId = Assert.Single(await pairing.ListTokensAsync()).Id;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var middleware = new BearerTokenMiddleware(async context =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // Expected when token revocation aborts this transport.
                }
            });
            var context = new DefaultHttpContext();
            using var requestLifetime = new TestRequestLifetimeFeature();
            context.Features.Set<IHttpRequestLifetimeFeature>(requestLifetime);
            context.Request.Path = "/hubs/events";
            context.Request.QueryString = QueryString.Create("access_token", token);
            context.Response.Body = new MemoryStream();

            var invocation = middleware.InvokeAsync(context, pairing);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pairing.RevokeTokenAsync(tokenId);
            await invocation.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(context.RequestAborted.IsCancellationRequested);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(181, 60)]
    [InlineData(61, 60)]
    [InlineData(180, 180)]
    [InlineData(60, 60)]
    public async Task TokenValidation_RejectsAbsoluteAndIdleExpiry(int ageDays, int idleDays)
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                Tokens = new TokenOptions
                {
                    MaximumActiveTokens = 5,
                    AbsoluteLifetimeDays = 180,
                    IdleLifetimeDays = idleDays
                }
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var hash = new string(ageDays > 100 ? 'A' : 'B', 64);
            var now = DateTimeOffset.UtcNow;
            await store.StoreTokenAsync(hash, now.AddDays(-ageDays));

            Assert.False(await store.ValidateAndTouchTokenAsync(hash, now));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ConcurrentPairingExchange_AtomicallyEnforcesLimitAndPreservesRejectedCode()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                TokenPepper = new string('c', 48),
                Tokens = new TokenOptions
                {
                    MaximumActiveTokens = 1,
                    AbsoluteLifetimeDays = 180,
                    IdleLifetimeDays = 60
                }
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var pairing = new PairingService(options, store);
            var codes = new[] { await pairing.CreateCodeAsync(), await pairing.CreateCodeAsync() };

            async Task<(int Index, string? Token, AgentOperationException? Error)> Exchange(int index)
            {
                try
                {
                    return (index, await pairing.ExchangeAsync(codes[index].Code), null);
                }
                catch (AgentOperationException exception)
                {
                    return (index, null, exception);
                }
            }

            var attempts = await Task.WhenAll(Exchange(0), Exchange(1));
            var successful = Assert.Single(attempts, attempt => attempt.Token is not null);
            var rejected = Assert.Single(attempts, attempt => attempt.Error is not null);
            Assert.Equal(AgentErrorCodes.TokenLimitReached, rejected.Error!.Code);
            var active = Assert.Single(await pairing.ListTokensAsync(), token => !token.Expired);

            await pairing.RevokeTokenAsync(active.Id);
            var retried = await pairing.ExchangeAsync(codes[rejected.Index].Code);

            Assert.False(await pairing.ValidateTokenAsync(successful.Token!));
            Assert.True(await pairing.ValidateTokenAsync(retried));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task RotatingExpiredToken_CannotExceedActiveTokenLimit()
    {
        var folder = CreateFolder();
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = true,
                Tokens = new TokenOptions
                {
                    MaximumActiveTokens = 1,
                    AbsoluteLifetimeDays = 180,
                    IdleLifetimeDays = 60
                }
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            await store.StoreTokenAsync(new string('A', 64), now.AddDays(-181));
            await store.StoreTokenAsync(new string('B', 64), now);
            var expired = Assert.Single(await store.GetApiTokensAsync(now), token => token.Expired);

            var exception = await Assert.ThrowsAsync<AgentOperationException>(() =>
                store.RotateTokenAsync(expired.Id, new string('C', 64), now));

            Assert.Equal(AgentErrorCodes.TokenLimitReached, exception.Code);
            var tokens = await store.GetApiTokensAsync(now);
            Assert.Single(tokens, token => !token.Expired);
            Assert.Null(Assert.Single(tokens, token => token.Id == expired.Id).RevokedUtc);
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

    [Fact]
    public void ConfigurationRejectsMoreThanFiveActiveTokens()
    {
        var options = new AgentOptions
        {
            DataDirectory = Path.GetTempPath(),
            Tokens = new TokenOptions { MaximumActiveTokens = 6 },
            Switches = [new SwitchOptions()]
        };

        var exception = Assert.Throws<AgentConfigurationException>(() =>
            AgentOptionsValidator.ValidateAndNormalize(options, Path.GetTempPath()));

        Assert.Equal("CONFIG_INVALID", exception.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    public void ConfigurationRejectsCertificatePinOverlapOutsideFourteenDayWindow(int days)
    {
        var folder = CreateFolder();
        try
        {
            var certificatePath = Path.Combine(folder, "placeholder.pfx");
            File.WriteAllBytes(certificatePath, [0x01]);
            var options = new AgentOptions
            {
                DataDirectory = folder,
                Https = new HttpsOptions
                {
                    Enabled = true,
                    CertificatePath = certificatePath,
                    PreviousCertificateSha256Fingerprint = new string('A', 64),
                    PreviousCertificateAcceptUntilUtc = DateTimeOffset.UtcNow.AddDays(days)
                },
                Switches = [new SwitchOptions()]
            };

            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentOptionsValidator.ValidateAndNormalize(options, folder));

            Assert.Equal("CONFIG_INVALID", exception.Code);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ConfigurationRejectsUnexpectedCharactersInCertificateFingerprint()
    {
        var folder = CreateFolder();
        try
        {
            var certificatePath = Path.Combine(folder, "placeholder.pfx");
            File.WriteAllBytes(certificatePath, [0x01]);
            var options = new AgentOptions
            {
                DataDirectory = folder,
                Https = new HttpsOptions
                {
                    Enabled = true,
                    CertificatePath = certificatePath,
                    PreviousCertificateSha256Fingerprint = "G" + new string('A', 64),
                    PreviousCertificateAcceptUntilUtc = DateTimeOffset.UtcNow.AddDays(7)
                },
                Switches = [new SwitchOptions()]
            };

            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentOptionsValidator.ValidateAndNormalize(options, folder));

            Assert.Equal("CONFIG_INVALID", exception.Code);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ConfigurationAcceptsAndNormalizesBoundedCertificatePinOverlap()
    {
        var folder = CreateFolder();
        try
        {
            var certificatePath = Path.Combine(folder, "placeholder.pfx");
            File.WriteAllBytes(certificatePath, [0x01]);
            var separatedFingerprint = string.Join(':', Enumerable.Repeat("ab", 32));
            var options = new AgentOptions
            {
                DataDirectory = folder,
                Https = new HttpsOptions
                {
                    Enabled = true,
                    CertificatePath = certificatePath,
                    PreviousCertificateSha256Fingerprint = separatedFingerprint,
                    PreviousCertificateAcceptUntilUtc = DateTimeOffset.UtcNow.AddDays(7)
                },
                Switches = [new SwitchOptions()]
            };

            AgentOptionsValidator.ValidateAndNormalize(options, folder);

            Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)),
                options.Https.PreviousCertificateSha256Fingerprint);
        }
        finally
        {
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

    private sealed class TestRequestLifetimeFeature : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        public CancellationToken RequestAborted
        {
            get => _cancellation.Token;
            set { }
        }

        public void Abort() => _cancellation.Cancel();

        public void Dispose() => _cancellation.Dispose();
    }
}
