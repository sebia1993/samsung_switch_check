namespace SamsungSwitchWatch.Viewer.Services;

internal enum DeviceManagementOperation
{
    Load,
    Save,
    Delete,
    Close
}

/// <summary>
/// Converts local device-store failures into stable, non-sensitive error codes.
/// Exception text is deliberately not written to diagnostics.
/// </summary>
internal static class DeviceManagementFailureMapper
{
    private static readonly HashSet<string> SafeValidationMessages =
        new(StringComparer.Ordinal)
        {
            "같은 장비 IP가 이미 등록되어 있습니다.",
            "저장된 계정을 읽을 수 없습니다. 로그인 ID와 PW를 다시 입력해 주세요.",
            "로그인 비밀번호를 입력해 주세요."
        };

    public static string ToErrorCode(
        Exception exception,
        DeviceManagementOperation operation)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception.Message.Equals(
                "VIEWER_DEVICE_STORE_CORRUPT",
                StringComparison.Ordinal))
        {
            return "VIEWER_DEVICE_STORE_CORRUPT";
        }

        if (exception.Message.Equals(
                "VIEWER_DEVICE_STORE_UNAVAILABLE",
                StringComparison.Ordinal))
        {
            return "VIEWER_DEVICE_STORE_UNAVAILABLE";
        }

        if (exception is KeyNotFoundException
            || exception.Message.Equals(
                "VIEWER_DEVICE_NOT_FOUND",
                StringComparison.Ordinal))
        {
            return "VIEWER_DEVICE_NOT_FOUND";
        }

        if (exception is InvalidDataException
            && exception.Message.Equals(
                "VIEWER_CREDENTIAL_CORRUPT",
                StringComparison.Ordinal))
        {
            return "VIEWER_CREDENTIAL_CORRUPT";
        }

        if (exception is IOException or UnauthorizedAccessException)
        {
            return operation is DeviceManagementOperation.Save
                or DeviceManagementOperation.Delete
                ? "VIEWER_DEVICE_STORE_WRITE_FAILED"
                : "VIEWER_DEVICE_STORE_UNAVAILABLE";
        }

        return "VIEWER_UNEXPECTED_ERROR";
    }

    public static bool TryGetValidationMessage(
        Exception exception,
        out string message)
    {
        if (exception is InvalidDataException
            && SafeValidationMessages.Contains(exception.Message))
        {
            message = exception.Message;
            return true;
        }

        message = string.Empty;
        return false;
    }
}
