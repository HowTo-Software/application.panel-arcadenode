using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.DTOs;
using ServerPanel.API.Models;
using ServerPanel.API.Security;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Services;

public sealed class DockerService(PanelDbContext db, IDockerClientFactory clients, DockerEnrollment enrollment, ServerOperationGate operations, ILogger<DockerService> logger) : IDockerService
{
    public Task<string> CreateContainerAsync(GameServer server) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.Provisioning, () => CreateContainerAsyncCore(server, null), server.Id, server.NodeId);

    public Task<string> PrepareRestoredContainerAsync(GameServer server, string digest) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.Provisioning, () => CreateContainerAsyncCore(server, digest), server.Id, server.NodeId);

    private async Task<string> CreateContainerAsyncCore(GameServer server, string? restoredDigest)
    {
        var node = await db.Nodes.SingleAsync(n => n.Id == server.NodeId);
        var enrolled = enrollment.RequireNode(node.DockerEndpoint);
        enrollment.RequireProvisioning(enrolled);
        var image = enrollment.RequireImage(server.DockerImage);
        var client = clients.GetClient(node.DockerEndpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var imageInfo = await DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerImage,
            () => client.Images.InspectImageAsync(image.Image, timeout.Token), server.Id, node.Id);
        if (Value(imageInfo.Config?.Labels, "arcadenode.managed") != "2" || imageInfo.Config?.Volumes?.Count > 0)
            throw DiagnosticLog.Mark(new InvalidOperationException("Rebuild managed images without implicit unbounded volumes before provisioning."), DiagnosticPoints.DockerImage);
        if (restoredDigest is not null && Value(imageInfo.Config?.Labels, "arcadenode.restore") != "1")
            throw DiagnosticLog.Mark(new InvalidOperationException("The approved image does not declare support for restored volumes."), DiagnosticPoints.DockerImage);

        var volumeName = $"arcadenode-{server.Id:N}-data";
        var quota = checked((long)server.DiskLimit * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var volume = await DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerVolume,
            () => restoredDigest is not null ? client.Volumes.InspectAsync(volumeName, timeout.Token) : client.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = volumeName, Driver = enrolled.QuotaVolumeDriver,
            DriverOpts = new Dictionary<string, string> { [enrolled.QuotaSizeOption] = quota },
            Labels = new Dictionary<string, string> { ["panel.server.id"] = server.Id.ToString() }
        }, timeout.Token), server.Id, node.Id);
        if (volume.Name != volumeName || volume.Driver != enrolled.QuotaVolumeDriver || Value(volume.Options, enrolled.QuotaSizeOption) != quota ||
            Value(volume.Labels, "panel.server.id") != server.Id.ToString() ||
            (restoredDigest is not null && Value(volume.Labels, "panel.restore.sha256") != restoredDigest))
            throw DiagnosticLog.Mark(new InvalidOperationException("The data volume did not confirm its expected quota configuration."), DiagnosticPoints.DockerVolume);

        var container = ContainerPolicy.Build(server, enrolled, image, volumeName);
        if (restoredDigest is not null)
        {
            container.HostConfig.Mounts[0].VolumeOptions = new VolumeOptions { NoCopy = true };
            // A retry after Docker completed but before the database commit may reuse its stopped result.
            try
            {
                var previous = await client.Containers.InspectContainerAsync(container.Name, timeout.Token);
                if (previous.State is null || previous.State.Running || string.IsNullOrEmpty(previous.ID) ||
                    previous.Config?.Image != image.Image ||
                    (previous.HostConfig?.CpusetCpus ?? "") != (container.HostConfig.CpusetCpus ?? "") ||
                    container.Labels.Any(label => Value(previous.Config?.Labels, label.Key) != label.Value) ||
                    previous.Mounts?.Count != 1 || !previous.Mounts.Any(mount => mount.Type == "volume" &&
                        mount.Name == volumeName && mount.Destination == image.DataPath))
                    throw new InvalidOperationException("Existing migration container does not match the staged target.");
                return previous.ID;
            }
            catch (DockerApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        }
        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(server.EnvironmentVariables ?? "{}") ?? new();
        SecurityLimits.EnvironmentVariables(env);
        env.Remove("ARCADENODE_RESTORED_VOLUME");
        env.Remove("ARCADENODE_PUBLISHED_PORTS");
        if (restoredDigest is not null) env["ARCADENODE_RESTORED_VOLUME"] = "true";
        if (restoredDigest is not null)
        {
            var ports = JsonSerializer.Deserialize<int[]>(server.AdditionalPorts ?? "[]") ?? [];
            env["ARCADENODE_PUBLISHED_PORTS"] = string.Join(',', ports.Prepend(server.Port)
                .Select(port => port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        env["SERVER_MEMORY"] = server.MemoryLimit.ToString(CultureInfo.InvariantCulture);
        // Leave a quarter of the container's memory for non-heap JVM allocations and native code.
        env["ARCADENODE_JAVA_HEAP_MIB"] = (server.MemoryLimit * 3 / 4).ToString(CultureInfo.InvariantCulture);
        env["SERVER_PORT"] = server.Port.ToString(CultureInfo.InvariantCulture);
        container.Env = env.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
        // Image ENTRYPOINT is trusted and fixed. User/cartridge text is never evaluated by a shell.
        var created = await DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerCreate,
            () => client.Containers.CreateContainerAsync(container, timeout.Token), server.Id, node.Id);
        return created.ID;
    }

    private async Task<(GameServer Server, DockerClient Client, ContainerInspectResponse Inspect)> ResolveAsync(Guid id, CancellationToken token)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Node).SingleOrDefaultAsync(s => s.Id == id, token)
            ?? throw new InvalidOperationException("Server not found.");
        if (string.IsNullOrWhiteSpace(server.ContainerId)) throw new InvalidOperationException("Container not provisioned.");
        var client = clients.GetClient(server.Node.DockerEndpoint);
        var inspect = await DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerIdentity,
            () => client.Containers.InspectContainerAsync(server.ContainerId, token), server.Id, server.NodeId);
        if (!MatchesContainerLabels(server.Id, server.NodeId, server.OwnerId, inspect.Config?.Labels))
            throw DiagnosticLog.Mark(new InvalidOperationException("Container ownership labels do not match the server record."), DiagnosticPoints.DockerIdentity);
        return (server, client, inspect);
    }

    internal static bool MatchesContainerLabels(Guid serverId, Guid nodeId, Guid ownerId, IDictionary<string, string>? labels) =>
        Value(labels, "panel.server.id") == serverId.ToString() && Value(labels, "panel.node.id") == nodeId.ToString() &&
        Value(labels, "panel.owner.id") == ownerId.ToString();

    internal static ServerStatus? RuntimeStatus(string? state) => state switch
    {
        "running" => ServerStatus.Running,
        "created" or "exited" => ServerStatus.Stopped,
        "paused" => ServerStatus.Paused,
        "restarting" => ServerStatus.Starting,
        "dead" or "removing" => ServerStatus.Error,
        _ => null
    };

    private static ContainerRuntime? ReadRuntime(ContainerState? state)
    {
        if (state is null || RuntimeStatus(state.Status) is not { } status) return null;
        if (status == ServerStatus.Running && (!state.Running || state.Paused || state.Restarting)) return null;
        DateTime? startedAt = DateTimeOffset.TryParse(state.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var started) && started > DateTimeOffset.UnixEpoch ? started.UtcDateTime : null;
        var exited = state.Status is "exited" or "dead";
        return new ContainerRuntime(status, startedAt, exited ? state.ExitCode : null, exited ? state.OOMKilled : null);
    }

    public async Task<ContainerRuntime?> ObserveRuntimeAsync(Guid serverId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var (server, _, inspect) = await ResolveAsync(serverId, timeout.Token);
            return server.IsSuspended ? new ContainerRuntime(ServerStatus.Suspended, null) : ReadRuntime(inspect.State);
        }
        catch (DockerApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return new ContainerRuntime(ServerStatus.Error, null);
        }
    }

    public Task<bool> StartContainerAsync(Guid serverId) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerPower, () => StartContainerAsyncCore(serverId), serverId);

    public async Task<bool> IsContainerStoppedAsync(Guid serverId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var (_, _, inspect) = await ResolveAsync(serverId, timeout.Token);
        return IsStopped(inspect.State);
    }

    private async Task<bool> StartContainerAsyncCore(Guid serverId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var operation = await operations.AcquireAsync(serverId, timeout.Token);
        var (server, client, _) = await ResolveAsync(serverId, timeout.Token);
        return await StartResolvedAsync(server, client, timeout);
    }

    private async Task<bool> StartResolvedAsync(GameServer server, DockerClient client, CancellationTokenSource timeout)
    {
        if (server.IsSuspended) throw new ValidationException("Server is suspended.");
        try
        {
            return await client.Containers.StartContainerAsync(server.ContainerId, new ContainerStartParameters(), timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Docker may have started the container before the request expired.
            using var check = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var observed = await client.Containers.InspectContainerAsync(server.ContainerId, check.Token);
                if (MatchesContainerLabels(server.Id, server.NodeId, server.OwnerId, observed.Config?.Labels) &&
                    ReadRuntime(observed.State)?.Status == ServerStatus.Running)
                {
                    logger.LogInformation("Container is running after the Docker start request timed out. ServerId={ServerId} NodeId={NodeId}",
                        server.Id, server.NodeId);
                    return true;
                }
            }
            catch (Exception error) when (error is IOException or HttpRequestException or DockerApiException or OperationCanceledException)
            {
                DiagnosticLog.Write(logger, DiagnosticPoints.DockerIdentity, error, server.Id, server.NodeId, LogLevel.Warning);
            }
            throw;
        }
    }

    public Task<bool> StopContainerAsync(Guid serverId) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerPower, () => StopContainerAsyncCore(serverId), serverId);

    public Task<bool> KillContainerAsync(Guid serverId) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerPower, () => KillContainerAsyncCore(serverId), serverId);

    private async Task<bool> StopContainerAsyncCore(Guid serverId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var operation = await operations.AcquireAsync(serverId, timeout.Token);
        var (server, client, inspect) = await ResolveAsync(serverId, timeout.Token);
        if (!server.IsSuspended && ShutdownProfile(enrollment.FindImage(server.DockerImage)) is { } shutdown)
            return await StopGameAsync(server, client, inspect.State, shutdown.Command, shutdown.WaitSeconds);

        return await client.Containers.StopContainerAsync(server.ContainerId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, timeout.Token);
    }

    private async Task<bool> KillContainerAsyncCore(Guid serverId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var operation = await operations.AcquireAsync(serverId, timeout.Token);
        var (server, client, _) = await ResolveAsync(serverId, timeout.Token);
        if (server.IsSuspended) throw new ValidationException("Server is suspended.");
        try
        {
            await client.Containers.KillContainerAsync(server.ContainerId, new ContainerKillParameters { Signal = "SIGKILL" }, timeout.Token);
            return true;
        }
        catch (DockerApiException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            // Retrying an already-stopped process is harmless. Other Docker conflicts still fail.
            var state = (await client.Containers.InspectContainerAsync(server.ContainerId, timeout.Token)).State;
            if (state is { Running: false, Status: "created" or "exited" }) return true;
            throw;
        }
    }

    public Task<bool> RestartContainerAsync(Guid serverId) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerPower, () => RestartContainerAsyncCore(serverId), serverId);

    private async Task<bool> RestartContainerAsyncCore(Guid serverId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var operation = await operations.AcquireAsync(serverId, timeout.Token);
        var (server, client, inspect) = await ResolveAsync(serverId, timeout.Token);
        if (server.IsSuspended) throw new ValidationException("Server is suspended.");
        if (ShutdownProfile(enrollment.FindImage(server.DockerImage)) is { } shutdown)
        {
            if (!await StopGameAsync(server, client, inspect.State, shutdown.Command, shutdown.WaitSeconds))
                return false;

            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var (current, currentClient, stopped) = await ResolveAsync(serverId, startTimeout.Token);
            if (current.NodeId != server.NodeId || current.ContainerId != server.ContainerId || !IsStopped(stopped.State)) return false;
            return await StartResolvedAsync(current, currentClient, startTimeout);
        }

        await client.Containers.RestartContainerAsync(server.ContainerId, new ContainerRestartParameters { WaitBeforeKillSeconds = 10 }, timeout.Token);
        return true;
    }

    private static (string? Command, int WaitSeconds)? ShutdownProfile(ApprovedGameImage? image) =>
        (image?.Alias ?? image?.Image, image?.DataPath) switch
        {
            ("arcadenode/minecraft:secure", "/home/minecraft") => ("stop", 45),
            ("arcadenode/minecraft-bedrock:secure", "/home/bedrock") => ("stop", 90),
            ("arcadenode/project-zomboid:secure", "/home/pzserver") => ("quit", 90),
            ("arcadenode/arma-reforger:secure", "/home/reforger") => (null, 90),
            _ => null
        };

    private async Task<bool> StopGameAsync(GameServer server, DockerClient client, ContainerState? state, string? command, int waitSeconds)
    {
        if (IsStopped(state)) return true;
        if (ReadRuntime(state)?.Status != ServerStatus.Running) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds));
        using var send = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        send.CancelAfter(TimeSpan.FromSeconds(5));
        var rejected = false;
        try
        {
            if (command is null)
                await client.Containers.KillContainerAsync(server.ContainerId, new ContainerKillParameters { Signal = "SIGINT" }, send.Token);
            else
                await WriteConsoleLineAsync(server, command, send.Token);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or DockerApiException or OperationCanceledException)
        {
            rejected = error is HttpRequestException { StatusCode: not null } or DockerApiException;
            // A lost response may still have delivered the request. Observe the process without a second write.
            DiagnosticLog.Write(logger, DiagnosticPoints.DockerPower, error, server.Id, server.NodeId, LogLevel.Warning);
        }

        using var poll = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                var observed = await client.Containers.InspectContainerAsync(server.ContainerId, timeout.Token);
                if (!MatchesContainerLabels(server.Id, server.NodeId, server.OwnerId, observed.Config?.Labels))
                    throw DiagnosticLog.Mark(new InvalidOperationException("Container ownership labels changed during shutdown."), DiagnosticPoints.DockerIdentity);
                if (observed.State is { Running: false, Status: "exited" } stopped)
                    return !stopped.OOMKilled && (stopped.ExitCode == 0 || (command is null && stopped.ExitCode == 130));
                if (rejected || observed.State?.Status is "created" or "dead" or "removing") return false;
            }
            while (await poll.WaitForNextTickAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }

        logger.LogWarning("Game shutdown was not confirmed. ServerId={ServerId} NodeId={NodeId}", server.Id, server.NodeId);
        return false;
    }

    private static bool IsStopped(ContainerState? state) => state is { Running: false, Status: "created" or "exited" };

    public Task<bool> RemoveContainerAsync(Guid serverId) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerPower, () => RemoveContainerAsyncCore(serverId), serverId);

    private async Task<bool> RemoveContainerAsyncCore(Guid serverId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var operation = await operations.AcquireAsync(serverId, timeout.Token);
        var (server, client, _) = await ResolveAsync(serverId, timeout.Token);
        if (server.Status != ServerStatus.Stopped && server.Status != ServerStatus.Error)
            throw new ValidationException("Stop the server before removing it.");
        await client.Containers.RemoveContainerAsync(server.ContainerId, new ContainerRemoveParameters { Force = false, RemoveVolumes = false }, timeout.Token);
        return true;
    }

    public Task<ServerStatsDto?> GetContainerStatsAsync(Guid serverId, CancellationToken token = default) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerStats, () => GetContainerStatsAsyncCore(serverId, token), serverId);

    private async Task<ServerStatsDto?> GetContainerStatsAsyncCore(Guid serverId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var (server, client, inspect) = await ResolveAsync(serverId, timeout.Token);
        var runtime = ReadRuntime(inspect.State);
        if (runtime is null) return null;
        if (server.IsSuspended || runtime.Status != ServerStatus.Running)
            return new ServerStatsDto(serverId, server.IsSuspended ? ServerStatus.Suspended : runtime.Status, 0, 0, null, 0, 0, 0, DateTime.UtcNow)
            {
                StartedAt = runtime.StartedAt, ExitCode = runtime.ExitCode, OomKilled = runtime.OomKilled
            };
        ContainerStatsResponse? stats = null;
        await client.Containers.GetContainerStatsAsync(server.ContainerId, new ContainerStatsParameters { Stream = false },
            new InlineProgress<ContainerStatsResponse>(value => stats = value), timeout.Token);
        if (stats is null)
        {
            DiagnosticLog.Write(logger, DiagnosticPoints.DockerStats, serverId: serverId, nodeId: server.NodeId);
            return null;
        }
        double cpu = 0;
        if (stats.CPUStats?.CPUUsage is not null && stats.PreCPUStats?.CPUUsage is not null)
        {
            var delta = (double)stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage;
            var total = (double)stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;
            var cores = stats.CPUStats.OnlineCPUs > 0
                ? stats.CPUStats.OnlineCPUs
                : (uint)(stats.CPUStats.CPUUsage.PercpuUsage?.Count ?? 0);
            if (delta > 0 && total > 0 && cores > 0) cpu = delta / total * cores * 100;
        }
        long memory = (long)(stats.MemoryStats?.Usage ?? 0);
        if (stats.MemoryStats?.Stats is { } memoryStats)
        {
            foreach (var metric in new[] { "total_inactive_file", "inactive_file", "cache" })
            {
                if (!memoryStats.TryGetValue(metric, out var cache)) continue;
                if (cache < (ulong)memory) memory -= (long)cache;
                break;
            }
        }
        var readWrite = stats.BlkioStats?.IoServiceBytesRecursive?.Where(io => io.Op is "Read" or "Write" or "read" or "write").Sum(io => (long)io.Value) ?? 0;
        return new ServerStatsDto(serverId, runtime.Status, memory, Math.Round(cpu, 2), null, readWrite,
            stats.Networks?.Values.Sum(net => (long)net.RxBytes) ?? 0, stats.Networks?.Values.Sum(net => (long)net.TxBytes) ?? 0, DateTime.UtcNow)
        {
            StartedAt = runtime.StartedAt, MetricsAvailable = true
        };
    }

    public Task<string> GetContainerLogsAsync(Guid serverId, int tail = 100) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerLogs, () => GetContainerLogsAsyncCore(serverId, tail), serverId);

    public async Task FollowContainerLogsAsync(Guid serverId, int tail,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> receive, CancellationToken token)
    {
        var (server, client, _) = await ResolveAsync(serverId, token);
        using var logs = await client.Containers.GetContainerLogsAsync(server.ContainerId, true,
            new ContainerLogsParameters
            {
                ShowStdout = true, ShowStderr = true, Follow = true, Timestamps = true,
                Tail = Math.Clamp(tail, 1, SecurityLimits.MaxLogLines).ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, token);
        var buffer = new byte[8192];
        var remaining = SecurityLimits.MaxLogBytes;
        while (remaining > 0)
        {
            var chunk = await logs.ReadOutputAsync(buffer, 0, Math.Min(buffer.Length, remaining), token);
            if (chunk.Count == 0) break;
            await receive(buffer.AsMemory(0, chunk.Count), token);
            remaining -= chunk.Count;
        }
    }

    private async Task<string> GetContainerLogsAsyncCore(Guid serverId, int tail = 100)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (server, client, _) = await ResolveAsync(serverId, timeout.Token);
        using var logs = await client.Containers.GetContainerLogsAsync(server.ContainerId, true,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = Math.Clamp(tail, 1, SecurityLimits.MaxLogLines).ToString(), Timestamps = true }, timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (output.Length < SecurityLimits.MaxLogBytes)
        {
            var result = await logs.ReadOutputAsync(buffer, 0, Math.Min(buffer.Length, SecurityLimits.MaxLogBytes - (int)output.Length), timeout.Token);
            if (result.Count == 0) break;
            output.Write(buffer, 0, result.Count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public Task<bool> SendCommandAsync(Guid serverId, string command) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.DockerConsole, () => SendCommandAsyncCore(serverId, command), serverId);

    private async Task<bool> SendCommandAsyncCore(Guid serverId, string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 512 || command.Any(char.IsControl))
            throw new ValidationException("Commands must be one line of at most 512 characters.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var operation = await operations.AcquireAsync(serverId, timeout.Token);
        var (server, _, inspect) = await ResolveAsync(serverId, timeout.Token);
        if (server.IsSuspended || inspect.State is not { Status: "running", Running: true })
            throw new ValidationException("Server is unavailable for commands.");
        await WriteConsoleLineAsync(server, command, timeout.Token);
        return true;
    }

    private Task WriteConsoleLineAsync(GameServer server, string command, CancellationToken token) =>
        clients.WriteConsoleInputAsync(server.Node.DockerEndpoint, server.ContainerId!, Encoding.UTF8.GetBytes(command + "\n"), token);

    private static string? Value(IDictionary<string, string>? values, string key) => values is not null && values.TryGetValue(key, out var value) ? value : null;

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
