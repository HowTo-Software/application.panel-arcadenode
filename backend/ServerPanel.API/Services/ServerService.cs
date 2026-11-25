using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.DTOs;
using ServerPanel.API.Models;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using ServerPanel.API.Security;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Services;

public class ServerService : IServerService
{
    private readonly PanelDbContext _context;
    private readonly IDockerService _dockerService;
    private readonly ICartridgeService _cartridgeService;
    private readonly ILogger<ServerService> _logger;
    private readonly DockerEnrollment _enrollment;
    private readonly EggRuntimeProfiles _eggRuntimes;

    public ServerService(
        PanelDbContext context, 
        IDockerService dockerService,
        ICartridgeService cartridgeService,
        ILogger<ServerService> logger, DockerEnrollment enrollment, EggRuntimeProfiles eggRuntimes)
    {
        _context = context;
        _dockerService = dockerService;
        _cartridgeService = cartridgeService;
        _logger = logger;
        _enrollment = enrollment;
        _eggRuntimes = eggRuntimes;
    }

    private static string GenerateServerIdentifier() => Guid.NewGuid().ToString("N")[..8];

    public async Task<PagedResult<ServerDto>> GetAllServersAsync(int page = 1, int pageSize = 50)
    {
        (page, pageSize) = ServerPanel.API.Common.Pagination.Resolve(page, pageSize);
        var query = _context.Servers
            .Include(s => s.Owner)
            .Include(s => s.Node)
            .OrderByDescending(s => s.CreatedAt);

        var total = await query.CountAsync();
        var skip = Common.Pagination.Skip(page, pageSize);
        var servers = await query.Skip(skip).Take(pageSize).ToListAsync();

        return new PagedResult<ServerDto>(servers.Select(MapToDto).ToList(), page, pageSize, total);
    }

    public async Task<PagedResult<ServerDto>> GetUserServersAsync(Guid userId, int page = 1, int pageSize = 50)
    {
        (page, pageSize) = ServerPanel.API.Common.Pagination.Resolve(page, pageSize);
        var query = _context.Servers
            .Include(s => s.Owner)
            .Include(s => s.Node)
            .Where(s => s.OwnerId == userId || _context.ServerMembers.Any(member => member.ServerId == s.Id && member.UserId == userId))
            .OrderByDescending(s => s.CreatedAt);

        var total = await query.CountAsync();
        var skip = Common.Pagination.Skip(page, pageSize);
        var servers = await query.Skip(skip).Take(pageSize).ToListAsync();

        return new PagedResult<ServerDto>(servers.Select(server => server.OwnerId == userId ? MapToDto(server) :
            MapToDto(server) with { StartupCommand = null }).ToList(), page, pageSize, total);
    }

    public async Task<ServerDto?> GetServerByIdAsync(Guid id)
    {
        var server = await _context.Servers.AsNoTracking()
            .Include(s => s.Owner)
            .Include(s => s.Node)
            .FirstOrDefaultAsync(s => s.Id == id);

        return server == null ? null : MapToDto(server);
    }

    public async Task<ServerDto?> GetServerByIdentifierAsync(string identifier)
    {
        var server = await _context.Servers
            .Include(s => s.Owner)
            .Include(s => s.Node)
            .FirstOrDefaultAsync(s => s.Identifier == identifier);

        return server == null ? null : MapToDto(server);
    }

    public Task<ServerDto?> CreateServerAsync(CreateServerRequest request, Guid ownerId) =>
        CreateServerCoreAsync(request, ownerId, null);

    private async Task<ServerDto?> CreateServerCoreAsync(CreateServerRequest request, Guid ownerId, Guid? approvedEggId, int[]? secondaryPorts = null)
    {
        SecurityLimits.Resources(request.MemoryLimit, request.CpuLimit, request.DiskLimit, request.Port);
        var secondary = secondaryPorts ?? [];
        if (!SecurityLimits.ValidSecondaryPorts(request.Port, secondary))
            throw new ValidationException("Invalid secondary ports.");
        SecurityLimits.Text(request.Name, request.Description);
        SecurityLimits.EnvironmentVariables(request.EnvironmentVariables);
        if (!string.IsNullOrWhiteSpace(request.StartupCommand))
            throw new ValidationException("Custom executable startup commands are not supported. Use a managed image entrypoint.");
        if (request.EggId is { } eggId && !await _context.Eggs.AnyAsync(e => e.Id == eggId && e.PterodactylSha256 == null))
            throw new ValidationException("The selected egg is unavailable for provisioning.");
        var node = await _context.Nodes.SingleOrDefaultAsync(n => n.Id == request.NodeId);
        if (node is null || node.IsMaintenanceMode || !await _context.Users.AnyAsync(u => u.Id == ownerId && u.IsActive))
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.ServerAdmission, nodeId: request.NodeId);
            return null;
        }
        _enrollment.RequireImage(request.DockerImage);
        var enrolled = _enrollment.RequireNode(node.DockerEndpoint);
        _enrollment.RequireProvisioning(enrolled);
        var cpuSet = SecurityLimits.CpuSet(request.CpuCores, request.CpuLimit, enrolled);
        if (node.TotalMemory <= 0 || node.TotalDisk <= 0)
            throw DiagnosticLog.Mark(new ValidationException("Set finite node memory and disk capacities before provisioning."), DiagnosticPoints.ServerAdmission);
        var reserved = await NodeResources.ReservedAsync(_context.Servers.Where(s => s.NodeId == node.Id));
        if (!reserved.Reserve(request.MemoryLimit, request.CpuLimit, request.DiskLimit).Fits(node, enrolled))
            throw DiagnosticLog.Mark(new ValidationException("Node capacity exceeded."), DiagnosticPoints.ServerAdmission);
        var ports = secondary.Prepend(request.Port).ToArray();
        if (await _context.Servers.AnyAsync(s => s.NodeId == node.Id && ports.Contains(s.Port)) ||
            await _context.Allocations.AnyAsync(a => a.NodeId == node.Id && ports.Contains(a.Port) && a.ServerId != null))
            throw DiagnosticLog.Mark(new ValidationException("Port is already reserved on this node."), DiagnosticPoints.ServerAdmission);
        // All reservations update this concurrency token in the same SaveChanges transaction.
        node.ReservationVersion = Guid.NewGuid();
        var server = new GameServer
        {
            Identifier = GenerateServerIdentifier(), Name = request.Name.Trim(), Description = request.Description ?? "",
            DockerImage = request.DockerImage, StartupCommand = null,
            MemoryLimit = request.MemoryLimit, CpuLimit = request.CpuLimit, CpuSet = cpuSet, DiskLimit = request.DiskLimit,
            Port = request.Port, AdditionalPorts = secondary.Length == 0 ? null : JsonSerializer.Serialize(secondary),
            EnvironmentVariables = JsonSerializer.Serialize(request.EnvironmentVariables ?? new()),
            OwnerId = ownerId, NodeId = node.Id, EggId = approvedEggId ?? request.EggId, Status = ServerStatus.Installing
        };
        if (secondary.Length > 0)
        {
            var pool = await _context.Allocations.Where(a => a.NodeId == node.Id && a.IpAddress == enrolled.GameBindAddress &&
                secondary.Contains(a.Port) && a.ServerId == null).ToDictionaryAsync(a => a.Port);
            foreach (var port in secondary)
            {
                var allocation = pool.GetValueOrDefault(port) ?? new Allocation
                {
                    NodeId = node.Id, IpAddress = enrolled.GameBindAddress, Port = port
                };
                allocation.IsAssigned = true;
                server.Allocations.Add(allocation);
            }
        }
        _context.Servers.Add(server);
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        try
        {
            server.ContainerId = await _dockerService.CreateContainerAsync(server);
            server.Status = ServerStatus.Stopped;
            await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(_logger, DiagnosticLog.Find(ex) ?? DiagnosticPoints.Provisioning, ex, server.Id, server.NodeId);
            server.Status = ServerStatus.Error;
            await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
            throw new InvalidOperationException("Provisioning failed; inspect node configuration.", ex);
        }
        return await GetServerByIdAsync(server.Id);
    }

    public async Task<ServerDto?> CreateServerFromEggAsync(Guid eggId, CreateEggServerRequest request, Guid ownerId)
    {
        var egg = await _context.Eggs.Where(e => e.Id == eggId).Select(e => new { e.PterodactylSha256, e.Variables }).SingleOrDefaultAsync();
        if (egg?.PterodactylSha256 is not { } digest) return null;
        var runtime = _eggRuntimes.Resolve(digest, egg.Variables)
            ?? throw new ValidationException("This imported egg has no approved runtime profile.");
        return await ProvisionCartridgeAsync(runtime.Cartridge, new CreateCartridgeServerRequest(request.Name, request.Description,
            request.NodeId, runtime.Cartridge.Id, request.MemoryLimit, request.CpuLimit, request.DiskLimit, request.Port,
            runtime.ResolveValues(request.Variables), request.CpuCores), ownerId, eggId);
    }

    public async Task<ServerDto?> CreateServerFromCartridgeAsync(CreateCartridgeServerRequest request, Guid ownerId)
    {
        var cartridge = _cartridgeService.GetCartridgeById(request.CartridgeId);
        if (cartridge is null)
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.CartridgeLoad, nodeId: request.NodeId);
            return null;
        }
        return await ProvisionCartridgeAsync(cartridge, request, ownerId, null);
    }

    private async Task<ServerDto?> ProvisionCartridgeAsync(Cartridge cartridge, CreateCartridgeServerRequest request, Guid ownerId, Guid? eggId)
    {
        var values = request.Variables ?? new Dictionary<string, object>();
        var (valid, errors) = _cartridgeService.ValidateVariables(cartridge, values);
        if (!valid) throw DiagnosticLog.Mark(new ValidationException("Invalid cartridge variables: " + string.Join(", ", errors)), DiagnosticPoints.CartridgeVariables);
        var primaryDefault = cartridge.Ports.Values.FirstOrDefault()?.Default ?? 25565;
        var port = request.Port ?? await GetNextAvailablePortAsync(request.NodeId, primaryDefault);
        var namedPorts = new Dictionary<string, int>();
        var reservedPorts = new HashSet<int> { port };
        foreach (var (name, socket) in cartridge.Ports.Skip(1).Where(entry => entry.Value.Required))
        {
            var selected = socket.Default == primaryDefault ? port :
                await GetNextAvailablePortAsync(request.NodeId, socket.Default, reservedPorts);
            namedPorts.Add(name, selected);
            reservedPorts.Add(selected);
        }
        var env = _cartridgeService.BuildEnvironmentVariables(cartridge, values, port, namedPorts);
        // Share admission, quota and reservation checks with every other creation flow.
        return await CreateServerCoreAsync(new CreateServerRequest(request.Name, request.Description, request.NodeId, null,
            cartridge.Docker.Image, null, request.MemoryLimit, request.CpuLimit, request.DiskLimit, port, env, request.CpuCores),
            ownerId, eggId, reservedPorts.Where(extra => extra != port).Order().ToArray());
    }

    private async Task<int> GetNextAvailablePortAsync(Guid nodeId, int startPort, IReadOnlySet<int>? selected = null)
    {
        var first = Math.Max(1024, startPort);
        var last = Math.Min(65535, startPort + 255);
        var reserved = await _context.Servers.Where(s => s.NodeId == nodeId && s.Port >= first && s.Port <= last).Select(s => s.Port)
            .Union(_context.Allocations.Where(a => a.NodeId == nodeId && a.ServerId != null && a.Port >= first && a.Port <= last)
                .Select(a => a.Port)).ToListAsync();
        var inUse = reserved.ToHashSet();
        for (var port = first; port <= last; port++)
            if (!inUse.Contains(port) && selected?.Contains(port) != true) return port;
        throw DiagnosticLog.Mark(new ValidationException("No port available in the automatic allocation range."), DiagnosticPoints.ServerAdmission);
    }

    public async Task<ServerDto?> UpdateServerAsync(Guid id, UpdateServerRequest request)
    {
        var server = await _context.Servers.FindAsync(id);
        if (server == null) return null;

        SecurityLimits.Text(request.Name, request.Description);
        if (request.StartupCommand is not null || request.MemoryLimit.HasValue || request.CpuLimit.HasValue ||
            request.DiskLimit.HasValue || request.EnvironmentVariables is not null)
            throw new ValidationException("This endpoint only changes the server name and description. Resource changes require reprovisioning.");

        if (!string.IsNullOrEmpty(request.Name)) server.Name = request.Name;
        if (request.Description != null) server.Description = request.Description;

        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return await GetServerByIdAsync(id);
    }

    public async Task<bool> DeleteServerAsync(Guid id)
    {
        var server = await _context.Servers.FindAsync(id);
        if (server == null) return false;
        if (server.MigrationPending) throw new ValidationException("This server is reserved for a staged migration.");

        // Stop and remove container
        if (!string.IsNullOrEmpty(server.ContainerId))
        {
            await _dockerService.RemoveContainerAsync(server.Id);
        }

        var node = await _context.Nodes.FindAsync(server.NodeId);
        if (node is not null) node.ReservationVersion = Guid.NewGuid();
        foreach (var allocation in await _context.Allocations.Where(a => a.ServerId == server.Id).ToListAsync())
        {
            allocation.ServerId = null;
            allocation.IsAssigned = false;
        }
        _context.Servers.Remove(server);
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    public async Task<bool> UpdateServerStatusAsync(Guid id, ServerStatus status)
    {
        var now = DateTime.UtcNow;
        return await _context.Servers.Where(server => server.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(server => server.Status, server => server.IsSuspended ? ServerStatus.Suspended : status)
            .SetProperty(server => server.LastStartedAt, server => status == ServerStatus.Running && !server.IsSuspended ? now : server.LastStartedAt)) > 0;
    }

    public async Task<bool> SuspendServerAsync(Guid id, bool suspended)
    {
        var server = await _context.Servers.FindAsync(id);
        if (server == null) return false;
        if (server.MigrationPending) throw new ValidationException("Finish the staged migration before changing its suspension.");
        if (!suspended && server.RestoredVolumeSha256 is not null && server.MigrationReleasedAt is null)
            throw new ValidationException("Release this restored server through the migration workflow first.");

        server.IsSuspended = suspended;
        if (suspended)
        {
            server.Status = ServerStatus.Suspended;
            // Admission closes before waiting for an in-flight Docker write.
            await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
            if (!string.IsNullOrEmpty(server.ContainerId))
            {
                await _dockerService.StopContainerAsync(server.Id);
            }
        }

        if (!suspended) server.Status = ServerStatus.Stopped;
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    private static ServerDto MapToDto(GameServer server) => new(
        server.Id,
        server.Name,
        server.Description,
        server.Identifier,
        server.ContainerId,
        server.DockerImage,
        server.StartupCommand,
        server.MemoryLimit,
        server.CpuLimit,
        server.DiskLimit,
        server.Port,
        server.Node?.Fqdn ?? server.IpAddress, // Use node's public IP/FQDN instead of 0.0.0.0
        server.Status,
        server.IsSuspended,
        server.CreatedAt,
        server.LastStartedAt,
        server.OwnerId,
        server.Owner?.Username ?? "Unknown",
        server.NodeId,
        server.Node?.Name ?? "Unknown"
    )
    {
        AdditionalPorts = server.AdditionalPorts is null ? [] : JsonSerializer.Deserialize<int[]>(server.AdditionalPorts) ?? []
    };
}
