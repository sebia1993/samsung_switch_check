using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

internal sealed class UnavailableAgentClient : IAgentClient
{
    public event EventHandler<AgentEventChangeDto>? EventChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ConnectionStateChanged?.Invoke(this, AgentConnectionState.NeedsConnection);
        return Task.CompletedTask;
    }

    public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
        Task.FromException<AgentSnapshotDto>(new InvalidOperationException("VIEWER_CONNECTION_REQUIRED"));

    public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);

    public Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));

    public Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken) =>
        Task.FromResult(new CommandResultDto(false, "Agent 연결 설정이 필요합니다.", "VIEWER_CONNECTION_REQUIRED"));

    public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
        string deviceId,
        string command,
        CancellationToken cancellationToken) =>
        Task.FromException<ReadOnlyQueryResultDto>(new AgentClientException(
            "VIEWER_CONNECTION_REQUIRED",
            AgentConnectionState.NeedsConnection));

    public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
