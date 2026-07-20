using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public interface IAgentClient : IAsyncDisposable
{
    event EventHandler<SwitchEventDto>? EventReceived;
    event EventHandler<SwitchEventDto>? EventUpdated;
    event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    Task StartAsync(CancellationToken cancellationToken);
    Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SwitchEventDto>> GetEventsAfterAsync(long sequence, CancellationToken cancellationToken);
    Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken);
    Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken);
}

public interface IAgentClientFactory
{
    IAgentClient Create(ViewerSettings settings);
}

public sealed class AgentClientFactory : IAgentClientFactory
{
    public IAgentClient Create(ViewerSettings settings) => settings.DemoMode
        ? new DemoAgentClient()
        : new HttpAgentClient(settings);
}
