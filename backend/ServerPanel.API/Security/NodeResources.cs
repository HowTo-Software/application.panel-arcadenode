using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Models;

namespace ServerPanel.API.Security;

internal readonly record struct NodeResources(long Memory, long Cpu, long Disk)
{
    public static long Capacity(int total, int overallocate) => (long)total * (100 + overallocate) / 100;

    public static Task<NodeResources> ReservedAsync(IQueryable<GameServer> servers, CancellationToken token = default) =>
        servers.GroupBy(_ => 1)
            .Select(group => new NodeResources(group.Sum(server => (long)server.MemoryLimit),
                group.Sum(server => (long)server.CpuLimit), group.Sum(server => (long)server.DiskLimit)))
            .SingleOrDefaultAsync(token);

    public NodeResources Reserve(int memory, int cpu, int disk) => new(Memory + memory, Cpu + cpu, Disk + disk);

    public bool Fits(Node node, EnrolledDockerNode enrollment) =>
        Memory <= Capacity(node.TotalMemory, node.MemoryOverallocate) &&
        Disk <= Capacity(node.TotalDisk, node.DiskOverallocate) &&
        (enrollment.CpuCapacityPercent is not { } capacity || Cpu <= capacity);
}
