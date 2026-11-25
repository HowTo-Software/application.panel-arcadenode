using Docker.DotNet;
using System.ComponentModel.DataAnnotations;
using System.Net;
using ServerPanel.API.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServerPanel.API.Data;
using ServerPanel.API.DTOs;
using ServerPanel.API.Models;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Services;

public class NodeService : INodeService
{
    private readonly PanelDbContext _context;
    private readonly ILogger<NodeService> _logger;
    private readonly IDockerClientFactory _clients;
    private readonly DockerEnrollment _enrollment;

    public NodeService(PanelDbContext context, ILogger<NodeService> logger, IDockerClientFactory clients, DockerEnrollment enrollment)
    {
        _context = context;
        _logger = logger;
        _clients = clients;
        _enrollment = enrollment;
    }

    public async Task<PagedResult<NodeDto>> GetAllNodesAsync(int page = 1, int pageSize = 50)
    {
        (page, pageSize) = ServerPanel.API.Common.Pagination.Resolve(page, pageSize);
        var query = _context.Nodes.AsNoTracking().OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id);

        var total = await query.CountAsync();
        var skip = Common.Pagination.Skip(page, pageSize);
        var nodes = await WithReservations(query.Skip(skip).Take(pageSize)).ToListAsync();

        return new PagedResult<NodeDto>(nodes.Select(row => MapToDto(row.Node, row.Reserved, row.ServerCount)).ToList(), page, pageSize, total);
    }

    public async Task<NodeDto?> GetNodeByIdAsync(Guid id)
    {
        var row = await WithReservations(_context.Nodes.AsNoTracking().Where(n => n.Id == id)).SingleOrDefaultAsync();
        return row is null ? null : MapToDto(row.Node, row.Reserved, row.ServerCount);
    }

    public async Task<NodeDto?> CreateNodeAsync(CreateNodeRequest request)
    {
        var enrolled = _enrollment.RequireNode(request.DockerEndpoint);
        var endpoint = DockerEnrollment.CanonicalEndpoint(enrolled.Endpoint);
        ValidateNode(request.Name, request.Fqdn, request.TotalMemory ?? 0, request.TotalDisk ?? 0,
            request.MemoryOverallocate ?? 0, request.DiskOverallocate ?? 0, request.DaemonPort ?? 8080, request.SftpPort ?? 2022);

        if (await _context.Nodes.AnyAsync(n => n.Fqdn == request.Fqdn || n.DockerEndpoint == endpoint))
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.NodeEnrollment);
            return null;
        }

        var node = new Node
        {
            Name = request.Name,
            Description = request.Description ?? "",
            Fqdn = request.Fqdn,
            DaemonPort = request.DaemonPort ?? 8080,
            SftpPort = request.SftpPort ?? 2022,
            UseSsl = request.UseSsl ?? false,
            BehindProxy = request.BehindProxy ?? false,
            TotalMemory = request.TotalMemory ?? 0,
            TotalDisk = request.TotalDisk ?? 0,
            MemoryOverallocate = request.MemoryOverallocate ?? 0,
            DiskOverallocate = request.DiskOverallocate ?? 0,
            DockerEndpoint = endpoint
        };

        _context.Nodes.Add(node);
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        
        _logger.LogInformation("Node created successfully with ID: {NodeId}", node.Id);

        return MapToDto(node, default, 0);
    }

    public async Task<NodeDto?> UpdateNodeAsync(Guid id, UpdateNodeRequest request)
    {
        var row = await WithReservations(_context.Nodes.Where(n => n.Id == id)).SingleOrDefaultAsync();
        if (row is null) return null;
        var node = row.Node;

        var memory = request.TotalMemory ?? node.TotalMemory;
        var disk = request.TotalDisk ?? node.TotalDisk;
        var memoryOver = request.MemoryOverallocate ?? node.MemoryOverallocate;
        var diskOver = request.DiskOverallocate ?? node.DiskOverallocate;
        ValidateNode(request.Name ?? node.Name, request.Fqdn ?? node.Fqdn, memory, disk, memoryOver, diskOver,
            request.DaemonPort ?? node.DaemonPort, request.SftpPort ?? node.SftpPort);
        if (((request.TotalMemory.HasValue || request.MemoryOverallocate.HasValue) &&
                NodeResources.Capacity(memory, memoryOver) < row.Reserved.Memory) ||
            ((request.TotalDisk.HasValue || request.DiskOverallocate.HasValue) &&
                NodeResources.Capacity(disk, diskOver) < row.Reserved.Disk))
            throw new ValidationException("Node capacity cannot be reduced below reserved resources.");
        node.ReservationVersion = Guid.NewGuid();
        if (!string.IsNullOrEmpty(request.Name)) node.Name = request.Name;
        if (request.Description != null) node.Description = request.Description;
        if (!string.IsNullOrEmpty(request.Fqdn))
        {
            if (await _context.Nodes.AnyAsync(n => n.Fqdn == request.Fqdn && n.Id != id))
            {
                return null;
            }
            node.Fqdn = request.Fqdn;
        }
        if (request.DaemonPort.HasValue) node.DaemonPort = request.DaemonPort.Value;
        if (request.SftpPort.HasValue) node.SftpPort = request.SftpPort.Value;
        if (request.UseSsl.HasValue) node.UseSsl = request.UseSsl.Value;
        node.TotalMemory = memory;
        node.TotalDisk = disk;
        node.MemoryOverallocate = memoryOver;
        node.DiskOverallocate = diskOver;

        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return MapToDto(node, row.Reserved, row.ServerCount);
    }

    public async Task<bool> DeleteNodeAsync(Guid id)
    {
        var node = await _context.Nodes.FindAsync(id);

        if (node == null) return false;

        if (await _context.Servers.AnyAsync(server => server.NodeId == id))
        {
            return false; // Can't delete node with servers
        }

        _context.Nodes.Remove(node);
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    public async Task<bool> UpdateNodeStatusAsync(Guid id, bool isOnline)
    {
        var node = await _context.Nodes.FindAsync(id);
        if (node == null) return false;

        node.IsOnline = isOnline;
        node.LastCheckedAt = DateTime.UtcNow;
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    public async Task<PagedResult<AllocationDto>> GetNodeAllocationsAsync(Guid nodeId, int page = 1, int pageSize = 50)
    {
        (page, pageSize) = Common.Pagination.Resolve(page, pageSize);
        var query = _context.Allocations
            .Include(a => a.Server)
            .Where(a => a.NodeId == nodeId);
        var total = await query.CountAsync();
        var allocations = await query.OrderBy(a => a.Port).ThenBy(a => a.Id)
            .Skip(Common.Pagination.Skip(page, pageSize)).Take(pageSize)
            .ToListAsync();

        var items = allocations.Select(a => new AllocationDto(
            a.Id,
            a.IpAddress,
            a.Port,
            a.Alias,
            a.IsAssigned,
            a.NodeId,
            a.ServerId,
            a.Server?.Name
        )).ToList();
        return new PagedResult<AllocationDto>(items, page, pageSize, total);
    }

    public async Task<bool> CreateAllocationsAsync(CreateAllocationRequest request)
    {
        var node = await _context.Nodes.FindAsync(request.NodeId);
        if (node == null) return false;

        var endPort = request.EndPort ?? request.StartPort;
        if (!IPAddress.TryParse(request.IpAddress, out _) || request.StartPort < 1024 || endPort > 65535 ||
            endPort < request.StartPort || (long)endPort - request.StartPort + 1 > SecurityLimits.MaxAllocations || request.Alias?.Length > 100)
            throw new ValidationException("Allocation batches must contain 1 to 100 valid ports on a valid IP address.");
        var existing = await _context.Allocations.Where(a => a.NodeId == request.NodeId && a.IpAddress == request.IpAddress &&
            a.Port >= request.StartPort && a.Port <= endPort).Select(a => a.Port).ToListAsync();
        for (var port = request.StartPort; port <= endPort; port++)
            if (!existing.Contains(port)) _context.Allocations.Add(new Allocation { NodeId = request.NodeId,
                IpAddress = request.IpAddress, Port = port, Alias = request.Alias });

        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    public async Task<bool> DeleteAllocationAsync(Guid id)
    {
        var allocation = await _context.Allocations.FindAsync(id);
        if (allocation == null) return false;

        if (allocation.IsAssigned || allocation.ServerId != null)
        {
            return false; // Can't delete assigned allocation
        }

        _context.Allocations.Remove(allocation);
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    public async Task<NodeConnectionTestResult> TestNodeConnectionAsync(string dockerEndpoint)
    {
        _enrollment.RequireNode(dockerEndpoint);
        try
        {
            var dockerClient = _clients.GetClient(dockerEndpoint);
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var system = dockerClient.System.GetSystemInfoAsync(cts.Token);
            var version = dockerClient.System.GetVersionAsync(cts.Token);
            await Task.WhenAll(system, version);
            var systemInfo = await system;
            var versionInfo = await version;

            return new NodeConnectionTestResult(
                Success: true,
                Message: "Successfully connected to Docker daemon",
                DockerVersion: versionInfo.Version,
                OperatingSystem: systemInfo.OperatingSystem,
                ContainerCount: checked((int)systemInfo.Containers),
                MemoryTotal: systemInfo.MemTotal > 0 ? systemInfo.MemTotal : null,
                MemoryAvailable: null,
                LogicalCpuCount: systemInfo.NCPU > 0 ? checked((int)systemInfo.NCPU) : null
            );
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.NodeConnection, exception);
            return new NodeConnectionTestResult(
                Success: false,
                Message: "[AN0401] Tempo de conexão esgotado / Node connection timed out.",
                DockerVersion: null,
                OperatingSystem: null,
                ContainerCount: null,
                MemoryTotal: null,
                MemoryAvailable: null
            );
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.NodeConnection, exception);
            return new NodeConnectionTestResult(
                Success: false,
                Message: "[AN0401] Falha na conexão com o node. Verifique certificados e rede / Node connection failed. Check certificates and network.",
                DockerVersion: null,
                OperatingSystem: null,
                ContainerCount: null,
                MemoryTotal: null,
                MemoryAvailable: null
            );
        }
    }

    public async Task<NodeConnectionTestResult> TestNodeConnectionByIdAsync(Guid nodeId)
    {
        var node = await _context.Nodes.FindAsync(nodeId);
        if (node == null)
        {
            return new NodeConnectionTestResult(
                Success: false,
                Message: "Node not found",
                DockerVersion: null,
                OperatingSystem: null,
                ContainerCount: null,
                MemoryTotal: null,
                MemoryAvailable: null
            );
        }

        var result = await TestNodeConnectionAsync(node.DockerEndpoint);
        
        // Update node status based on connection test
        node.IsOnline = result.Success;
        node.LastCheckedAt = DateTime.UtcNow;
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        
        return result;
    }

    private static void ValidateNode(string name, string fqdn, int memory, int disk, int memoryOver, int diskOver, int daemon, int sftp)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || Uri.CheckHostName(fqdn) == UriHostNameType.Unknown ||
            memory < 0 || disk < 0 || memoryOver is < 0 or > 100 || diskOver is < 0 or > 100 ||
            daemon is < 1 or > 65535 || sftp is < 1 or > 65535)
            throw new ValidationException("Invalid node configuration.");
    }

    private static IQueryable<NodeReservation> WithReservations(IQueryable<Node> nodes) =>
        nodes.Select(node => new NodeReservation(node,
            new NodeResources(node.Servers.Sum(server => (long)server.MemoryLimit),
                node.Servers.Sum(server => (long)server.CpuLimit), node.Servers.Sum(server => (long)server.DiskLimit)),
            node.Servers.Count));

    private sealed record NodeReservation(Node Node, NodeResources Reserved, int ServerCount);

    private NodeDto MapToDto(Node node, NodeResources reserved, int serverCount) => new(
        node.Id,
        node.Name,
        node.Description,
        node.Fqdn,
        node.DaemonPort,
        node.SftpPort,
        node.UseSsl,
        node.BehindProxy,
        node.DockerEndpoint,
        node.TotalMemory,
        node.TotalDisk,
        reserved.Memory,
        reserved.Disk,
        node.IsOnline,
        node.IsMaintenanceMode,
        serverCount,
        node.CreatedAt,
        node.LastCheckedAt
    )
    {
        MemoryCapacity = NodeResources.Capacity(node.TotalMemory, node.MemoryOverallocate),
        DiskCapacity = NodeResources.Capacity(node.TotalDisk, node.DiskOverallocate),
        CpuCapacityPercent = _enrollment.GetCpuCapacity(node.DockerEndpoint),
        AllocatedCpuPercent = reserved.Cpu
    };
}
