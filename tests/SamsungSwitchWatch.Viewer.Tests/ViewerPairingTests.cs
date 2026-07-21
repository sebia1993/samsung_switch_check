using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerPairingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Fingerprint = new('A', 64);

    [Fact]
    public void Bundle_ParseAcceptsSingleSafeSsw1Value()
    {
        var encoded = CreateBundle("https://agent.example.test:18443", Now.AddMinutes(10));

        var bundle = ViewerPairingBundle.Parse(encoded, Now);

        Assert.Equal(1, bundle.Version);
        Assert.Equal("https://agent.example.test:18443", bundle.AgentUri);
        Assert.Equal(Fingerprint, bundle.CertificateSha256);
        Assert.Equal("ABCD-EFGH-JKLM", bundle.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SSW2:AAAA")]
    [InlineData("SSW1:not+base64")]
    [InlineData("SSW1:AAAA")]
    public void Bundle_RejectsMalformedValues(string input)
    {
        var exception = Assert.Throws<ViewerPairingException>(() => ViewerPairingBundle.Parse(input, Now));
        Assert.Equal("PAIRING_STRING_INVALID", exception.ErrorCode);
    }

    [Theory]
    [InlineData("http://agent.example.test:18443")]
    [InlineData("https://user:secret@agent.example.test:18443")]
    [InlineData("https://agent.example.test:18443/path")]
    public void Bundle_RejectsUnsafeAgentUris(string uri)
    {
        var exception = Assert.Throws<ViewerPairingException>(() =>
            ViewerPairingBundle.Parse(CreateBundle(uri, Now.AddMinutes(10)), Now));

        Assert.Equal("PAIRING_ADDRESS_INVALID", exception.ErrorCode);
    }

    [Fact]
    public void Bundle_ReportsExpiredCodeBeforeNetworkUse()
    {
        var exception = Assert.Throws<ViewerPairingException>(() =>
            ViewerPairingBundle.Parse(CreateBundle("https://agent.example.test:18443", Now.AddSeconds(-1)), Now));

        Assert.Equal("PAIRING_EXPIRED", exception.ErrorCode);
    }

    [Fact]
    public void Bundle_RejectsDuplicateJsonProperties()
    {
        var raw = $$"""
        {"version":1,"version":1,"agentUri":"https://agent.example.test:18443","certificateSha256":"{{Fingerprint}}","code":"ABCD-EFGH-JKLM","expiresUtc":"{{Now.AddMinutes(10):O}}"}
        """;

        var exception = Assert.Throws<ViewerPairingException>(() =>
            ViewerPairingBundle.Parse(EncodeBundle(raw), Now));

        Assert.Equal("PAIRING_STRING_INVALID", exception.ErrorCode);
    }

    [Fact]
    public async Task PairAsync_ExchangesCodeAndKeepsFinalTokenOutOfJson()
    {
        var token = new string('T', 43);
        string? requestBody = null;
        var service = CreateService(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, new { token, tokenType = "Bearer" });
        });
        var original = new ViewerSettings
        {
            DemoMode = true,
            MiniTopmost = false,
            LastEventSequence = 19
        };

        var result = await service.PairAsync(
            CreateBundle("https://agent.example.test:18443", Now.AddMinutes(10)),
            original,
            CancellationToken.None);

        Assert.False(result.DemoMode);
        Assert.Equal("https://agent.example.test:18443", result.AgentUri);
        Assert.Equal(Fingerprint, result.CertificateFingerprint);
        Assert.Equal(token, result.BearerToken);
        Assert.False(result.MiniTopmost);
        Assert.Equal(19, result.LastEventSequence);
        Assert.Contains("ABCD-EFGH-JKLM", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "PAIRING_INVALID")]
    [InlineData(HttpStatusCode.TooManyRequests, "PAIRING_RATE_LIMITED")]
    [InlineData(HttpStatusCode.Conflict, "TOKEN_LIMIT_REACHED")]
    public async Task PairAsync_PreservesStableAgentErrorCodes(HttpStatusCode status, string code)
    {
        var service = CreateService(_ => Task.FromResult(Json(status, new
        {
            error = new { code, message = "sanitized" }
        })));

        var exception = await Assert.ThrowsAsync<ViewerPairingException>(() => service.PairAsync(
            CreateBundle("https://agent.example.test:18443", Now.AddMinutes(10)),
            new ViewerSettings(),
            CancellationToken.None));

        Assert.Equal(code, exception.ErrorCode);
        Assert.DoesNotContain("sanitized", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairAsync_RejectsUnexpectedTokenResponse()
    {
        var service = CreateService(_ => Task.FromResult(Json(HttpStatusCode.OK, new
        {
            token = "short",
            tokenType = "Bearer"
        })));

        var exception = await Assert.ThrowsAsync<ViewerPairingException>(() => service.PairAsync(
            CreateBundle("https://agent.example.test:18443", Now.AddMinutes(10)),
            new ViewerSettings(),
            CancellationToken.None));

        Assert.Equal("PAIRING_RESPONSE_INVALID", exception.ErrorCode);
    }

    [Fact]
    public async Task PairAsync_RejectsOversizedResponseBeforeParsing()
    {
        var service = CreateService(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('A', 64 * 1024 + 1), Encoding.UTF8, "application/json")
        }));

        var exception = await Assert.ThrowsAsync<ViewerPairingException>(() => service.PairAsync(
            CreateBundle("https://agent.example.test:18443", Now.AddMinutes(10)),
            new ViewerSettings(),
            CancellationToken.None));

        Assert.Equal("PAIRING_RESPONSE_INVALID", exception.ErrorCode);
    }

    [Fact]
    public async Task PairAsync_RejectsDuplicateSuccessProperties()
    {
        var token = new string('T', 43);
        var service = CreateService(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"token\":\"{token}\",\"token\":\"{token}\",\"tokenType\":\"Bearer\"}}",
                Encoding.UTF8,
                "application/json")
        }));

        var exception = await Assert.ThrowsAsync<ViewerPairingException>(() => service.PairAsync(
            CreateBundle("https://agent.example.test:18443", Now.AddMinutes(10)),
            new ViewerSettings(),
            CancellationToken.None));

        Assert.Equal("PAIRING_RESPONSE_INVALID", exception.ErrorCode);
    }

    [Fact]
    public async Task PairAsync_ReportsCertificatePinRejectionPrecisely()
    {
        var handler = new ThrowingHandler(new HttpRequestException("TLS failed"));
        var service = new ViewerPairingService(
            _ => new PairingHttpClientContext(handler, () => true),
            () => Now);

        var exception = await Assert.ThrowsAsync<ViewerPairingException>(() => service.PairAsync(
            CreateBundle("https://agent.example.test:18443", Now.AddMinutes(10)),
            new ViewerSettings(),
            CancellationToken.None));

        Assert.Equal("TLS_PIN_MISMATCH", exception.ErrorCode);
    }

    [Fact]
    public void PinnedHandler_DisablesRedirectsAndProxyAndChecksRealCertificateBytes()
    {
        using var expected = CreateCertificate("expected");
        using var other = CreateCertificate("other");
        using var handler = new PairingPinnedHttpClientHandler(SHA256.HashData(expected.RawData));
        var callback = handler.ServerCertificateCustomValidationCallback!;

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.True(callback(new HttpRequestMessage(), expected, null, SslPolicyErrors.None));
        Assert.False(callback(new HttpRequestMessage(), other, null, SslPolicyErrors.None));
        Assert.True(handler.PinRejected);
    }

    [Fact]
    public async Task PairingFlow_ReusesIssuedTokenAfterApplyFailure()
    {
        var exchangeCount = 0;
        var token = new string('T', 43);
        var service = CreateService(_ =>
        {
            exchangeCount++;
            return Task.FromResult(Json(HttpStatusCode.OK, new { token, tokenType = "Bearer" }));
        });
        var flow = new ViewerPairingFlow(service);
        var bundle = CreateBundle("https://agent.example.test:18443", Now.AddMinutes(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.PairAndApplyAsync(
            bundle,
            new ViewerSettings(),
            false,
            (_, _) => throw new InvalidOperationException("snapshot failed"),
            CancellationToken.None));

        Assert.True(flow.HasPendingSettings);
        var applied = await flow.PairAndApplyAsync(
            bundle,
            new ViewerSettings(),
            false,
            (_, _) => Task.CompletedTask,
            CancellationToken.None);
        Assert.Equal(token, applied.BearerToken);
        Assert.Equal(1, exchangeCount);
        Assert.False(flow.HasPendingSettings);
    }

    [Fact]
    public async Task ConnectionApply_PersistsTokenBeforeNetworkPreflight()
    {
        var order = new List<string>();
        var settings = new ViewerSettings { BearerToken = new string('T', 43) };
        ViewerSettings? persisted = null;

        await Assert.ThrowsAsync<HttpRequestException>(() => ViewerConnectionApply.PersistThenSwitchAsync(
            settings,
            candidate =>
            {
                order.Add("persist");
                persisted = candidate;
            },
            (_, _) =>
            {
                order.Add("preflight");
                throw new HttpRequestException("offline");
            },
            CancellationToken.None));

        Assert.Equal(["persist", "preflight"], order);
        Assert.Equal(settings.BearerToken, persisted?.BearerToken);
    }

    [Fact]
    public void MissingSettings_StartInSafeDefaultsAndRequestWizard()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-PairingTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ViewerSettingsStore(Path.Combine(folder, "viewer-settings.json"));
            var loaded = store.Load();

            Assert.Equal(ViewerSettingsLoadStatus.Missing, store.LastLoadStatus);
            Assert.True(loaded.DemoMode);
            Assert.True(StartupWindowPolicy.ShouldShowMainWindow(loaded, needsPairing: true));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static ViewerPairingService CreateService(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
    {
        return new ViewerPairingService(
            _ => new PairingHttpClientContext(new StubHandler(responseFactory), () => false),
            () => Now);
    }

    private static string CreateBundle(string uri, DateTimeOffset expiresUtc)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            agentUri = uri,
            certificateSha256 = Fingerprint,
            code = "ABCD-EFGH-JKLM",
            expiresUtc
        });
        return EncodeBundle(json);
    }

    private static string EncodeBundle(string json) =>
        "SSW1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static X509Certificate2 CreateCertificate(string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(exception);
    }
}
