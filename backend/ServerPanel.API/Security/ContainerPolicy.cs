using Docker.DotNet.Models;
using System.Text.Json;
using System.Globalization;
using ServerPanel.API.Models;

namespace ServerPanel.API.Security;

public static class ContainerPolicy
{
    public static CreateContainerParameters Build(GameServer server, EnrolledDockerNode node, ApprovedGameImage image, string volume)
    {
        SecurityLimits.Resources(server.MemoryLimit, server.CpuLimit, server.DiskLimit, server.Port);
        var secondary = server.AdditionalPorts is null ? [] : JsonSerializer.Deserialize<int[]>(server.AdditionalPorts) ?? [];
        if (!SecurityLimits.ValidSecondaryPorts(server.Port, secondary))
            throw new InvalidOperationException("Invalid secondary ports.");
        var cpuCores = server.CpuSet?.Split(',').Select(core => int.Parse(core, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
        var cpuSet = SecurityLimits.CpuSet(cpuCores, server.CpuLimit, node);
        var bindings = new Dictionary<string, IList<PortBinding>>();
        foreach (var port in secondary.Prepend(server.Port))
        {
            var hostPort = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bindings.Add(hostPort + "/tcp", new[] { new PortBinding { HostIP = node.GameBindAddress, HostPort = hostPort } });
            bindings.Add(hostPort + "/udp", new[] { new PortBinding { HostIP = node.GameBindAddress, HostPort = hostPort } });
        }
        return new CreateContainerParameters
        {
            Name = $"arcadenode-{server.Id:N}", Image = image.Image, User = "10001:10001",
            Tty = true, OpenStdin = true, AttachStdin = true, StdinOnce = false,
            Labels = new Dictionary<string, string> { ["panel.server.id"] = server.Id.ToString(), ["panel.node.id"] = server.NodeId.ToString(), ["panel.owner.id"] = server.OwnerId.ToString() },
            HostConfig = new HostConfig
            {
                Memory = checked((long)server.MemoryLimit * 1024 * 1024),
                MemorySwap = checked((long)server.MemoryLimit * 1024 * 1024),
                NanoCPUs = checked((long)server.CpuLimit * 10_000_000), CpusetCpus = cpuSet, PidsLimit = 512,
                Privileged = false, ReadonlyRootfs = true, NetworkMode = node.GameNetwork,
                CapDrop = new[] { "ALL" }, SecurityOpt = new[] { "no-new-privileges:true" },
                Mounts = new[] { new Mount { Type = "volume", Source = volume, Target = image.DataPath, ReadOnly = false } },
                Tmpfs = new Dictionary<string, string> { ["/tmp"] = "rw,nosuid,nodev,noexec,size=64m,mode=1777" },
                PortBindings = bindings,
                LogConfig = new LogConfig { Type = "json-file", Config = new Dictionary<string, string> { ["max-size"] = "10m", ["max-file"] = "3" } },
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No }
            }
        };
    }
}
