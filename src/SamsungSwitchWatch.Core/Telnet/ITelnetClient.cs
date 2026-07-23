using SamsungSwitchWatch.Core.Profiles;

namespace SamsungSwitchWatch.Core.Telnet;

public interface ITelnetClient
{
    Task<TelnetSessionResult> ExecuteRegisteredAsync(
        TelnetEndpoint endpoint,
        TelnetCredentials credentials,
        DeviceCommandProfile profile,
        IReadOnlyCollection<string> commandIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes Viewer-supplied, policy-validated show commands in a fresh Telnet
/// session. Implementations must not retain endpoint, credential, command, or
/// output values after the call completes.
/// </summary>
public interface IAdHocTelnetClient
{
    Task<TelnetInteractiveResult> ExecuteAsync(
        TelnetEndpoint endpoint,
        TelnetCredentials credentials,
        TelnetPromptProfile promptProfile,
        IReadOnlyList<string> commands,
        CancellationToken cancellationToken = default);
}
