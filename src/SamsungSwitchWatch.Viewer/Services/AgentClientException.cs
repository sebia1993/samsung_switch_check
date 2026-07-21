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
            return new AgentClientException("VIEWER_PAIRING_REQUIRED", AgentConnectionState.NeedsPairing);
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

    public static AgentClientException Translate(Exception exception, bool certificatePinRejected = false)
    {
        if (exception is AgentClientException typed) return typed;
        if (certificatePinRejected)
        {
            return new AgentClientException("TLS_PIN_MISMATCH", AgentConnectionState.Offline, exception);
        }
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
                return new AgentClientException("VIEWER_PAIRING_REQUIRED", AgentConnectionState.NeedsPairing, exception);
            }
            if (request.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                return new AgentClientException("TLS_HANDSHAKE_FAILED", AgentConnectionState.Offline, exception);
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
