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
        var serverCode = ExtractStableServerCode(responseBody);
        if (IsReadOnlyQueryCode(serverCode))
        {
            return new AgentClientException(serverCode!, AgentConnectionState.Stale);
        }

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

    private static bool IsReadOnlyQueryCode(string? code) => code is
        "QUERY_DISABLED" or
        "QUERY_COMMAND_BLOCKED" or
        "REQUEST_INVALID" or
        "REQUEST_TOO_LARGE" or
        "TARGET_NOT_ALLOWED" or
        "IPV6_UNSUPPORTED" or
        "AGENT_BUSY" or
        "CONFIG_INVALID" or
        "TLS_IDENTITY_INVALID" or
        "ENABLE_FAILED" or
        "OUTPUT_LIMIT_EXCEEDED" or
        "DEVICE_NOT_FOUND" or
        "DEVICE_BUSY" or
        "QUERY_RATE_LIMITED" or
        "QUERY_TIMEOUT" or
        "CREDENTIAL_NOT_FOUND" or
        "CREDENTIAL_DECRYPT_FAILED" or
        "CREDENTIAL_CORRUPT" or
        "CREDENTIAL_UNAVAILABLE" or
        "AUTH_FAILED" or
        "TCP_TIMEOUT" or
        "LOGIN_PROMPT_NOT_FOUND" or
        "PROMPT_PARSE_FAILED" or
        "TELNET_NEGOTIATION_FAILED" or
        "TELNET_SESSION_CLOSED" or
        "COMMAND_TIMEOUT" or
        "SESSION_CLOSED";
}

internal static class ViewerConnectionMessages
{
    public static string ForCode(string? errorCode) => errorCode switch
    {
        "AGENT_DNS_FAILED" => "Agent PC 이름을 찾지 못했습니다. 주소 또는 사내 DNS 연결을 확인해 주세요.",
        "AGENT_CONNECTION_REFUSED" => "Agent가 연결을 거부했습니다. 서비스 실행 상태와 방화벽을 확인해 주세요.",
        "AGENT_UNREACHABLE" => "Agent PC까지 통신하지 못했습니다. 네트워크 경로와 방화벽을 확인해 주세요.",
        "AGENT_TIMEOUT" => "Agent 응답 시간이 초과되었습니다. 네트워크 경로를 확인해 주세요.",
        "AGENT_HTTP_ERROR" or "AGENT_INTERNAL_ERROR" => "Agent가 요청을 처리하지 못했습니다. Agent 서비스 상태와 진단 로그를 확인해 주세요.",
        "AGENT_ACCESS_DENIED" => "Agent 접근이 거부되었습니다. Windows 방화벽의 허용 Viewer IPv4를 확인해 주세요.",
        "AGENT_PROTOCOL_MISMATCH" => "Agent와 Viewer의 통신 방식이 다릅니다. Agent를 최신 버전으로 먼저 업데이트해 주세요.",
        "AGENT_IDENTITY_CHANGED" => "이전에 연결한 Agent와 인증 정보가 다릅니다. Agent 교체 여부를 확인한 뒤 신뢰를 다시 설정해 주세요.",
        "AGENT_NOT_READY" or "STORAGE_WRITE_FAILED" => "Agent가 아직 상태 제공을 준비하지 못했습니다. Agent 상태를 확인해 주세요.",
        "AGENT_RESPONSE_INVALID" => "Agent 응답 형식이 올바르지 않습니다. Agent와 Viewer 버전을 확인해 주세요.",
        "QUERY_DISABLED" => "Agent에서 장비 명령 기능이 꺼져 있습니다. Agent 설치 설정을 확인해 주세요.",
        "QUERY_COMMAND_BLOCKED" => "안전 정책에 따라 이 명령은 실행할 수 없습니다. 허용된 show 조회 명령만 입력해 주세요.",
        "DEVICE_NOT_FOUND" or "VIEWER_DEVICE_NOT_FOUND" => "Viewer에 저장된 장비 정보를 찾지 못했습니다. 장비 관리에서 다시 확인해 주세요.",
        "VIEWER_DEVICE_INVALID" => "장비 IP, 모델 또는 Telnet 포트가 올바르지 않습니다.",
        "VIEWER_CONNECTION_TEST_REQUIRED" => "접속 시험에 성공한 뒤 주기 감시를 켜 주세요.",
        "DEVICE_BUSY" => "선택한 장비가 다른 점검을 수행 중입니다. 잠시 후 다시 시도해 주세요.",
        "AGENT_BUSY" => "Agent가 다른 장비 요청을 처리 중입니다. 잠시 후 다시 시도해 주세요.",
        "TARGET_NOT_ALLOWED" => "Agent에서 이 장비 관리망으로의 접속을 허용하지 않았습니다.",
        "REQUEST_INVALID" or "IPV6_UNSUPPORTED" => "장비 IP, 모델 또는 요청 형식을 확인해 주세요.",
        "REQUEST_TOO_LARGE" => "한 번에 전송한 명령이 너무 많거나 요청 크기가 너무 큽니다.",
        "CONFIG_INVALID" => "Agent의 관리망 허용 설정이 올바르지 않습니다.",
        "TLS_IDENTITY_INVALID" => "Agent의 HTTPS 식별 정보를 확인하지 못했습니다.",
        "QUERY_RATE_LIMITED" => "짧은 시간에 요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.",
        "QUERY_TIMEOUT" or "COMMAND_TIMEOUT" => "장비 응답 시간이 초과되었습니다. Telnet 연결과 장비 상태를 확인해 주세요.",
        "CREDENTIAL_NOT_FOUND" or "CREDENTIAL_DECRYPT_FAILED" or "CREDENTIAL_CORRUPT" or "CREDENTIAL_UNAVAILABLE" or "VIEWER_CREDENTIAL_CORRUPT" => "Viewer에서 장비 계정을 사용할 수 없습니다. 장비 관리에서 비밀번호를 다시 저장해 주세요.",
        "AUTH_FAILED" => "스위치 로그인이 실패했습니다. Viewer의 장비 ID와 비밀번호를 확인해 주세요.",
        "TCP_TIMEOUT" => "스위치 Telnet 연결 시간이 초과되었습니다. Agent PC에서 장비 경로를 확인해 주세요.",
        "LOGIN_PROMPT_NOT_FOUND" => "스위치 로그인 프롬프트를 인식하지 못했습니다. 모델 또는 펌웨어 출력을 확인해 주세요.",
        "PROMPT_PARSE_FAILED" => "스위치 명령 프롬프트를 인식하지 못했습니다. 모델 프로파일을 확인해 주세요.",
        "ENABLE_FAILED" => "enable 권한 전환에 실패했습니다. Viewer의 enable 비밀번호를 확인해 주세요.",
        "OUTPUT_LIMIT_EXCEEDED" => "장비 출력이 안전 제한을 초과했습니다. 더 범위가 좁은 show 명령을 사용해 주세요.",
        "OUTPUT_TRUNCATED" => "장비 출력이 안전 제한에서 잘렸습니다. 기존 감시 기준은 유지되며 다음 점검에서 다시 확인합니다.",
        "COMMAND_OUTPUT_MISSING" or "COMMAND_OUTPUT_EMPTY" => "장비 명령 결과가 완전하게 도착하지 않았습니다. 기존 상태를 유지하고 다음 점검에서 다시 확인합니다.",
        "COMMAND_UNSUPPORTED" => "이 장비 또는 펌웨어가 등록된 조회 명령을 지원하지 않습니다.",
        "PARSER_UNSUPPORTED" => "장비 출력 형식을 아직 해석하지 못했습니다. 원문 결과와 모델·펌웨어를 확인해 주세요.",
        "INCOMPLETE_OUTPUT" => "장비 출력이 완전하지 않아 상태를 갱신하지 않았습니다. 다음 점검에서 다시 확인합니다.",
        "VIEWER_MONITOR_STATE_WRITE_FAILED" => "Viewer 감시 이력을 저장하지 못했습니다. 사용자 폴더 권한과 디스크 여유 공간을 확인해 주세요.",
        "VIEWER_MONITOR_CYCLE_FAILED" => "주기 감시 중 예상하지 못한 오류가 발생했습니다. 다음 주기에 다시 시도합니다.",
        "VIEWER_SETTINGS_WRITE_FAILED" => "Viewer 설정을 저장하지 못했습니다. 사용자 폴더 권한과 디스크 여유 공간을 확인해 주세요.",
        "VIEWER_CONNECTION_REQUIRED" => "먼저 Agent 연결 설정을 완료해 주세요.",
        "VIEWER_CONFIGURATION_INVALID" => "Viewer 설정값이 올바르지 않습니다. Agent 연결과 장비 설정을 확인해 주세요.",
        "VIEWER_UNEXPECTED_ERROR" => "Viewer에서 예상하지 못한 오류가 발생했습니다. 다시 시도하고 계속되면 진단 로그를 확인해 주세요.",
        "TELNET_NEGOTIATION_FAILED" => "스위치와 Telnet 옵션 협상에 실패했습니다. 장비 Telnet 상태를 확인해 주세요.",
        "TELNET_SESSION_CLOSED" or "SESSION_CLOSED" => "명령 결과를 받기 전에 Telnet 세션이 종료되었습니다. 장비 세션 제한을 확인해 주세요.",
        _ => "Agent에 연결하지 못했습니다. 서비스와 네트워크 경로를 확인해 주세요."
    };
}
