using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class AgentClientException : Exception
{
    public AgentClientException(
        string errorCode,
        AgentConnectionState suggestedConnectionState,
        Exception? innerException = null)
        : base(errorCode, innerException)
    {
        ErrorCode = errorCode;
        SuggestedConnectionState = suggestedConnectionState;
    }

    public string ErrorCode { get; }
    public AgentConnectionState SuggestedConnectionState { get; }
}

internal static class AgentClientErrors
{
    public static AgentClientException FromStatus(HttpStatusCode statusCode, string? responseBody = null)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new AgentClientException("AGENT_ACCESS_DENIED", AgentConnectionState.Stale);
        }

        if (statusCode == HttpStatusCode.ServiceUnavailable)
        {
            return new AgentClientException(
                ExtractStableServerCode(responseBody) ?? "AGENT_NOT_READY",
                AgentConnectionState.Stale);
        }

        if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
        {
            return new AgentClientException("AGENT_TIMEOUT", AgentConnectionState.Offline);
        }

        if (statusCode is HttpStatusCode.BadGateway)
        {
            return new AgentClientException("AGENT_UNREACHABLE", AgentConnectionState.Offline);
        }

        return new AgentClientException("AGENT_HTTP_ERROR", AgentConnectionState.Stale);
    }

    public static AgentClientException Translate(Exception exception)
    {
        if (exception is AgentClientException typed) return typed;
        if (exception is JsonException or InvalidDataException or FormatException or InvalidOperationException)
        {
            return new AgentClientException("AGENT_RESPONSE_INVALID", AgentConnectionState.Stale, exception);
        }
        if (exception is TaskCanceledException or TimeoutException)
        {
            return new AgentClientException("AGENT_TIMEOUT", AgentConnectionState.Offline, exception);
        }
        if (exception is HttpRequestException request)
        {
            if (request.StatusCode is { } statusCode) return FromStatus(statusCode);
            if (request.HttpRequestError == HttpRequestError.NameResolutionError)
            {
                return new AgentClientException("AGENT_DNS_FAILED", AgentConnectionState.Offline, exception);
            }
            if (request.HttpRequestError == HttpRequestError.UserAuthenticationError)
            {
                return new AgentClientException("AGENT_ACCESS_DENIED", AgentConnectionState.Stale, exception);
            }
            if (request.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                return new AgentClientException("AGENT_PROTOCOL_MISMATCH", AgentConnectionState.Offline, exception);
            }
            if (FindSocketException(request) is { } socket) return FromSocket(socket, exception);
            if (request.HttpRequestError == HttpRequestError.ConnectionError)
            {
                return new AgentClientException("AGENT_UNREACHABLE", AgentConnectionState.Offline, exception);
            }
        }
        if (FindSocketException(exception) is { } nestedSocket) return FromSocket(nestedSocket, exception);
        return new AgentClientException("AGENT_UNREACHABLE", AgentConnectionState.Offline, exception);
    }

    public static string? ExtractStableServerCode(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("error", out var error)
                || !error.TryGetProperty("code", out var codeElement)) return null;
            var code = codeElement.GetString();
            return IsStableCode(code) ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentClientException FromSocket(SocketException socket, Exception source) => socket.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
            new AgentClientException("AGENT_DNS_FAILED", AgentConnectionState.Offline, source),
        SocketError.ConnectionRefused =>
            new AgentClientException("AGENT_CONNECTION_REFUSED", AgentConnectionState.Offline, source),
        SocketError.TimedOut =>
            new AgentClientException("AGENT_TIMEOUT", AgentConnectionState.Offline, source),
        _ => new AgentClientException("AGENT_UNREACHABLE", AgentConnectionState.Offline, source)
    };

    private static SocketException? FindSocketException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket) return socket;
        }
        return null;
    }

    private static bool IsStableCode(string? code) => !string.IsNullOrWhiteSpace(code)
        && code.Length <= 64
        && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
}

internal static class ViewerConnectionMessages
{
    public static string ForCode(string? errorCode) => errorCode switch
    {
        "AGENT_DNS_FAILED" => "Agent PC 이름을 찾지 못했습니다. 주소 또는 사내 DNS 연결을 확인해 주세요.",
        "AGENT_CONNECTION_REFUSED" => "Agent가 연결을 거부했습니다. 서비스 실행 상태와 방화벽을 확인해 주세요.",
        "AGENT_TIMEOUT" => "Agent 응답 시간이 초과되었습니다. 네트워크 경로를 확인해 주세요.",
        "AGENT_ACCESS_DENIED" => "Agent 접근이 거부되었습니다. Windows 방화벽의 허용 Viewer IPv4를 확인해 주세요.",
        "AGENT_PROTOCOL_MISMATCH" => "Agent와 Viewer의 통신 방식이 다릅니다. Agent를 v0.6 이상으로 먼저 업데이트해 주세요.",
        "AGENT_NOT_READY" or "STORAGE_WRITE_FAILED" => "Agent가 아직 상태 제공을 준비하지 못했습니다. Agent 상태를 확인해 주세요.",
        "AGENT_RESPONSE_INVALID" => "Agent 응답 형식이 올바르지 않습니다. Agent와 Viewer 버전을 확인해 주세요.",
        _ => "Agent에 연결하지 못했습니다. 서비스와 네트워크 경로를 확인해 주세요."
    };
}
