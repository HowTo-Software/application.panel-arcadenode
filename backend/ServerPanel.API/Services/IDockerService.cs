using ServerPanel.API.DTOs;
using ServerPanel.API.Models;

namespace ServerPanel.API.Services;

public interface IDockerService
{
    Task<string> CreateContainerAsync(GameServer server);
    Task<string> PrepareRestoredContainerAsync(GameServer server, string digest);
    Task<bool> StartContainerAsync(Guid serverId);
    Task<bool> StopContainerAsync(Guid serverId);
    Task<bool> KillContainerAsync(Guid serverId);
    Task<bool> RestartContainerAsync(Guid serverId);
    Task<bool> RemoveContainerAsync(Guid serverId);
    Task<bool> IsContainerStoppedAsync(Guid serverId, CancellationToken token);
    Task<ContainerRuntime?> ObserveRuntimeAsync(Guid serverId, CancellationToken token);
    Task<ServerStatsDto?> GetContainerStatsAsync(Guid serverId, CancellationToken token = default);
    Task<string> GetContainerLogsAsync(Guid serverId, int tail = 100);
    Task FollowContainerLogsAsync(Guid serverId, int tail,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> receive, CancellationToken token);
    Task<bool> SendCommandAsync(Guid serverId, string command);
}

public sealed record ContainerRuntime(ServerStatus Status, DateTime? StartedAt, long? ExitCode = null, bool? OomKilled = null);
