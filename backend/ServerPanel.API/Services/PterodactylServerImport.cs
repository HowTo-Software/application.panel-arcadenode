using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;

namespace ServerPanel.API.Services;

public static class PterodactylServerImport
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    public static async Task<Report> RunAsync(PanelDbContext db, DockerEnrollment enrollment, ICartridgeService cartridges, Stream input, bool apply,
        CancellationToken token = default, Stream? mappingInput = null, EggRuntimeProfiles? eggRuntimes = null)
    {
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(input, InputOptions, token);
        if (manifest is not { SchemaVersion: 1 or 2, Servers.Length: <= 10000 })
            throw new ValidationException("Expected a version 1 or 2 Pterodactyl server manifest.");
        PterodactylServerMappings? mappings = null;
        if (manifest.SchemaVersion == 2)
        {
            if (mappingInput is null || eggRuntimes is null)
                throw new ValidationException("Provide the reviewed server mapping to use the version 2 configuration export.");
            mappings = await PterodactylServerMappings.ReadAsync(mappingInput, InputOptions, token);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var issues = new List<Issue>();
        var pending = new List<GameServer>();
        var seenIds = new HashSet<Guid>();
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservations = new Dictionary<Guid, Reservation>();
        var resolvedEggs = new Dictionary<Guid, EggRuntimeProfiles.Runtime?>();
        List<Placement>? placements = mappings is null ? null : [];
        var alreadyPresent = 0;
        foreach (var row in manifest.Servers)
        {
            if (row is null) throw new ValidationException("The manifest contains an invalid server.");
            string? problem;
            if (row.ServerUuid == Guid.Empty || row.OwnerUuid == Guid.Empty ||
                row.Identifier is not { Length: 8 } || row.Identifier.Any(c => !char.IsAsciiHexDigit(c)) ||
                string.IsNullOrWhiteSpace(row.Name) || row.SecondaryPorts is null ||
                (mappings is null && (row.TargetNodeId == Guid.Empty || row.ApprovedImage is not { Length: > 0 })))
                problem = "invalid_or_incomplete_mapping";
            else if (!seenIds.Add(row.ServerUuid) || !identifiers.Add(row.Identifier))
                problem = "duplicate_server";
            else if (!SecurityLimits.ValidSecondaryPorts(row.Port, row.SecondaryPorts))
                problem = "invalid_secondary_ports";
            else if (!string.IsNullOrWhiteSpace(row.SourceThreads) && row.TargetCpuCores is null or { Length: 0 })
                problem = "destination_cpu_affinity_required";
            else
                problem = await StageAsync(row);
            if (problem is not null) issues.Add(new(row.ServerUuid, problem));
        }

        if (apply && issues.Count == 0)
        {
            foreach (var reservation in reservations.Values.Where(r => r.IsModified))
                reservation.Node.ReservationVersion = Guid.NewGuid();
            foreach (var server in pending.Where(s => s.AdditionalPorts != null))
            {
                var reservation = reservations[server.NodeId];
                var extraPorts = JsonSerializer.Deserialize<int[]>(server.AdditionalPorts!)!;
                var pool = await db.Allocations.Where(a => a.NodeId == server.NodeId && a.IpAddress == reservation.BindAddress &&
                    extraPorts.Contains(a.Port) && a.ServerId == null).ToDictionaryAsync(a => a.Port, token);
                foreach (var port in extraPorts)
                {
                    var allocation = pool.GetValueOrDefault(port) ?? new Allocation
                    {
                        NodeId = server.NodeId, IpAddress = reservation.BindAddress, Port = port
                    };
                    allocation.IsAssigned = true;
                    server.Allocations.Add(allocation);
                }
            }
            db.Servers.AddRange(pending);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        return new(pending.Count, alreadyPresent, apply && issues.Count == 0 ? pending.Count : 0, issues, placements);

        async Task<string?> StageAsync(ServerEntry row)
        {
            Guid? targetEggId = null;
            Cartridge? eggCartridge = null;
            var values = row.Variables ?? new Dictionary<string, object>();
            if (mappings is not null)
            {
                if (row.SourceNodeId <= 0 || row.SourceEggId <= 0) return "invalid_source_egg_or_node";
                var targetNode = row.TargetNodeId == Guid.Empty ? mappings.TargetNode(row.SourceNodeId) : row.TargetNodeId;
                if (targetNode == Guid.Empty) return "source_node_mapping_missing";
                var profile = mappings.TargetProfile(row.SourceEggId, row.SourceImage);
                if (profile is null) return "source_egg_or_image_mapping_missing";
                if (row.SourceVariables is null || !profile.SourceVariables.SetEquals(row.SourceVariables.Keys))
                    return "source_variables_missing_or_unexpected";

                if (!resolvedEggs.TryGetValue(profile.TargetEggId, out var runtime))
                {
                    var egg = await db.Eggs.AsNoTracking().Where(e => e.Id == profile.TargetEggId)
                        .Select(e => new { e.PterodactylSha256, e.Variables }).SingleOrDefaultAsync(token);
                    try { runtime = egg?.PterodactylSha256 is { } digest ? eggRuntimes!.Resolve(digest, egg.Variables) : null; }
                    catch (Exception error) when (error is ValidationException or JsonException) { runtime = null; }
                    resolvedEggs.Add(profile.TargetEggId, runtime);
                }
                if (runtime is null) return "destination_egg_runtime_unapproved";
                eggCartridge = runtime.Cartridge;
                if (!string.IsNullOrWhiteSpace(row.CartridgeId) && row.CartridgeId != eggCartridge.Id)
                    return "destination_egg_cartridge_mismatch";
                try { values = runtime.ResolveMigratedValues(row.SourceVariables, row.Variables); }
                catch (ValidationException) { return "destination_egg_variables_invalid"; }
                targetEggId = profile.TargetEggId;
                row = row with { TargetNodeId = targetNode, ApprovedImage = row.ApprovedImage ?? eggCartridge.Docker.Image,
                    CartridgeId = eggCartridge.Id };
            }

            var secondary = row.SecondaryPorts!.Order().ToArray();
            var serialized = secondary.Length == 0 ? null : JsonSerializer.Serialize(secondary);
            ApprovedGameImage approvedImage;
            try
            {
                SecurityLimits.Text(row.Name, row.Description);
                SecurityLimits.Resources(row.Memory, row.Cpu, row.Disk, row.Port);
                approvedImage = enrollment.RequireImage(row.ApprovedImage!);
            }
            catch (ValidationException) { return "unsupported_resources_or_image"; }
            catch (InvalidOperationException) { return "unsupported_resources_or_image"; }

            string? environment = null;
            if (!string.IsNullOrWhiteSpace(row.CartridgeId))
            {
                var cartridge = eggCartridge ?? cartridges.GetCartridgeById(row.CartridgeId);
                if (cartridge is null) return "destination_cartridge_not_found";
                try
                {
                    if (enrollment.RequireImage(cartridge.Docker.Image).Image != approvedImage.Image)
                        return "destination_cartridge_image_mismatch";
                }
                catch (ValidationException) { return "destination_cartridge_image_mismatch"; }
                if (!cartridges.ValidateVariables(cartridge, values, forRestore: true).IsValid) return "destination_variables_invalid";
                var namedPorts = row.NamedPorts ?? new Dictionary<string, int>();
                var primaryName = cartridge.Ports.Keys.FirstOrDefault();
                if (namedPorts.Any(pair => !cartridge.Ports.ContainsKey(pair.Key) ||
                    (pair.Key == primaryName ? pair.Value != row.Port : pair.Value != row.Port && !secondary.Contains(pair.Value))))
                    return "destination_named_ports_invalid";
                var mapped = cartridges.BuildEnvironmentVariables(cartridge, values, row.Port, namedPorts, forRestore: true);
                try { SecurityLimits.EnvironmentVariables(mapped); }
                catch (ValidationException) { return "destination_variables_invalid"; }
                environment = JsonSerializer.Serialize(new SortedDictionary<string, string>(mapped, StringComparer.Ordinal));
            }
            else if (row.Variables is { Count: > 0 } || row.NamedPorts is { Count: > 0 })
            {
                return "destination_cartridge_required_for_variables";
            }

            if (!await db.Users.AnyAsync(u => u.Id == row.OwnerUuid && u.IsActive, token))
                return "destination_owner_missing_or_inactive";
            if (!reservations.TryGetValue(row.TargetNodeId, out var reservation))
            {
                var node = await db.Nodes.SingleOrDefaultAsync(n => n.Id == row.TargetNodeId, token);
                if (node is null || node.IsMaintenanceMode || node.TotalMemory <= 0 || node.TotalDisk <= 0)
                    return "destination_node_unavailable";
                EnrolledDockerNode enrolled;
                try { enrolled = enrollment.RequireNode(node.DockerEndpoint); enrollment.RequireProvisioning(enrolled); }
                catch (ValidationException) { return "destination_node_not_enrolled"; }
                catch (InvalidOperationException) { return "destination_node_not_enrolled"; }

                var servers = db.Servers.Where(s => s.NodeId == node.Id);
                var assigned = await db.Allocations.Where(a => a.NodeId == node.Id && a.ServerId != null)
                    .Select(a => a.Port).Union(servers.Select(s => s.Port)).ToListAsync(token);
                var reserved = await NodeResources.ReservedAsync(servers, token);
                reservation = new Reservation(node, enrolled, reserved, assigned.ToHashSet());
                reservations.Add(node.Id, reservation);
            }

            string? cpuSet;
            try { cpuSet = SecurityLimits.CpuSet(row.TargetCpuCores, row.Cpu, reservation.Enrollment); }
            catch (ValidationException) { return "destination_cpu_affinity_invalid"; }
            var matches = await db.Servers.AsNoTracking().Where(s => s.Id == row.ServerUuid || s.Identifier == row.Identifier)
                .Take(2).ToListAsync(token);
            if (matches.Count != 0)
            {
                if (matches is [var match] && match.MigrationPending && match.Status == ServerStatus.Suspended && match.IsSuspended && match.ContainerId == null &&
                    match.Id == row.ServerUuid && match.OwnerId == row.OwnerUuid && match.NodeId == row.TargetNodeId &&
                    match.Identifier == row.Identifier && match.DockerImage == row.ApprovedImage && match.Port == row.Port &&
                    match.MemoryLimit == row.Memory && match.CpuLimit == row.Cpu && match.CpuSet == cpuSet && match.DiskLimit == row.Disk &&
                    match.Name == row.Name && match.Description == (row.Description ?? "") && match.AdditionalPorts == serialized &&
                    match.EnvironmentVariables == environment && match.EggId == targetEggId)
                {
                    alreadyPresent++;
                    RecordPlacement();
                    return null;
                }
                return "existing_server_conflict";
            }

            if (reservation.Ports.Contains(row.Port) || secondary.Any(reservation.Ports.Contains)) return "destination_port_reserved";
            var reservedAfterImport = reservation.Resources.Reserve(row.Memory, row.Cpu, row.Disk);
            if (!reservedAfterImport.Fits(reservation.Node, reservation.Enrollment))
                return "destination_capacity_exceeded";

            reservation.Ports.Add(row.Port);
            reservation.Ports.UnionWith(secondary);
            reservation.Resources = reservedAfterImport;
            reservation.IsModified = true;
            pending.Add(new GameServer
            {
                Id = row.ServerUuid, Identifier = row.Identifier!, OwnerId = row.OwnerUuid, NodeId = row.TargetNodeId,
                Name = row.Name!, Description = row.Description ?? "", DockerImage = row.ApprovedImage!, EggId = targetEggId,
                EnvironmentVariables = environment,
                MemoryLimit = row.Memory, CpuLimit = row.Cpu, CpuSet = cpuSet, DiskLimit = row.Disk, Port = row.Port, AdditionalPorts = serialized,
                Status = ServerStatus.Suspended, IsSuspended = true, MigrationPending = true
            });
            RecordPlacement();
            return null;

            void RecordPlacement()
            {
                if (targetEggId is { } id)
                    placements!.Add(new(row.ServerUuid, row.TargetNodeId, id, row.SourceVariables!.Count));
            }
        }
    }

    private sealed class Reservation(Node node, EnrolledDockerNode enrollment, NodeResources resources, HashSet<int> ports)
    {
        public Node Node { get; } = node;
        public EnrolledDockerNode Enrollment { get; } = enrollment;
        public string BindAddress => Enrollment.GameBindAddress;
        public NodeResources Resources { get; set; } = resources;
        public HashSet<int> Ports { get; } = ports;
        public bool IsModified { get; set; }
    }

    private sealed record Manifest(int SchemaVersion, ServerEntry?[] Servers);
    private sealed record ServerEntry(Guid ServerUuid, Guid OwnerUuid, Guid TargetNodeId, string? Identifier, string? Name, string? Description,
        string? ApprovedImage, int Memory, int Cpu, int Disk, int Port, int[]? SecondaryPorts,
        string? SourceThreads = null, int[]? TargetCpuCores = null,
        string? CartridgeId = null, Dictionary<string, object>? Variables = null, Dictionary<string, int>? NamedPorts = null,
        long SourceNodeId = 0, long SourceEggId = 0, string? SourceImage = null, Dictionary<string, string?>? SourceVariables = null);
    public sealed record Issue(Guid ServerUuid, string Reason);
    public sealed record Placement(Guid ServerUuid, Guid TargetNodeId, Guid TargetEggId, int PreservedVariables);
    public sealed record Report(int Pending, int AlreadyPresent, int Imported, IReadOnlyList<Issue> Issues,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Placement>? Placements);
}
