using System.Text.Json;
using System.Text.Json.Serialization;

var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false,
    WriteIndented = true
};

if (args is ["capture-volume" or "verify-volume" or "restore-backup" or "restore-pterodactyl-backup" or
    "capture-pterodactyl-volume" or "confirm-pterodactyl-source", ..])
    return await VolumeVerification.RunAsync(args, json);
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Preflight <ptero-inventory.json>");
    return 1;
}

try
{
    await using var file = File.OpenRead(args[0]);
    var inventory = await JsonSerializer.DeserializeAsync<Inventory>(file, json);
    if (inventory?.SchemaVersion != 1) throw new InvalidDataException("Expected a version 1 Pterodactyl inventory.");

    var users = Index(inventory.Users, x => x.Id, "users");
    var nodes = Index(inventory.Nodes, x => x.Id, "nodes");
    var eggs = Index(inventory.Eggs, x => x.Id, "eggs");
    var servers = Index(inventory.Servers, x => x.Id, "servers");
    var allocations = Index(inventory.Allocations, x => x.Id, "allocations");
    var schedules = Index(inventory.Schedules, x => x.Id, "schedules");
    var tasks = Index(inventory.Tasks, x => x.Id, "tasks");
    var databasesById = Index(inventory.Databases, x => x.Id, "databases");
    if (inventory.Servers.Any(s => s.Uuid == Guid.Empty) || inventory.Servers.Select(s => s.Uuid).Distinct().Count() != servers.Count ||
        inventory.Backups.Any(b => b == null || b.Uuid == Guid.Empty) || inventory.Backups.Select(b => b.Uuid).Distinct().Count() != inventory.Backups.Length)
        throw new InvalidDataException("Server and backup UUIDs must be nonempty and unique in their groups.");
    if (inventory.Subusers.Any(s => s == null)) throw new InvalidDataException("Invalid subuser entry.");

    var invalidLinks = new List<string>();
    foreach (var allocation in allocations.Values)
    {
        if (!nodes.ContainsKey(allocation.NodeId) ||
            (allocation.ServerId is { } owner && (!servers.TryGetValue(owner, out var server) || server.NodeId != allocation.NodeId)))
            invalidLinks.Add($"allocation:{allocation.Id}");
    }
    foreach (var schedule in schedules.Values)
        if (!servers.ContainsKey(schedule.ServerId)) invalidLinks.Add($"schedule:{schedule.Id}");
    foreach (var task in tasks.Values)
        if (!schedules.ContainsKey(task.ScheduleId)) invalidLinks.Add($"task:{task.Id}");
    foreach (var subuser in inventory.Subusers)
        if (!servers.ContainsKey(subuser.ServerId) || !users.ContainsKey(subuser.UserId))
            invalidLinks.Add($"subuser:{subuser.ServerId}/{subuser.UserId}");
    foreach (var backup in inventory.Backups)
        if (!servers.ContainsKey(backup.ServerId)) invalidLinks.Add($"backup:{backup.Uuid}");
    foreach (var database in databasesById.Values)
        if (!servers.ContainsKey(database.ServerId)) invalidLinks.Add($"database:{database.Id}");

    var assigned = allocations.Values.Where(a => a.ServerId != null).ToLookup(a => a.ServerId!.Value);
    var portConflicts = allocations.Values.Where(a => a.ServerId != null)
        .GroupBy(a => (a.NodeId, a.Port)).Where(g => g.Count() > 1).SelectMany(g => g.Select(a => a.Id)).ToHashSet();
    var scheduleCounts = schedules.Values.ToLookup(x => x.ServerId);
    var backups = inventory.Backups.ToLookup(x => x.ServerId);
    var databases = databasesById.Values.ToLookup(x => x.ServerId);
    var subusers = inventory.Subusers.ToLookup(x => x.ServerId);

    var reports = new List<ServerReport>();
    foreach (var server in inventory.Servers.OrderBy(x => x.Id))
    {
        var findings = new List<string>();
        if (!users.ContainsKey(server.OwnerId)) findings.Add("missing_owner");
        if (!nodes.ContainsKey(server.NodeId)) findings.Add("missing_node");
        if (!eggs.TryGetValue(server.EggId, out var egg) || egg.NestId != server.NestId)
            findings.Add("missing_or_mismatched_egg");
        if (!allocations.TryGetValue(server.AllocationId, out var primary) || primary.ServerId != server.Id || primary.NodeId != server.NodeId)
            findings.Add("missing_or_mismatched_primary_allocation");
        if (assigned[server.Id].Any(a => portConflicts.Contains(a.Id))) findings.Add("duplicate_node_port");
        if (assigned[server.Id].Any(a => a.Port is < 1024 or > 65535)) findings.Add("unsupported_port");
        if (server.Memory is < 128 or > 1048576 || server.Cpu is < 1 or > 6400 || server.Disk is < 256 or > 10485760)
            findings.Add("resource_limits_require_mapping");

        reports.Add(new ServerReport(server.Id, server.Uuid, server.NodeId, server.OwnerId, server.PanelStatus,
            assigned[server.Id].Count(), scheduleCounts[server.Id].Count(s => s.IsActive != 0),
            backups[server.Id].Count(), databases[server.Id].Count(), subusers[server.Id].Count(), findings));
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Mode = "inventory_preflight",
        InventoryCapturedAt = inventory.CapturedAt,
        RuntimeVerified = false,
        UsersWithTotp = users.Values.Count(u => u.UseTotp != 0),
        SourceAdministrators = users.Values.Count(u => u.RootAdmin != 0),
        InvalidLinks = invalidLinks,
        Servers = reports
    }, json));
    return invalidLinks.Count == 0 && reports.All(r => r.Findings.Count == 0) ? 0 : 2;
}
catch (InvalidDataException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Could not read the inventory. Check the path, permissions and version 1 schema.");
    return 1;
}

static Dictionary<long, T> Index<T>(IEnumerable<T> items, Func<T, long> getId, string group)
{
    var result = new Dictionary<long, T>();
    foreach (var item in items)
    {
        if (item is null) throw new InvalidDataException($"Invalid entry in {group}.");
        var id = getId(item);
        if (id <= 0) throw new InvalidDataException($"Invalid ID in {group}.");
        if (!result.TryAdd(id, item)) throw new InvalidDataException($"Duplicate ID in {group}.");
    }
    return result;
}

internal sealed record Inventory(int SchemaVersion, DateTimeOffset CapturedAt, bool LiveRuntimeChecked,
    UserEntry[] Users, NodeEntry[] Nodes, EggEntry[] Eggs, ServerEntry[] Servers, AllocationEntry[] Allocations,
    SubuserEntry[] Subusers, ScheduleEntry[] Schedules, TaskEntry[] Tasks, BackupEntry[] Backups, DatabaseEntry[] Databases);
internal sealed record UserEntry(long Id, int RootAdmin, int UseTotp);
internal sealed record NodeEntry(long Id);
internal sealed record EggEntry(long Id, long NestId);
internal sealed record ServerEntry(long Id, Guid Uuid, long NodeId, long OwnerId, long NestId, long EggId, long AllocationId,
    string? PanelStatus, int Memory, int Disk, int Cpu);
internal sealed record AllocationEntry(long Id, long NodeId, long? ServerId, int Port);
internal sealed record SubuserEntry(long ServerId, long UserId);
internal sealed record ScheduleEntry(long Id, long ServerId, int IsActive);
internal sealed record TaskEntry(long Id, long ScheduleId);
internal sealed record BackupEntry(Guid Uuid, long ServerId);
internal sealed record DatabaseEntry(long Id, long ServerId);
internal sealed record ServerReport(long SourceId, Guid SourceUuid, long SourceNodeId, long SourceOwnerId, string? PanelStatus,
    int Allocations, int ActiveSchedules, int Backups, int Databases, int Subusers, List<string> Findings);
