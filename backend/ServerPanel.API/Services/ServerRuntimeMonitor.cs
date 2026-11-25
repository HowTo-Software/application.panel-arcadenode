using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;

namespace ServerPanel.API.Services;

public sealed class ServerRuntimeMonitor(IServiceScopeFactory scopes, ILogger<ServerRuntimeMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ReconcileOnceAsync(scopes, logger, stoppingToken); }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not refresh server runtime states ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    public static async Task ReconcileOnceAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken token)
    {
        List<RuntimeNode> nodes;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
            nodes = await ManagedServers(db).Where(server => server.Node.DockerEndpoint != "")
                .Select(server => new RuntimeNode(server.NodeId, server.Node.DockerEndpoint)).Distinct().ToListAsync(token);
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token };
        await Parallel.ForEachAsync(nodes, options, async (node, cancellationToken) =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(12));
            try { await ReconcileNodeAsync(scopes, node, budget.Token); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Could not refresh runtime state on node {NodeId} ({ErrorType}).", node.Id, error.GetType().Name);
            }
        });
    }

    private static async Task ReconcileNodeAsync(IServiceScopeFactory scopes, RuntimeNode node, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        var servers = await ManagedServers(db).Where(server => server.NodeId == node.Id).OrderBy(server => server.Id)
            .Select(server => new RuntimeTarget(server.Id, server.ContainerId!, server.OwnerId, server.Status, server.LastStartedAt))
            .ToListAsync(token);
        if (servers.Count == 0) return;

        var client = scope.ServiceProvider.GetRequiredService<IDockerClientFactory>().GetClient(node.Endpoint);
        IList<ContainerListResponse> containers;
        using (var listTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            listTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            containers = await client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { [$"panel.node.id={node.Id:D}"] = true }
                }
            }, listTimeout.Token);
        }
        var byId = new Dictionary<string, ContainerListResponse>(StringComparer.Ordinal);
        foreach (var container in containers)
            if (!string.IsNullOrEmpty(container.ID)) byId.TryAdd(container.ID, container);

        var docker = scope.ServiceProvider.GetRequiredService<IDockerService>();
        foreach (var server in servers)
        {
            if (byId.TryGetValue(server.ContainerId, out var summary))
            {
                if (!DockerService.MatchesContainerLabels(server.Id, node.Id, server.OwnerId, summary.Labels) ||
                    DockerService.RuntimeStatus(summary.State) is not { } observed || observed == server.Status) continue;
            }
            else if (server.Status == ServerStatus.Error) continue;

            await using var lockScope = scopes.CreateAsyncScope();
            var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
            await using var lease = await gate.AcquireAsync(server.Id, token);
            // The summary selects candidates. Only a fresh, authenticated inspect can confirm a transition.
            var current = await docker.ObserveRuntimeAsync(server.Id, token);
            if (current is null || current.Status == ServerStatus.Suspended || current.Status == server.Status) continue;

            var runningSince = current.Status == ServerStatus.Running ? current.StartedAt ?? server.LastStartedAt : server.LastStartedAt;
            await ManagedServers(db).Where(row => row.Id == server.Id && row.NodeId == node.Id && row.OwnerId == server.OwnerId &&
                row.ContainerId == server.ContainerId && row.Status == server.Status && row.LastStartedAt == server.LastStartedAt)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, current.Status)
                    .SetProperty(row => row.LastStartedAt, runningSince), token);
        }
    }

    private static IQueryable<GameServer> ManagedServers(PanelDbContext db) => db.Servers.AsNoTracking().Where(server =>
        !server.IsSuspended && !server.MigrationPending && server.Status != ServerStatus.Installing &&
        server.ContainerId != null && server.ContainerId != "");

    private sealed record RuntimeNode(Guid Id, string Endpoint);
    private sealed record RuntimeTarget(Guid Id, string ContainerId, Guid OwnerId, ServerStatus Status, DateTime? LastStartedAt);
}
