using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SamsungSwitchWatch.Viewer.Services;

internal sealed record ViewerPairingBundle(
    int Version,
    string AgentUri,
    string CertificateSha256,
    string Code,
    DateTimeOffset ExpiresUtc)
{
    private const string Prefix = "SSW1:";
    private static readonly Regex PairingCodePattern = new(
        "^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ViewerPairingBundle Parse(string? input, DateTimeOffset now)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length is < 8 or > 4096 || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new ViewerPairingException("PAIRING_STRING_INVALID");
        }

        var encoded = text[Prefix.Length..];
        if (encoded.Length == 0 || encoded.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ViewerPairingException("PAIRING_STRING_INVALID");
        }

        byte[] decoded;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            decoded = Convert.FromBase64String(padded);
        }
        catch (FormatException exception)
        {
            throw new ViewerPairingException("PAIRING_STRING_INVALID", exception);
        }

        if (decoded.Length is < 20 or > 3072)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new ViewerPairingException("PAIRING_STRING_INVALID");
        }

        try
        {
            using var document = JsonDocument.Parse(decoded, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var versionElement) ||
                !versionElement.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("agentUri", out var uriElement) ||
                !root.TryGetProperty("certificateSha256", out var fingerprintElement) ||
                !root.TryGetProperty("code", out var codeElement) ||
                !root.TryGetProperty("expiresUtc", out var expiresElement))
            {
                throw new ViewerPairingException("PAIRING_STRING_INVALID");
            }

            var allowed = new HashSet<string>(
                ["version", "agentUri", "certificateSha256", "code", "expiresUtc"],
                StringComparer.Ordinal);
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != allowed.Count ||
                properties.Any(property => !allowed.Contains(property.Name)) ||
                properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            {
                throw new ViewerPairingException("PAIRING_STRING_INVALID");
            }

            var agentUri = uriElement.GetString()?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(agentUri, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ViewerPairingException("PAIRING_ADDRESS_INVALID");
            }

            var fingerprintInput = fingerprintElement.GetString() ?? string.Empty;
            var fingerprint = ViewerSettingsSanitizer.NormalizeFingerprint(fingerprintInput);
            if (fingerprint.Length != 64 || fingerprintInput.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new ViewerPairingException("PAIRING_FINGERPRINT_INVALID");
            }

            var code = codeElement.GetString()?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!PairingCodePattern.IsMatch(code))
            {
                throw new ViewerPairingException("PAIRING_CODE_INVALID");
            }

            if (expiresElement.ValueKind != JsonValueKind.String ||
                !expiresElement.TryGetDateTimeOffset(out var expiresUtc))
            {
                throw new ViewerPairingException("PAIRING_STRING_INVALID");
            }
            if (expiresUtc <= now)
            {
                throw new ViewerPairingException("PAIRING_EXPIRED");
            }
            if (expiresUtc > now.AddHours(2))
            {
                throw new ViewerPairingException("PAIRING_STRING_INVALID");
            }

            return new ViewerPairingBundle(
                version,
                uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
                fingerprint,
                code,
                expiresUtc.ToUniversalTime());
        }
        catch (ViewerPairingException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new ViewerPairingException("PAIRING_STRING_INVALID", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }
}

public sealed class ViewerPairingException : Exception
{
    public ViewerPairingException(string errorCode, Exception? innerException = null)
        : base(errorCode, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
    public string UserMessage => ViewerPairingMessages.ForCode(ErrorCode);
}

internal sealed record PairingHttpClientContext(
    HttpMessageHandler Handler,
    Func<bool> CertificatePinRejected);

public sealed class ViewerPairingService
{
    private readonly Func<ViewerPairingBundle, PairingHttpClientContext> _transportFactory;
    private readonly Func<DateTimeOffset> _clock;

    public ViewerPairingService()
        : this(CreatePinnedTransport, () => DateTimeOffset.UtcNow)
    {
    }

    internal ViewerPairingService(
        Func<ViewerPairingBundle, PairingHttpClientContext> transportFactory,
        Func<DateTimeOffset>? clock = null)
    {
        _transportFactory = transportFactory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ViewerSettings> PairAsync(
        string pairingString,
        ViewerSettings original,
        CancellationToken cancellationToken)
    {
        var bundle = ViewerPairingBundle.Parse(pairingString, _clock());
        var transport = _transportFactory(bundle);
        using var handler = transport.Handler;
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(bundle.AgentUri),
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SamsungSwitchWatch.Viewer/2.0-pairing");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                "/api/v1/pairing/exchange",
                new { code = bundle.Code },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (transport.CertificatePinRejected())
            {
                throw new ViewerPairingException("TLS_PIN_MISMATCH", exception);
            }

            var translated = AgentClientErrors.Translate(exception);
            throw new ViewerPairingException(translated.ErrorCode, exception);
        }

        using (response)
        {
            string responseBody;
            try
            {
                responseBody = await ReadBoundedResponseBodyAsync(
                    response.Content,
                    64 * 1024,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ViewerPairingException("PAIRING_RESPONSE_INVALID", exception);
            }

            if (!response.IsSuccessStatusCode)
            {
                var serverCode = AgentClientErrors.ExtractStableServerCode(responseBody);
                var fallback = response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "PAIRING_INVALID",
                    HttpStatusCode.TooManyRequests => "PAIRING_RATE_LIMITED",
                    HttpStatusCode.Conflict => "TOKEN_LIMIT_REACHED",
                    _ => "AGENT_HTTP_ERROR"
                };
                throw new ViewerPairingException(serverCode ?? fallback);
            }

            string token;
            try
            {
                using var document = JsonDocument.Parse(responseBody, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
                var root = document.RootElement;
                var properties = root.ValueKind == JsonValueKind.Object
                    ? root.EnumerateObject().ToArray()
                    : [];
                var allowed = new HashSet<string>(["token", "tokenType"], StringComparer.Ordinal);
                if (properties.Length != allowed.Count ||
                    properties.Any(property => !allowed.Contains(property.Name)) ||
                    properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
                {
                    throw new ViewerPairingException("PAIRING_RESPONSE_INVALID");
                }
                token = root.TryGetProperty("token", out var tokenElement)
                    ? tokenElement.GetString() ?? string.Empty
                    : string.Empty;
                var tokenType = root.TryGetProperty("tokenType", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                if (token.Length != 43 ||
                    token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
                    !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ViewerPairingException("PAIRING_RESPONSE_INVALID");
                }
            }
            catch (ViewerPairingException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new ViewerPairingException("PAIRING_RESPONSE_INVALID", exception);
            }

            var paired = CopySettings(original);
            paired.DemoMode = false;
            paired.AgentUri = bundle.AgentUri;
            paired.CertificateFingerprint = bundle.CertificateSha256;
            paired.CertificateFingerprints = [bundle.CertificateSha256];
            paired.BearerToken = token;
            paired.ProtectedBearerToken = string.Empty;
            var clean = ViewerSettingsSanitizer.Sanitize(paired);
            clean.BearerToken = token;
            return clean;
        }
    }

    private static async Task<string> ReadBoundedResponseBodyAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declaredLength && declaredLength > maximumBytes)
        {
            throw new ViewerPairingException("PAIRING_RESPONSE_INVALID");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 4096));
        var block = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(block.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
            {
                throw new ViewerPairingException("PAIRING_RESPONSE_INVALID");
            }
            buffer.Write(block, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static PairingHttpClientContext CreatePinnedTransport(ViewerPairingBundle bundle)
    {
        var handler = new PairingPinnedHttpClientHandler(Convert.FromHexString(bundle.CertificateSha256));
        return new PairingHttpClientContext(handler, () => handler.PinRejected);
    }

    internal static ViewerSettings CopySettings(ViewerSettings source) => new()
    {
        DemoMode = source.DemoMode,
        AgentUri = source.AgentUri,
        CertificateFingerprint = source.CertificateFingerprint,
        CertificateFingerprints = [.. source.CertificateFingerprints],
        ProtectedBearerToken = source.ProtectedBearerToken,
        BearerToken = source.BearerToken,
        LastEventSequence = source.LastEventSequence,
        EventCursors = new Dictionary<string, long>(source.EventCursors, StringComparer.Ordinal),
        MiniTopmost = source.MiniTopmost,
        MiniLeft = source.MiniLeft,
        MiniTop = source.MiniTop,
        MainLeft = source.MainLeft,
        MainTop = source.MainTop,
        MainWidth = source.MainWidth,
        MainHeight = source.MainHeight,
        StartMinimizedToTray = source.StartMinimizedToTray
    };
}

internal sealed class ViewerPairingFlow(ViewerPairingService pairingService)
{
    private ViewerSettings? _pendingSettings;
    private string? _pendingInputHash;

    public bool HasPendingSettings => _pendingSettings is not null;

    public async Task<ViewerSettings> PairAndApplyAsync(
        string pairingString,
        ViewerSettings original,
        bool startMinimizedToTray,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applySettingsAsync);
        var inputHash = PairingInputHash(pairingString);
        if (_pendingSettings is null || !string.Equals(_pendingInputHash, inputHash, StringComparison.Ordinal))
        {
            _pendingSettings = await pairingService.PairAsync(pairingString, original, cancellationToken)
                .ConfigureAwait(false);
            _pendingInputHash = inputHash;
        }

        _pendingSettings.StartMinimizedToTray = startMinimizedToTray;
        await applySettingsAsync(_pendingSettings, cancellationToken).ConfigureAwait(false);
        var applied = _pendingSettings;
        _pendingSettings = null;
        _pendingInputHash = null;
        return applied;
    }

    private static string PairingInputHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input.Trim());
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal static class ViewerConnectionApply
{
    public static async Task PersistThenSwitchAsync(
        ViewerSettings settings,
        Action<ViewerSettings> persist,
        Func<ViewerSettings, CancellationToken, Task> switchClientAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(switchClientAsync);
        persist(settings);
        await switchClientAsync(settings, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class PairingPinnedHttpClientHandler : HttpClientHandler
{
    private readonly byte[] _expectedPin;
    private int _pinRejected;

    public PairingPinnedHttpClientHandler(byte[] expectedPin)
    {
        _expectedPin = expectedPin;
        AllowAutoRedirect = false;
        UseProxy = false;
        ServerCertificateCustomValidationCallback = Validate;
    }

    public bool PinRejected => Volatile.Read(ref _pinRejected) != 0;

    private bool Validate(
        HttpRequestMessage _,
        X509Certificate2? certificate,
        X509Chain? __,
        SslPolicyErrors ___)
    {
        if (certificate is null)
        {
            Interlocked.Exchange(ref _pinRejected, 1);
            return false;
        }

        var actual = SHA256.HashData(certificate.RawData);
        var accepted = CryptographicOperations.FixedTimeEquals(actual, _expectedPin);
        if (!accepted) Interlocked.Exchange(ref _pinRejected, 1);
        return accepted;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CryptographicOperations.ZeroMemory(_expectedPin);
        base.Dispose(disposing);
    }
}

internal static class ViewerPairingMessages
{
    public static string ForCode(string? errorCode) => errorCode switch
    {
        "PAIRING_STRING_INVALID" => "연결 문자열을 읽을 수 없습니다. Agent PC에서 새 문자열을 만든 뒤 전체를 붙여 넣으세요.",
        "PAIRING_ADDRESS_INVALID" => "연결 문자열의 Agent HTTPS 주소가 올바르지 않습니다.",
        "PAIRING_FINGERPRINT_INVALID" => "연결 문자열의 인증서 정보가 올바르지 않습니다.",
        "PAIRING_CODE_INVALID" => "연결 문자열의 일회용 코드 형식이 올바르지 않습니다.",
        "PAIRING_EXPIRED" or "PAIRING_INVALID" => "연결 문자열이 만료되었거나 이미 사용되었습니다. Agent PC에서 새 문자열을 만드세요.",
        "PAIRING_RATE_LIMITED" => "페어링 시도가 너무 많습니다. 잠시 기다린 뒤 새 문자열로 다시 시도하세요.",
        "TOKEN_LIMIT_REACHED" => "등록 가능한 Viewer 수를 초과했습니다. Agent PC에서 사용하지 않는 토큰을 해지하세요.",
        "TLS_PIN_MISMATCH" => "Agent 인증서가 연결 문자열과 일치하지 않아 연결을 차단했습니다.",
        "AGENT_DNS_FAILED" => "Agent PC 이름을 찾지 못했습니다. 주소 또는 사내 DNS 연결을 확인하세요.",
        "AGENT_CONNECTION_REFUSED" => "Agent가 연결을 거부했습니다. 서비스 실행 상태와 방화벽을 확인하세요.",
        "AGENT_TIMEOUT" => "Agent 응답 시간이 초과되었습니다. 네트워크 경로를 확인하세요.",
        "PAIRING_RESPONSE_INVALID" or "AGENT_RESPONSE_INVALID" => "Agent의 페어링 응답 형식이 올바르지 않습니다.",
        _ => "Agent에 연결하지 못했습니다. Agent 서비스와 네트워크 경로를 확인하세요."
    };
}
