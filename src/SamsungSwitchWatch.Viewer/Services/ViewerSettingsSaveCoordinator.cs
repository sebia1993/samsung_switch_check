using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

/// <summary>
/// Keeps settings persistence failures separate from Agent connectivity failures.
/// Diagnostic callbacks receive only an allowlisted stage and stable error code.
/// </summary>
internal sealed class ViewerSettingsSaveCoordinator
{
    internal const string ErrorCode = "VIEWER_SETTINGS_WRITE_FAILED";
    internal const string UnexpectedErrorCode = "VIEWER_UNEXPECTED_ERROR";

    private readonly ViewerSettingsStore _store;
    private readonly Action<string, string> _writeDiagnostic;

    public ViewerSettingsSaveCoordinator(
        ViewerSettingsStore store,
        Action<string, string>? writeDiagnostic = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _writeDiagnostic = writeDiagnostic ?? ((_, _) => { });
    }

    public bool TrySave(
        ViewerSettings settings,
        string stage,
        out string errorCode)
    {
        try
        {
            _store.Save(settings);
            errorCode = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            errorCode = Classify(exception);
            WriteDiagnostic(stage, errorCode);
            return false;
        }
    }

    public void SaveOrThrow(ViewerSettings settings, string stage)
    {
        try
        {
            _store.Save(settings);
        }
        catch (Exception exception)
        {
            var errorCode = Classify(exception);
            WriteDiagnostic(stage, errorCode);
            throw new AgentClientException(
                errorCode,
                AgentConnectionState.Stale,
                exception);
        }
    }

    private static string Classify(Exception exception) =>
        exception is IOException or UnauthorizedAccessException
            ? ErrorCode
            : UnexpectedErrorCode;

    private void WriteDiagnostic(string stage, string errorCode)
    {
        try
        {
            _writeDiagnostic(stage, errorCode);
        }
        catch
        {
            // Diagnostic reporting must not turn a recoverable save failure
            // into an application failure.
        }
    }
}
