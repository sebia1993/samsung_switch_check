using System.IO;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class DeviceManagementFailureMapperTests
{
    [Theory]
    [InlineData("Load", "VIEWER_DEVICE_STORE_UNAVAILABLE")]
    [InlineData("Close", "VIEWER_DEVICE_STORE_UNAVAILABLE")]
    [InlineData("Save", "VIEWER_DEVICE_STORE_WRITE_FAILED")]
    [InlineData("Delete", "VIEWER_DEVICE_STORE_WRITE_FAILED")]
    public void StorageFailure_MapsByOperation(
        string operationName,
        string expected)
    {
        var operation = Enum.Parse<DeviceManagementOperation>(operationName);
        var code = DeviceManagementFailureMapper.ToErrorCode(
            new IOException("sensitive path must not escape"),
            operation);

        Assert.Equal(expected, code);
    }

    [Fact]
    public void KnownDeviceFailures_MapToStableCodes()
    {
        Assert.Equal(
            "VIEWER_DEVICE_NOT_FOUND",
            DeviceManagementFailureMapper.ToErrorCode(
                new KeyNotFoundException("VIEWER_DEVICE_NOT_FOUND"),
                DeviceManagementOperation.Delete));
        Assert.Equal(
            "VIEWER_CREDENTIAL_CORRUPT",
            DeviceManagementFailureMapper.ToErrorCode(
                new InvalidDataException("VIEWER_CREDENTIAL_CORRUPT"),
                DeviceManagementOperation.Load));
        Assert.Equal(
            "VIEWER_DEVICE_STORE_CORRUPT",
            DeviceManagementFailureMapper.ToErrorCode(
                new Exception("VIEWER_DEVICE_STORE_CORRUPT"),
                DeviceManagementOperation.Load));
    }

    [Fact]
    public void UnexpectedFailure_DoesNotExposeExceptionText()
    {
        var code = DeviceManagementFailureMapper.ToErrorCode(
            new InvalidOperationException("host=192.0.2.10 password=secret"),
            DeviceManagementOperation.Save);

        Assert.Equal("VIEWER_UNEXPECTED_ERROR", code);
        Assert.DoesNotContain("192.0.2.10", code, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", code, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendlyValidationMessage_IsPreservedWithoutTreatingStableCodeAsText()
    {
        Assert.True(
            DeviceManagementFailureMapper.TryGetValidationMessage(
                new InvalidDataException("같은 장비 IP가 이미 등록되어 있습니다."),
                out var message));
        Assert.Equal("같은 장비 IP가 이미 등록되어 있습니다.", message);

        Assert.False(
            DeviceManagementFailureMapper.TryGetValidationMessage(
                new InvalidDataException("VIEWER_CREDENTIAL_CORRUPT"),
                out _));
    }

    [Fact]
    public void ArbitraryInvalidDataMessage_IsNeverExposedAsValidationText()
    {
        const string sensitive =
            "host=192.0.2.10 user=operator password=secret";

        Assert.False(
            DeviceManagementFailureMapper.TryGetValidationMessage(
                new InvalidDataException(sensitive),
                out var message));
        Assert.Empty(message);
        Assert.Equal(
            "VIEWER_UNEXPECTED_ERROR",
            DeviceManagementFailureMapper.ToErrorCode(
                new InvalidDataException(sensitive),
                DeviceManagementOperation.Save));
    }
}
