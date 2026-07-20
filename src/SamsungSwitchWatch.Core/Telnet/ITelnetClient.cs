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
