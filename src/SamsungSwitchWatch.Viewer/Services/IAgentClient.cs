using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public interface IAgentClient : IAsyncDisposable
{
    event EventHandler<AgentEventChangeDto>? EventChanged;
    event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    Task StartAsync(CancellationToken cancellationToken);
    Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken);
    Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken);
    Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken);
    Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(string deviceId, string command, CancellationToken cancellationToken);
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
