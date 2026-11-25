extern alias MigrationPreflight;

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;
using ServerPanel.API.Services;
using PterodactylSource = MigrationPreflight::PterodactylSource;
using RestoredGameVolumes = MigrationPreflight::RestoredGameVolumes;
using VolumeVerification = MigrationPreflight::VolumeVerification;
using ZomboidVolume = MigrationPreflight::ProjectZomboidVolume;

namespace SecurityRegression;

public static class Runner
{
    private const string Password = "SyntheticOnly123!";
    private static readonly List<object> Results = new();
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Check(string name, Func<Task> action) { await action(); Results.Add(new { name, passed = true }); }

    public static async Task<int> Main(string[] eggFiles)
    {
        if (eggFiles is ["--sqlserver"])
        {
            try
            {
                await SqlServerSmoke.RunAsync();
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"SQL Server smoke failed ({error.GetType().Name}): {error.Message}");
                return 1;
            }
        }
        var fixture = new SecurityFixture();
        fixture.FirstNode = new FakeDockerNode(fixture); fixture.SecondNode = new FakeDockerNode(fixture);
        await fixture.FirstNode.StartAsync(); await fixture.SecondNode.StartAsync();
        try
        {
            using var anonymous = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                await db.Database.EnsureCreatedAsync();
                foreach (var (id, username, role) in new[] { (fixture.OwnerId, "owner", UserRole.User), (fixture.OtherId, "other", UserRole.User), (fixture.AdminId, "admin", UserRole.SuperAdmin) })
                    db.Users.Add(new User { Id = id, Username = username, Email = username + "@example.test", Role = role, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password) });
                db.Nodes.AddRange(new Node { Id = fixture.NodeId, Name = "First", Fqdn = "first.example.test", DockerEndpoint = fixture.FirstNode.Endpoint, TotalMemory = 65536, TotalDisk = 524288 },
                    new Node { Id = fixture.SecondNodeId, Name = "Second", Fqdn = "second.example.test", DockerEndpoint = fixture.SecondNode.Endpoint, TotalMemory = 65536, TotalDisk = 524288 });
                db.Servers.AddRange(new GameServer { Id = fixture.ServerId, Name = "Owned", Identifier = "audit001", DockerImage = "audit/synthetic:secure", MemoryLimit = 4096, Port = 25565, OwnerId = fixture.OwnerId, NodeId = fixture.NodeId, ContainerId = "owned", Status = ServerStatus.Running },
                    new GameServer { Id = fixture.SecondServerId, Name = "Second", Identifier = "audit002", DockerImage = "audit/synthetic:secure", MemoryLimit = 4096, Port = 25565, OwnerId = fixture.OwnerId, NodeId = fixture.SecondNodeId, ContainerId = "second", Status = ServerStatus.Stopped });
                await db.SaveChangesAsync();
            }
            fixture.FirstNode.Seed("owned", fixture.ServerId, fixture.NodeId, fixture.OwnerId);
            fixture.SecondNode.Seed("second", fixture.SecondServerId, fixture.SecondNodeId, fixture.OwnerId);
            using var owner = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var other = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var admin = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            await Login(owner, "owner", Password); await Login(other, "other", Password); await Login(admin, "admin", Password);

            await Check("Node CPU reservations are shared between provisioning and migration", async () =>
            {
                var path = $"/api/nodes/{fixture.SecondNodeId}";
                var original = await admin.GetFromJsonAsync<JsonElement>(path);
                Assert(original.GetProperty("cpuCapacityPercent").GetInt32() == 1200 &&
                    original.GetProperty("allocatedCpuPercent").GetInt64() == 100 &&
                    original.GetProperty("allocatedMemory").GetInt64() == 4096 &&
                    original.GetProperty("allocatedDisk").GetInt64() == 10240,
                    "The node response did not account for its existing reservation");
                var list = await admin.GetFromJsonAsync<JsonElement>("/api/nodes");
                Assert(list.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == fixture.SecondNodeId)
                    .GetProperty("allocatedCpuPercent").GetInt64() == 100, "Node list and detail disagree");

                var before = fixture.SecondNode.Requests.Count;
                using var refused = await admin.PostAsJsonAsync("/api/servers/from-cartridge", new
                {
                    nodeId = fixture.SecondNodeId, cartridgeId = "minecraft-java", name = "CPU capacity fixture",
                    memoryLimit = 2048, cpuLimit = 1101, diskLimit = 4096, port = 23112
                });
                Assert(refused.StatusCode == HttpStatusCode.BadRequest && fixture.SecondNode.Requests.Count == before &&
                    (await refused.Content.ReadAsStringAsync()).Contains("Node capacity exceeded"),
                    "Provisioning exceeded CPU capacity or contacted Docker before admission");

                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var enrollment = scope.ServiceProvider.GetRequiredService<DockerEnrollment>();
                var cartridges = scope.ServiceProvider.GetRequiredService<ICartridgeService>();
                var firstId = Guid.NewGuid();
                var secondId = Guid.NewGuid();
                object Entry(Guid id, int cpu, int port) => new { server_uuid = id, owner_uuid = fixture.OwnerId, target_node_id = fixture.SecondNodeId,
                    identifier = id.ToString("N")[..8], name = "CPU migration fixture", description = (string?)null, approved_image = "audit/synthetic:secure",
                    memory = 2048, cpu, disk = 4096, port, secondary_ports = Array.Empty<int>() };
                async Task<PterodactylServerImport.Report> Import(bool apply, params object[] servers)
                {
                    using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, servers }));
                    return await PterodactylServerImport.RunAsync(db, enrollment, cartridges, input, apply);
                }
                var lastFit = Entry(firstId, 1100, 23112);
                var refusedBatch = await Import(true, lastFit, Entry(secondId, 1, 23113));
                Assert(refusedBatch is { Imported: 0 } && refusedBatch.Issues.Single() is { Reason: "destination_capacity_exceeded" } &&
                    !await db.Servers.AnyAsync(server => server.Id == firstId || server.Id == secondId),
                    "The migration batch exceeded the node budget or saved a partial reservation");
                var reserved = await Import(true, lastFit);
                Assert(reserved is { Imported: 1, Issues.Count: 0 }, "The migration did not accept the exact remaining CPU capacity");
                try
                {
                    var response = await admin.GetFromJsonAsync<JsonElement>(path);
                    Assert(response.GetProperty("allocatedCpuPercent").GetInt64() == 1200, "Migration reservation was not reflected in node totals");
                    using var competing = await admin.PostAsJsonAsync("/api/servers", new
                    {
                        nodeId = fixture.SecondNodeId, dockerImage = "audit/synthetic:secure", name = "Competing CPU fixture",
                        memoryLimit = 2048, cpuLimit = 1, diskLimit = 4096, port = 23113
                    });
                    Assert(competing.StatusCode == HttpStatusCode.BadRequest && fixture.SecondNode.Requests.Count == before,
                        "A normal creation bypassed a staged migration reservation");
                }
                finally
                {
                    db.Servers.Remove((await db.Servers.FindAsync(firstId))!);
                    await db.SaveChangesAsync();
                }
            });
            await Check("Node edits respect effective memory and disk already reserved", async () =>
            {
                var path = $"/api/nodes/{fixture.SecondNodeId}";
                var adjusted = await admin.PutAsJsonAsync(path, new { totalMemory = 3072, memoryOverallocate = 50, totalDisk = 8192, diskOverallocate = 50 });
                Assert(adjusted.IsSuccessStatusCode, "Overallocated capacity was compared against the raw totals");
                try
                {
                    var node = await adjusted.Content.ReadFromJsonAsync<JsonElement>();
                    Assert(node.GetProperty("memoryCapacity").GetInt64() == 4608 && node.GetProperty("diskCapacity").GetInt64() == 12288,
                        "The response did not report effective capacity including over-allocation");
                    using var memory = await admin.PutAsJsonAsync(path, new { memoryOverallocate = 0 });
                    using var disk = await admin.PutAsJsonAsync(path, new { diskOverallocate = 0 });
                    Assert(memory.StatusCode == HttpStatusCode.BadRequest && disk.StatusCode == HttpStatusCode.BadRequest,
                        "Dropping an over-allocation alone invalidated existing reservations");
                }
                finally
                {
                    adjusted.Dispose();
                    using var restored = await admin.PutAsJsonAsync(path, new { totalMemory = 65536, memoryOverallocate = 0,
                        totalDisk = 524288, diskOverallocate = 0 });
                    Assert(restored.IsSuccessStatusCode, "Could not restore the node fixture settings");
                }
            });

            await Check("Anonymous requests cannot access servers", async () =>
                Assert((await anonymous.GetAsync("/api/servers")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous server access allowed"));
            await Check("Linux memory and CPU counters are interpreted separately from block I/O", async () =>
            {
                using var response = await owner.GetAsync($"/api/servers/{fixture.ServerId}/stats");
                var diagnostic = response.Headers.TryGetValues("X-Diagnostic-Code", out var codes) ? string.Join(", ", codes) : "none";
                Assert(response.IsSuccessStatusCode, $"Stats request failed with HTTP {(int)response.StatusCode} ({diagnostic})");
                var stats = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert(stats.GetProperty("memoryUsage").GetInt64() == 700 && stats.GetProperty("cpuUsage").GetDouble() == 20,
                    "cgroup v2 cache or legacy CPU count was interpreted incorrectly");
                Assert(stats.GetProperty("diskUsage").ValueKind == JsonValueKind.Null && stats.GetProperty("blockIoBytes").GetInt64() == 750,
                    "Block I/O was confused with storage usage");
            });
            await Check("Runtime monitoring follows the assigned node and online schedules confirm the actual container", async () =>
            {
                var scopes = fixture.Services.GetRequiredService<IServiceScopeFactory>();
                var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var first = db.Servers.Where(server => server.Id == fixture.ServerId);
                var second = db.Servers.Where(server => server.Id == fixture.SecondServerId);
                var firstRequests = fixture.FirstNode.Requests.Count;
                var secondRequests = fixture.SecondNode.Requests.Count;
                var started = new DateTime(2026, 11, 24, 14, 20, 0, DateTimeKind.Utc);
                var running = new Docker.DotNet.Models.ContainerState { Status = "running", Running = true, StartedAt = "2026-11-24T14:20:00.000000000Z" };
                var exited = new Docker.DotNet.Models.ContainerState { Status = "exited", ExitCode = 137, OOMKilled = true, StartedAt = running.StartedAt };
                fixture.FirstNode.RuntimeOverrides["owned"] = exited;
                fixture.SecondNode.RuntimeOverrides["second"] = running;
                var due = DateTime.UtcNow.AddMinutes(-1);
                var schedule = new Schedule { ServerId = fixture.ServerId, Name = "Actual online test", CronExpression = "* * * * *", OnlyWhenOnline = true, NextRunAt = due };
                schedule.Tasks.Add(new ScheduleTask { Action = TaskAction.Power, Payload = "restart" });
                db.Schedules.Add(schedule);
                await db.SaveChangesAsync();
                try
                {
                    using var runningCheck = scopes.CreateScope();
                    Assert(await ScheduleWorker.RunOneAsync(runningCheck.ServiceProvider, due, CancellationToken.None) &&
                        !fixture.FirstNode.Requests.Skip(firstRequests).Any(request => request.Path.EndsWith("/restart")),
                        "An online-only schedule acted on an exited container with a stale running row");

                    using var response = await owner.GetAsync($"/api/servers/{fixture.ServerId}/stats");
                    var stats = await response.Content.ReadFromJsonAsync<JsonElement>();
                    Assert(response.IsSuccessStatusCode && stats.GetProperty("status").GetString() == "Stopped" &&
                        stats.GetProperty("exitCode").GetInt64() == 137 && stats.GetProperty("oomKilled").GetBoolean() &&
                        !stats.GetProperty("metricsAvailable").GetBoolean() &&
                        !fixture.FirstNode.Requests.Skip(firstRequests).Any(request => request.Path.EndsWith("/stats")),
                        "Exited runtime sampled unusable counters or suppressed Docker's termination reason");

                    await ServerRuntimeMonitor.ReconcileOnceAsync(scopes, logger, CancellationToken.None);
                    var firstStatus = await first.Select(item => item.Status).SingleAsync();
                    var secondStatus = await second.Select(item => item.Status).SingleAsync();
                    var recordedStart = await second.Select(item => item.LastStartedAt).SingleAsync();
                    var firstLists = fixture.FirstNode.Requests.Skip(firstRequests).Count(item => item.Path.EndsWith("/containers/json"));
                    var secondLists = fixture.SecondNode.Requests.Skip(secondRequests).Count(item => item.Path.EndsWith("/containers/json"));
                    Assert(firstStatus == ServerStatus.Stopped && secondStatus == ServerStatus.Running && recordedStart == started &&
                        firstLists == 1 && secondLists == 1,
                        $"Unexpected reconciled states {firstStatus}/{secondStatus}, start {recordedStart:O}, list calls {firstLists}/{secondLists}");

                    fixture.FirstNode.RuntimeOverrides["owned"] = running;
                    fixture.FirstNode.ContainerListUnavailable = true;
                    await second.ExecuteUpdateAsync(update => update.SetProperty(item => item.IsSuspended, true).SetProperty(item => item.Status, ServerStatus.Suspended));
                    await ServerRuntimeMonitor.ReconcileOnceAsync(scopes, logger, CancellationToken.None);
                    Assert(await first.Select(item => item.Status).SingleAsync() == ServerStatus.Stopped &&
                        await second.Select(item => item.Status).SingleAsync() == ServerStatus.Suspended,
                        "The monitor overwrote DB state from a node outage or changed a suspended server");

                    fixture.FirstNode.ContainerListUnavailable = false;
                    fixture.FirstNode.WrongOwnership = true;
                    await ServerRuntimeMonitor.ReconcileOnceAsync(scopes, logger, CancellationToken.None);
                    Assert(await first.Select(item => item.Status).SingleAsync() == ServerStatus.Stopped,
                        "A wrong ownership label overwrote the recorded server status");
                    fixture.FirstNode.WrongOwnership = false;
                    await ServerRuntimeMonitor.ReconcileOnceAsync(scopes, logger, CancellationToken.None);
                    Assert(await first.Select(item => item.Status).SingleAsync() == ServerStatus.Running,
                        "The monitor did not recover when the authorized node became available");
                }
                finally
                {
                    fixture.FirstNode.RuntimeOverrides.TryRemove("owned", out _);
                    fixture.SecondNode.RuntimeOverrides.TryRemove("second", out _);
                    fixture.FirstNode.ContainerListUnavailable = false;
                    fixture.FirstNode.WrongOwnership = false;
                    await db.Schedules.Where(item => item.Id == schedule.Id).ExecuteDeleteAsync();
                    await first.ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, ServerStatus.Running)
                        .SetProperty(item => item.LastStartedAt, (DateTime?)null));
                    await second.ExecuteUpdateAsync(update => update.SetProperty(item => item.IsSuspended, false)
                        .SetProperty(item => item.Status, ServerStatus.Stopped).SetProperty(item => item.LastStartedAt, (DateTime?)null));
                }
            });
            await Check("Local assistant uses authorized resource context without Docker access", async () =>
            {
                var path = $"/api/servers/{fixture.ServerId}/assistant";
                var body = new { question = "Como ajustar a memória?", endpoint = "https://untrusted.invalid/", model = "other-model" };
                var dockerRequests = fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count;
                using var denied = await other.PostAsJsonAsync(path, new { body.question, includeRuntime = true });
                Assert(denied.StatusCode == HttpStatusCode.Forbidden && fixture.AssistantRequests.IsEmpty, "Foreign account sent a prompt");
                using var accepted = await owner.PostAsJsonAsync(path, body);
                Assert(accepted.IsSuccessStatusCode, "Authorized assistant request failed");
                var answer = await accepted.Content.ReadFromJsonAsync<JsonElement>();
                Assert(answer.GetProperty("answer").GetString() == "Resposta sintética do modelo." &&
                    !answer.GetProperty("runtimeIncluded").GetBoolean(), "Assistant answer or omitted runtime status was lost");
                Assert(fixture.AssistantRequests.TryDequeue(out var request) && request.Uri?.Host == "local-assistant.invalid" &&
                    request.Uri.AbsolutePath == "/api/chat" && request.Body.Contains("synthetic-local:latest") &&
                    request.Body.Contains("MemoryLimit") && !request.Body.Contains("untrusted.invalid") && !request.Body.Contains("other-model") &&
                    !request.Body.Contains("OwnerId") && !request.Body.Contains("EnvironmentVariables") && !request.Body.Contains("\"tools\""),
                    "Assistant leaked fields or accepted a client-provided provider");
                using var chatRequest = JsonDocument.Parse(request.Body!);
                Assert(chatRequest.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() == 64,
                    "Assistant ignored the operator's response token limit");
                Assert(fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count == dockerRequests, "Assistant contacted Docker");
            });
            await Check("Assistant runtime context is optional, scoped to the server and degrades when telemetry is unavailable", async () =>
            {
                var path = $"/api/servers/{fixture.SecondServerId}/assistant";
                var body = new { question = "Quanto de CPU o jogo está usando?", includeRuntime = true };
                var otherNode = fixture.FirstNode.Requests.Count;
                var before = fixture.SecondNode.Requests.Count;
                using var live = await owner.PostAsJsonAsync(path, body);
                using var liveMetadata = await ReadMetadata(live, included: true);
                var runtime = liveMetadata.RootElement.GetProperty("Runtime");
                Assert(runtime.GetProperty("MemoryUsageBytes").GetInt64() == 700 &&
                    runtime.GetProperty("CpuUsagePercent").GetDouble() == 20 &&
                    runtime.GetProperty("Status").GetString() == "Running" &&
                    runtime.GetProperty("SampledAt").GetDateTime() <= DateTime.UtcNow,
                    "The displayed runtime sample was unavailable, stale or did not use measured resource values");
                Assert(fixture.FirstNode.Requests.Count == otherNode &&
                    fixture.SecondNode.Requests.Skip(before).Count(call => call.Path.EndsWith("/second/stats")) == 1 &&
                    !runtime.TryGetProperty("NetworkRxBytes", out _) && !runtime.TryGetProperty("DiskUsage", out _),
                    "The assistant accessed another node or forwarded unrelated telemetry");

                fixture.SecondNode.RuntimeOverrides["second"] = new()
                {
                    Status = "exited", Running = false, ExitCode = 137, OOMKilled = true
                };
                try
                {
                    var statsCalls = fixture.SecondNode.Requests.Count(call => call.Path.EndsWith("/stats"));
                    using var exited = await owner.PostAsJsonAsync(path, body);
                    using var exitedMetadata = await ReadMetadata(exited, included: true);
                    var stopped = exitedMetadata.RootElement.GetProperty("Runtime");
                    Assert(stopped.GetProperty("ExitCode").GetInt64() == 137 && stopped.GetProperty("OomKilled").GetBoolean() &&
                        stopped.GetProperty("MemoryUsageBytes").ValueKind == JsonValueKind.Null &&
                        fixture.SecondNode.Requests.Count(call => call.Path.EndsWith("/stats")) == statsCalls,
                        "An exited instance was presented as a live sample or its exit information was lost");
                }
                finally { fixture.SecondNode.RuntimeOverrides.TryRemove("second", out _); }

                fixture.SecondNode.WrongOwnership = true;
                try
                {
                    using var unavailable = await owner.PostAsJsonAsync(path, body);
                    using var unavailableMetadata = await ReadMetadata(unavailable, included: false);
                    Assert(unavailableMetadata.RootElement.GetProperty("Runtime").ValueKind == JsonValueKind.Null,
                        "Unverified telemetry reached the assistant");
                }
                finally { fixture.SecondNode.WrongOwnership = false; }

                async Task<JsonDocument> ReadMetadata(HttpResponseMessage response, bool included)
                {
                    Assert(response.IsSuccessStatusCode, "The assistant did not return a usable answer");
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                    Assert(result.GetProperty("runtimeIncluded").GetBoolean() == included, "The answer reported the wrong telemetry state");
                    Assert(fixture.AssistantRequests.TryDequeue(out var request), "The local provider was not called");
                    using var payload = JsonDocument.Parse(request.Body!);
                    var prompt = payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                    var begin = prompt.IndexOf('\n') + 1;
                    var end = prompt.IndexOf("\n\nQuestion:\n", StringComparison.Ordinal);
                    return JsonDocument.Parse(prompt[begin..end]);
                }
            });
            await Check("Pterodactyl migration preserves ownership and releases only a verified, stopped destination", async () =>
            {
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var enrollment = scope.ServiceProvider.GetRequiredService<DockerEnrollment>();
                var sourceId = Guid.NewGuid();
                var json = $$"""
                    {"schema_version":1,"servers":[{"server_uuid":"{{sourceId}}","owner_uuid":"{{fixture.OwnerId}}",
                    "target_node_id":"{{fixture.NodeId}}","identifier":"fedc0012","name":"Staged world","description":null,
                    "approved_image":"audit/synthetic:secure","memory":2048,"cpu":100,"disk":4096,"port":25566,"secondary_ports":[25567],
                    "source_threads":"0-1","target_cpu_cores":[3,1],
                    "cartridge_id":"minecraft-java","variables":{"MOTD":"Imported world"},"named_ports":{"query":25567} }]}
                    """;
                async Task<PterodactylServerImport.Report> Import(string manifest, bool apply)
                {
                    using var data = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
                    var cartridges = scope.ServiceProvider.GetRequiredService<ICartridgeService>();
                    return await PterodactylServerImport.RunAsync(db, enrollment, cartridges, data, apply);
                }
                var before = fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count;
                Assert(await Import(json, false) is { Pending: 1, Imported: 0 }, "Server staging plan failed");
                Assert(await Import(json.Replace("[3,1]", "null"), true) is { Imported: 0, Issues: [{ Reason: "destination_cpu_affinity_required" }] },
                    "Server staging dropped source CPU affinity");
                Assert(await Import(json.Replace("[3,1]", "[0]"), true) is { Imported: 0, Issues: [{ Reason: "destination_cpu_affinity_invalid" }] },
                    "Server staging accepted a logical core outside the enrolled node set");
                Assert(await Import(json.Replace("\"MOTD\"", "\"UNAPPROVED_VARIABLE\""), true) is
                    { Imported: 0, Issues: [{ Reason: "destination_variables_invalid" }] }, "Server staging accepted an unapproved environment variable");
                Assert(await Import(json.Replace("\"query\":25567", "\"query\":27777"), true) is
                    { Imported: 0, Issues: [{ Reason: "destination_named_ports_invalid" }] }, "Server staging accepted an unreserved query port");
                Assert(await Import(json.Replace("25566", "25565"), true) is { Imported: 0, Issues: [{ Reason: "destination_port_reserved" }] },
                    "Server staging accepted a reserved port");
                Assert(await Import(json.Replace("25567", "25565"), true) is { Imported: 0, Issues: [{ Reason: "destination_port_reserved" }] },
                    "Server staging accepted a secondary port used by another server");
                Assert(!await db.Servers.AnyAsync(s => s.Id == sourceId), "Plan or conflict wrote a server");
                Assert(await Import(json, true) is { Imported: 1 }, "Server staging apply failed");
                Assert(await Import(json, true) is { Imported: 0, AlreadyPresent: 1 }, "Server staging was not idempotent");
                var record = await db.Servers.AsNoTracking().SingleAsync(s => s.Id == sourceId);
                Assert(record.IsSuspended && record.Status == ServerStatus.Suspended && record.ContainerId is null &&
                    record.OwnerId == fixture.OwnerId && record.NodeId == fixture.NodeId,
                    "Staging lost server ownership or made the server runnable");
                Assert(await db.Allocations.AnyAsync(a => a.ServerId == sourceId && a.Port == 25567 && a.IsAssigned),
                    "The secondary port was not reserved");
                using var secondaryConflict = await admin.PostAsJsonAsync("/api/servers", new
                {
                    name = "Conflicting server", nodeId = fixture.NodeId, dockerImage = "audit/synthetic:secure", port = 25567
                });
                Assert(secondaryConflict.StatusCode == HttpStatusCode.BadRequest, "Normal provisioning took a migration reservation");
                Assert(fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count == before, "Staging contacted Docker");
                const string migrationsRoute = "/api/admin/migrations/servers";
                var migrationSearch = $"{migrationsRoute}?page=1&pageSize=1&query={sourceId:D}";
                async Task<JsonElement> GetMigrationAsync(string stage)
                {
                    using var listing = await admin.GetAsync(migrationSearch);
                    Assert(listing.IsSuccessStatusCode && listing.Headers.GetValues("X-Total-Count").Single() == "1" &&
                        listing.Headers.GetValues("X-Page-Size").Single() == "1", "Migration search did not return pagination");
                    var item = (await listing.Content.ReadFromJsonAsync<JsonElement[]>())!.Single();
                    Assert(item.GetProperty("id").GetGuid() == sourceId && item.GetProperty("stage").GetString() == stage &&
                        item.GetProperty("nodeName").GetString() == "First", "Administrative migration status differed from the stored state");
                    return item;
                }
                using var forbiddenList = await owner.GetAsync(migrationsRoute);
                using var anonymousList = await anonymous.GetAsync(migrationsRoute);
                using var ordinaryServer = await admin.GetAsync($"{migrationsRoute}?query={fixture.ServerId:D}");
                Assert(forbiddenList.StatusCode == HttpStatusCode.Forbidden && anonymousList.StatusCode == HttpStatusCode.Unauthorized &&
                    (await ordinaryServer.Content.ReadFromJsonAsync<JsonElement[]>()) is { Length: 0 }, "Migration listing leaked data or included a nonmigrated server");
                await GetMigrationAsync("AwaitingVerification");
                Assert(fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count == before, "Migration listing contacted Docker");
                var digest = new string('A', 64);
                var sourceContainer = new string('C', 64);
                var stoppedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
                object Verification(string sourceDigest, string? destinationDigest = null, bool matches = true,
                    int version = 2, DateTimeOffset? destinationAt = null, string? observedContainer = null,
                    DateTimeOffset? observedStop = null, DateTimeOffset? sourceAt = null) => new
                {
                    mode = "verify_volume", matches,
                    source = new
                    {
                        schema_version = version, server_id = sourceId, files = 5, directories = 2, bytes = 1234,
                        tree_sha256 = sourceDigest, captured_at = sourceAt ?? stoppedAt.AddMinutes(5),
                        source_process = new { container_id = observedContainer ?? sourceContainer, stopped_at = observedStop ?? stoppedAt }
                    },
                    destination = new
                    {
                        schema_version = version, server_id = sourceId, files = 5, directories = 2, bytes = 1234,
                        tree_sha256 = destinationDigest ?? sourceDigest, captured_at = destinationAt ?? DateTimeOffset.UtcNow
                    }
                };
                object Fence(string? container = null, DateTimeOffset? stopped = null, DateTimeOffset? observed = null,
                    bool fenced = true, bool omitDestination = false, int version = 2, object? reverified = null) => new
                {
                    schema_version = version, server_id = sourceId, tree_sha256 = digest, writes_fenced = fenced,
                    source_process = new { container_id = container ?? sourceContainer, stopped_at = stopped ?? stoppedAt },
                    observed_at = observed ?? DateTimeOffset.UtcNow,
                    destination_verification = omitDestination ? null : reverified ?? Verification(digest, observedContainer: container, observedStop: stopped)
                };
                var prepare = $"/api/admin/migrations/servers/{sourceId}/prepare-restored";
                using var denied = await owner.PostAsJsonAsync(prepare, Verification(digest));
                Assert(denied.StatusCode == HttpStatusCode.Forbidden, "Owner approved cutover without an administrator");
                using var invalid = await admin.PostAsJsonAsync(prepare, Verification(digest, matches: false));
                using var altered = await admin.PostAsJsonAsync(prepare, Verification(digest, new string('B', 64)));
                using var noPermissions = await admin.PostAsJsonAsync(prepare, Verification(digest, version: 1));
                using var shortApproval = await admin.PostAsJsonAsync(prepare, new { serverId = sourceId, matches = true, treeSha256 = digest });
                Assert(invalid.StatusCode == HttpStatusCode.BadRequest && altered.StatusCode == HttpStatusCode.BadRequest &&
                    noPermissions.StatusCode == HttpStatusCode.BadRequest && shortApproval.StatusCode == HttpStatusCode.BadRequest,
                    "A changed, incomplete or legacy report could prepare a new migration");
                fixture.FirstNode.SeedRestoredVolume(sourceId, 4096, digest);
                var beforePrepare = fixture.FirstNode.Requests.Count;
                fixture.FirstNode.RestoreUnsupported = true;
                using var unsupported = await admin.PostAsJsonAsync(prepare, Verification(digest));
                fixture.FirstNode.RestoreUnsupported = false;
                Assert(unsupported.StatusCode == HttpStatusCode.Conflict, "Image without restore support could mount a restored volume");
                using var wrongDigest = await admin.PostAsJsonAsync(prepare, Verification(new string('B', 64)));
                Assert(wrongDigest.StatusCode == HttpStatusCode.Conflict, "Restore accepted a conflicting volume digest");
                using var prepared = await admin.PostAsJsonAsync(prepare, Verification(digest));
                Assert(prepared.StatusCode == HttpStatusCode.NoContent, "Restored container was not prepared");
                using var retried = await admin.PostAsJsonAsync(prepare, Verification(digest));
                Assert(retried.StatusCode == HttpStatusCode.NoContent, "Completed restore was not idempotent");
                var displayed = await GetMigrationAsync("Prepared");
                Assert(displayed.GetProperty("volumeSha256").GetString() == digest &&
                    displayed.GetProperty("releasedAt").ValueKind == JsonValueKind.Null, "The prepared inventory displayed incorrect evidence");
                var finalized = await db.Servers.AsNoTracking().SingleAsync(s => s.Id == sourceId);
                Assert(finalized.Status == ServerStatus.Suspended && finalized.IsSuspended && !finalized.MigrationPending &&
                    finalized.ContainerId != null && finalized.RestoredVolumeSha256 == digest, "A finalized server was not left suspended");
                var dockerRequests = fixture.FirstNode.Requests.Skip(beforePrepare).ToArray();
                Assert(dockerRequests.Count(r => r.Path.EndsWith("/containers/create")) == 1 &&
                    !dockerRequests.Any(r => r.Path.EndsWith("/volumes/create") || r.Path.EndsWith("/start")),
                    "Restore started a server or created a replacement volume");
                using var container = JsonDocument.Parse(dockerRequests.Single(r => r.Path.EndsWith("/containers/create")).Body);
                var hostConfig = container.RootElement.GetProperty("HostConfig");
                var ports = hostConfig.GetProperty("PortBindings");
                Assert(ports.TryGetProperty("25567/tcp", out _) && ports.TryGetProperty("25567/udp", out _),
                    "Restored container was missing its secondary bindings");
                Assert(hostConfig.GetProperty("CpusetCpus").GetString() == "1,3", "Restored container lost its approved CPU affinity");
                Assert(container.RootElement.GetProperty("Env").EnumerateArray().Any(value => value.GetString() == "MOTD=Imported world"),
                    "Restored container lost its mapped configuration");
                Assert(container.RootElement.GetProperty("Env").EnumerateArray().Any(value => value.GetString() == "QUERY_PORT=25567"),
                    "Restored server listens on a different port than the published secondary port");
                Assert(container.RootElement.GetProperty("Env").EnumerateArray().Any(value => value.GetString() == "ARCADENODE_RESTORED_VOLUME=true"),
                    "Restored container lost its explicit restore mode");
                Assert(container.RootElement.GetProperty("Env").EnumerateArray().Any(value => value.GetString() == "ARCADENODE_PUBLISHED_PORTS=25566,25567"),
                    "Restored container did not receive its published port list");
                using var bypass = await admin.PostAsync($"/api/servers/{sourceId}/suspend?suspended=false", null);
                using var prematureStart = await owner.PostAsJsonAsync($"/api/servers/{sourceId}/power", new { action = "start" });
                Assert(!bypass.IsSuccessStatusCode && !prematureStart.IsSuccessStatusCode,
                    "The prepared destination became runnable before confirmation of source shutdown");
                var release = $"/api/admin/migrations/servers/{sourceId}/release-prepared";
                using var missingFence = await admin.PostAsJsonAsync(release, Fence(fenced: false));
                using var legacyFence = await admin.PostAsJsonAsync(release, Fence(version: 1));
                using var missingDestination = await admin.PostAsJsonAsync(release, Fence(omitDestination: true));
                using var destinationOld = await admin.PostAsJsonAsync(release,
                    Fence(reverified: Verification(digest, destinationAt: DateTimeOffset.UtcNow.AddMinutes(-20))));
                using var destinationDrift = await admin.PostAsJsonAsync(release, Fence(reverified: Verification(digest, new string('B', 64))));
                using var inconsistentSource = await admin.PostAsJsonAsync(release,
                    Fence(reverified: Verification(digest, observedContainer: new string('D', 64))));
                using var outdated = await admin.PostAsJsonAsync(release, Fence(observed: DateTimeOffset.UtcNow.AddMinutes(-20)));
                using var newerStop = await admin.PostAsJsonAsync(release, Fence(stopped: stoppedAt.AddMinutes(1)));
                using var replacement = await admin.PostAsJsonAsync(release, Fence(container: new string('D', 64)));
                using var liveDestination = await admin.PostAsJsonAsync(release, Fence());
                Assert(missingFence.StatusCode == HttpStatusCode.BadRequest && legacyFence.StatusCode == HttpStatusCode.BadRequest &&
                    missingDestination.StatusCode == HttpStatusCode.BadRequest && destinationOld.StatusCode == HttpStatusCode.BadRequest &&
                    destinationDrift.StatusCode == HttpStatusCode.BadRequest && inconsistentSource.StatusCode == HttpStatusCode.BadRequest &&
                    outdated.StatusCode == HttpStatusCode.BadRequest &&
                    newerStop.StatusCode == HttpStatusCode.Conflict && replacement.StatusCode == HttpStatusCode.Conflict &&
                    liveDestination.StatusCode == HttpStatusCode.Conflict, "Release accepted stale, conflicting or running migration evidence");

                var newDigest = new string('E', 64);
                using var refreshLive = await admin.PostAsJsonAsync(prepare, Verification(newDigest, sourceAt: DateTimeOffset.UtcNow));
                fixture.FirstNode.ReportStopped = true;
                using var refreshStale = await admin.PostAsJsonAsync(prepare, Verification(newDigest));
                using var refresh = await admin.PostAsJsonAsync(prepare, Verification(newDigest, sourceAt: DateTimeOffset.UtcNow));
                using var oldRelease = await admin.PostAsJsonAsync(release, Fence());
                var reverified = await GetMigrationAsync("Prepared");
                Assert(refreshLive.StatusCode == HttpStatusCode.Conflict && refreshStale.StatusCode == HttpStatusCode.Conflict &&
                    refresh.StatusCode == HttpStatusCode.NoContent && oldRelease.StatusCode == HttpStatusCode.Conflict &&
                    reverified.GetProperty("volumeSha256").GetString() == newDigest &&
                    fixture.FirstNode.Requests.Skip(beforePrepare).Count(r => r.Path.EndsWith("/containers/create")) == 1,
                    "Refreshing the prepared digest did not invalidate the old report or attempted to replace the container");
                digest = newDigest;
                var refreshedFence = Fence(reverified: Verification(digest, sourceAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
                using var released = await admin.PostAsJsonAsync(release, refreshedFence);
                fixture.FirstNode.ReportStopped = false;
                using var releaseAgain = await admin.PostAsJsonAsync(release, refreshedFence);
                var displayedRelease = await GetMigrationAsync("Released");
                Assert(displayedRelease.GetProperty("releasedAt").GetDateTime().Kind == DateTimeKind.Utc, "Release time lacks its UTC offset");
                var cutover = await db.Servers.AsNoTracking().SingleAsync(s => s.Id == sourceId);
                Assert(released.StatusCode == HttpStatusCode.NoContent && releaseAgain.StatusCode == HttpStatusCode.NoContent &&
                    cutover is { IsSuspended: false, Status: ServerStatus.Stopped, MigrationReleasedAt: not null } &&
                    cutover.MigrationReleasedById == fixture.AdminId && cutover.MigrationSourceStoppedAt == stoppedAt.UtcDateTime &&
                    !fixture.FirstNode.Requests.Skip(beforePrepare).Any(r => r.Path.EndsWith("/start")),
                    "Cutover did not release a stopped destination with a durable operator receipt");
                using var deleted = await admin.DeleteAsync($"/api/servers/{sourceId}");
                Assert(deleted.StatusCode == HttpStatusCode.NoContent &&
                    await db.Allocations.AnyAsync(a => a.NodeId == fixture.NodeId && a.Port == 25567 && a.ServerId == null && !a.IsAssigned),
                    "Deleting the server did not release the secondary port");
            });
            await Check("Preparing and releasing Zomboid require the restored save name and exact configured ports", async () =>
            {
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var enrollment = scope.ServiceProvider.GetRequiredService<DockerEnrollment>();
                var cartridges = scope.ServiceProvider.GetRequiredService<ICartridgeService>();
                var id = Guid.NewGuid();
                using var manifest = new MemoryStream(Encoding.UTF8.GetBytes($$"""
                    {"schema_version":1,"servers":[{"server_uuid":"{{id}}","owner_uuid":"{{fixture.OwnerId}}",
                    "target_node_id":"{{fixture.NodeId}}","identifier":"faed0042","name":"Zomboid cutover","description":null,
                    "approved_image":"audit/synthetic-zomboid:secure","memory":4096,"cpu":100,"disk":4096,
                    "port":27861,"secondary_ports":[27862],"cartridge_id":"project-zomboid",
                    "variables":{"SERVER_NAME":"Fort West"},"named_ports":{"steam":27862} }]}
                    """));
                Assert(await PterodactylServerImport.RunAsync(db, enrollment, cartridges, manifest, apply: true) is { Imported: 1 },
                    "Could not stage the Zomboid cutover fixture");

                var digest = new string('D', 64);
                var observed = DateTimeOffset.UtcNow;
                var process = new { container_id = new string('E', 64), stopped_at = observed.AddMinutes(-2) };
                object Settings(string name = "Fort West", int direct = 27862, int rcon = 27015, string layout = "pterodactyl") =>
                    new { layout, save_name = name, game_port = 27861, direct_port = direct, rcon_port = rcon };
                object Verification(object? settings, DateTimeOffset capturedAt, int? destinationLinks = 2) => new
                {
                    mode = "verify_volume", matches = true, project_zomboid = settings,
                    source = new { schema_version = 3, server_id = id, files = 6, directories = 7, bytes = 128, links = 2,
                        tree_sha256 = digest, captured_at = observed, source_process = process },
                    destination = new { schema_version = 3, server_id = id, files = 6, directories = 7, bytes = 128, links = destinationLinks,
                        tree_sha256 = digest, captured_at = capturedAt }
                };
                object Fence(object? settings) => new
                {
                    schema_version = 2, server_id = id, tree_sha256 = digest, source_process = process,
                    writes_fenced = true, observed_at = DateTimeOffset.UtcNow,
                    destination_verification = Verification(settings, DateTimeOffset.UtcNow)
                };

                var route = $"/api/admin/migrations/servers/{id:D}";
                fixture.FirstNode.SeedRestoredVolume(id, 4096, digest);
                var dockerCalls = fixture.FirstNode.Requests.Count;
                foreach (var settings in new[] { null, Settings(name: "Fort_West"), Settings(direct: 16262),
                    Settings(rcon: 27861), Settings(layout: "unknown") })
                {
                    using var rejected = await admin.PostAsJsonAsync(route + "/prepare-restored", Verification(settings, observed));
                    Assert(rejected.StatusCode == HttpStatusCode.BadRequest, "Preparation accepted missing or conflicting Zomboid evidence");
                }
                foreach (var links in new int?[] { null, -1, 1 })
                {
                    using var rejected = await admin.PostAsJsonAsync(route + "/prepare-restored", Verification(Settings(), observed, links));
                    Assert(rejected.StatusCode == HttpStatusCode.BadRequest, "Preparation accepted an invalid volume link count");
                }
                Assert(fixture.FirstNode.Requests.Count == dockerCalls, "Invalid Zomboid evidence reached Docker");
                using var prepared = await admin.PostAsJsonAsync(route + "/prepare-restored", Verification(Settings(), observed));
                Assert(prepared.StatusCode == HttpStatusCode.NoContent, "Matching Zomboid evidence failed to prepare the restore");
                var create = fixture.FirstNode.Requests.Skip(dockerCalls).Single(request => request.Path.EndsWith("/containers/create"));
                using var container = JsonDocument.Parse(create.Body);
                var bindings = container.RootElement.GetProperty("HostConfig").GetProperty("PortBindings");
                Assert(bindings.TryGetProperty("27862/udp", out _) && !bindings.TryGetProperty("27015/tcp", out _),
                    "Cutover dropped the direct connection port or published private RCON");

                fixture.FirstNode.ReportStopped = true;
                try
                {
                    using var missing = await admin.PostAsJsonAsync(route + "/release-prepared", Fence(null));
                    using var mismatch = await admin.PostAsJsonAsync(route + "/release-prepared", Fence(Settings(name: "Elsewhere")));
                    Assert(missing.StatusCode == HttpStatusCode.BadRequest && mismatch.StatusCode == HttpStatusCode.BadRequest,
                        "Release accepted missing or conflicting Zomboid evidence");
                    using var released = await admin.PostAsJsonAsync(route + "/release-prepared", Fence(Settings()));
                    Assert(released.StatusCode == HttpStatusCode.NoContent &&
                        await db.Servers.AsNoTracking().AnyAsync(server => server.Id == id && server.MigrationReleasedAt != null && !server.IsSuspended),
                        "The verified Zomboid destination was not released");
                }
                finally { fixture.FirstNode.ReportStopped = false; }
                using var deleted = await admin.DeleteAsync($"/api/servers/{id:D}");
                Assert(deleted.StatusCode == HttpStatusCode.NoContent, "Could not clean up the Zomboid cutover fixture");
            });
            await Check("Zomboid volume settings preserve valid direct and private RCON ports and reject inconsistent inputs", () =>
            {
                const string ports = "DefaultPort=16261\r\nUDPPort=16262\r\n";
                var disabled = ZomboidVolume.ReadSettings("pterodactyl", "Fort", ports + "RCONPassword=\r\n");
                Assert(disabled is { SaveName: "Fort", GamePort: 16261, DirectPort: 16262, RconPort: null },
                    "The disabled RCON setting was parsed incorrectly");
                var enabled = ZomboidVolume.ReadSettings("arcadenode", "Fort", ports + "RCONPort=27015\r\nRCONPassword=synthetic-passphrase\r\n");
                Assert(enabled.RconPort == 27015, "The verified RCON port was lost");
                AssertThrows<InvalidDataException>(() => ZomboidVolume.ReadSettings("pterodactyl", "Fort", ports + "UDPPort=16263"));
                AssertThrows<InvalidDataException>(() => ZomboidVolume.ReadSettings("pterodactyl", "Fort", "DefaultPort=16261\nUDPPort=16261"));
                AssertThrows<InvalidDataException>(() => ZomboidVolume.ReadSettings("pterodactyl", "Fort", ports + "RCONPort=27015\nRCONPassword=changeme"));
                AssertThrows<InvalidDataException>(() => ZomboidVolume.ReadSettings("pterodactyl", "Fort", ports + "RCONPassword=synthetic-passphrase"));
                return Task.CompletedTask;
            });
            await Check("Pterodactyl server settings preserve per-server values and the approved egg while staging", async () =>
            {
                using var nest = await admin.PostAsJsonAsync("/api/admin/egg-catalog/nests", new { name = "Migration settings fixtures" });
                var nestId = (await nest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                using var eggJson = JsonDocument.Parse("""
                    {"meta":{"version":"PTDL_v2"},"name":"Staging settings egg","docker_images":{"Fixture":"example.test/yolk:java_21"},
                    "startup":"untrusted source startup","variables":[
                       {"name":"Game version","env_variable":"VANILLA_VERSION","default_value":"latest","user_viewable":true,"user_editable":true},
                       {"name":"Server binary","env_variable":"SERVER_JARFILE","default_value":"server.jar","user_viewable":true,"user_editable":false},
                       {"name":"Private setting","env_variable":"SOURCE_PASSWORD","default_value":"","user_viewable":false,"user_editable":false}
                    ]}
                    """);
                using var add = await admin.PostAsJsonAsync($"/api/admin/egg-catalog/nests/{nestId}/eggs/import", eggJson.RootElement);
                Assert(add.StatusCode == HttpStatusCode.Created, "Migration egg fixture could not be imported");
                var imported = await add.Content.ReadFromJsonAsync<JsonElement>();
                var eggId = imported.GetProperty("id").GetGuid();
                var digest = imported.GetProperty("sha256").GetString();
                var configuration = fixture.Services.GetRequiredService<IConfiguration>();
                var profile = $"EggRuntimes:{digest}";
                configuration[$"{profile}:CartridgeId"] = "minecraft-java";
                configuration[$"{profile}:FixedValues:SERVER_TYPE"] = "vanilla";
                configuration[$"{profile}:VariableMap:VANILLA_VERSION"] = "MC_VERSION";
                configuration[$"{profile}:VariableMap:SERVER_JARFILE"] = "SERVER_JAR";
                configuration[$"{profile}:VariableMap:SOURCE_PASSWORD"] = "RCON_PASSWORD";

                var firstId = Guid.NewGuid();
                var secondId = Guid.NewGuid();
                const string image = "example.test/yolk:java_21";
                const string firstSecret = "synthetic-source-one-value";
                const string secondSecret = "synthetic-source-two-value";
                var firstValues = new Dictionary<string, string> { ["VANILLA_VERSION"] = "1.21.1", ["SERVER_JARFILE"] = "first.jar", ["SOURCE_PASSWORD"] = firstSecret };
                var secondValues = new Dictionary<string, string> { ["VANILLA_VERSION"] = "1.21.4", ["SERVER_JARFILE"] = "second.jar", ["SOURCE_PASSWORD"] = secondSecret };
                object Server(Guid id, int port, Dictionary<string, string> saved, string sourceImage = image, Guid targetNodeId = default,
                    Dictionary<string, string>? overrides = null) => new
                {
                    server_uuid = id, owner_uuid = fixture.OwnerId, target_node_id = targetNodeId,
                    identifier = id.ToString("N")[..8], name = $"Source {port}", description = "Synthetic", approved_image = (string?)null,
                    source_node_id = 8, source_egg_id = 19, source_image = sourceImage, memory = 512, cpu = 100, disk = 1024, port,
                    secondary_ports = Array.Empty<int>(), source_variables = saved, variables = overrides
                };
                object Plan(Guid targetEgg) => new
                {
                    schema_version = 1, nodes = new[] { new { source_node_id = 8, target_node_id = fixture.NodeId } },
                    profiles = new[] { new { source_egg_id = 19, source_image = image, target_egg_id = targetEgg,
                        source_variables = new[] { "VANILLA_VERSION", "SERVER_JARFILE", "SOURCE_PASSWORD" } } }
                };
                var first = Server(firstId, 28570, firstValues);
                var second = Server(secondId, 28571, secondValues, targetNodeId: fixture.SecondNodeId,
                    overrides: new() { ["MOTD"] = "Second saved world" });

                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var enrollment = scope.ServiceProvider.GetRequiredService<DockerEnrollment>();
                var cartridges = scope.ServiceProvider.GetRequiredService<ICartridgeService>();
                var runtimes = scope.ServiceProvider.GetRequiredService<EggRuntimeProfiles>();
                async Task<PterodactylServerImport.Report> Run(object firstServer, object secondServer, bool apply, object? plan = null)
                {
                    using var manifest = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 2, servers = new[] { firstServer, secondServer } }));
                    using var mappings = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(plan ?? Plan(eggId)));
                    return await PterodactylServerImport.RunAsync(db, enrollment, cartridges, manifest, apply, mappingInput: mappings, eggRuntimes: runtimes);
                }

                var before = fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count;
                var plan = await Run(first, second, false);
                Assert(plan is { Pending: 2, Imported: 0, Placements.Count: 2, Issues.Count: 0 } &&
                    plan.Placements[0] == new PterodactylServerImport.Placement(firstId, fixture.NodeId, eggId, 3) &&
                    plan.Placements[1] == new PterodactylServerImport.Placement(secondId, fixture.SecondNodeId, eggId, 3) &&
                    !JsonSerializer.Serialize(plan).Contains(firstSecret), "The preview lost a placement or exposed a saved value");
                Assert(await Run(first, Server(secondId, 28571, secondValues, sourceImage: image.ToUpperInvariant()), true) is
                    { Imported: 0, Issues: [{ Reason: "source_egg_or_image_mapping_missing" }] }, "A different source image matched the plan");
                Assert(await Run(first, Server(secondId, 28571, new(secondValues) { ["UNSELECTED"] = "new" }), true) is
                    { Imported: 0, Issues: [{ Reason: "source_variables_missing_or_unexpected" }] }, "Unexpected settings did not block the batch");
                var incomplete = new Dictionary<string, string>(secondValues);
                incomplete.Remove("SERVER_JARFILE");
                Assert(await Run(first, Server(secondId, 28571, incomplete), true) is
                    { Imported: 0, Issues: [{ Reason: "source_variables_missing_or_unexpected" }] }, "Missing settings did not block the batch");
                Assert(await Run(first, Server(secondId, 28571, secondValues, overrides: new() { ["mc_version"] = "latest" }), true) is
                    { Imported: 0, Issues: [{ Reason: "destination_egg_variables_invalid" }] }, "A destination override replaced an approved source value");
                Assert(await Run(first, second, true, Plan(Guid.NewGuid())) is
                    { Imported: 0, Issues: [{ Reason: "destination_egg_runtime_unapproved" }, { Reason: "destination_egg_runtime_unapproved" }] },
                    "An unapproved destination egg was eligible for staging");
                Assert(!await db.Servers.AnyAsync(row => row.Id == firstId || row.Id == secondId), "A failed batch or preview committed partial changes");
                Assert(await Run(first, second, true) is { Imported: 2 } && await Run(first, second, true) is { Imported: 0, AlreadyPresent: 2 },
                    "The valid settings import was not atomic and idempotent");

                var staged = await db.Servers.AsNoTracking().Where(s => s.Id == firstId || s.Id == secondId).ToDictionaryAsync(s => s.Id);
                foreach (var (id, saved, targetNode) in new[] { (firstId, firstValues, fixture.NodeId), (secondId, secondValues, fixture.SecondNodeId) })
                {
                    var server = staged[id];
                    var env = JsonSerializer.Deserialize<Dictionary<string, string>>(server.EnvironmentVariables!)!;
                    Assert(server is { IsSuspended: true, MigrationPending: true, ContainerId: null, StartupCommand: null } &&
                        server.OwnerId == fixture.OwnerId && server.NodeId == targetNode && server.EggId == eggId &&
                        env["MC_VERSION"] == saved["VANILLA_VERSION"] && env["SERVER_JAR"] == saved["SERVER_JARFILE"] &&
                        env["RCON_PASSWORD"] == saved["SOURCE_PASSWORD"] && env["SERVER_TYPE"] == "vanilla",
                        "Staging lost the owner, approved profile or individual settings of one server");
                    if (id == secondId) Assert(env["MOTD"] == "Second saved world", "An independent destination variable was dropped");
                }
                Assert(fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count == before, "The migration planner contacted Docker");
                await db.Servers.Where(row => row.Id == firstId || row.Id == secondId).ExecuteDeleteAsync();
            });
            await Check("Pterodactyl cutover rehashes a stopped source and detects data drift", async () =>
            {
                var root = Path.Combine(fixture.Root, "cutover-fenced-snapshot");
                var config = Path.Combine(root, "setup.cfg");
                var world = Directory.CreateDirectory(Path.Combine(root, "world"));
                await File.WriteAllTextAsync(config, "first world");
                var id = Guid.NewGuid();
                var process = new PterodactylSource.ProcessReceipt(new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(-2));
                var observation = new PterodactylSource.Observation(process, root);
                var version = OperatingSystem.IsLinux() ? 2 : 1;
                var receipt = (await VolumeVerification.CaptureAsync(id, root, version, default)) with { SourceProcess = process };
                var checks = 0;
                Task<PterodactylSource.Observation> Observe(Guid serverId, CancellationToken token)
                {
                    Assert(serverId == id, "Source confirmation observed another server");
                    checks++;
                    return Task.FromResult(observation);
                }
                var confirmation = await VolumeVerification.ConfirmSourceAsync(receipt, Observe, default);
                Assert(confirmation is { SchemaVersion: 2, WritesFenced: true } && confirmation.TreeSha256 == receipt.TreeSha256 &&
                    confirmation.ObservedAt >= receipt.CapturedAt && checks == 2, "Source confirmation did not bracket its file scan");

                async Task RejectChangeAsync(Func<Guid, CancellationToken, Task<PterodactylSource.Observation>> observe)
                {
                    try { await VolumeVerification.ConfirmSourceAsync(receipt, observe, default); }
                    catch (InvalidDataException) { return; }
                    throw new InvalidOperationException("Source confirmation accepted a changed snapshot or process.");
                }
                await File.WriteAllTextAsync(config, "other world");
                await RejectChangeAsync(Observe);
                await File.WriteAllTextAsync(config, "first world");
                checks = 0;
                await RejectChangeAsync((_, _) => Task.FromResult(++checks == 1 ? observation :
                    observation with { Process = process with { StoppedAt = process.StoppedAt?.AddSeconds(1) } }));
                if (OperatingSystem.IsLinux())
                {
                    var original = File.GetUnixFileMode(config);
                    File.SetUnixFileMode(config, original ^ UnixFileMode.UserExecute);
                    await RejectChangeAsync(Observe);
                    File.SetUnixFileMode(config, original);
                    File.SetUnixFileMode(world.FullName, File.GetUnixFileMode(world.FullName) ^ UnixFileMode.GroupWrite);
                    await RejectChangeAsync(Observe);
                }
            });
            await Check("Pterodactyl schedules import paused in task order and block unsupported batches", async () =>
            {
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var manifest = $$"""
                    {"schema_version":1,"schedules":[{"id":23,"server_uuid":"{{fixture.ServerId}}","name":"Imported schedule",
                    "cron_expression":"0 4 * * *","only_when_online":true,"was_active":true,"tasks":[
                    {"sequence_id":2,"action":"power","payload":"restart","time_offset":5,"continue_on_failure":false},
                    {"sequence_id":3,"action":"backup","payload":"logs/\n*.tmp","time_offset":10,"continue_on_failure":false},
                    {"sequence_id":4,"action":"power","payload":"kill","time_offset":15,"continue_on_failure":false},
                    {"sequence_id":1,"action":"command","payload":"save-all","time_offset":0,"continue_on_failure":true}]}]}
                    """;
                async Task<PterodactylScheduleImport.Report> Import(string value, bool apply)
                {
                    using var input = new MemoryStream(Encoding.UTF8.GetBytes(value));
                    return await PterodactylScheduleImport.RunAsync(db, input, apply);
                }
                Assert(await Import(manifest, false) is { Pending: 1, Imported: 0 }, "Schedule plan failed");
                Assert(await Import(manifest.Replace("logs/\\n*.tmp", "["), true) is
                    { Imported: 0, Issues: [{ Reason: "unsupported_or_duplicate_task" }] }, "A backup task with malformed exclusions was imported");
                Assert(await Import(manifest.Replace("\"action\":\"backup\"", "\"action\":\"unknown\""), true) is
                    { Imported: 0, Issues: [{ Reason: "unsupported_or_duplicate_task" }] }, "An unknown schedule action was treated as a backup");
                Assert(!await db.Schedules.AnyAsync(), "Schedule plan or blocked import wrote to database");
                Assert(await Import(manifest, true) is { Imported: 1 }, "Schedule apply failed");
                Assert(await Import(manifest, true) is { AlreadyPresent: 1, Imported: 0 }, "Duplicate schedule imported");
                var stored = await db.Schedules.AsNoTracking().Include(s => s.Tasks).SingleAsync();
                Assert(!stored.IsActive && stored.NextRunAt == null && stored.OnlyWhenOnline &&
                    stored.Tasks.OrderBy(t => t.SequenceId).Select(t => t.Payload).SequenceEqual(new[] { "save-all", "restart", "logs/\n*.tmp", "kill" }) &&
                    stored.Tasks.Single(task => task.SequenceId == 3).Action == TaskAction.Backup,
                    "Schedule lost order or was enabled before review");
                var route = $"/api/servers/{fixture.ServerId:D}/schedules";
                using var listing = await owner.GetAsync(route);
                var originalRevision = (await listing.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("revision").GetString();
                var edit = new
                {
                    name = "Revised import", cronExpression = "0 6 * * *", isActive = false, onlyWhenOnline = false,
                    tasks = new[]
                    {
                        new { action = "Power", payload = "stop", timeOffset = 0, continueOnFailure = false },
                        new { action = "Command", payload = "save-all", timeOffset = 5, continueOnFailure = true }
                    }
                };
                var target = $"{route}/{stored.Id:D}";
                async Task<HttpResponseMessage> SaveEditAsync(HttpClient caller)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, target) { Content = JsonContent.Create(edit) };
                    request.Headers.TryAddWithoutValidation("If-Match", $"\"{originalRevision}\"");
                    return await caller.SendAsync(request);
                }
                using var unsigned = await SaveEditAsync(anonymous);
                using var foreign = await SaveEditAsync(other);
                using var unversioned = await owner.PutAsJsonAsync(target, edit);
                Assert(unsigned.StatusCode == HttpStatusCode.Unauthorized && foreign.StatusCode == HttpStatusCode.Forbidden &&
                    (int)unversioned.StatusCode == 428, "A schedule edit accepted an unauthorized or unversioned request");
                var filter = db.Schedules.Where(s => s.Id == stored.Id);
                var lastRun = DateTime.UtcNow.AddHours(-1);
                await filter.ExecuteUpdateAsync(update => update.SetProperty(s => s.IsProcessing, true).SetProperty(s => s.LastRunAt, lastRun));
                using var busy = await SaveEditAsync(owner);
                Assert(busy.StatusCode == HttpStatusCode.Conflict, "An executing job could be replaced");
                await filter.ExecuteUpdateAsync(update => update.SetProperty(s => s.IsProcessing, false));
                var memberRoute = $"/api/servers/{fixture.ServerId:D}/members/{fixture.OtherId:D}";
                using var limited = await owner.PutAsJsonAsync(memberRoute, new { permissions = new[] { "schedule.update" } });
                using var missingTaskRights = await SaveEditAsync(other);
                Assert(limited.IsSuccessStatusCode && missingTaskRights.StatusCode == HttpStatusCode.Forbidden,
                    "Schedule access bypassed permission for a replacement task");
                using var allowed = await owner.PutAsJsonAsync(memberRoute,
                    new { permissions = new[] { "schedule.update", "control.stop", "control.console" } });
                using var saved = await SaveEditAsync(other);
                using var stale = await SaveEditAsync(owner);
                Assert(allowed.IsSuccessStatusCode && saved.StatusCode == HttpStatusCode.NoContent && stale.StatusCode == HttpStatusCode.PreconditionFailed,
                    "A valid edit failed or an older revision overwrote it");
                var edited = await filter.AsNoTracking().Include(s => s.Tasks).SingleAsync();
                Assert(edited is { Name: "Revised import", CronExpression: "0 6 * * *", IsActive: false,
                    IsProcessing: false, NextRunAt: null, OnlyWhenOnline: false } && edited.LastRunAt == lastRun &&
                    edited.DelegatedUserId == fixture.OtherId && edited.DelegatedMemberRevision.HasValue &&
                    edited.Tasks.OrderBy(task => task.SequenceId).Select(task => task.Payload).SequenceEqual(new[] { "stop", "save-all" }) &&
                    edited.Tasks.Single(task => task.SequenceId == 2) is { TimeOffset: 5, ContinueOnFailure: true },
                    "Editing a paused import lost its run history, revision binding or ordered tasks");
                using var revoked = await owner.DeleteAsync(memberRoute);
                Assert(revoked.StatusCode == HttpStatusCode.NoContent, "Could not remove the delegated fixture rights");
                await db.Schedules.Where(s => s.Id == stored.Id).ExecuteDeleteAsync();
            });
            await Check("Server schedules are owner-scoped and execute a due occurrence once", async () =>
            {
                var path = $"/api/servers/{fixture.ServerId}/schedules";
                static object SchedulePayload(string cron, string action, string payload) => new
                {
                    name = "Maintenance", cronExpression = cron, isActive = true, onlyWhenOnline = true,
                    tasks = new[] { new { action, payload, timeOffset = 0, continueOnFailure = false } }
                };
                using var denied = await other.PostAsJsonAsync(path, SchedulePayload("* * * * *", "Power", "restart"));
                Assert(denied.StatusCode == HttpStatusCode.Forbidden, "A foreign user could schedule restart");
                using var invalid = await owner.PostAsJsonAsync(path, SchedulePayload("* * * * *", "Power", "pause"));
                Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Unsupported action accepted");
                using var created = await owner.PostAsJsonAsync(path, SchedulePayload("0 0 1 * MON", "Power", "restart"));
                Assert(created.StatusCode == HttpStatusCode.Created, "Schedule was not created");
                var schedule = await created.Content.ReadFromJsonAsync<JsonElement>();
                var id = schedule.GetProperty("id").GetGuid();
                var dueAt = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
                using var paused = await owner.PutAsJsonAsync($"{path}/{id}/activity", new { isActive = false });
                Assert(paused.StatusCode == HttpStatusCode.NoContent, "Schedule did not pause");
                using var resumed = await owner.PutAsJsonAsync($"{path}/{id}/activity", new { isActive = true });
                Assert(resumed.StatusCode == HttpStatusCode.NoContent, "Schedule did not resume");

                var beforeCount = fixture.FirstNode.Requests.Count(r => r.Path.Contains("/restart"));
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                await db.Schedules.Where(s => s.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(s => s.NextRunAt, dueAt));
                Assert(await ScheduleWorker.RunOneAsync(scope.ServiceProvider, dueAt, CancellationToken.None), "Due schedule not executed");
                Assert(!await ScheduleWorker.RunOneAsync(scope.ServiceProvider, dueAt, CancellationToken.None), "Due schedule replayed");
                var persisted = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == id);
                Assert(persisted.NextRunAt == new DateTime(2026, 11, 30) && !persisted.IsProcessing,
                    "Cron did not honor the original OR semantics for day fields");
                Assert(fixture.FirstNode.Requests.Count(r => r.Path.Contains("/restart")) == beforeCount + 1,
                    "The scheduled restart did not use the assigned node exactly once");
                using var removed = await owner.DeleteAsync($"{path}/{id}");
                Assert(removed.StatusCode == HttpStatusCode.NoContent, "Schedule was not removed");

                using var forceStop = await owner.PostAsJsonAsync(path, SchedulePayload("* * * * *", "Power", "kill"));
                Assert(forceStop.StatusCode == HttpStatusCode.Created, "A legitimate force-stop schedule was refused");
                var forceStopId = (await forceStop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                var beforeKill = fixture.FirstNode.Requests.Count(r => r.Path.EndsWith("/kill", StringComparison.Ordinal));
                await db.Schedules.Where(s => s.Id == forceStopId).ExecuteUpdateAsync(setters => setters.SetProperty(s => s.NextRunAt, dueAt));
                Assert(await ScheduleWorker.RunOneAsync(scope.ServiceProvider, dueAt, CancellationToken.None) &&
                    fixture.FirstNode.Requests.Count(r => r.Path.EndsWith("/kill", StringComparison.Ordinal)) == beforeKill + 1 &&
                    await db.Servers.Where(s => s.Id == fixture.ServerId).Select(s => s.Status).SingleAsync() == ServerStatus.Stopped,
                    "Scheduled force stop did not send SIGKILL and persist the stopped state");
                using var removeForceStop = await owner.DeleteAsync($"{path}/{forceStopId}");
                Assert(removeForceStop.StatusCode == HttpStatusCode.NoContent, "Force-stop schedule was not removed");
                using var restoreRunningState = await owner.PostAsJsonAsync($"/api/servers/{fixture.ServerId}/power", new { action = "start" });
                Assert(restoreRunningState.IsSuccessStatusCode, "The server did not start after the scheduled force stop");
            });
            await Check("The file route checks server ownership and requires an enrolled file node", async () =>
            {
                var route = $"/api/servers/{fixture.ServerId}/files/download";
                using var unsigned = await anonymous.GetAsync(route + "?path=server.properties");
                using var foreign = await other.GetAsync(route + "?path=server.properties");
                using var unsafePath = await owner.GetAsync(route + "?path=..%2Fsettings");
                using var unavailable = await owner.GetAsync(route + "?path=server.properties");
                using var foreignListing = await other.GetAsync($"/api/servers/{fixture.ServerId}/files");
                using var listingTraversal = await owner.GetAsync($"/api/servers/{fixture.ServerId}/files?path=..%2Fsettings");
                using var unavailableListing = await owner.GetAsync($"/api/servers/{fixture.ServerId}/files");
                using var foreignStorage = await other.GetAsync($"/api/servers/{fixture.ServerId}/storage");
                using var unavailableStorage = await owner.GetAsync($"/api/servers/{fixture.ServerId}/storage");
                var uploadRoute = $"/api/servers/{fixture.ServerId}/files/upload?path=server.properties";
                static ByteArrayContent UploadContent()
                {
                    var body = new ByteArrayContent(Encoding.UTF8.GetBytes("synthetic"));
                    body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return body;
                }
                using var foreignUpload = await other.PostAsync(uploadRoute, UploadContent());
                using var unavailableUpload = await owner.PostAsync(uploadRoute, UploadContent());
                var editRoute = $"/api/servers/{fixture.ServerId}/files/text?path=server.properties";
                var edit = new { content = "motd=synthetic", sha256 = new string('A', 64) };
                using var foreignEdit = await other.PutAsJsonAsync(editRoute, edit);
                using var runningEdit = await owner.PutAsJsonAsync(editRoute, edit);
                Assert(unsigned.StatusCode == HttpStatusCode.Unauthorized && foreign.StatusCode == HttpStatusCode.Forbidden,
                    "File reads were allowed without ownership");
                Assert(unsafePath.StatusCode == HttpStatusCode.BadRequest, "File path crossed the game volume boundary");
                Assert(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "Download allowed without a file node registration");
                Assert(foreignListing.StatusCode == HttpStatusCode.Forbidden && listingTraversal.StatusCode == HttpStatusCode.BadRequest &&
                    unavailableListing.StatusCode == HttpStatusCode.ServiceUnavailable, "Listing bypassed file route protections");
                Assert(foreignStorage.StatusCode == HttpStatusCode.Forbidden && unavailableStorage.StatusCode == HttpStatusCode.ServiceUnavailable,
                    "Storage measurement bypassed server ownership or node enrollment");
                Assert(foreignUpload.StatusCode == HttpStatusCode.Forbidden && unavailableUpload.StatusCode == HttpStatusCode.ServiceUnavailable,
                    "Upload bypassed file route protections");
                Assert(foreignEdit.StatusCode == HttpStatusCode.Forbidden && runningEdit.StatusCode == HttpStatusCode.Conflict,
                    "A foreign user or running server reached the text editor");
            });
            await Check("Backups serialize requests and record receipts only for stopped, authorized servers", async () =>
            {
                var config = fixture.Services.GetRequiredService<IConfiguration>();
                var enrollment = fixture.Enrollment(fixture.SecondNode);
                var prefix = $"FileNodes:{fixture.SecondNodeId:D}";
                config[$"{prefix}:Endpoint"] = enrollment.Endpoint;
                config[$"{prefix}:ClientCertificatePath"] = enrollment.ClientCertificatePath;
                config[$"{prefix}:ClientKeyPath"] = enrollment.ClientKeyPath;
                config[$"{prefix}:ServerCertificateSha256"] = fixture.ServerCertificate.GetCertHashString(HashAlgorithmName.SHA256);

                var path = $"/api/servers/{fixture.SecondServerId}/backups";
                var input = new { name = "  Before update  ", exclusions = "logs/\r\n*.tmp" };
                using var unsigned = await anonymous.GetAsync(path);
                using var denied = await other.GetAsync(path);
                using var foreign = await other.PostAsJsonAsync(path, input);
                using var running = await owner.PostAsJsonAsync($"/api/servers/{fixture.ServerId}/backups", input);
                Assert(unsigned.StatusCode == HttpStatusCode.Unauthorized && denied.StatusCode == HttpStatusCode.Forbidden &&
                    foreign.StatusCode == HttpStatusCode.Forbidden && running.StatusCode == HttpStatusCode.Conflict,
                    "Backup access or stopped-state checks were bypassed");

                var requests = await Task.WhenAll(owner.PostAsJsonAsync(path, input), owner.PostAsJsonAsync(path, input));
                using var first = requests[0];
                using var second = requests[1];
                Assert(requests.Count(r => r.StatusCode == HttpStatusCode.Accepted) == 1 &&
                    requests.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, "Concurrent requests queued duplicate backups");
                var queued = await requests.Single(r => r.StatusCode == HttpStatusCode.Accepted).Content.ReadFromJsonAsync<JsonElement>();
                var id = queued.GetProperty("id").GetGuid();
                Assert(queued.GetProperty("isQueued").GetBoolean() && queued.GetProperty("name").GetString() == "Before update",
                    "Accepted backup had invalid metadata");

                fixture.SecondNode.ReportStopped = true;
                var scopes = fixture.Services.GetRequiredService<IServiceScopeFactory>();
                var processed = await Task.WhenAll(BackupWorker.RunOneAsync(scopes, CancellationToken.None),
                    BackupWorker.RunOneAsync(scopes, CancellationToken.None));
                Assert(processed.Count(didWork => didWork) == 1, "More than one worker processed the backup");
                var nodePath = $"/v1/servers/{fixture.SecondServerId:D}/backups/{id:D}/with-exclusions";
                Assert(fixture.SecondNode.Requests.Count(request => request.Path == nodePath && request.Body == "logs/\n*.tmp") == 1 &&
                    !fixture.FirstNode.Requests.Any(request => request.Path == nodePath), "Backup contacted the wrong node or was repeated");
                using var response = await owner.GetAsync(path);
                var completed = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!.Single();
                Assert(!completed.GetProperty("isQueued").GetBoolean() && completed.GetProperty("isSuccessful").GetBoolean() &&
                    completed.GetProperty("size").GetInt64() == FakeDockerNode.BackupArchive.Length &&
                    completed.GetProperty("checksum").GetString() == FakeDockerNode.BackupChecksum,
                    "The authenticated node receipt was not recorded");

                fixture.SecondNode.ReportStopped = false;
                using var stale = await owner.PostAsJsonAsync(path, new { name = "Stale panel status" });
                Assert(stale.StatusCode == HttpStatusCode.Accepted && await BackupWorker.RunOneAsync(scopes, CancellationToken.None),
                    "The worker failed to handle a stale panel status");
                using var scope = scopes.CreateScope();
                var failed = await scope.ServiceProvider.GetRequiredService<PanelDbContext>().Backups.SingleAsync(b => b.Name == "Stale panel status");
                Assert(!failed.IsQueued && !failed.IsSuccessful && failed.CompletedAt.HasValue &&
                    !fixture.SecondNode.Requests.Any(request => request.Path.EndsWith($"/backups/{failed.Id:D}/with-exclusions")),
                    "An externally running container was allowed to create a backup");
            });
            await Check("Backup downloads use expiring session grants once and verify archive bytes", async () =>
            {
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var backup = await db.Backups.AsNoTracking().SingleAsync(b => b.IsSuccessful);
                var failed = await db.Backups.AsNoTracking().SingleAsync(b => !b.IsSuccessful);
                var path = $"/api/servers/{fixture.SecondServerId:D}/backups/{backup.Id:D}/download";
                using var denied = await other.PostAsync(path + "-grant", null);
                using var incomplete = await owner.PostAsync($"/api/servers/{fixture.SecondServerId:D}/backups/{failed.Id:D}/download-grant", null);
                Assert(denied.StatusCode == HttpStatusCode.Forbidden && incomplete.StatusCode == HttpStatusCode.Conflict,
                    "A foreign user or an unsuccessful backup received a download grant");

                async Task<string> Issue(HttpClient client)
                {
                    using var result = await client.PostAsync(path + "-grant", null);
                    Assert(result.IsSuccessStatusCode, $"Backup grant failed ({result.StatusCode})");
                    return (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
                }

                async Task<HttpResponseMessage> Redeem(string ticket, string? target = null)
                {
                    using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket });
                    return await anonymous.PostAsync(target ?? path, form);
                }

                var ticket = await Issue(owner);
                Assert(!await db.BackupDownloadGrants.AnyAsync(g => g.TokenHash == ticket), "Backup grant was stored in plaintext");
                using var redirected = await Redeem(ticket, path.Replace(fixture.SecondServerId.ToString("D"), fixture.ServerId.ToString("D")));
                Assert(redirected.StatusCode == HttpStatusCode.Unauthorized, "The grant was accepted for another server");
                var transfers = await Task.WhenAll(Redeem(ticket), Redeem(ticket));
                using var first = transfers[0];
                using var second = transfers[1];
                Assert(transfers.Count(r => r.StatusCode == HttpStatusCode.OK) == 1 && transfers.Count(r => r.StatusCode == HttpStatusCode.Unauthorized) == 1,
                    "Concurrent transfers reused a download grant");
                var downloaded = transfers.Single(r => r.StatusCode == HttpStatusCode.OK);
                Assert(downloaded.Content.Headers.ContentDisposition?.FileName == $"arcadenode-{backup.Id:N}.tar.gz" &&
                    downloaded.Content.Headers.ContentType?.MediaType == "application/gzip" &&
                    (await downloaded.Content.ReadAsByteArrayAsync()).SequenceEqual(FakeDockerNode.BackupArchive),
                    "The panel did not return the exact node archive as an attachment");

                using var temporarySession = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
                await Login(temporarySession, "owner", Password);
                var revokedTicket = await Issue(temporarySession);
                using var loggedOut = await temporarySession.PostAsync("/api/auth/logout", null);
                Assert(loggedOut.IsSuccessStatusCode, "Temporary backup session did not log out");
                using var revoked = await Redeem(revokedTicket);
                Assert(revoked.StatusCode == HttpStatusCode.Unauthorized, "Logout did not cancel the backup grant");

                var expiredTicket = await Issue(owner);
                var expiredHash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(expiredTicket)));
                await db.BackupDownloadGrants.Where(g => g.TokenHash == expiredHash)
                    .ExecuteUpdateAsync(g => g.SetProperty(p => p.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
                using var expired = await Redeem(expiredTicket);
                Assert(expired.StatusCode == HttpStatusCode.Unauthorized, "An expired backup grant was accepted");

                var corruptTicket = await Issue(owner);
                fixture.SecondNode.CorruptBackupArchive = true;
                var rejected = false;
                try
                {
                    using var corrupted = await Redeem(corruptTicket);
                    rejected = !corrupted.IsSuccessStatusCode || (await corrupted.Content.ReadAsByteArrayAsync()).Length < backup.Size;
                }
                catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
                {
                    rejected = true;
                }
                finally { fixture.SecondNode.CorruptBackupArchive = false; }
                Assert(rejected, "A corrupted archive reached the browser in full");
            });
            await Check("Backup restoration verifies archives and only publishes a fresh staging directory", () =>
                BackupRestoreProbe.RunAsync(fixture.Root));
            await Check("Pterodactyl archives restore implicit directories only after backup bytes and checksums match", () =>
                BackupRestoreProbe.RunPterodactylAsync(fixture.Root));
            if (OperatingSystem.IsLinux())
                await Check("ArcadeNode and Wings backups restore only contained symbolic links with archived targets", () =>
                    BackupRestoreProbe.RunContainedLinksAsync(fixture.Root));
            await Check("Panel recovery stays on the assigned node and leaves game power available", async () =>
            {
                var scopes = fixture.Services.GetRequiredService<IServiceScopeFactory>();
                using var scope = scopes.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var backupId = await database.Backups.Where(b => b.IsSuccessful).Select(b => b.Id).SingleAsync();
                var path = $"/api/servers/{fixture.SecondServerId:D}/backups/{backupId:D}/restore";
                var nodePath = $"/v1/servers/{fixture.SecondServerId:D}/backups/{backupId:D}/restore";
                using var denied = await other.PostAsync(path, null);
                using var unavailable = await owner.PostAsync(path, null);
                Assert(denied.StatusCode == HttpStatusCode.Forbidden && unavailable.StatusCode == HttpStatusCode.ServiceUnavailable,
                    "Recovery ignored server ownership or node configuration");
                var config = fixture.Services.GetRequiredService<IConfiguration>();
                config[$"FileNodes:{fixture.SecondNodeId:D}:RestoreEnabled"] = "true";

                await database.Servers.Where(s => s.Id == fixture.SecondServerId)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, ServerStatus.Running));
                using var queue = await owner.PostAsync(path, null);
                using var replay = await owner.PostAsync(path, null);
                Assert(queue.StatusCode == HttpStatusCode.Accepted && replay.StatusCode == HttpStatusCode.Accepted,
                    "Recovery required downtime or could not be requested idempotently");

                fixture.SecondNode.RestoreBusy = true;
                Assert(!await BackupWorker.RunOneRestoreAsync(scopes, CancellationToken.None) &&
                    await database.Backups.AnyAsync(b => b.Id == backupId && b.RestoreStatus == BackupRestoreStatus.Queued),
                    "The restore queue discarded a busy node response");
                fixture.SecondNode.RestoreBusy = false;
                fixture.SecondNode.WrongRestoreChecksum = true;
                Assert(await BackupWorker.RunOneRestoreAsync(scopes, CancellationToken.None) &&
                    await database.Backups.AnyAsync(b => b.Id == backupId && b.RestoreStatus == BackupRestoreStatus.Failed),
                    "Recovery accepted a receipt belonging to a different backup");
                fixture.SecondNode.WrongRestoreChecksum = false;
                fixture.SecondNode.RestoreBytes = 64L * 1024 * 1024 * 1024 + 1;
                var oversized = await fixture.Services.GetRequiredService<NodeFileClient>().RestoreBackupAsync(fixture.SecondNodeId,
                    fixture.SecondServerId, backupId, FakeDockerNode.BackupChecksum, CancellationToken.None);
                Assert(oversized is { Status: HttpStatusCode.BadGateway, Receipt: null }, "The panel accepted a restore above the configured hard limit");
                using var retry = await owner.PostAsync(path, null);
                Assert(retry.StatusCode == HttpStatusCode.Accepted, "A failed restore could not be retried");

                const long largeRestoreBytes = 7_180_013_985;
                fixture.SecondNode.RestoreBytes = largeRestoreBytes;
                fixture.SecondNode.RestoreEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.SecondNode.ReleaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var running = BackupWorker.RunOneRestoreAsync(scopes, CancellationToken.None);
                try
                {
                    await fixture.SecondNode.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    using var power = await owner.PostAsJsonAsync($"/api/servers/{fixture.SecondServerId:D}/power", new { action = "restart" });
                    Assert(power.IsSuccessStatusCode && !running.IsCompleted, "Staging recovery blocked the game's power control");
                }
                finally { fixture.SecondNode.ReleaseRestore.TrySetResult(); }
                Assert(await running && !await BackupWorker.RunOneRestoreAsync(scopes, CancellationToken.None), "Completed recovery was replayed by the queue");
                using var prepared = await owner.PostAsync(path, null);
                var receipt = await prepared.Content.ReadFromJsonAsync<JsonElement>();
                Assert(prepared.StatusCode == HttpStatusCode.OK && receipt.GetProperty("restoreStatus").GetString() == "Prepared" &&
                    receipt.GetProperty("restoredFiles").GetInt32() == 3 && receipt.GetProperty("restoredDirectories").GetInt32() == 2 &&
                    receipt.GetProperty("restoredBytes").GetInt64() == largeRestoreBytes && receipt.GetProperty("restoreNodeId").GetGuid() == fixture.SecondNodeId,
                    "The persisted restoration state did not reflect the node receipt");
                Assert(fixture.SecondNode.Requests.Count(r => r.Path == nodePath) == 4 && !fixture.FirstNode.Requests.Any(r => r.Path == nodePath),
                    "Recovery used the wrong node or duplicate requests");
                fixture.SecondNode.RestoreBytes = 130000;
                await database.Servers.Where(s => s.Id == fixture.SecondServerId)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, ServerStatus.Stopped));
            });
            await Check("Scheduled backups persist their receipt wait and resume the remaining tasks once", async () =>
            {
                var scopes = fixture.Services.GetRequiredService<IServiceScopeFactory>();
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var server = db.Servers.Where(s => s.Id == fixture.SecondServerId);
                var path = $"/api/servers/{fixture.SecondServerId:D}/schedules";
                var dueAt = new DateTime(2026, 1, 2, 4, 0, 0, DateTimeKind.Utc);
                var resumedAt = dueAt.AddMinutes(11);
                async Task<bool> TickAsync(DateTime now)
                {
                    using var worker = scopes.CreateScope();
                    return await ScheduleWorker.RunOneAsync(worker.ServiceProvider, now, CancellationToken.None);
                }

                async Task Scenario(bool backupSucceeds, bool continueOnFailure)
                {
                    await server.ExecuteUpdateAsync(update => update.SetProperty(s => s.Status, ServerStatus.Running));
                    var before = fixture.SecondNode.Requests.Count;
                    using var response = await owner.PostAsJsonAsync(path, new
                    {
                        name = "Scheduled save", cronExpression = "0 4 1 1 *", onlyWhenOnline = true, isActive = true,
                        tasks = new[] {
                            new { action = "Power", payload = "stop", timeOffset = 0, continueOnFailure = false },
                            new { action = "Backup", payload = "cache/\r\n!scripts/cache", timeOffset = 0, continueOnFailure },
                            new { action = "Power", payload = "start", timeOffset = 0, continueOnFailure = false }
                        }
                    });
                    Assert(response.StatusCode == HttpStatusCode.Created, "The schedule API rejected a full backup task");
                    var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                    await db.Schedules.Where(s => s.Id == id).ExecuteUpdateAsync(update => update.SetProperty(s => s.NextRunAt, dueAt));
                    Assert(await TickAsync(dueAt), "The scheduled backup occurrence was not claimed");
                    var waiting = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == id);
                    if (waiting.PendingBackupId is not { } backupId) throw new InvalidOperationException("Missing durable backup reference");
                    Assert(waiting.IsProcessing && await db.Backups.AnyAsync(b => b.Id == backupId && b.IsQueued) &&
                        fixture.SecondNode.Requests.Skip(before).Count(request => request.Path.EndsWith("/stop")) == 1 &&
                        !fixture.SecondNode.Requests.Skip(before).Any(request => request.Path.EndsWith("/start")),
                        "The schedule did not stop once and persist its wait before reaching the start task");

                    var competing = new Schedule { Name = "Another maintenance", CronExpression = "0 4 1 1 *",
                        ServerId = fixture.SecondServerId, IsActive = true, NextRunAt = dueAt };
                    db.Schedules.Add(competing);
                    await db.SaveChangesAsync();
                    Assert(!await TickAsync(resumedAt), "A different schedule executed while the same server waited for its backup");
                    await db.Schedules.Where(s => s.Id == competing.Id).ExecuteDeleteAsync();
                    Assert(await db.Schedules.AnyAsync(s => s.Id == id && s.IsProcessing && s.PendingBackupId == backupId),
                        "A fresh worker expired an occurrence that was still waiting for a receipt");

                    fixture.SecondNode.ReportStopped = backupSucceeds;
                    if (backupSucceeds)
                    {
                        fixture.SecondNode.BackupBusy = true;
                        Assert(!await BackupWorker.RunOneAsync(scopes, CancellationToken.None) &&
                            await db.Backups.AnyAsync(b => b.Id == backupId && b.IsQueued && b.NextAttemptAt != null) &&
                            !await TickAsync(resumedAt), "A busy node released the following scheduled tasks");
                        fixture.SecondNode.BackupBusy = false;
                        await db.Backups.Where(b => b.Id == backupId)
                            .ExecuteUpdateAsync(update => update.SetProperty(b => b.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
                    }
                    Assert(await BackupWorker.RunOneAsync(scopes, CancellationToken.None), "The worker did not finish the scheduled backup");
                    var receipt = await db.Backups.AsNoTracking().SingleAsync(b => b.Id == backupId);
                    Assert(!receipt.IsQueued && receipt.IsSuccessful == backupSucceeds &&
                        (!backupSucceeds || (receipt.Checksum == FakeDockerNode.BackupChecksum && fixture.SecondNode.Requests.Any(request =>
                            request.Path.EndsWith($"/{backupId:D}/with-exclusions") && request.Body == "cache/\n!scripts/cache"))),
                        "Unexpected scheduled backup result or node exclusion list");
                    var resumes = await Task.WhenAll(TickAsync(resumedAt), TickAsync(resumedAt));
                    var completed = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == id);
                    Assert(resumes.Count(value => value) == 1 && !completed.IsProcessing && completed.PendingBackupId == null &&
                        completed.LastRunAt == dueAt &&
                        fixture.SecondNode.Requests.Skip(before).Count(request => request.Path.EndsWith("/start")) ==
                            ((backupSucceeds || continueOnFailure) ? 1 : 0), "The schedule replayed, skipped, or reordered work after the backup");
                    using var removed = await owner.DeleteAsync($"{path}/{id:D}");
                    Assert(removed.StatusCode == HttpStatusCode.NoContent, "A completed backup occurrence left its schedule locked");
                    await db.Backups.Where(b => b.Id == backupId).ExecuteDeleteAsync();
                    fixture.SecondNode.ReportStopped = false;
                }

                await Scenario(backupSucceeds: true, continueOnFailure: false);
                await Scenario(backupSucceeds: false, continueOnFailure: false);
                await Scenario(backupSucceeds: false, continueOnFailure: true);
                await server.ExecuteUpdateAsync(update => update.SetProperty(s => s.Status, ServerStatus.Stopped));
            });
            await Check("Backup exclusions preserve Git ignore rules and reject malformed or excessive lists", BackupIgnoreProbe.RunAsync);
            await Check("Protected and reserved backups cannot be deleted and deletion waits for the enrolled node", async () =>
            {
                var scopes = fixture.Services.GetRequiredService<IServiceScopeFactory>();
                using var scope = scopes.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var backup = new Backup { ServerId = fixture.SecondServerId, Name = "Retention sample", IsSuccessful = true,
                    Checksum = FakeDockerNode.BackupChecksum, Size = FakeDockerNode.BackupArchive.Length, CompletedAt = DateTime.UtcNow };
                database.Backups.Add(backup);
                await database.SaveChangesAsync();
                var row = database.Backups.Where(b => b.Id == backup.Id);
                var path = $"/api/servers/{fixture.SecondServerId:D}/backups/{backup.Id:D}";
                var nodePath = $"/v1/servers/{fixture.SecondServerId:D}/backups/{backup.Id:D}";

                using var unauthorized = await anonymous.DeleteAsync(path);
                using var foreign = await other.DeleteAsync(path);
                using var foreignProtection = await other.PutAsJsonAsync(path + "/protection", new { isLocked = true });
                using var missingProtection = await owner.PutAsJsonAsync(path + "/protection", new { });
                using var protect = await owner.PutAsJsonAsync(path + "/protection", new { isLocked = true });
                using var locked = await owner.DeleteAsync(path);
                using var unlock = await owner.PutAsJsonAsync(path + "/protection", new { isLocked = false });
                Assert(unauthorized.StatusCode == HttpStatusCode.Unauthorized && foreign.StatusCode == HttpStatusCode.Forbidden &&
                    foreignProtection.StatusCode == HttpStatusCode.Forbidden && missingProtection.StatusCode == HttpStatusCode.BadRequest &&
                    protect.StatusCode == HttpStatusCode.NoContent && locked.StatusCode == HttpStatusCode.Conflict &&
                    unlock.StatusCode == HttpStatusCode.NoContent, "Backup protection or ownership was bypassed");

                var schedule = new Schedule { ServerId = backup.ServerId, Name = "Retention reservation", CronExpression = "* * * * *",
                    IsActive = false, IsProcessing = true, PendingBackupId = backup.Id };
                database.Schedules.Add(schedule);
                await database.SaveChangesAsync();
                using var scheduled = await owner.DeleteAsync(path);
                await database.Schedules.Where(s => s.Id == schedule.Id).ExecuteDeleteAsync();
                await row.ExecuteUpdateAsync(change => change.SetProperty(b => b.RestoreStatus, BackupRestoreStatus.Prepared));
                using var staged = await owner.DeleteAsync(path);
                await row.ExecuteUpdateAsync(change => change.SetProperty(b => b.RestoreStatus, BackupRestoreStatus.None));
                Assert(scheduled.StatusCode == HttpStatusCode.Conflict && staged.StatusCode == HttpStatusCode.Conflict,
                    "An archive reserved by a schedule or restore could be deleted");

                using var first = await owner.DeleteAsync(path);
                using var second = await owner.DeleteAsync(path);
                using var download = await owner.PostAsync(path + "/download-grant", null);
                using var restore = await owner.PostAsync(path + "/restore", null);
                using var protectDuringDeletion = await owner.PutAsJsonAsync(path + "/protection", new { isLocked = true });
                Assert(first.StatusCode == HttpStatusCode.Accepted && second.StatusCode == HttpStatusCode.Accepted &&
                    download.StatusCode == HttpStatusCode.Conflict && restore.StatusCode == HttpStatusCode.Conflict &&
                    protectDuringDeletion.StatusCode == HttpStatusCode.Conflict &&
                    await row.AnyAsync(b => b.DeletionStatus == BackupDeletionStatus.Queued) &&
                    !fixture.SecondNode.Requests.Any(r => r.Path == nodePath), "The deletion request was not durably queued and isolated");

                fixture.SecondNode.BackupDeletionUnavailable = true;
                try
                {
                    Assert(await BackupWorker.RunOneDeletionAsync(scopes, CancellationToken.None) &&
                        await row.AnyAsync(b => b.DeletionStatus == BackupDeletionStatus.Failed),
                        "A node failure discarded the archive record");
                }
                finally { fixture.SecondNode.BackupDeletionUnavailable = false; }

                using var retry = await owner.DeleteAsync(path);
                Assert(retry.StatusCode == HttpStatusCode.Accepted, "The owner could not retry a failed deletion");
                var attempts = await Task.WhenAll(BackupWorker.RunOneDeletionAsync(scopes, CancellationToken.None),
                    BackupWorker.RunOneDeletionAsync(scopes, CancellationToken.None));
                using var gone = await owner.DeleteAsync(path);
                Assert(attempts.Count(worked => worked) == 1 && !await row.AnyAsync() && gone.StatusCode == HttpStatusCode.NotFound &&
                    fixture.SecondNode.Requests.Count(r => r.Path == nodePath) == 2 && !fixture.FirstNode.Requests.Any(r => r.Path == nodePath),
                    "The record was not removed exactly once by the enrolled node's acknowledgement");
            });
            await Check("Directory pagination forwards its scope only to the assigned file node", async () =>
            {
                var route = $"/api/servers/{fixture.SecondServerId:D}/files";
                var nodeRoute = $"/v1/servers/{fixture.SecondServerId:D}/files/page";
                var cursor = new string('A', 80);
                var request = $"{route}?path=world%2Fconfigs&filter=zip%20%26%20cfg&cursor={cursor}";
                var before = fixture.SecondNode.Requests.Count(entry => entry.Path == nodeRoute);
                using var denied = await other.GetAsync(request);
                using var malformed = await owner.GetAsync(route + "?cursor=broken");
                using var tooLong = await owner.GetAsync(route + "?filter=" + new string('f', 129));
                Assert(denied.StatusCode == HttpStatusCode.Forbidden && malformed.StatusCode == HttpStatusCode.BadRequest &&
                    tooLong.StatusCode == HttpStatusCode.BadRequest && fixture.SecondNode.Requests.Count(entry => entry.Path == nodeRoute) == before,
                    "Invalid or unauthorized pagination requests reached the file node");

                using var page = await owner.GetAsync(request);
                var data = await page.Content.ReadFromJsonAsync<JsonElement>();
                Assert(page.IsSuccessStatusCode && data.GetProperty("path").GetString() == "world/configs" &&
                    data.GetProperty("cursor").GetString() == cursor && data.GetProperty("filter").GetString() == "zip & cfg",
                    "The file node did not receive the exact directory, cursor and filter");
                using var expired = await owner.GetAsync(route + "?cursor=" + new string('B', 80));
                Assert(expired.StatusCode == HttpStatusCode.Conflict && fixture.SecondNode.Requests.Count(entry => entry.Path == nodeRoute) == before + 2 &&
                    !fixture.FirstNode.Requests.Any(entry => entry.Path == nodeRoute), "The panel lost a stale cursor response or reached the wrong node");
            });
            await Check("Directory creation and moves authorize the owner and require a stopped container on the correct node", async () =>
            {
                var path = $"/api/servers/{fixture.SecondServerId:D}/files";
                var directory = new { path = "world/configs" };
                var move = new { source = "world/server.cfg", destination = "world/configs/server.cfg" };
                var before = fixture.SecondNode.Requests.Count;
                var firstBefore = fixture.FirstNode.Requests.Count;
                using var unauthorized = await anonymous.PostAsJsonAsync(path + "/directories", directory);
                using var foreign = await other.PostAsJsonAsync(path + "/move", move);
                using var foreignDirectory = await other.PostAsJsonAsync(path + "/directories", directory);
                using var invalidDirectory = await owner.PostAsJsonAsync(path + "/directories", new { path = "../outside" });
                using var invalidMove = await owner.PostAsJsonAsync(path + "/move", new { source = "world", destination = "world/subdir/world" });
                Assert(unauthorized.StatusCode == HttpStatusCode.Unauthorized && foreign.StatusCode == HttpStatusCode.Forbidden &&
                    foreignDirectory.StatusCode == HttpStatusCode.Forbidden && invalidDirectory.StatusCode == HttpStatusCode.BadRequest &&
                    invalidMove.StatusCode == HttpStatusCode.BadRequest && fixture.SecondNode.Requests.Count == before,
                    "Invalid or unauthorized file changes reached a node");

                using var running = await owner.PostAsJsonAsync(path + "/move", move);
                Assert(running.StatusCode == HttpStatusCode.Conflict &&
                    !fixture.SecondNode.Requests.Skip(before).Any(r => r.Path.EndsWith("/files/move")),
                    "A stale stopped status let a running server's files move");

                fixture.SecondNode.ReportStopped = true;
                using var created = await owner.PostAsJsonAsync(path + "/directories", directory);
                using var moved = await owner.PostAsJsonAsync(path + "/move", move);
                using var conflict = await owner.PostAsJsonAsync(path + "/move", new { move.source, destination = "world/existing.cfg" });
                fixture.SecondNode.ReportStopped = false;
                Assert(created.StatusCode == HttpStatusCode.Created && moved.StatusCode == HttpStatusCode.NoContent &&
                    conflict.StatusCode == HttpStatusCode.Conflict && fixture.FirstNode.Requests.Count == firstBefore,
                    "A valid change reached the wrong node or lost its conflict response");
                var fileRoute = $"/v1/servers/{fixture.SecondServerId:D}/files";
                Assert(fixture.SecondNode.Requests.Skip(before).Any(r => r.Path == fileRoute + "/directories" && r.Body.Contains("world/configs")) &&
                    fixture.SecondNode.Requests.Skip(before).Count(r => r.Path == fileRoute + "/move" && r.Body.Contains("world/configs/server.cfg")) == 1,
                    "The enrolled file service did not receive the requested paths");
            });
            await Check("File copy requires the owner and a stopped container and preserves node errors", async () =>
            {
                var path = $"/api/servers/{fixture.SecondServerId:D}/files/copy";
                var nodePath = $"/v1/servers/{fixture.SecondServerId:D}/files/copy";
                var input = new { source = "world/server.cfg", destination = "configs/copied.cfg" };
                using var unsigned = await anonymous.PostAsJsonAsync(path, input);
                using var foreign = await other.PostAsJsonAsync(path, input);
                using var traversal = await owner.PostAsJsonAsync(path, new { input.source, destination = "../elsewhere" });
                using var same = await owner.PostAsJsonAsync(path, new { input.source, destination = input.source });
                using var running = await owner.PostAsJsonAsync(path, input);
                Assert(unsigned.StatusCode == HttpStatusCode.Unauthorized && foreign.StatusCode == HttpStatusCode.Forbidden &&
                    traversal.StatusCode == HttpStatusCode.BadRequest && same.StatusCode == HttpStatusCode.BadRequest &&
                    running.StatusCode == HttpStatusCode.Conflict && !fixture.SecondNode.Requests.Any(r => r.Path == nodePath),
                    "An unauthorized, invalid or running copy reached the node");

                fixture.SecondNode.ReportStopped = true;
                try
                {
                    using var copied = await owner.PostAsJsonAsync(path, input);
                    using var conflict = await owner.PostAsJsonAsync(path, new { input.source, destination = "world/existing.cfg" });
                    using var tooLarge = await owner.PostAsJsonAsync(path, new { source = "world/oversized.bin", input.destination });
                    Assert(copied.StatusCode == HttpStatusCode.Created && conflict.StatusCode == HttpStatusCode.Conflict &&
                        tooLarge.StatusCode == HttpStatusCode.RequestEntityTooLarge &&
                        fixture.SecondNode.Requests.Count(r => r.Path == nodePath) == 3 &&
                        fixture.SecondNode.Requests.Any(r => r.Path == nodePath && r.Body.Contains(input.source) && r.Body.Contains(input.destination)) &&
                        !fixture.FirstNode.Requests.Any(r => r.Path == nodePath), "The copy lost the node result or used the wrong node");
                }
                finally { fixture.SecondNode.ReportStopped = false; }
            });
            await Check("Selected file archives require a dedicated permission and a stopped container on the assigned node", async () =>
            {
                var server = $"/api/servers/{fixture.SecondServerId:D}";
                var path = $"{server}/files/compress";
                var nodePath = $"/v1/servers/{fixture.SecondServerId:D}/files/compress";
                var memberPath = $"{server}/members/{fixture.OtherId:D}";
                var members = new { permissions = new[] { "file.create" } };
                var selected = new { directory = "configs", entries = new[] { "server.cfg", "world" }, destination = "selected.tar.gz" };
                using var share = await owner.PutAsJsonAsync(memberPath, members);
                using var unsigned = await anonymous.PostAsJsonAsync(path, selected);
                using var createOnly = await other.PostAsJsonAsync(path, selected);
                using var invalidDirectory = await owner.PostAsJsonAsync(path, new { directory = "../elsewhere", selected.entries, selected.destination });
                using var invalidEntry = await owner.PostAsJsonAsync(path, new { selected.directory, entries = new[] { "world/save.dat" }, selected.destination });
                using var repeated = await owner.PostAsJsonAsync(path, new { selected.directory, entries = new[] { "world", "world" }, selected.destination });
                using var running = await owner.PostAsJsonAsync(path, selected);
                Assert(share.StatusCode == HttpStatusCode.NoContent && unsigned.StatusCode == HttpStatusCode.Unauthorized &&
                    createOnly.StatusCode == HttpStatusCode.Forbidden && invalidDirectory.StatusCode == HttpStatusCode.BadRequest &&
                    invalidEntry.StatusCode == HttpStatusCode.BadRequest && repeated.StatusCode == HttpStatusCode.BadRequest &&
                    running.StatusCode == HttpStatusCode.Conflict && !fixture.SecondNode.Requests.Any(request => request.Path == nodePath),
                    "An unauthorized, invalid or running archive reached its node");

                using var allowArchive = await owner.PutAsJsonAsync(memberPath, new { permissions = new[] { "file.archive" } });
                fixture.SecondNode.ReportStopped = true;
                try
                {
                    using var archived = await other.PostAsJsonAsync(path, selected);
                    using var conflict = await owner.PostAsJsonAsync(path, new { selected.directory, selected.entries, destination = "existing.tar.gz" });
                    using var tooLarge = await owner.PostAsJsonAsync(path, new { selected.directory, selected.entries, destination = "oversized.tar.gz" });
                    var calls = fixture.SecondNode.Requests.Where(request => request.Path == nodePath).ToArray();
                    Assert(allowArchive.StatusCode == HttpStatusCode.NoContent && archived.StatusCode == HttpStatusCode.Created &&
                        conflict.StatusCode == HttpStatusCode.Conflict && tooLarge.StatusCode == HttpStatusCode.RequestEntityTooLarge &&
                        calls.Length == 3 && !fixture.FirstNode.Requests.Any(request => request.Path == nodePath),
                        "The panel lost archive status or contacted an unrelated node");
                    using var payload = JsonDocument.Parse(calls[0].Body);
                    Assert(payload.RootElement.GetProperty("directory").GetString() == selected.directory &&
                        payload.RootElement.GetProperty("destination").GetString() == selected.destination &&
                        payload.RootElement.GetProperty("entries").EnumerateArray().Select(entry => entry.GetString()).SequenceEqual(selected.entries),
                        "The file scope or selection was lost in transit");
                }
                finally
                {
                    fixture.SecondNode.ReportStopped = false;
                    using var removed = await owner.DeleteAsync(memberPath);
                    Assert(removed.StatusCode == HttpStatusCode.NoContent, "Could not revoke the synthetic archive access");
                }
            });
            await Check("ZIP and TAR extraction validates file content, archive budgets and safe paths", () => ArchiveExtractionProbe.RunAsync(fixture.Root));
            await Check("Extracting an archive uses file creation permission and the stopped server node", async () =>
            {
                var baseRoute = $"/api/servers/{fixture.SecondServerId:D}";
                var path = $"{baseRoute}/files/extract";
                var nodePath = $"/v1/servers/{fixture.SecondServerId:D}/files/extract";
                var membership = $"{baseRoute}/members/{fixture.OtherId:D}";
                var input = new { source = "uploads/world.tar.gz", destination = "world-restored" };
                using var onlyCompress = await owner.PutAsJsonAsync(membership, new { permissions = new[] { "file.archive" } });
                using var denied = await other.PostAsJsonAsync(path, input);
                using var wrongFormat = await owner.PostAsJsonAsync(path, new { source = "world.dat", input.destination });
                using var unsafeDestination = await owner.PostAsJsonAsync(path, new { input.source, destination = "../outside" });
                using var running = await owner.PostAsJsonAsync(path, input);
                Assert(onlyCompress.StatusCode == HttpStatusCode.NoContent && denied.StatusCode == HttpStatusCode.Forbidden &&
                    wrongFormat.StatusCode == HttpStatusCode.BadRequest && unsafeDestination.StatusCode == HttpStatusCode.BadRequest &&
                    running.StatusCode == HttpStatusCode.Conflict && !fixture.SecondNode.Requests.Any(request => request.Path == nodePath),
                    "An invalid, unauthorized or running extraction reached the node");
                using var allowExtract = await owner.PutAsJsonAsync(membership, new { permissions = new[] { "file.create" } });
                fixture.SecondNode.ReportStopped = true;
                try
                {
                    using var extracted = await other.PostAsJsonAsync(path, input);
                    using var invalid = await other.PostAsJsonAsync(path, new { source = "invalid.zip", input.destination });
                    Assert(allowExtract.StatusCode == HttpStatusCode.NoContent && extracted.StatusCode == HttpStatusCode.Created &&
                        invalid.StatusCode == HttpStatusCode.UnprocessableEntity &&
                        fixture.SecondNode.Requests.Count(request => request.Path == nodePath) == 2 &&
                        fixture.SecondNode.Requests.Any(request => request.Path == nodePath && request.Body.Contains(input.source) &&
                            request.Body.Contains(input.destination)) && !fixture.FirstNode.Requests.Any(request => request.Path == nodePath),
                        "The assigned node did not receive the expected source and destination or lost its error result");
                }
                finally
                {
                    fixture.SecondNode.ReportStopped = false;
                    using var revoked = await owner.DeleteAsync(membership);
                    Assert(revoked.StatusCode == HttpStatusCode.NoContent, "Could not clear the synthetic file grant");
                }
            });
            await Check("File deletion requires file.delete and a stopped server on the assigned node", async () =>
            {
                var server = $"/api/servers/{fixture.SecondServerId:D}";
                var route = $"{server}/files/delete";
                var nodeRoute = $"/v1/servers/{fixture.SecondServerId:D}/files/delete";
                var member = $"{server}/members/{fixture.OtherId:D}";
                var selected = new { directory = "world/logs", entries = new[] { "old.txt", "old-world" } };
                using var share = await owner.PutAsJsonAsync(member, new { permissions = new[] { "file.create", "file.update", "file.archive" } });
                using var unsigned = await anonymous.PostAsJsonAsync(route, selected);
                using var wrongRight = await other.PostAsJsonAsync(route, selected);
                using var traversal = await owner.PostAsJsonAsync(route, new { directory = "../outside", selected.entries });
                using var nested = await owner.PostAsJsonAsync(route, new { selected.directory, entries = new[] { "nested/old.txt" } });
                using var duplicates = await owner.PostAsJsonAsync(route, new { selected.directory, entries = new[] { "old.txt", "old.txt" } });
                using var oversized = await owner.PostAsJsonAsync(route, new { selected.directory, entries = Enumerable.Range(0, 41).Select(x => $"{x}.txt") });
                using var running = await owner.PostAsJsonAsync(route, selected);
                Assert(share.StatusCode == HttpStatusCode.NoContent && unsigned.StatusCode == HttpStatusCode.Unauthorized &&
                    wrongRight.StatusCode == HttpStatusCode.Forbidden && traversal.StatusCode == HttpStatusCode.BadRequest &&
                    nested.StatusCode == HttpStatusCode.BadRequest && duplicates.StatusCode == HttpStatusCode.BadRequest &&
                    oversized.StatusCode == HttpStatusCode.BadRequest && running.StatusCode == HttpStatusCode.Conflict &&
                    !fixture.SecondNode.Requests.Any(request => request.Path == nodeRoute), "An invalid deletion reached the node");

                using var allow = await owner.PutAsJsonAsync(member, new { permissions = new[] { "file.delete" } });
                fixture.SecondNode.ReportStopped = true;
                try
                {
                    using var completed = await other.PostAsJsonAsync(route, selected);
                    using var missing = await other.PostAsJsonAsync(route, new { selected.directory, entries = new[] { "missing" } });
                    using var tooLarge = await other.PostAsJsonAsync(route, new { selected.directory, entries = new[] { "oversized" } });
                    var forwarded = fixture.SecondNode.Requests.Where(req => req.Path == nodeRoute).ToArray();
                    Assert(allow.StatusCode == HttpStatusCode.NoContent && completed.StatusCode == HttpStatusCode.NoContent &&
                        missing.StatusCode == HttpStatusCode.NotFound && tooLarge.StatusCode == HttpStatusCode.RequestEntityTooLarge &&
                        forwarded.Length == 3 && !fixture.FirstNode.Requests.Any(req => req.Path == nodeRoute),
                        "The panel lost a node response or contacted an unrelated node");
                    using var payload = JsonDocument.Parse(forwarded[0].Body);
                    Assert(payload.RootElement.GetProperty("directory").GetString() == selected.directory &&
                        payload.RootElement.GetProperty("entries").EnumerateArray().Select(item => item.GetString()).SequenceEqual(selected.entries),
                        "The deletion request lost its directory or selected entries");
                }
                finally
                {
                    fixture.SecondNode.ReportStopped = false;
                    using var revoke = await owner.DeleteAsync(member);
                    Assert(revoke.StatusCode == HttpStatusCode.NoContent, "Could not revoke the fixture's file deletion right");
                }
            });
            if (OperatingSystem.IsLinux())
                await Check("Linux deletion preflights a complete selection and unlinks symlinks without traversing them", () => FileDeletionProbe.RunAsync(fixture.Root));
            await Check("File modes require file.update and a stopped server on the assigned node", async () =>
            {
                var server = $"/api/servers/{fixture.SecondServerId:D}";
                var route = $"{server}/files/chmod";
                var nodeRoute = $"/v1/servers/{fixture.SecondServerId:D}/files/chmod";
                var member = $"{server}/members/{fixture.OtherId:D}";
                var batch = new { directory = "world", entries = new[] { "server.cfg", "plugins" }, mode = "0750" };
                using var share = await owner.PutAsJsonAsync(member, new { permissions = new[] { "file.create" } });
                using var unsigned = await anonymous.PostAsJsonAsync(route, batch);
                using var denied = await other.PostAsJsonAsync(route, batch);
                using var incorrectMode = await owner.PostAsJsonAsync(route, new { batch.directory, batch.entries, mode = "4750" });
                using var traversal = await owner.PostAsJsonAsync(route, new { directory = "world/../..", batch.entries, batch.mode });
                using var duplicate = await owner.PostAsJsonAsync(route, new { batch.directory, entries = new[] { "server.cfg", "server.cfg" }, batch.mode });
                using var running = await owner.PostAsJsonAsync(route, batch);
                Assert(share.StatusCode == HttpStatusCode.NoContent && unsigned.StatusCode == HttpStatusCode.Unauthorized &&
                    denied.StatusCode == HttpStatusCode.Forbidden && incorrectMode.StatusCode == HttpStatusCode.BadRequest &&
                    traversal.StatusCode == HttpStatusCode.BadRequest && duplicate.StatusCode == HttpStatusCode.BadRequest &&
                    running.StatusCode == HttpStatusCode.Conflict && !fixture.SecondNode.Requests.Any(request => request.Path == nodeRoute),
                    "An unauthorized, malformed or running-server mode change reached the node");

                using var allow = await owner.PutAsJsonAsync(member, new { permissions = new[] { "file.update" } });
                fixture.SecondNode.ReportStopped = true;
                try
                {
                    using var applied = await other.PostAsJsonAsync(route, batch);
                    using var missing = await other.PostAsJsonAsync(route, new { batch.directory, entries = new[] { "missing" }, batch.mode });
                    var forwarded = fixture.SecondNode.Requests.Where(request => request.Path == nodeRoute).ToArray();
                    Assert(allow.StatusCode == HttpStatusCode.NoContent && applied.StatusCode == HttpStatusCode.NoContent &&
                        missing.StatusCode == HttpStatusCode.NotFound && forwarded.Length == 2 &&
                        !fixture.FirstNode.Requests.Any(request => request.Path == nodeRoute), "The file mode request was sent to the wrong node or lost its status");
                    using var payload = JsonDocument.Parse(forwarded[0].Body);
                    Assert(payload.RootElement.GetProperty("directory").GetString() == batch.directory &&
                        payload.RootElement.GetProperty("entries").EnumerateArray().Select(item => item.GetString()).SequenceEqual(batch.entries) &&
                        payload.RootElement.GetProperty("mode").GetString() == batch.mode, "The mode or selected file names changed in transit");
                }
                finally
                {
                    fixture.SecondNode.ReportStopped = false;
                    using var revoke = await owner.DeleteAsync(member);
                    Assert(revoke.StatusCode == HttpStatusCode.NoContent, "Could not remove the fixture's mode change right");
                }
            });
            var fileGrantRoute = $"/api/servers/{fixture.SecondServerId:D}/files/download-grant";
            var fileDownloadRoute = $"/api/servers/{fixture.SecondServerId:D}/files/download";
            const string largeFilePath = "world/mapa São Paulo & gelo.bin";
            async Task<string> IssueFileDownloadAsync(HttpClient client, string path)
            {
                using var issue = await client.PostAsJsonAsync(fileGrantRoute, new { path });
                Assert(issue.StatusCode == HttpStatusCode.OK, $"File grant could not be issued ({issue.StatusCode})");
                return (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
            }
            async Task<HttpResponseMessage> RedeemFileDownloadAsync(string ticket, CancellationToken token = default, string? route = null)
            {
                using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket });
                using var request = new HttpRequestMessage(HttpMethod.Post, route ?? fileDownloadRoute) { Content = form };
                return await anonymous.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }
            await Check("Large file downloads stream early and atomically consume a grant for the assigned server", async () =>
            {
                var nodeRoute = $"/v1/servers/{fixture.SecondServerId:D}/files/stream";
                using var foreign = await other.PostAsJsonAsync(fileGrantRoute, new { path = largeFilePath });
                using var invalid = await owner.PostAsJsonAsync(fileGrantRoute, new { path = "../world" });
                using var missing = await owner.PostAsJsonAsync(fileGrantRoute, new { path = "missing.bin" });
                using var oversized = await owner.PostAsJsonAsync(fileGrantRoute, new { path = "oversized.dat" });
                Assert(foreign.StatusCode == HttpStatusCode.Forbidden && invalid.StatusCode == HttpStatusCode.BadRequest &&
                    missing.StatusCode == HttpStatusCode.NotFound && oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge,
                    "The panel lost permission, path or node preflight errors");

                var ticket = await IssueFileDownloadAsync(owner, largeFilePath);
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var hash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(ticket)));
                Assert(await db.FileDownloadGrants.AnyAsync(grant => grant.TokenHash == hash && grant.Path == largeFilePath) &&
                    !await db.FileDownloadGrants.AnyAsync(grant => grant.TokenHash == ticket), "The grant was stored in plaintext or lost the chosen file");
                using var wrongServer = await RedeemFileDownloadAsync(ticket, route: $"/api/servers/{fixture.ServerId:D}/files/download");
                Assert(wrongServer.StatusCode == HttpStatusCode.Unauthorized, "A file grant worked on another server");

                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.SecondNode.ReleaseFileDownload = release;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    using var response = await RedeemFileDownloadAsync(ticket, deadline.Token);
                    var payload = FakeDockerNode.LargeFile;
                    Assert(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength == payload.Length &&
                        response.Content.Headers.ContentDisposition is { DispositionType: "attachment", FileNameStar: "mapa São Paulo & gelo.bin" } &&
                        response.Headers.GetValues("X-Accel-Buffering").Single() == "no", "Attachment headers or filename were changed");
                    await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                    var firstChunk = new byte[65536];
                    await body.ReadExactlyAsync(firstChunk, deadline.Token);
                    Assert(firstChunk.AsSpan().SequenceEqual(payload.AsSpan(0, firstChunk.Length)), "The first chunk changed in transit");
                    using var duplicate = await RedeemFileDownloadAsync(ticket, deadline.Token);
                    Assert(duplicate.StatusCode == HttpStatusCode.Unauthorized, "A grant could be reused during the transfer");
                    release.TrySetResult();
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    digest.AppendData(firstChunk);
                    int read;
                    while ((read = await body.ReadAsync(firstChunk, deadline.Token)) != 0) digest.AppendData(firstChunk.AsSpan(0, read));
                    Assert(digest.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(payload)), "The downloaded bytes changed in transit");
                }
                finally { release.TrySetResult(); fixture.SecondNode.ReleaseFileDownload = null; }
                Assert(!fixture.FirstNode.Requests.Any(request => request.Path.EndsWith("/files/stream")) &&
                    fixture.SecondNode.Requests.Count(request => request.Path == nodeRoute) == 4, "A file request reached the wrong node or bypassed the ticket");
            });
            await Check("File download grants respect changes to access, expiration and truncated responses", async () =>
            {
                var memberRoute = $"/api/servers/{fixture.SecondServerId:D}/members/{fixture.OtherId:D}";
                using var add = await owner.PutAsJsonAsync(memberRoute, new { permissions = new[] { "file.read-content", "file.read" } });
                Assert(add.StatusCode == HttpStatusCode.NoContent, "Could not share the synthetic server");
                try
                {
                    var revokedTicket = await IssueFileDownloadAsync(other, largeFilePath);
                    using var change = await owner.PutAsJsonAsync(memberRoute, new { permissions = new[] { "file.read" } });
                    using var revoked = await RedeemFileDownloadAsync(revokedTicket);
                    Assert(change.StatusCode == HttpStatusCode.NoContent && revoked.StatusCode == HttpStatusCode.Unauthorized,
                        "An already issued file ticket survived permission revocation");
                }
                finally
                {
                    using var remove = await owner.DeleteAsync(memberRoute);
                    Assert(remove.StatusCode == HttpStatusCode.NoContent, "Could not remove the synthetic membership");
                }

                var expiredTicket = await IssueFileDownloadAsync(owner, largeFilePath);
                var hash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(expiredTicket)));
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                await db.FileDownloadGrants.Where(grant => grant.TokenHash == hash)
                    .ExecuteUpdateAsync(update => update.SetProperty(grant => grant.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
                using var expired = await RedeemFileDownloadAsync(expiredTicket);
                using var truncated = await RedeemFileDownloadAsync(await IssueFileDownloadAsync(owner, "truncated.bin"));
                using var empty = await RedeemFileDownloadAsync(await IssueFileDownloadAsync(owner, "empty.dat"));
                Assert(expired.StatusCode == HttpStatusCode.Unauthorized && truncated.StatusCode == HttpStatusCode.BadGateway &&
                    truncated.Content.Headers.ContentDisposition is null && empty.StatusCode == HttpStatusCode.OK && empty.Content.Headers.ContentLength == 0,
                    "An expired grant, truncated upstream or empty file returned the wrong result");
            });
            await Check("Large uploads stream to the assigned node before the browser finishes sending", async () =>
            {
                var route = $"/api/servers/{fixture.SecondServerId:D}/files/upload?path=world%2Fmap.bin";
                var nodeRoute = $"/v1/servers/{fixture.SecondServerId:D}/files/upload";
                // The test client's redirect handler would copy the entire request body before sending it.
                using var uploadClient = fixture.CreateClient(new WebApplicationFactoryClientOptions
                    { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
                uploadClient.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
                using var deniedContent = new ByteArrayContent([11]);
                deniedContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var denied = await other.PostAsync(route, deniedContent);
                using var oversizeContent = new ByteArrayContent([]);
                oversizeContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                oversizeContent.Headers.ContentLength = (long)ArcadeNode.Transfers.GameFileTransfer.MaximumBytes + 1;
                using var tooLarge = await uploadClient.PostAsync(route, oversizeContent);
                Assert(denied.StatusCode == HttpStatusCode.Forbidden && tooLarge.StatusCode == HttpStatusCode.RequestEntityTooLarge &&
                    !fixture.SecondNode.Requests.Any(request => request.Path == nodeRoute), "An invalid upload reached a file node");

                var payload = Encoding.ASCII.GetBytes(new string('m', 262144) + "end of world");
                var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var headers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.SecondNode.UploadChunkReceived = firstChunk;
                fixture.SecondNode.UploadHeadersReceived = headers;
                using var upload = new PausedUploadContent(payload, resume.Task);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var sent = uploadClient.PostAsync(route, upload, timeout.Token);
                bool forwarded;
                bool started;
                try
                {
                    var completed = await Task.WhenAny(firstChunk.Task, sent, Task.Delay(TimeSpan.FromSeconds(8)));
                    if (completed == sent)
                    {
                        using var early = await sent;
                        throw new InvalidOperationException($"The upload returned {early.StatusCode} before the node received its first block.");
                    }
                    forwarded = completed == firstChunk.Task;
                    started = headers.Task.IsCompletedSuccessfully;
                    Assert(!sent.IsCompleted && !fixture.SecondNode.Requests.Any(request => request.Path == nodeRoute),
                        "The incomplete upload was treated as published");
                }
                finally
                {
                    resume.TrySetResult();
                    fixture.SecondNode.UploadChunkReceived = null;
                    fixture.SecondNode.UploadHeadersReceived = null;
                }
                using var result = await sent;
                Assert(forwarded, $"The node did not receive a block while the upload source was paused (headers received={started}, eventual status={result.StatusCode}).");
                var request = fixture.SecondNode.Requests.Single(request => request.Path == nodeRoute);
                Assert(result.StatusCode == HttpStatusCode.Created && request.Body == Encoding.ASCII.GetString(payload) &&
                    !fixture.FirstNode.Requests.Any(request => request.Path == nodeRoute), "Streaming changed the target node or uploaded bytes");
            });
            await Check("Imported memberships isolate actions and revoke scheduled tasks and backup grants", async () =>
            {
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var target = $"/api/servers/{fixture.SecondServerId:D}";
                var path = $"{target}/members/{fixture.OtherId:D}";
                var permissions = new[] { "backup.download", "control.start", "file.read", "file.delete", "schedule.create", "schedule.update",
                    "websocket.connect", "file.sftp" };
                using var manifest = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, members = new[]
                    { new { server_uuid = fixture.SecondServerId, user_uuid = fixture.OtherId, permissions } } }));
                var plan = await PterodactylMemberImport.RunAsync(db, manifest, apply: false);
                Assert(plan is { Proposed: 1, Imported: 0, Issues.Count: 0, UnsupportedPermissions: [{ Permission: "file.sftp" }] } &&
                    !await db.ServerMembers.AnyAsync(), "The migration applied the plan or failed to flag an unsupported permission");
                manifest.Position = 0;
                var applied = await PterodactylMemberImport.RunAsync(db, manifest, apply: true);
                manifest.Position = 0;
                var repeated = await PterodactylMemberImport.RunAsync(db, manifest, apply: true);
                Assert(applied.Imported == 1 && repeated is { Imported: 0, AlreadyPresent: 1, Issues.Count: 0 }, "Membership import was not idempotent");
                using var conflicting = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, members = new[]
                    { new { server_uuid = fixture.SecondServerId, user_uuid = fixture.OtherId, permissions = new[] { "control.stop" } } } }));
                var conflict = await PterodactylMemberImport.RunAsync(db, conflicting, apply: true);
                Assert(conflict is { Imported: 0, Issues: [{ Reason: "existing_subuser_conflict" }] }, "Import overwrote changed server rights");

                await db.Servers.Where(server => server.Id == fixture.SecondServerId)
                    .ExecuteUpdateAsync(update => update.SetProperty(server => server.StartupCommand, "synthetic private startup"));
                using var list = await other.GetAsync("/api/servers");
                var visible = (await list.Content.ReadFromJsonAsync<JsonElement[]>())!.Single();
                using var detail = await other.GetAsync(target);
                var server = await detail.Content.ReadFromJsonAsync<JsonElement>();
                var granted = server.GetProperty("sharedPermissions");
                Assert(visible.GetProperty("id").GetGuid() == fixture.SecondServerId && visible.GetProperty("startupCommand").ValueKind == JsonValueKind.Null &&
                    server.GetProperty("startupCommand").ValueKind == JsonValueKind.Null && granted.GetArrayLength() == permissions.Length - 1 &&
                    granted.EnumerateArray().Any(value => value.GetString() == "file.delete"),
                    "Shared server listing exposed private configuration or incorrect permissions");
                using var changeAccess = await other.PutAsJsonAsync(path, new { permissions = new[] { "control.stop" } });
                using var readAccess = await owner.GetAsync(target + "/members");
                using var power = await other.PostAsJsonAsync(target + "/power", new { action = "stop" });
                using var console = await other.GetAsync(target + "/logs");
                using var listing = await other.GetAsync(target + "/files");
                using var content = await other.GetAsync(target + "/files/download?path=server.properties");
                using var scheduleList = await other.GetAsync(target + "/schedules");
                Assert(changeAccess.StatusCode == HttpStatusCode.Forbidden && readAccess.IsSuccessStatusCode && power.StatusCode == HttpStatusCode.Forbidden &&
                    console.IsSuccessStatusCode && listing.IsSuccessStatusCode && content.StatusCode == HttpStatusCode.Forbidden &&
                    scheduleList.StatusCode == HttpStatusCode.Forbidden,
                    $"Unexpected membership results for access, roster, power, console, list, content or schedules: {changeAccess.StatusCode}, " +
                    $"{readAccess.StatusCode}, {power.StatusCode}, {console.StatusCode}, {listing.StatusCode}, {content.StatusCode}, {scheduleList.StatusCode}");

                object Schedule(string action, string payload) => new { name = "Shared schedule fixture", cronExpression = "0 4 * * *", isActive = true,
                    tasks = new[] { new { action, payload } } };
                using var deniedTask = await other.PostAsJsonAsync(target + "/schedules", Schedule("Command", "list"));
                using var allowedTask = await other.PostAsJsonAsync(target + "/schedules", Schedule("Power", "start"));
                Assert(deniedTask.StatusCode == HttpStatusCode.Forbidden && allowedTask.StatusCode == HttpStatusCode.Created,
                    "Schedules elevated a subuser's permissions or refused an authorized task");
                var scheduleId = (await allowedTask.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                await db.Schedules.Where(item => item.Id == scheduleId)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.NextRunAt, DateTime.UtcNow.AddMinutes(-2)));

                var backupId = await db.Backups.Where(backup => backup.IsSuccessful && backup.ServerId == fixture.SecondServerId).Select(backup => backup.Id).SingleAsync();
                var download = $"{target}/backups/{backupId:D}/download";
                using var issued = await other.PostAsync(download + "-grant", null);
                Assert(issued.IsSuccessStatusCode, "Backup download permission did not allow issuing a ticket");
                var ticket = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
                using var removeAccess = await owner.DeleteAsync(path);
                using var revoked = await other.GetAsync(target);
                using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket });
                using var redeemed = await anonymous.PostAsync(download, form);
                using var regrant = await owner.PutAsJsonAsync(path, new { permissions = permissions.Where(permission => permission != "file.sftp") });
                using var reissuedAccess = await other.GetAsync(target);
                using var oldForm = new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket });
                using var priorGrant = await anonymous.PostAsync(download, oldForm);
                var before = fixture.SecondNode.Requests.Count;
                Assert(await ScheduleWorker.RunOneAsync(scope.ServiceProvider, DateTime.UtcNow, CancellationToken.None), "The due delegate schedule was not processed");
                var paused = await db.Schedules.AsNoTracking().SingleAsync(item => item.Id == scheduleId);
                Assert(removeAccess.StatusCode == HttpStatusCode.NoContent && revoked.StatusCode == HttpStatusCode.Forbidden &&
                    redeemed.StatusCode == HttpStatusCode.Unauthorized && regrant.StatusCode == HttpStatusCode.NoContent &&
                    reissuedAccess.IsSuccessStatusCode && priorGrant.StatusCode == HttpStatusCode.Unauthorized && !paused.IsActive && !paused.IsProcessing &&
                    paused.DelegatedUserId == fixture.OtherId && fixture.SecondNode.Requests.Count == before,
                    "Revoking or reissuing a membership left an old backup grant or schedule usable");
                using var cleanupAccess = await owner.DeleteAsync(path);
                Assert(cleanupAccess.StatusCode == HttpStatusCode.NoContent, "Could not remove the synthetic membership");
                await db.Schedules.Where(item => item.Id == scheduleId).ExecuteDeleteAsync();
                await db.Servers.Where(row => row.Id == fixture.SecondServerId).ExecuteUpdateAsync(update => update.SetProperty(row => row.StartupCommand, (string?)null));
            });
            await Check("Delegated member management cannot promote itself, replace more privileged grants, or cross servers", async () =>
            {
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var targetId = Guid.NewGuid();
                var privilegedId = Guid.NewGuid();
                db.Users.AddRange(new User { Id = targetId, Username = "simple-member", Email = "simple-member@example.test", PasswordHash = "fixture" },
                    new User { Id = privilegedId, Username = "privileged-member", Email = "privileged-member@example.test", PasswordHash = "fixture" });
                await db.SaveChangesAsync();
                var roster = $"/api/servers/{fixture.SecondServerId:D}/members";
                string Member(Guid id) => $"{roster}/{id:D}";
                var actor = Member(fixture.OtherId);
                var target = Member(targetId);
                var stronger = Member(privilegedId);
                var permitted = new[] { "file.read", "user.read", "user.create", "user.update", "user.delete" };
                using var manifest = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, members = new[]
                    { new { server_uuid = fixture.SecondServerId, user_uuid = fixture.OtherId, permissions = permitted } } }));
                var imported = await PterodactylMemberImport.RunAsync(db, manifest, apply: true);
                Assert(imported is { Imported: 1, Issues.Count: 0, UnsupportedPermissions.Count: 0 }, "A supported Pterodactyl delegation was not imported");
                using var ownerGrant = await owner.PutAsJsonAsync(stronger, new { permissions = new[] { "control.stop" } });
                Assert(ownerGrant.StatusCode == HttpStatusCode.NoContent, "The synthetic privileged member could not be registered");

                using var rosterResponse = await other.GetAsync(roster);
                var rosterData = await rosterResponse.Content.ReadFromJsonAsync<JsonElement>();
                var members = rosterData.GetProperty("members").EnumerateArray().ToArray();
                Assert(rosterResponse.IsSuccessStatusCode && members.Length == 2 && members.All(member =>
                    !member.GetProperty("canEdit").GetBoolean() && !member.GetProperty("canRemove").GetBoolean()) &&
                    rosterData.GetProperty("availablePermissions").EnumerateArray().Select(item => item.GetString()).ToHashSet().SetEquals(permitted),
                    "The roster offered powers beyond the imported delegation or write access to protected members");

                using var overage = await other.PutAsJsonAsync(target, new { permissions = new[] { "control.stop" }, operation = "create" });
                using var ownUpdate = await other.PutAsJsonAsync(actor, new { permissions = Array.Empty<string>() });
                using var ownDelete = await other.DeleteAsync(actor);
                using var ownerUpdate = await other.PutAsJsonAsync(Member(fixture.OwnerId), new { permissions = Array.Empty<string>() });
                using var ownerDelete = await other.DeleteAsync(Member(fixture.OwnerId));
                using var higherUpdate = await other.PutAsJsonAsync(stronger, new { permissions = Array.Empty<string>() });
                using var higherDelete = await other.DeleteAsync(stronger);
                using var foreign = await other.PutAsJsonAsync($"/api/servers/{fixture.ServerId:D}/members/{targetId:D}",
                    new { permissions = new[] { "file.read" } });
                Assert(new[] { overage, ownUpdate, ownDelete, ownerUpdate, ownerDelete, higherUpdate, higherDelete, foreign }
                    .All(result => result.StatusCode == HttpStatusCode.Forbidden), "A delegated write changed its own rights, a protected membership, an owner, or a foreign server");

                var create = new { permissions = new[] { "file.read", "user.read" }, operation = "create" };
                var attempts = await Task.WhenAll(other.PutAsJsonAsync(target, create), other.PutAsJsonAsync(target, create));
                using var first = attempts[0];
                using var second = attempts[1];
                Assert(attempts.Count(result => result.StatusCode == HttpStatusCode.NoContent) == 1 &&
                    attempts.Count(result => result.StatusCode == HttpStatusCode.Conflict) == 1, "Explicit concurrent adds did not produce one grant");
                using var edit = await other.PutAsJsonAsync(target, new { permissions = new[] { "file.read" }, operation = "update" });
                Assert(edit.StatusCode == HttpStatusCode.NoContent && await db.ServerMembers.AnyAsync(member =>
                    member.ServerId == fixture.SecondServerId && member.UserId == targetId && member.Permissions == ServerPermission.FileList),
                    "A delegate could not edit a lower grant within its rights");

                using var createOnly = await owner.PutAsJsonAsync(actor, new { permissions = new[] { "user.create", "file.read" } });
                using var deniedRead = await other.GetAsync(roster);
                using var deniedUpdate = await other.PutAsJsonAsync(target, new { permissions = Array.Empty<string>() });
                using var deniedDelete = await other.DeleteAsync(target);
                using var updateOnly = await owner.PutAsJsonAsync(actor, new { permissions = new[] { "user.update", "file.read" } });
                using var deniedCreate = await other.PutAsJsonAsync(Member(Guid.NewGuid()), new { permissions = new[] { "file.read" } });
                using var deleteGrant = await owner.PutAsJsonAsync(actor, new { permissions = new[] { "user.delete", "file.read" } });
                using var remove = await other.DeleteAsync(target);
                using var removeActor = await owner.DeleteAsync(actor);
                using var deniedAfterRevoke = await other.PutAsJsonAsync(target, create);
                Assert(createOnly.IsSuccessStatusCode && updateOnly.IsSuccessStatusCode && deleteGrant.IsSuccessStatusCode &&
                    new[] { deniedRead, deniedUpdate, deniedDelete, deniedCreate, deniedAfterRevoke }.All(result => result.StatusCode == HttpStatusCode.Forbidden) &&
                    remove.StatusCode == HttpStatusCode.NoContent && removeActor.StatusCode == HttpStatusCode.NoContent &&
                    !await db.ServerMembers.AnyAsync(member => member.ServerId == fixture.SecondServerId && member.UserId == targetId),
                    "A membership action used a different delegation flag or survived revocation");
                using var cleanup = await owner.DeleteAsync(stronger);
                Assert(cleanup.StatusCode == HttpStatusCode.NoContent, "The owner could not revoke the synthetic privileged assignment");
                await db.Users.Where(user => user.Id == targetId || user.Id == privilegedId).ExecuteDeleteAsync();
            });
            if (OperatingSystem.IsLinux())
            {
                await Check("Linux migration receipts preserve the spelling of contained symbolic links and reject unavailable targets", async () =>
                {
                    var volume = Directory.CreateDirectory(Path.Combine(fixture.Root, "migration-links")).FullName;
                    var data = Directory.CreateDirectory(Path.Combine(volume, "data")).FullName;
                    var game = Directory.CreateDirectory(Path.Combine(volume, "game")).FullName;
                    await File.WriteAllTextAsync(Path.Combine(data, "first"), "game-data");
                    await File.WriteAllTextAsync(Path.Combine(data, "second"), "game-data");
                    Directory.CreateSymbolicLink(Path.Combine(game, "shared"), "../data");
                    var link = Path.Combine(game, "config");
                    File.CreateSymbolicLink(link, "../data/first");
                    var id = Guid.NewGuid();
                    var original = await Capture();
                    Assert(original is { SchemaVersion: 3, Links: 2, Files: 2, Directories: 2, Bytes: 18 } &&
                        (await Capture()).TreeSha256 == original.TreeSha256, "The receipt counted a symbolic link as a file or did not produce a stable hash");
                    File.Delete(link);
                    File.CreateSymbolicLink(link, "../data/second");
                    Assert((await Capture()).TreeSha256 != original.TreeSha256, "Changing a symbolic link target was invisible in the volume digest");

                    foreach (var target in new[] { Path.Combine(data, "first"), "../../outside", "../data/missing", "shared/first" })
                    {
                        File.Delete(link);
                        File.CreateSymbolicLink(link, target);
                        try
                        {
                            await Capture();
                            throw new InvalidOperationException("An unsupported symbolic link passed the volume scan.");
                        }
                        catch (IOException) { }
                    }

                    Task<VolumeVerification.VolumeReceipt> Capture() =>
                        VolumeVerification.CaptureAsync(id, volume, VolumeVerification.ContainedLinksVersion, CancellationToken.None);
                });
                await Check("Linux Zomboid volume verification reads both layouts without changing launchers, saves or the player database", async () =>
                {
                    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                    foreach (var managed in new[] { false, true })
                    {
                        var layout = managed ? "arcadenode" : "pterodactyl";
                        var volume = Directory.CreateDirectory(Path.Combine(fixture.Root, $"zomboid-{layout}")).FullName;
                        var installation = managed ? Directory.CreateDirectory(Path.Combine(volume, "server")).FullName : volume;
                        var state = Path.Combine(volume, managed ? "data" : ".cache");
                        var launcher = Path.Combine(installation, managed ? "start-server.sh" : "ProjectZomboid64");
                        await MakeLauncher(launcher);
                        if (!managed)
                        {
                            var java = Directory.CreateDirectory(Path.Combine(volume, "jre64", "bin")).FullName;
                            await MakeLauncher(Path.Combine(java, "java"));
                        }
                        var launchJson = Path.Combine(installation, "ProjectZomboid64.json");
                        await File.WriteAllTextAsync(launchJson, "{\"vmArgs\":[\"-Xmx2g\"]}");
                        const string configured = "Fort São";
                        var saveName = managed ? "Fort_So" : configured;
                        Directory.CreateDirectory(Path.Combine(state, "Saves", "Multiplayer", saveName));
                        Directory.CreateDirectory(Path.Combine(state, "Server"));
                        Directory.CreateDirectory(Path.Combine(state, "db"));
                        if (managed)
                        {
                            var active = Directory.CreateDirectory(Path.Combine(volume, "Zomboid")).FullName;
                            foreach (var part in new[] { "Saves", "Server", "db" })
                                Directory.CreateSymbolicLink(Path.Combine(active, part), $"../data/{part}");
                        }
                        await File.WriteAllTextAsync(Path.Combine(state, "Server", $"{saveName}.ini"), "DefaultPort=16261\nUDPPort=16262\n");
                        var dbPath = Path.Combine(state, "db", $"{saveName}.db");
                        await using (var players = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                            { DataSource = dbPath, Pooling = false }.ToString()))
                        {
                            await players.OpenAsync();
                            using var schema = players.CreateCommand();
                            schema.CommandText = "CREATE TABLE players(name TEXT); INSERT INTO players VALUES ('existing-player');";
                            await schema.ExecuteNonQueryAsync();
                        }
                        var original = await File.ReadAllBytesAsync(dbPath);
                        var settings = await ZomboidVolume.ReadAsync(volume, configured, CancellationToken.None);
                        var verified = await File.ReadAllBytesAsync(dbPath);
                        Assert(settings.Layout == layout && settings.SaveName == saveName && settings.DirectPort == 16262 &&
                            original.AsSpan().SequenceEqual(verified) &&
                            (await File.ReadAllTextAsync(launchJson)).Contains("-Xmx2g"), "Reading verification data changed the restored state");

                        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        await RequireRefusal();
                        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        await File.WriteAllBytesAsync(dbPath, original[..100]);
                        await RequireRefusal();

                        async Task RequireRefusal()
                        {
                            try
                            {
                                await ZomboidVolume.ReadAsync(volume, configured, CancellationToken.None);
                                throw new InvalidOperationException("An incomplete restore passed the Zomboid volume verification.");
                            }
                            catch (InvalidDataException) { }
                        }
                    }

                    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
                    static async Task MakeLauncher(string path)
                    {
                        await File.WriteAllTextAsync(path, "#!/bin/sh\nexit 0\n");
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                });
                await Check("Linux game file reader confines access to regular files in the assigned volume", async () =>
                {
                    var volume = Directory.CreateDirectory(Path.Combine(fixture.Root, "game-volume")).FullName;
                    var outside = Path.Combine(fixture.Root, "outside-game-volume.txt");
                    await File.WriteAllTextAsync(outside, "private");
                    await File.WriteAllTextAsync(Path.Combine(volume, "server.properties"), "motd=ArcadeNode");
                    File.CreateSymbolicLink(Path.Combine(volume, "link"), outside);
                    Directory.CreateSymbolicLink(Path.Combine(volume, "alias"), fixture.Root);
                    Directory.CreateDirectory(Path.Combine(volume, "world"));
                    using var files = new ArcadeNode.Node.LinuxGameFiles(volume);
                    using var reader = new StreamReader(files.OpenRead("server.properties"));
                    Assert(await reader.ReadToEndAsync() == "motd=ArcadeNode", "Regular game file was unreadable");
                    var root = files.List("", CancellationToken.None);
                    Assert(!root.Truncated && root.Entries.Count == 2 &&
                        root.Entries.Any(e => e.Name == "world" && e.IsDirectory) &&
                        root.Entries.Any(e => e.Name == "server.properties" && !e.IsDirectory),
                        "Directory listing was missing entries or showed links");
                    Assert(files.List("world", CancellationToken.None).Entries.Count == 0, "Empty subdirectory was not empty");
                    using var source = new MemoryStream(Encoding.UTF8.GetBytes("new-file"));
                    Assert(await files.UploadNewAsync("world/new.cfg", source, source.Length, CancellationToken.None), "Upload failed");
                    using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("overwritten"));
                    Assert(!await files.UploadNewAsync("world/new.cfg", replacement, replacement.Length, CancellationToken.None),
                        "Upload replaced an existing file");
                    using var partial = new MemoryStream(new byte[65537]);
                    try
                    {
                        await files.UploadNewAsync("world/partial.cfg", partial, partial.Length + 1, CancellationToken.None);
                        throw new InvalidOperationException("A truncated upload was accepted.");
                    }
                    catch (EndOfStreamException) { }
                    Assert(!File.Exists(Path.Combine(volume, "world", "partial.cfg")), "A truncated upload was published");
                    using var uploaded = new StreamReader(files.OpenRead("world/new.cfg"));
                    Assert(await uploaded.ReadToEndAsync() == "new-file", "Upload changed existing data");
                    foreach (var denied in new[] { "../outside-game-volume.txt", outside, "link", "alias/outside-game-volume.txt", "alias" })
                    {
                        try
                        {
                            using var escaped = files.OpenRead(denied);
                            throw new InvalidOperationException("Out-of-volume file was readable.");
                        }
                        catch (Exception error) when (error is ArgumentException or IOException)
                        {
                            // The only accepted outcomes are rejection by path validation or the kernel.
                        }
                    }
                });
            }
            await Check("TOTP accepts standard authenticator codes and rejects consumed time steps", () =>
            {
                var secret = Totp.DecodeSecret("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
                Assert(secret is not null && Encoding.ASCII.GetString(secret) == "12345678901234567890", "Base32 decoding failed");
                var time = DateTimeOffset.FromUnixTimeSeconds(59);
                Assert(Totp.MatchStep(secret, "287082", time, null) == 1, "RFC 6238 SHA-1 vector failed");
                Assert(Totp.MatchStep(secret, "287082", time, 1) is null, "Consumed time step was accepted");
                Assert(Totp.MatchStep(secret, "287082", time.AddSeconds(60), null) is null, "Expired code was accepted");
                Assert(Totp.MatchStep(secret, "abcdef", time, null) is null, "Invalid code was accepted");
                Assert(Totp.DecodeSecret("GEZDGNBVGY3TQOJ!") is null, "Invalid Base32 was accepted");
                return Task.CompletedTask;
            });
            await Check("Approved Pterodactyl administrators keep their original TOTP and receive panel administration", async () =>
            {
                const string base32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
                using (var scope = fixture.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                    var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                        .CreateProtector(Totp.ProtectionPurpose);
                    var id = Guid.NewGuid();
                    var passwordHash = BCrypt.Net.BCrypt.HashPassword(Password);
                    var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, users = new[]
                    {
                        new { uuid = id, username = "totp-owner", email = "totp-owner@example.test", name_first = "Totp",
                            name_last = "Owner", password = passwordHash, root_admin = true, use_totp = true, totp_secret = base32 }
                    } });
                    using var accountStream = new MemoryStream(manifest);
                    Assert(await PterodactylAccountImport.RunAsync(db, accountStream, true, protector) is
                        { Imported: 0, Issues: [{ Reason: "administrator_requires_review" }] }, "Unapproved administrator was imported");
                    accountStream.Position = 0;
                    var approved = new HashSet<Guid> { id };
                    using var missingTotp = new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest)
                        .Replace("\"use_totp\":true", "\"use_totp\":false")));
                    Assert(await PterodactylAccountImport.RunAsync(db, missingTotp, true, protector, approved) is
                        { Imported: 0, Issues: [{ Reason: "administrator_requires_totp" }] }, "Administrator bypassed TOTP during import");
                    Assert(await PterodactylAccountImport.RunAsync(db, accountStream, true, protector, approved) is { Imported: 1 }, "Approved administrator import failed");
                    accountStream.Position = 0;
                    Assert(await PterodactylAccountImport.RunAsync(db, accountStream, true, protector, approved) is { Imported: 0, AlreadyPresent: 1 },
                        "TOTP import was not idempotent");
                    var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
                    Assert(stored.Role == UserRole.Admin && stored.ProtectedTotpSecret is { } encrypted && !encrypted.Contains(base32),
                        "Incorrect imported administrator role or TOTP secret persisted in cleartext");
                }
                using var noCode = await anonymous.PostAsJsonAsync("/api/auth/login", new { username = "totp-owner", password = Password });
                Assert(noCode.StatusCode == HttpStatusCode.Unauthorized, "Password-only login bypassed TOTP");
                var counter = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
                var hash = System.Security.Cryptography.HMACSHA1.HashData(Encoding.ASCII.GetBytes("12345678901234567890"), counter);
                var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(hash[^1] & 15));
                var code = ((value & 0x7fff_ffff) % 1_000_000).ToString("D6");
                var input = new { username = "totp-owner", password = Password, totpCode = code };
                using var accepted = await anonymous.PostAsJsonAsync("/api/auth/login", input);
                Assert(accepted.IsSuccessStatusCode, "Valid TOTP login was rejected");
                using var importedAdmin = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
                var auth = await accepted.Content.ReadFromJsonAsync<JsonElement>();
                importedAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.GetProperty("token").GetString());
                using var dashboard = await importedAdmin.GetAsync("/api/admin/dashboard");
                Assert(dashboard.IsSuccessStatusCode, "Imported administrator could not use administrative API");
                using var replay = await anonymous.PostAsJsonAsync("/api/auth/login", input);
                Assert(replay.StatusCode == HttpStatusCode.Unauthorized, "TOTP replay signed in");
            });
            await Check("Pterodactyl egg imports preserve source and cannot provision containers", async () =>
            {
                const string v2 = """
                    {"meta":{"version":"PTDL_v2","update_url":null},"name":"Paper draft","description":"Test export",
                     "docker_images":{"Java 21":"example.test/java:21","Java 17":"example.test/java:17"},"startup":"java -jar {{SERVER_JARFILE}}",
                     "file_denylist":["private.txt"],"config":{"files":"{}","startup":{"done":"Done"},"stop":"stop"},
                     "scripts":{"installation":{"script":"echo metadata-only","container":"example.test/installer:1","entrypoint":"bash"}},
                     "variables":[{"name":"Server JAR","env_variable":"SERVER_JARFILE","default_value":"server.jar","user_viewable":true,"user_editable":0,"rules":"required|string"}]}
                    """;
                const string v1 = """
                    {"meta":{"version":"PTDL_v1"},"name":"Legacy draft","image":"example.test/legacy:1","startup":"./server","config":{"files":"{}"},"variables":[]}
                    """;
                using var nest = await admin.PostAsJsonAsync("/api/admin/egg-catalog/nests", new { name = "Migration test" });
                Assert(nest.StatusCode == HttpStatusCode.Created, "Failed to create egg nest");
                var nestId = (await nest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                var path = $"/api/admin/egg-catalog/nests/{nestId}/eggs";
                Task<HttpResponseMessage> Import(HttpClient client, string json) =>
                    client.PostAsync(path + "/import", new StringContent(json, Encoding.UTF8, "application/json"));

                var dockerRequests = fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count;
                using var forbidden = await Import(owner, v2);
                Assert(forbidden.StatusCode == HttpStatusCode.Forbidden, "Regular user can import eggs");
                using var invalid = await Import(admin, v2.Replace("\"name\":\"Paper draft\"", "\"name\":\"original\",\"name\":\"duplicate\""));
                Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Ambiguous egg JSON accepted");
                using var imported = await Import(admin, v2);
                Assert(imported.StatusCode == HttpStatusCode.Created, "PTDL_v2 export rejected");
                var id = (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                using var duplicate = await Import(admin, v2);
                Assert(duplicate.StatusCode == HttpStatusCode.OK && (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid() == id,
                    "Exact re-import created a duplicate");
                using var legacy = await Import(admin, v1);
                Assert(legacy.StatusCode == HttpStatusCode.Created, "PTDL_v1 export rejected");
                using var listing = await admin.GetAsync(path + "?pageSize=1");
                Assert(listing.IsSuccessStatusCode, $"Egg listing failed ({listing.StatusCode}): {await listing.Content.ReadAsStringAsync()}");
                Assert(listing.Headers.GetValues("X-Total-Count").Single() == "2", "Unexpected catalog total");
                var items = await listing.Content.ReadFromJsonAsync<JsonElement>();
                Assert(items.GetArrayLength() == 1 && !items[0].TryGetProperty("installScript", out _), "Catalog exposed installation script");
                using var exported = await admin.GetAsync(imported.Headers.Location);
                Assert((await exported.Content.ReadAsStringAsync()) == v2 && exported.Headers.CacheControl?.NoStore == true &&
                    exported.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Original export was changed or was made cacheable");
                using var deniedExport = await owner.GetAsync(imported.Headers.Location);
                Assert(deniedExport.StatusCode == HttpStatusCode.Forbidden, "Regular user can export eggs");
                using var createServer = await admin.PostAsJsonAsync("/api/servers", new
                {
                    name = "Draft server", nodeId = fixture.NodeId, eggId = id, dockerImage = "audit/synthetic:secure", port = 25566
                });
                Assert(createServer.StatusCode == HttpStatusCode.BadRequest, "Imported draft was eligible for provisioning");
                Assert(dockerRequests == fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count, "Egg import or provisioning contacted Docker");
                using var scope = fixture.Services.CreateScope();
                var stored = await scope.ServiceProvider.GetRequiredService<PanelDbContext>().Eggs.FindAsync(id);
                Assert(stored?.DockerImage == "example.test/java:21" && stored.ConfigStop == "stop" &&
                    JsonSerializer.Deserialize<EggVariable[]>(stored.Variables!) is [{ UserViewable: true, UserEditable: false }],
                    "Imported egg metadata was not normalized");

                var eggProvision = $"/api/servers/from-egg/{id}";
                var options = new { name = "Approved egg", nodeId = fixture.NodeId, port = 28565,
                    startupCommand = "do not execute imported startup", dockerImage = "example.test/java:21" };
                using var unmapped = await admin.PostAsJsonAsync(eggProvision, options);
                Assert(unmapped.StatusCode == HttpStatusCode.BadRequest, "Unmapped egg was provisioned");
                fixture.Services.GetRequiredService<IConfiguration>()[$"EggRuntimes:{stored!.PterodactylSha256}:CartridgeId"] = "minecraft-java";
                using var runtime = await admin.GetAsync($"/api/admin/egg-catalog/eggs/{id}/runtime");
                var selectedRuntime = await runtime.Content.ReadFromJsonAsync<JsonElement>();
                Assert(selectedRuntime.GetProperty("cartridgeId").GetString() == "minecraft-java", "Approved egg profile was not resolved");
                using var approved = await admin.PostAsJsonAsync(eggProvision, options);
                Assert(approved.StatusCode == HttpStatusCode.Created, "Managed egg profile could not provision: " + await approved.Content.ReadAsStringAsync());
                var createdFromEgg = await approved.Content.ReadFromJsonAsync<JsonElement>();
                var serverId = createdFromEgg.GetProperty("id").GetGuid();
                var linked = await scope.ServiceProvider.GetRequiredService<PanelDbContext>().Servers.AsNoTracking().SingleAsync(s => s.Id == serverId);
                Assert(linked.EggId == id && linked.StartupCommand is null, "Spawn did not retain the egg or accepted arbitrary startup");
                var request = fixture.FirstNode.Requests.Last(r => r.Path.EndsWith("/containers/create"));
                Assert(!request.Body.Contains("example.test/java") && !request.Body.Contains("do not execute") && !request.Body.Contains("metadata-only"),
                    "Imported commands, images or scripts reached Docker");
                using var deleteFromEgg = await admin.DeleteAsync($"/api/servers/{serverId}");
                Assert(deleteFromEgg.StatusCode == HttpStatusCode.NoContent, "Could not release egg test resources");

                foreach (var eggFile in eggFiles)
                {
                    var manifest = await File.ReadAllTextAsync(eggFile);
                    using var realEgg = await Import(admin, manifest);
                    Assert(realEgg.StatusCode == HttpStatusCode.Created,
                        $"Egg fixture {Path.GetFileName(eggFile)} was rejected: {await realEgg.Content.ReadAsStringAsync()}");
                    using var realExport = await admin.GetAsync(realEgg.Headers.Location);
                    using var source = JsonDocument.Parse(manifest);
                    Assert(await realExport.Content.ReadAsStringAsync() == source.RootElement.GetRawText(), "Egg fixture failed round trip");
                }
            });
            await Check("Approved egg mappings carry their defaults and enforce visibility and editability during provisioning", async () =>
            {
                using var nest = await admin.PostAsJsonAsync("/api/admin/egg-catalog/nests", new { name = "Mapped egg fixtures" });
                var nestId = (await nest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                const string hidden = "Synthetic-private-rcon-default";
                using var manifest = JsonDocument.Parse($$"""
                    {"meta":{"version":"PTDL_v2"},"name":"Synthetic Vanilla","docker_images":{"Imported":"example.test/java:unknown"},
                     "startup":"unsupported imported commands", "variables":[
                       {"name":"Vanilla version","env_variable":"VANILLA_VERSION","default_value":"latest","user_viewable":true,"user_editable":true},
                       {"name":"Fixed source JAR","env_variable":"SERVER_JARFILE","default_value":"world-runtime.jar","user_viewable":true,"user_editable":false},
                       {"name":"Private operator value","env_variable":"PRIVATE_RCON","default_value":"{{hidden}}","user_viewable":false,"user_editable":true}
                     ]}
                    """);
                using var imported = await admin.PostAsJsonAsync($"/api/admin/egg-catalog/nests/{nestId}/eggs/import", manifest.RootElement);
                Assert(imported.StatusCode == HttpStatusCode.Created, "Mapped egg fixture could not be imported");
                var egg = await imported.Content.ReadFromJsonAsync<JsonElement>();
                var eggId = egg.GetProperty("id").GetGuid();
                var config = fixture.Services.GetRequiredService<IConfiguration>();
                var profile = $"EggRuntimes:{egg.GetProperty("sha256").GetString()}";
                config[$"{profile}:CartridgeId"] = "minecraft-java";
                config[$"{profile}:FixedValues:SERVER_TYPE"] = "vanilla";
                config[$"{profile}:VariableMap:VANILLA_VERSION"] = "MC_VERSION";
                config[$"{profile}:VariableMap:SERVER_JARFILE"] = "SERVER_JAR";
                config[$"{profile}:VariableMap:PRIVATE_RCON"] = "RCON_PASSWORD";
                var cartridge = await admin.GetFromJsonAsync<JsonElement>("/api/cartridges/minecraft-java");
                Assert(cartridge.GetProperty("resources").GetProperty("cpu").GetProperty("unit").GetString() == "cores",
                    "The wizard cannot distinguish cartridge CPU cores from a Docker CPU percentage");
                var runtimeRoute = $"/api/admin/egg-catalog/eggs/{eggId}/runtime";
                using var runtime = await admin.GetAsync(runtimeRoute);
                using var settings = JsonDocument.Parse(await runtime.Content.ReadAsStringAsync());
                var variables = settings.RootElement.GetProperty("variables").EnumerateArray()
                    .ToDictionary(rule => rule.GetProperty("envVariable").GetString()!);
                Assert(variables["MC_VERSION"].GetProperty("defaultValue").GetString() == "latest" &&
                    variables["MC_VERSION"].GetProperty("userEditable").GetBoolean() &&
                    !variables["SERVER_JAR"].GetProperty("userEditable").GetBoolean() &&
                    variables["SERVER_TYPE"].GetProperty("defaultValue").GetString() == "vanilla" &&
                    !variables["RCON_PASSWORD"].GetProperty("userViewable").GetBoolean() &&
                    !settings.RootElement.GetRawText().Contains(hidden), "Runtime summary discarded public options or exposed a private default");

                Task<HttpResponseMessage> Deploy(Dictionary<string, object> supplied) => admin.PostAsJsonAsync($"/api/servers/from-egg/{eggId}", new
                {
                    name = "Vanilla mapped", nodeId = fixture.NodeId, port = 28567, variables = supplied
                });
                var before = fixture.FirstNode.Requests.Count;
                using var changedType = await Deploy(new() { ["server_type"] = "paper" });
                using var changedJar = await Deploy(new() { ["SERVER_JARFILE"] = "new.jar" });
                using var changedHidden = await Deploy(new() { ["rcon_password"] = "override" });
                using var duplicate = await Deploy(new() { ["MC_VERSION"] = "1.21.1", ["VANILLA_VERSION"] = "1.21.4" });
                using var unknown = await Deploy(new() { ["STARTUP"] = "new value" });
                using var invalidValue = await Deploy(new() { ["VANILLA_VERSION"] = new { version = "1.21.1" } });
                Assert(new[] { changedType, changedJar, changedHidden, duplicate, unknown, invalidValue }.All(response =>
                    response.StatusCode == HttpStatusCode.BadRequest) && fixture.FirstNode.Requests.Count == before,
                    "An invalid or unauthorized egg override contacted Docker");

                config[$"{profile}:VariableMap:PRIVATE_RCON"] = "SERVER_JAR";
                using var invalidMapping = await admin.GetAsync(runtimeRoute);
                using var invalidProvision = await Deploy([]);
                config[$"{profile}:VariableMap:PRIVATE_RCON"] = "RCON_PASSWORD";
                Assert(invalidMapping.StatusCode == HttpStatusCode.BadRequest && invalidProvision.StatusCode == HttpStatusCode.BadRequest &&
                    fixture.FirstNode.Requests.Count == before, "Conflicting variable mappings could provision a game");

                using var provisioned = await Deploy(new() { ["VANILLA_VERSION"] = "1.21.1", ["MOTD"] = "{{RCON_PASSWORD}}" });
                Assert(provisioned.StatusCode == HttpStatusCode.Created,
                    $"Approved mapped game creation failed: {await provisioned.Content.ReadAsStringAsync()}");
                var server = await provisioned.Content.ReadFromJsonAsync<JsonElement>();
                var serverId = server.GetProperty("id").GetGuid();
                var request = fixture.FirstNode.Requests.Skip(before).Single(record => record.Path.EndsWith("/containers/create"));
                using var docker = JsonDocument.Parse(request.Body);
                var env = docker.RootElement.GetProperty("Env").EnumerateArray().Select(item => item.GetString()).ToHashSet();
                Assert(env.Contains("SERVER_TYPE=vanilla") && env.Contains("MC_VERSION=1.21.1") &&
                    env.Contains("SERVER_JAR=world-runtime.jar") && env.Contains($"RCON_PASSWORD={hidden}") &&
                    env.Contains("MOTD={{RCON_PASSWORD}}") && !request.Body.Contains("unsupported imported commands"),
                    "The assigned node did not receive the approved values as literal environment variables");
                using var removed = await admin.DeleteAsync($"/api/servers/{serverId}");
                Assert(removed.StatusCode == HttpStatusCode.NoContent, "Could not release the mapped test game");
            });
            await Check("Zomboid migration preserves the Steam branch and takes administrator credentials from the restored database", async () =>
            {
                using var nest = await admin.PostAsJsonAsync("/api/admin/egg-catalog/nests", new { name = "Zomboid restore fixture" });
                var nestId = (await nest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                using var manifest = JsonDocument.Parse("""
                    {"meta":{"version":"PTDL_v2"},"name":"Zomboid restore fixture","docker_images":{"Imported":"example.test/steamcmd"},
                     "startup":"./ProjectZomboid64", "variables":[
                       {"name":"Steam branch","env_variable":"SRCDS_BETAID","default_value":"legacy41","user_viewable":true,"user_editable":true},
                       {"name":"Update","env_variable":"AUTO_UPDATE","default_value":"0","user_viewable":true,"user_editable":true},
                       {"name":"Legacy administrator password","env_variable":"ADMIN_PASSWORD","default_value":"oldpass","user_viewable":false,"user_editable":true}
                     ]}
                    """);
                using var imported = await admin.PostAsJsonAsync($"/api/admin/egg-catalog/nests/{nestId}/eggs/import", manifest.RootElement);
                Assert(imported.StatusCode == HttpStatusCode.Created, "Zomboid egg fixture did not import.");
                var egg = await imported.Content.ReadFromJsonAsync<JsonElement>();
                using var scope = fixture.Services.CreateScope();
                var provider = scope.ServiceProvider;
                var config = provider.GetRequiredService<IConfiguration>();
                var cartridges = provider.GetRequiredService<ICartridgeService>();
                var digest = egg.GetProperty("sha256").GetString();
                var profile = $"EggRuntimes:{digest}";
                config[$"{profile}:CartridgeId"] = "project-zomboid";
                config[$"{profile}:VariableMap:SRCDS_BETAID"] = "BETA_BRANCH";
                config[$"{profile}:VariableMap:AUTO_UPDATE"] = "AUTO_UPDATE";
                config[$"{profile}:VariableMap:ADMIN_PASSWORD"] = "ADMIN_PASSWORD";

                var metadata = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/egg-catalog/eggs/{egg.GetProperty("id").GetGuid()}/runtime");
                var defaults = metadata.GetProperty("variables").EnumerateArray()
                    .ToDictionary(row => row.GetProperty("envVariable").GetString()!,
                        row => row.TryGetProperty("defaultValue", out var value) ? value.GetString() : null);
                Assert(defaults["BETA_BRANCH"] == "legacy41" && defaults["AUTO_UPDATE"] == "false" && defaults["ADMIN_PASSWORD"] is null,
                    "The profile lost the source branch or exposed its private password default");

                var cartridge = cartridges.GetCartridgeById("project-zomboid") ?? throw new InvalidOperationException("Zomboid cartridge did not load.");
                var variables = new Dictionary<string, object> {
                    ["BETA_BRANCH"] = defaults["BETA_BRANCH"]!, ["AUTO_UPDATE"] = defaults["AUTO_UPDATE"]!,
                    ["NO_STEAM"] = true, ["ADMIN_PASSWORD"] = "oldpass"
                };
                Assert(cartridges.ValidateVariables(cartridge, variables, forRestore: true).IsValid &&
                    !cartridges.ValidateVariables(cartridge, variables).IsValid,
                    "The restored game incorrectly depended on the policy for bootstrap passwords");
                var ports = new Dictionary<string, int> { ["steam"] = 27079 };
                var restored = cartridges.BuildEnvironmentVariables(cartridge, variables, 27078, ports, forRestore: true);
                Assert(restored["BETA_BRANCH"] == "legacy41" && restored["AUTO_UPDATE"] == "false" &&
                    restored["STEAM_PORT"] == "27079" && restored["NO_STEAM"] == "true" && !restored.ContainsKey("ADMIN_PASSWORD"),
                    "The restored process lost its version, Steam mode or approved port, or received a bootstrap password");

                variables["ADMIN_PASSWORD"] = Password;
                Assert(cartridges.ValidateVariables(cartridge, variables).IsValid &&
                    cartridges.BuildEnvironmentVariables(cartridge, variables, 27078)["ADMIN_PASSWORD"] == Password,
                    "A fresh Zomboid server lost its bootstrap credentials");
                variables["BETA_BRANCH"] = "legacy41 +quit";
                Assert(!cartridges.ValidateVariables(cartridge, variables, forRestore: true).IsValid,
                    "A malformed branch reached the game environment");
            });
            await Check("Bedrock restore keeps the disk configuration and binds the approved IPv6 port", () =>
            {
                using var scope = fixture.Services.CreateScope();
                var cartridges = scope.ServiceProvider.GetRequiredService<ICartridgeService>();
                var cartridge = cartridges.GetCartridgeById("minecraft-bedrock") ?? throw new InvalidOperationException("Bedrock cartridge did not load");
                var values = new Dictionary<string, object> { ["VERSION"] = "1.26.66.1", ["SERVER_NAME"] = "Recovered Bedrock", ["ALLOW_CHEATS"] = true };
                Assert(cartridges.ValidateVariables(cartridge, values).IsValid,
                    "Bedrock rejected valid settings from a runtime profile");
                var restore = cartridges.BuildEnvironmentVariables(cartridge, values, 20123, forRestore: true);
                Assert(restore["SERVER_PORTV6"] == "19133" && !restore.ContainsKey("VERSION") &&
                    !restore.ContainsKey("SERVER_NAME") && !restore.ContainsKey("ALLOW_CHEATS"),
                    "Bedrock changed the unpublished listener or sent install settings to a restored world");
                var named = cartridges.BuildEnvironmentVariables(cartridge, values, 20123,
                    new Dictionary<string, int> { ["ipv6"] = 20155 }, forRestore: true);
                Assert(named["SERVER_PORT"] == "20123" && named["SERVER_PORTV6"] == "20155",
                    "The approved Bedrock listener did not override the default");
                return Task.CompletedTask;
            });
            await Check("Reforger provisions and reserves its required Steam query port", async () =>
            {
                var before = fixture.FirstNode.Requests.Count;
                using var response = await admin.PostAsJsonAsync("/api/servers/from-cartridge", new
                {
                    name = "Reforger allocation fixture", nodeId = fixture.NodeId, cartridgeId = "arma-reforger",
                    memoryLimit = 4096, cpuLimit = 100, diskLimit = 4096, port = 22001,
                    variables = new Dictionary<string, object> { ["ADMIN_PASSWORD"] = Password }
                });
                Assert(response.StatusCode == HttpStatusCode.Created, "Reforger provisioning failed: " + await response.Content.ReadAsStringAsync());
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                var serverId = body.GetProperty("id").GetGuid();
                Assert(body.GetProperty("additionalPorts").EnumerateArray().Single().GetInt32() == 17777,
                    "The response omitted the published query port");
                var create = fixture.FirstNode.Requests.Skip(before).Single(request => request.Path.EndsWith("/containers/create"));
                using var payload = JsonDocument.Parse(create.Body);
                var env = payload.RootElement.GetProperty("Env").EnumerateArray().Select(item => item.GetString()).ToHashSet();
                var bindings = payload.RootElement.GetProperty("HostConfig").GetProperty("PortBindings");
                Assert(env.Contains("A2S_PORT=17777") && bindings.TryGetProperty("17777/udp", out _) &&
                    !bindings.TryGetProperty("19999/udp", out _), "The Docker bindings diverged from the cartridge");
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                Assert(await db.Allocations.AnyAsync(allocation => allocation.ServerId == serverId &&
                    allocation.Port == 17777 && allocation.IsAssigned), "The query port was published without a reservation");
                using var conflict = await admin.PostAsJsonAsync("/api/servers/from-cartridge", new
                {
                    name = "Reserved port fixture", nodeId = fixture.NodeId, cartridgeId = "minecraft-java",
                    memoryLimit = 2048, cpuLimit = 100, diskLimit = 4096, port = 17777
                });
                Assert(conflict.StatusCode == HttpStatusCode.BadRequest, "A second game claimed the query port");
                using var removed = await admin.DeleteAsync($"/api/servers/{serverId}");
                Assert(removed.StatusCode == HttpStatusCode.NoContent, "Could not release the Reforger test game");
                Assert(await db.Allocations.AnyAsync(allocation => allocation.NodeId == fixture.NodeId &&
                    allocation.Port == 17777 && allocation.ServerId == null && !allocation.IsAssigned), "Deletion leaked the reservation");
            });
            await Check("Bedrock and Reforger verification reads listener settings without exporting credentials", async () =>
            {
                const string bedrockConfig = "server-port=23132\r\nserver-portv6=23133\r\nlevel-name=Existing world\r\n";
                const string reforgerConfig = """
                    {"bindPort":23001,"publicPort":23001,"a2s":{"port":23002},"rcon":{"port":23003,"password":"PrivateFixtureOnly1"},
                     "game":{"passwordAdmin":"PrivateFixtureOnly2"}}
                    """;
                var bedrock = RestoredGameVolumes.ReadBedrockSettings("arcadenode", bedrockConfig);
                Assert(bedrock.GamePort == 23132 && bedrock.Ipv6Port == 23133, "Bedrock listener parsing failed");
                AssertThrows<InvalidDataException>(() => RestoredGameVolumes.ReadBedrockSettings("arcadenode", bedrockConfig + "server-port=23132\n"));
                AssertThrows<InvalidDataException>(() => RestoredGameVolumes.ReadBedrockSettings("arcadenode", bedrockConfig.Replace("23133", "23132")));
                using var config = JsonDocument.Parse(reforgerConfig);
                var reforger = RestoredGameVolumes.ReadReforgerSettings("pterodactyl", config.RootElement);
                Assert(reforger.GamePort == 23001 && reforger.PublicPort == 23001 && reforger.A2sPort == 23002 &&
                    reforger.RconPort == 23003 && !JsonSerializer.Serialize(reforger).Contains("PrivateFixtureOnly"),
                    "Reforger listener evidence was incomplete or included a secret");
                using var invalid = JsonDocument.Parse(reforgerConfig.Replace("PrivateFixtureOnly1", "short"));
                AssertThrows<InvalidDataException>(() => RestoredGameVolumes.ReadReforgerSettings("pterodactyl", invalid.RootElement));
                using var legacy = JsonDocument.Parse("""{"gameHostBindPort":2001,"a2sQueryEnabled":true}""");
                Assert(RestoredGameVolumes.ReadReforgerSettings("pterodactyl", legacy.RootElement).A2sPort == 17777,
                    "The legacy query default did not survive migration");

                if (!OperatingSystem.IsLinux()) return;
                var bedrockRoot = Path.Combine(fixture.Root, "restore-bedrock-volume");
                Directory.CreateDirectory(Path.Combine(bedrockRoot, "server"));
                Directory.CreateDirectory(Path.Combine(bedrockRoot, "data/worlds"));
                Directory.CreateSymbolicLink(Path.Combine(bedrockRoot, "server/worlds"), "../data/worlds");
                var bedrockBinary = Path.Combine(bedrockRoot, "server/bedrock_server");
                await File.WriteAllTextAsync(bedrockBinary, "launcher fixture");
                File.SetUnixFileMode(bedrockBinary, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                await File.WriteAllTextAsync(Path.Combine(bedrockRoot, "server/server.properties"), bedrockConfig);
                Assert(await RestoredGameVolumes.ReadBedrockAsync(bedrockRoot, CancellationToken.None) == bedrock,
                    "Contained Bedrock world layout did not verify");

                var reforgerRoot = Path.Combine(fixture.Root, "restore-reforger-volume");
                Directory.CreateDirectory(Path.Combine(reforgerRoot, "profile"));
                Directory.CreateDirectory(Path.Combine(reforgerRoot, "addons/data"));
                var reforgerBinary = Path.Combine(reforgerRoot, "ArmaReforgerServer");
                await File.WriteAllTextAsync(reforgerBinary, "launcher fixture");
                File.SetUnixFileMode(reforgerBinary, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                await File.WriteAllTextAsync(Path.Combine(reforgerRoot, "addons/data/ArmaReforger.gproj"), "project fixture");
                await File.WriteAllTextAsync(Path.Combine(reforgerRoot, "config.json"), reforgerConfig);
                Assert(await RestoredGameVolumes.ReadReforgerAsync(reforgerRoot, CancellationToken.None) == reforger,
                    "The Reforger volume did not verify");
            });
            await Check("Bedrock and Reforger migrations require compatible listeners at both cutover gates", async () =>
            {
                foreach (var game in new[] { "minecraft_bedrock", "arma_reforger" })
                {
                    var isBedrock = game == "minecraft_bedrock";
                    var id = Guid.NewGuid();
                    var port = isBedrock ? 23150 : 23160;
                    var secondary = isBedrock ? new[] { 23155 } : new[] { 23165, 23166 };
                    var image = isBedrock ? "audit/synthetic-bedrock:secure" : "audit/synthetic-reforger:secure";
                    using var scope = fixture.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                    db.Servers.Add(new GameServer
                    {
                        Id = id, Name = game, Identifier = id.ToString("N")[..8], NodeId = fixture.NodeId, OwnerId = fixture.OwnerId,
                        DockerImage = image, Port = port, AdditionalPorts = JsonSerializer.Serialize(secondary),
                        EnvironmentVariables = isBedrock ? """{"SERVER_PORTV6":"23155"}""" : "{}",
                        MemoryLimit = 4096, CpuLimit = 100, DiskLimit = 4096,
                        MigrationPending = true, IsSuspended = true, Status = ServerStatus.Suspended
                    });
                    await db.SaveChangesAsync();
                    var digest = new string('B', 64);
                    var captured = DateTimeOffset.UtcNow;
                    var process = new { container_id = new string('F', 64), stopped_at = captured.AddMinutes(-1) };
                    object Settings(int listener) => isBedrock
                        ? new { layout = "arcadenode", game_port = port, ipv6_port = listener }
                        : new { layout = "pterodactyl", game_port = port, public_port = port, a2s_port = listener, rcon_port = secondary[1] };
                    object Verification(object? settings) => new Dictionary<string, object?>
                    {
                        ["mode"] = "verify_volume", ["matches"] = true, [game] = settings,
                        ["source"] = new { schema_version = 3, server_id = id, files = 5, directories = 4, bytes = 16, links = 0,
                            tree_sha256 = digest, captured_at = captured, source_process = process },
                        ["destination"] = new { schema_version = 3, server_id = id, files = 5, directories = 4, bytes = 16, links = 0,
                            tree_sha256 = digest, captured_at = DateTimeOffset.UtcNow }
                    };
                    object Fence(object? settings) => new { schema_version = 2, server_id = id, tree_sha256 = digest,
                        source_process = process, writes_fenced = true, observed_at = DateTimeOffset.UtcNow,
                        destination_verification = Verification(settings) };
                    var route = $"/api/admin/migrations/servers/{id:D}";
                    fixture.FirstNode.SeedRestoredVolume(id, 4096, digest);
                    var before = fixture.FirstNode.Requests.Count;
                    foreach (var invalid in new[] { null, Settings(port), Settings(23190) })
                    {
                        using var rejected = await admin.PostAsJsonAsync(route + "/prepare-restored", Verification(invalid));
                        Assert(rejected.StatusCode == HttpStatusCode.BadRequest, $"{game} prepared an invalid restore");
                    }
                    Assert(fixture.FirstNode.Requests.Count == before, "An invalid restore reached Docker");
                    var valid = Settings(secondary[0]);
                    using var prepared = await admin.PostAsJsonAsync(route + "/prepare-restored", Verification(valid));
                    Assert(prepared.StatusCode == HttpStatusCode.NoContent, $"{game} did not prepare: {await prepared.Content.ReadAsStringAsync()}");
                    fixture.FirstNode.ReportStopped = true;
                    try
                    {
                        using var rejected = await admin.PostAsJsonAsync(route + "/release-prepared", Fence(Settings(23190)));
                        Assert(rejected.StatusCode == HttpStatusCode.BadRequest, $"{game} released an invalid restore");
                        using var released = await admin.PostAsJsonAsync(route + "/release-prepared", Fence(valid));
                        Assert(released.StatusCode == HttpStatusCode.NoContent, $"{game} did not release: {await released.Content.ReadAsStringAsync()}");
                    }
                    finally { fixture.FirstNode.ReportStopped = false; }
                    using var removed = await admin.DeleteAsync($"/api/servers/{id:D}");
                    Assert(removed.StatusCode == HttpStatusCode.NoContent, $"Could not release the {game} test fixture");
                }
            });
            await Check("Foreign server and deployment status both return 403", async () =>
            {
                Assert((await other.GetAsync($"/api/servers/{fixture.ServerId}")).StatusCode == HttpStatusCode.Forbidden, "Foreign detail allowed");
                Assert((await other.GetAsync($"/api/deployment/status/{fixture.ServerId}")).StatusCode == HttpStatusCode.Forbidden, "Foreign deployment status allowed");
                Assert((await owner.GetAsync($"/api/deployment/status/{fixture.ServerId}")).IsSuccessStatusCode, "Owner deployment status rejected");
            });
            await Check("Owners can edit identification but cannot change resources or executable startup", async () =>
            {
                var response = await owner.PutAsJsonAsync($"/api/servers/{fixture.ServerId}", new { name = "Renamed", memoryLimit = 8192, startupCommand = "id" });
                Assert(response.StatusCode == HttpStatusCode.BadRequest, "Resource/startup update was allowed");
                response = await owner.PutAsJsonAsync($"/api/servers/{fixture.ServerId}", new { name = "Valid name", description = "Safe edit" });
                Assert(response.IsSuccessStatusCode, "Identification update rejected");
                using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var server = await db.Servers.FindAsync(fixture.ServerId);
                Assert(server!.Name == "Valid name" && server.MemoryLimit == 4096 && server.StartupCommand is null, "Rejected update leaked into audit SaveChanges");
            });
            await Check("Cartridge creation rejects invalid limits before provisioning", async () =>
            {
                var response = await admin.PostAsJsonAsync("/api/servers/from-cartridge", new { name = "Invalid", nodeId = fixture.NodeId, cartridgeId = "minecraft-java", memoryLimit = 0, cpuLimit = -1, diskLimit = 0, port = -1 });
                Assert(response.StatusCode == HttpStatusCode.BadRequest, "Invalid cartridge resources accepted");
            });
            await Check("Memory uses exact 64-bit limits; CPU, pids and root filesystem are constrained", () =>
            {
                foreach (var memory in new[] { 1024, 2048, 4096, 8192 })
                {
                    var policy = ContainerPolicy.Build(new GameServer { MemoryLimit = memory, CpuLimit = 200, DiskLimit = 1024, Port = 25565 }, fixture.Enrollment(fixture.FirstNode), new ApprovedGameImage { Image = "audit/synthetic:secure", DataPath = "/home/audit" }, "synthetic");
                    Assert(policy.HostConfig.Memory == (long)memory * 1024 * 1024 && policy.HostConfig.MemorySwap == policy.HostConfig.Memory, "Memory cap overflow");
                    Assert(policy.HostConfig.NanoCPUs == 2_000_000_000 && policy.HostConfig.PidsLimit == 512 && policy.HostConfig.ReadonlyRootfs && !policy.HostConfig.Privileged, "Isolation policy missing");
                    Assert(policy.User == "10001:10001" && policy.Cmd is null && policy.HostConfig.CapDrop.Contains("ALL"), "Executable startup or root user allowed");
                }
                return Task.CompletedTask;
            });
            await Check("Unenrolled, plaintext, credential-bearing and path-bearing Docker origins are rejected", async () =>
            {
                var before = fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count;
                foreach (var endpoint in new[] { "http://127.0.0.1:2375", "https://127.0.0.1:1", fixture.FirstNode.Endpoint + "/info", "https://user:pass@localhost:2376" })
                {
                    var response = await admin.PostAsJsonAsync("/api/nodes/test-connection", new { dockerEndpoint = endpoint });
                    Assert(response.StatusCode == HttpStatusCode.BadRequest, "Unenrolled Docker origin accepted");
                }
                Assert(before == fixture.FirstNode.Requests.Count + fixture.SecondNode.Requests.Count, "Rejected destination was contacted");
            });
            await Check("Enrolled node is reachable with mutual TLS", async () =>
            {
                var client = fixture.Services.GetRequiredService<IDockerClientFactory>().GetClient(fixture.FirstNode.Endpoint);
                await client.System.GetSystemInfoAsync();
                var before = fixture.FirstNode.Requests.Count;
                var response = await admin.PostAsJsonAsync("/api/nodes/test-connection", new { dockerEndpoint = fixture.FirstNode.Endpoint });
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert(response.IsSuccessStatusCode && result.GetProperty("success").GetBoolean(), "mTLS node unavailable");
                Assert(result.GetProperty("containerCount").GetInt32() == 7 && result.GetProperty("logicalCpuCount").GetInt32() == 12 &&
                    result.GetProperty("memoryTotal").GetInt64() == 64L * 1024 * 1024 * 1024 &&
                    result.GetProperty("memoryAvailable").ValueKind == JsonValueKind.Null, "Node capacity diverged from the Docker report");
                Assert(fixture.FirstNode.Requests.Skip(before).All(request => !request.Path.EndsWith("/containers/json", StringComparison.Ordinal)),
                    "Node connection testing still enumerates containers");
            });
            await Check("Mutual TLS rejects missing client credentials, wrong CAs and wrong hostnames", async () =>
            {
                static async Task Rejected(Func<Task> action)
                {
                    try { await action(); }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or System.Security.Authentication.AuthenticationException) { return; }
                    throw new InvalidOperationException("Invalid TLS trust was accepted");
                }
                using var noCertificateHandler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
                    ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && SecurityFixture.Trusts(cert, fixture.Ca) };
                using var noCertificateClient = new HttpClient(noCertificateHandler) { Timeout = TimeSpan.FromSeconds(5) };
                var before = fixture.FirstNode.Requests.Count;
                await Rejected(async () => { using var response = await noCertificateClient.GetAsync(fixture.FirstNode.Endpoint + "/info"); response.EnsureSuccessStatusCode(); });
                Assert(before == fixture.FirstNode.Requests.Count, "Unauthenticated TLS request reached Docker");

                DockerClientFactory Factory(EnrolledDockerNode node)
                {
                    var path = Path.Combine(fixture.Root, Guid.NewGuid() + ".json");
                    File.WriteAllText(path, JsonSerializer.Serialize(new { Docker = new { Nodes = new[] { node } } }));
                    var config = new ConfigurationBuilder().AddJsonFile(path).Build();
                    return new DockerClientFactory(new DockerEnrollment(config, fixture.Services.GetRequiredService<IWebHostEnvironment>()));
                }
                using var wrongKey = System.Security.Cryptography.RSA.Create(2048);
                var wrongCaRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Wrong Synthetic CA", wrongKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                using var wrongCa = wrongCaRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                var caPath = Path.Combine(fixture.Root, "wrong-ca.pem"); File.WriteAllText(caPath, wrongCa.ExportCertificatePem());
                var wrongEnrollment = fixture.Enrollment(fixture.FirstNode); wrongEnrollment.CaCertificatePath = caPath;
                using var wrongCaFactory = Factory(wrongEnrollment);
                await Rejected(async () => { await wrongCaFactory.GetClient(fixture.FirstNode.Endpoint).System.GetSystemInfoAsync(); });
                using var wrongHostname = fixture.IssueCertificate("different.example.test", true, false);
                await using var wrongHostnameNode = new FakeDockerNode(fixture, wrongHostname); await wrongHostnameNode.StartAsync();
                using var wrongHostnameFactory = Factory(fixture.Enrollment(wrongHostnameNode));
                await Rejected(async () => { await wrongHostnameFactory.GetClient(wrongHostnameNode.Endpoint).System.GetSystemInfoAsync(); });
                Assert(wrongHostnameNode.Requests.IsEmpty, "Wrong-hostname TLS request reached Docker");
            });
            await Check("Timed-out TLS handshakes close the underlying connection", async () =>
            {
                await using var stalled = new FakeDockerNode(fixture) { HangHandshake = true }; await stalled.StartAsync();
                var path = Path.Combine(fixture.Root, "stalled.json");
                File.WriteAllText(path, JsonSerializer.Serialize(new { Docker = new { Nodes = new[] { fixture.Enrollment(stalled) } } }));
                var config = new ConfigurationBuilder().AddJsonFile(path).Build();
                using var factory = new DockerClientFactory(new DockerEnrollment(config, fixture.Services.GetRequiredService<IWebHostEnvironment>()));
                using var cancel = new CancellationTokenSource();
                var pending = factory.GetClient(stalled.Endpoint).System.GetSystemInfoAsync(cancel.Token);
                await stalled.HandshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancel.Cancel();
                var denied = false;
                try { await pending; }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or System.Security.Authentication.AuthenticationException) { denied = true; }
                Assert(denied, "Stalled TLS handshake completed unexpectedly");
                Assert(await Task.WhenAny(stalled.HandshakeDisconnected.Task, Task.Delay(5000)) == stalled.HandshakeDisconnected.Task, "Cancelled handshake outlived the connection timeout");
            });
            await Check("Images require exact approval and data quotas fail closed", () =>
            {
                var enrollment = fixture.Services.GetRequiredService<DockerEnrollment>();
                AssertThrows<ValidationException>(() => enrollment.RequireImage("audit/synthetic:secure-evil"));
                var node = fixture.Enrollment(fixture.FirstNode); node.QuotaEnforcementVerified = false;
                AssertThrows<InvalidOperationException>(() => enrollment.RequireProvisioning(node));
                node.QuotaEnforcementVerified = true; node.QuotaVolumeDriver = "local";
                AssertThrows<InvalidOperationException>(() => enrollment.RequireProvisioning(node));
                return Task.CompletedTask;
            });
            await Check("Provisioning sends finite memory and a quota-controlled home volume", async () =>
            {
                using var cores = await admin.GetAsync($"/api/nodes/{fixture.NodeId}/cpu-cores");
                Assert((await cores.Content.ReadFromJsonAsync<int[]>()) is [1, 3], "Available core IDs did not match the node");
                using var rejected = await admin.PostAsJsonAsync("/api/servers", new
                {
                    name = "Invalid affinity", nodeId = fixture.NodeId, dockerImage = "audit/synthetic:secure",
                    cpuLimit = 200, port = 26000, cpuCores = new[] { 0, 1 }
                });
                Assert(rejected.StatusCode == HttpStatusCode.BadRequest, "Nonenrolled CPU core was accepted");
                var response = await admin.PostAsJsonAsync("/api/servers", new
                {
                    name = "Provisioned", nodeId = fixture.NodeId, dockerImage = "audit/synthetic:secure",
                    memoryLimit = 4096, cpuLimit = 200, diskLimit = 1024, port = 26000, cpuCores = new[] { 3, 1 },
                    environmentVariables = new Dictionary<string, string> { ["SERVER_MEMORY"] = "1", ["ARCADENODE_JAVA_HEAP_MIB"] = "4096" }
                });
                Assert(response.StatusCode == HttpStatusCode.Created, "Provisioning failed: " + await response.Content.ReadAsStringAsync());
                var create = fixture.FirstNode.Requests.Last(request => request.Path.EndsWith("/containers/create"));
                var json = JsonDocument.Parse(create.Body).RootElement;
                Assert(json.GetProperty("HostConfig").GetProperty("Memory").GetInt64() == 4294967296, "4 GB cap was not sent");
                var environment = json.GetProperty("Env").EnumerateArray().Select(value => value.GetString()).ToArray();
                Assert(environment.Contains("SERVER_MEMORY=4096") && environment.Contains("ARCADENODE_JAVA_HEAP_MIB=3072") &&
                    !environment.Contains("SERVER_MEMORY=1") && !environment.Contains("ARCADENODE_JAVA_HEAP_MIB=4096"),
                    "The managed Java heap or compatible server memory was overridden by input");
                Assert(json.GetProperty("HostConfig").GetProperty("CpusetCpus").GetString() == "1,3", "Selected CPU core affinity was not sent");
                Assert(!json.TryGetProperty("Cmd", out _), "Container command was supplied");
                var volume = JsonDocument.Parse(fixture.FirstNode.Requests.Last(request => request.Path.EndsWith("/volumes/create")).Body).RootElement;
                Assert(volume.GetProperty("Driver").GetString() == "synthetic-quota" && volume.GetProperty("DriverOpts").GetProperty("size").GetString() == "1073741824", "Quota not sent to driver");
            });
            await Check("Power requests use the server's own node", async () =>
            {
                var before = fixture.FirstNode.Requests.Count;
                var route = $"/api/servers/{fixture.SecondServerId}/power";
                using var response = await owner.PostAsJsonAsync(route, new { action = "start" });
                Assert(response.IsSuccessStatusCode && fixture.FirstNode.Requests.Count == before, "Power request reached the wrong node");
                Assert(fixture.SecondNode.Requests.Any(request => request.Path.EndsWith("/start")), "Second node did not receive power action");
                var beforeForce = fixture.SecondNode.Requests.Count;
                using var denied = await other.PostAsJsonAsync(route, new { action = "kill" });
                Assert(denied.StatusCode == HttpStatusCode.Forbidden && fixture.SecondNode.Requests.Count == beforeForce,
                    "An unauthorized account sent a force stop to the node");
                using var forced = await owner.PostAsJsonAsync(route, new { action = "kill" });
                Assert(forced.IsSuccessStatusCode && fixture.FirstNode.Requests.Count == before &&
                    fixture.SecondNode.Requests.Skip(beforeForce).Count(request => request.Path.EndsWith("/kill")) == 1 &&
                    !fixture.SecondNode.Requests.Skip(beforeForce).Any(request => request.Path.EndsWith("/stop")),
                    "Force stop did not send the required SIGKILL exactly once to the assigned node");
                fixture.SecondNode.ReportStopped = true;
                try
                {
                    using var retry = await owner.PostAsJsonAsync(route, new { action = "kill" });
                    Assert(retry.IsSuccessStatusCode, "A retry against an already-stopped container failed");
                }
                finally { fixture.SecondNode.ReportStopped = false; }
                using var restoreRunningState = await owner.PostAsJsonAsync(route, new { action = "start" });
                Assert(restoreRunningState.IsSuccessStatusCode, "The server did not start after the manual force stop");
            });
            await Check("Managed power waits for the game's shutdown and never escalates it", async () =>
            {
                var node = fixture.FirstNode;
                const string running = "running";
                using var scope = fixture.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var bedrock = await AddManagedAsync("bedrock");
                var zomboid = await AddManagedAsync("zomboid");
                var reforger = await AddManagedAsync("reforger");
                try
                {
                    await ExpectShutdown(bedrock, "stop", "stop\n", 0, expectedSuccess: true);
                    var beforeRetries = node.Commands.Count;
                    using var retry = await owner.PostAsJsonAsync(PowerRoute(bedrock), new { action = "stop" });
                    Assert((await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean() &&
                        node.Commands.Count == beforeRetries, "An already-stopped game received a duplicate command");

                    await ExpectShutdown(zomboid, "restart", "quit\n", 0, expectedSuccess: true);
                    await ExpectShutdown(zomboid, "restart", "quit\n", 137, expectedSuccess: false);
                    await ExpectShutdown(reforger, "stop", "SIGINT", 130, expectedSuccess: true);

                    node.RuntimeOverrides[bedrock.ContainerId!] = new() { Status = running, Running = true };
                    node.RefuseConsoleUpgrade = true;
                    var beforeFailure = node.Requests.Count;
                    using var failed = await owner.PostAsJsonAsync(PowerRoute(bedrock), new { action = "restart" });
                    Assert(!((await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean()) &&
                        !node.Requests.Skip(beforeFailure).Any(item => item.Path.EndsWith("/start") ||
                            item.Path.EndsWith("/stop") || item.Path.EndsWith("/restart") || item.Path.EndsWith("/kill")),
                        "A rejected console request caused a forceful shutdown or a new start");
                    Assert(await db.Servers.AsNoTracking().AnyAsync(item => item.Id == bedrock.Id && item.Status == ServerStatus.Running),
                        "An unconfirmed stop replaced the observed running state");
                }
                finally
                {
                    node.RefuseConsoleUpgrade = false;
                    db.Servers.RemoveRange(bedrock, zomboid, reforger);
                    await db.SaveChangesAsync();
                }

                async Task<GameServer> AddManagedAsync(string game)
                {
                    var id = Guid.NewGuid();
                    var server = new GameServer
                    {
                        Id = id, Name = $"Graceful {game} fixture", Identifier = id.ToString("N")[..8], DockerImage = $"audit/synthetic-{game}:secure",
                        ContainerId = "graceful-" + id.ToString("N"), NodeId = fixture.NodeId, OwnerId = fixture.OwnerId,
                        Port = game switch { "bedrock" => 23170, "zomboid" => 23171, _ => 23172 },
                        Status = ServerStatus.Running
                    };
                    db.Servers.Add(server);
                    await db.SaveChangesAsync();
                    node.Seed(server.ContainerId, server.Id, server.NodeId, server.OwnerId);
                    return server;
                }

                async Task ExpectShutdown(GameServer server, string action, string expected, long code, bool expectedSuccess)
                {
                    node.RuntimeOverrides[server.ContainerId!] = new() { Status = running, Running = true };
                    var before = node.Requests.Count;
                    var signal = expected == "SIGINT";
                    var output = signal ? node.Signals : node.Commands;
                    var offset = output.Count;
                    var pending = owner.PostAsJsonAsync(PowerRoute(server), new { action });
                    try
                    {
                        for (var attempt = 0; output.Count == offset && !pending.IsCompleted && attempt < 100; attempt++)
                            await Task.Delay(30);
                        Assert(output.Skip(offset).SequenceEqual(new[] { expected }) && !pending.IsCompleted &&
                            !node.Requests.Skip(before).Any(item => item.Path.EndsWith("/start")), "Power did not await the expected game shutdown");
                    }
                    finally
                    {
                        node.RuntimeOverrides[server.ContainerId!] = new() { Status = "exited", Running = false, ExitCode = code, OOMKilled = code == 137 };
                    }
                    using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert(response.IsSuccessStatusCode &&
                        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean() == expectedSuccess,
                        "The response did not match the exit result");
                    var requests = node.Requests.Skip(before).ToArray();
                    Assert(requests.Count(item => item.Path.EndsWith("/start")) == (expectedSuccess && action == "restart" ? 1 : 0) &&
                        !requests.Any(item => item.Path.EndsWith("/stop") || item.Path.EndsWith("/restart")) &&
                        requests.Count(item => item.Path.EndsWith("/kill")) == (signal ? 1 : 0),
                        "The wrong Docker operation followed a managed shutdown");
                }

                static string PowerRoute(GameServer server) => $"/api/servers/{server.Id}/power";
            });
            await Check("Legacy images fail before any volume or container is created", async () =>
            {
                var before = fixture.FirstNode.Requests.Count(request => request.Path.EndsWith("/volumes/create") || request.Path.EndsWith("/containers/create"));
                fixture.FirstNode.LegacyImage = true;
                var response = await admin.PostAsJsonAsync("/api/servers", new { name = "Legacy denied", nodeId = fixture.NodeId, dockerImage = "audit/synthetic:secure", memoryLimit = 1024, cpuLimit = 100, diskLimit = 1024, port = 26001 });
                fixture.FirstNode.LegacyImage = false;
                Assert(response.StatusCode == HttpStatusCode.Conflict, "Legacy image accepted");
                Assert(before == fixture.FirstNode.Requests.Count(request => request.Path.EndsWith("/volumes/create") || request.Path.EndsWith("/containers/create")), "Legacy image created Docker resources");
            });
            await Check("All creation flows enforce finite node capacity and reserve ports", async () =>
            {
                var before = fixture.FirstNode.Requests.Count;
                var generic = await admin.PostAsJsonAsync("/api/servers", new { name = "Over capacity", nodeId = fixture.NodeId, dockerImage = "audit/synthetic:secure", memoryLimit = 65536, cpuLimit = 100, diskLimit = 1024, port = 26002 });
                Assert(generic.StatusCode == HttpStatusCode.BadRequest, "Generic creation bypassed capacity");
                var cartridge = await admin.PostAsJsonAsync("/api/servers/from-cartridge", new { name = "Over capacity cartridge", nodeId = fixture.NodeId, cartridgeId = "minecraft-java", memoryLimit = 65536, cpuLimit = 100, diskLimit = 1024, port = 26003 });
                var body = await cartridge.Content.ReadAsStringAsync();
                Assert(cartridge.StatusCode == HttpStatusCode.BadRequest && body.Contains("Node capacity exceeded"), "Cartridge creation did not reach shared admission: " + body);
                var duplicatePort = await admin.PostAsJsonAsync("/api/servers", new { name = "Port conflict", nodeId = fixture.NodeId, dockerImage = "audit/synthetic:secure", memoryLimit = 1024, cpuLimit = 100, diskLimit = 1024, port = 25565 });
                Assert(duplicatePort.StatusCode == HttpStatusCode.BadRequest && before == fixture.FirstNode.Requests.Count, "Invalid reservation reached Docker");
            });
            await Check("Console sends stdin only; no Docker exec or shell is used", async () =>
            {
                var path = $"/api/servers/{fixture.ServerId}/command";
                var before = fixture.FirstNode.Commands.Count;
                using var response = await owner.PostAsJsonAsync(path, new { command = "id" });
                Assert(response.IsSuccessStatusCode, "stdin command failed: " + await response.Content.ReadAsStringAsync());
                for (var attempt = 0; attempt < 50 && fixture.FirstNode.Commands.Count == before; attempt++) await Task.Delay(10);
                Assert(fixture.FirstNode.Commands.Skip(before).SequenceEqual(new[] { "id\n" }), "Command did not reach stdin");
                Assert(!fixture.FirstNode.Requests.Any(request => request.Path.Contains("/exec")), "Docker exec was invoked");
                using var multiline = await owner.PostAsJsonAsync(path, new { command = "first\nsecond" });
                Assert(multiline.StatusCode == HttpStatusCode.BadRequest, "Multiline command accepted");
                fixture.FirstNode.RefuseConsoleUpgrade = true;
                try
                {
                    using var refused = await owner.PostAsJsonAsync(path, new { command = "refused-upgrade" });
                    Assert(refused.StatusCode == HttpStatusCode.BadGateway && fixture.FirstNode.Commands.Count == before + 1,
                        "Command was accepted without a Docker TCP upgrade");
                }
                finally { fixture.FirstNode.RefuseConsoleUpgrade = false; }
            });
            await Check("Container ownership labels are checked before operations", async () =>
            {
                fixture.FirstNode.WrongOwnership = true;
                var response = await owner.GetAsync($"/api/servers/{fixture.ServerId}/logs");
                fixture.FirstNode.WrongOwnership = false;
                Assert(response.StatusCode == HttpStatusCode.Conflict, "Foreign container labels accepted");
            });
            await Check("Logs reject unlimited tails and enforce a byte ceiling", async () =>
            {
                Assert((await owner.GetAsync($"/api/servers/{fixture.ServerId}/logs?tail=-1")).StatusCode == HttpStatusCode.BadRequest, "Unlimited tail accepted");
                var response = await owner.GetAsync($"/api/servers/{fixture.ServerId}/logs?tail=1000");
                Assert(response.IsSuccessStatusCode, "Bounded logs unavailable");
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert(json.GetProperty("logs").GetString()!.Length == SecurityLimits.MaxLogBytes, "Log ceiling missing");
                var livePath = $"/api/servers/{fixture.ServerId}/logs/live";
                using var foreign = await other.GetAsync(livePath);
                using var overLimit = await owner.GetAsync(livePath + "?tail=201");
                Assert(foreign.StatusCode == HttpStatusCode.Forbidden && overLimit.StatusCode == HttpStatusCode.BadRequest,
                    "Live log access or limits were bypassed");
                using var live = await owner.GetAsync(livePath + "?tail=200", HttpCompletionOption.ResponseHeadersRead);
                Assert(live.IsSuccessStatusCode && live.Content.Headers.ContentType?.MediaType == "text/plain" &&
                    live.Headers.GetValues("X-Accel-Buffering").Single() == "no", "Live console was buffered or changed content type");
                Assert((await live.Content.ReadAsByteArrayAsync()).Length == SecurityLimits.MaxLogBytes, "Live log byte ceiling missing");
            });
            await Check("Pagination is mandatory and allocation batches are bounded", async () =>
            {
                var response = await admin.GetAsync("/api/servers?pageSize=2147483647");
                Assert(response.IsSuccessStatusCode && response.Headers.GetValues("X-Page-Size").Single() == "200", "Pagination maximum missing");
                response = await admin.PostAsJsonAsync($"/api/nodes/{fixture.NodeId}/allocations", new { ipAddress = "192.0.2.1", startPort = 1024, endPort = 65000 });
                Assert(response.StatusCode == HttpStatusCode.BadRequest, "Unbounded allocation batch accepted");
            });
            await Check("A missing Docker start response is reconciled using the running container", async () =>
            {
                var node = fixture.SecondNode;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var before = node.Requests.Count;
                node.ReportStopped = true;
                node.StartEntered = entered;
                node.ReleaseStart = release;
                try
                {
                    var pending = owner.PostAsJsonAsync($"/api/servers/{fixture.SecondServerId:D}/power", new { action = "start" });
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    node.ReportStopped = false;
                    using var response = await pending.WaitAsync(TimeSpan.FromSeconds(30));
                    var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
                    var requests = node.Requests.Skip(before).Select(request => request.Path).ToArray();
                    var starts = requests.Count(path => path.EndsWith("/start"));
                    var inspections = requests.Count(path => path.EndsWith("/containers/second/json"));
                    Assert(response.IsSuccessStatusCode && payload.TryGetProperty("success", out var success) && success.GetBoolean() &&
                        starts == 1 && inspections >= 2,
                        $"The panel failed to confirm the start. HTTP {response.StatusCode}, starts {starts}, inspections {inspections}.");
                }
                finally
                {
                    release.TrySetResult();
                    node.StartEntered = null;
                    node.ReleaseStart = null;
                    node.ReportStopped = false;
                }
            });
            await Check("Suspension defeats an in-flight power action and stale status write", async () =>
            {
                fixture.FirstNode.StartEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.FirstNode.ReleaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = owner.PostAsJsonAsync($"/api/servers/{fixture.ServerId}/power", new { action = "start" });
                try
                {
                    await fixture.FirstNode.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    var suspend = admin.PostAsync($"/api/servers/{fixture.ServerId}/suspend?suspended=true", null);
                    var persisted = false;
                    for (var attempt = 0; attempt < 100 && !persisted; attempt++)
                    {
                        using var scope = fixture.Services.CreateScope();
                        persisted = await scope.ServiceProvider.GetRequiredService<PanelDbContext>().Servers.AsNoTracking().AnyAsync(server => server.Id == fixture.ServerId && server.IsSuspended);
                        if (!persisted) await Task.Delay(10);
                    }
                    Assert(persisted, "Suspension was not persisted before waiting for Docker");
                    Assert((await owner.PostAsJsonAsync($"/api/servers/{fixture.ServerId}/power", new { action = "restart" })).StatusCode == HttpStatusCode.BadRequest, "Suspension allowed another power action");
                    fixture.FirstNode.ReleaseStart.TrySetResult();
                    Assert((await start).IsSuccessStatusCode && (await suspend).IsSuccessStatusCode, "Concurrent power/suspend failed");
                    using var verify = fixture.Services.CreateScope();
                    var state = await verify.ServiceProvider.GetRequiredService<PanelDbContext>().Servers.AsNoTracking().SingleAsync(server => server.Id == fixture.ServerId);
                    Assert(state.IsSuspended && state.Status == ServerStatus.Suspended, "Stale start status overwrote suspension");
                    var paths = fixture.FirstNode.Requests.Select(request => request.Path).ToArray();
                    Assert(Array.FindLastIndex(paths, path => path.EndsWith("/stop")) > Array.FindLastIndex(paths, path => path.EndsWith("/start")), "Stop did not follow in-flight start");
                }
                finally { fixture.FirstNode.ReleaseStart.TrySetResult(); fixture.FirstNode.StartEntered = null; fixture.FirstNode.ReleaseStart = null; }
                Assert((await admin.PostAsync($"/api/servers/{fixture.ServerId}/suspend?suspended=false", null)).IsSuccessStatusCode, "Unsuspension failed");
                Assert((await owner.PostAsJsonAsync($"/api/servers/{fixture.ServerId}/power", new { action = "start" })).IsSuccessStatusCode, "Server could not restart after unsuspension");
            });
            await Check("Reservation concurrency tokens prevent stale capacity writes", async () =>
            {
                using var first = fixture.Services.CreateScope(); using var second = fixture.Services.CreateScope();
                var a = first.ServiceProvider.GetRequiredService<PanelDbContext>(); var b = second.ServiceProvider.GetRequiredService<PanelDbContext>();
                var na = await a.Nodes.FindAsync(fixture.NodeId); var nb = await b.Nodes.FindAsync(fixture.NodeId);
                na!.ReservationVersion = Guid.NewGuid(); nb!.ReservationVersion = Guid.NewGuid();
                await a.SaveChangesAsync();
                var rejected = false; try { await b.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { rejected = true; }
                Assert(rejected, "Stale reservation token was accepted");
            });
            await Check("An invitation with one use creates at most one account", async () =>
            {
                using (var scope = fixture.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                    db.InviteCodes.Add(new InviteCode { Code = "SYNTHETICONETIME", MaxUses = 1, ExpiresAt = DateTime.UtcNow.AddHours(1), CreatedByUserId = fixture.AdminId });
                    await db.SaveChangesAsync();
                }
                var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() => anonymous.PostAsJsonAsync("/api/auth/register", new { username = "invited" + index, email = $"invited{index}@example.test", password = Password, inviteCode = "SYNTHETICONETIME" }))));
                Assert(responses.Count(response => response.StatusCode == HttpStatusCode.Created) == 1, "Invitation consumed more than once");
                using var verify = fixture.Services.CreateScope();
                var verificationDb = verify.ServiceProvider.GetRequiredService<PanelDbContext>();
                Assert(await verificationDb.Users.CountAsync(user => user.Username.StartsWith("invited")) == 1, "Failed registration persisted an extra account");
            });
            await Check("Authentication needs no ambient cookies or stale antiforgery token", async () =>
            {
                var response = await owner.PutAsJsonAsync("/api/auth/me", new { firstName = "Updated" });
                Assert(response.IsSuccessStatusCode && !response.Headers.Contains("Set-Cookie"), "Bearer request required ambient credentials");
                var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"); request.Headers.Add("Origin", "https://evil.example");
                response = await owner.SendAsync(request);
                Assert(!response.Headers.Contains("Access-Control-Allow-Credentials") && !response.Headers.Contains("Access-Control-Allow-Origin"), "Untrusted cross-origin credentials enabled");
            });
            await Check("Invite creation is bounded, expires by default and revocation defeats a stale consumer", async () =>
            {
                Assert((await admin.PostAsJsonAsync("/api/admin/invites", new { maxUses = 0 })).StatusCode == HttpStatusCode.BadRequest, "Unlimited invitation accepted");
                var response = await admin.PostAsJsonAsync("/api/admin/invites", new { maxUses = 1 });
                Assert(response.StatusCode == HttpStatusCode.Created, "Finite invitation creation failed");
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(); var id = json.GetProperty("id").GetGuid();
                Assert(json.GetProperty("code").GetString()!.Length == 16 && json.GetProperty("expiresAt").GetDateTime() > DateTime.UtcNow, "Invitation entropy/expiry shape invalid");
                using var consumerScope = fixture.Services.CreateScope(); var consumer = consumerScope.ServiceProvider.GetRequiredService<PanelDbContext>();
                var stale = await consumer.InviteCodes.FindAsync(id); Assert(stale!.IsValid, "New invitation invalid");
                Assert((await admin.DeleteAsync($"/api/admin/invites/{id}")).StatusCode == HttpStatusCode.NoContent, "Invite revoke failed");
                stale.TimesUsed++; stale.ConcurrencyVersion = Guid.NewGuid();
                var denied = false; try { await consumer.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { denied = true; }
                Assert(denied, "Stale consumer survived invitation revocation");
            });
            await Check("Expired sessions and tampered JWTs are rejected", async () =>
            {
                var saved = owner.DefaultRequestHeaders.Authorization!;
                var token = saved.Parameter!; var parts = token.Split('.');
                parts[1] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"role\":\"SuperAdmin\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", string.Join('.', parts));
                Assert((await owner.GetAsync("/api/auth/me")).StatusCode == HttpStatusCode.Unauthorized, "Tampered JWT accepted");
                owner.DefaultRequestHeaders.Authorization = saved;
                using (var scope = fixture.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                    await db.AuthSessions.Where(session => session.UserId == fixture.OwnerId).ExecuteUpdateAsync(set => set.SetProperty(session => session.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
                }
                Assert((await owner.GetAsync("/api/auth/me")).StatusCode == HttpStatusCode.Unauthorized, "Expired session accepted");
                await Login(owner, "owner", Password);
            });
            await Check("Diagnostics correlate failed requests without logging credentials", async () =>
            {
                const string secret = "SyntheticDiagnosticSecretDoNotLog123!";
                var response = await anonymous.PostAsJsonAsync("/api/auth/login", new { username = "SyntheticDiagnosticPrivateUsername", password = secret });
                Assert(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.GetValues("X-Diagnostic-Code").Single() == "AN0201", "Login diagnostic code missing");
                var trace = response.Headers.GetValues("X-Trace-Id").Single();
                for (var attempt = 0; attempt < 100 && !fixture.DiagnosticLogs.Entries.Any(entry => entry.Code == "AN0201" && entry.Scope.Contains(trace)); attempt++) await Task.Delay(10);
                Assert(fixture.DiagnosticLogs.Entries.Any(entry => entry.Code == "AN0201" && entry.Scope.Contains(trace) && entry.Text.Contains("System did not validate the login")), "Trace correlation/bilingual diagnostic missing");
                Assert(!fixture.DiagnosticLogs.Entries.Any(entry => entry.Text.Contains(secret) || entry.Text.Contains("SyntheticDiagnosticPrivateUsername") || entry.HasException), "Diagnostic logger serialized credentials or raw exceptions");
                response = await other.GetAsync($"/api/servers/{fixture.ServerId}");
                Assert(response.StatusCode == HttpStatusCode.Forbidden && response.Headers.GetValues("X-Diagnostic-Code").Single() == "AN0802", "Authorization diagnostic missing");
            });
            await Check("A failed Docker stage keeps its precise diagnostic code and redacts daemon details", async () =>
            {
                const string secret = "SyntheticDaemonSecretDoNotLog456!";
                fixture.FirstNode.VolumeFailure = secret;
                HttpResponseMessage response;
                try
                {
                    response = await admin.PostAsJsonAsync("/api/servers", new { name = "Synthetic diagnostic failure", nodeId = fixture.NodeId, dockerImage = "audit/synthetic:secure", memoryLimit = 1024, cpuLimit = 100, diskLimit = 1024, port = 26010 });
                }
                finally { fixture.FirstNode.VolumeFailure = null; }
                Assert(response.StatusCode == HttpStatusCode.Conflict, "Synthetic volume failure was hidden");
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert(body.GetProperty("diagnosticCode").GetString() == "AN0502" && response.Headers.GetValues("X-Diagnostic-Code").Single() == "AN0502", "Specific volume code was replaced by a generic error");
                var trace = body.GetProperty("traceId").GetString()!;
                Assert(trace == response.Headers.GetValues("X-Trace-Id").Single(), "Body/header trace mismatch");
                Assert(fixture.DiagnosticLogs.Entries.Any(entry => entry.Code == "AN0502" && entry.Scope.Contains(trace) && entry.Text.Contains("DockerApiException")), "Specific Docker stage failure was not logged");
                Assert(!fixture.DiagnosticLogs.Entries.Any(entry => entry.Text.Contains(secret) || entry.HasException), "Daemon exception details leaked to diagnostics");
                Assert(!body.ToString().Contains(secret), "Daemon secret leaked to response");
            });
            await Check("Changing a password revokes the old JWT immediately", async () =>
            {
                var response = await owner.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "NewSynthetic456!" });
                Assert(response.IsSuccessStatusCode, "Password change failed");
                Assert((await owner.GetAsync("/api/auth/me")).StatusCode == HttpStatusCode.Unauthorized, "Old JWT survived password change");
                Assert((await other.GetAsync("/api/auth/me")).IsSuccessStatusCode, "Unrelated account was revoked");
                await Login(owner, "owner", "NewSynthetic456!");
            });
            await Check("PHP bcrypt accounts can sign in and rotate a short legacy password", async () =>
            {
                const string phpHash = "$2y$10$RkI0LBo9/q7Pn1L1DE4Hv.EQQEDUPu29SjIYScVFBBXQzzr4gHdki";
                using (var scope = fixture.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                    var userId = Guid.NewGuid();
                    var manifest = $$"""
                        {"schema_version":1,"users":[{"uuid":"{{userId}}","username":"ptero-import","email":"ptero-import@example.test",
                        "name_first":"Legacy","name_last":"User","password":"{{phpHash}}","root_admin":false,"use_totp":false}]}
                        """;
                    async Task<PterodactylAccountImport.ImportReport> ImportAccounts(string input, bool apply)
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
                        return await PterodactylAccountImport.RunAsync(db, stream, apply);
                    }
                    Assert(await ImportAccounts(manifest, false) is { Pending: 1, Imported: 0 }, "Dry-run failed");
                    Assert(await ImportAccounts(manifest.Replace("\"use_totp\":false", "\"use_totp\":true"), true) is { Issues: [{ Reason: "totp_requires_preservation" }] },
                        "Import would downgrade MFA");
                    Assert(!await db.Users.AnyAsync(u => u.Id == userId), "Dry-run or blocked import wrote accounts");
                    Assert(await ImportAccounts(manifest, true) is { Imported: 1 }, "Account import failed");
                    Assert(await ImportAccounts(manifest, true) is { AlreadyPresent: 1, Imported: 0 }, "Account re-import failed");
                    Assert(await ImportAccounts(manifest.Replace(userId.ToString(), Guid.NewGuid().ToString()), true) is { Issues: [{ Reason: "existing_identity_conflict" }] },
                        "Conflicting login was accepted");
                    db.Users.Add(new User { Username = "invalid-import", Email = "invalid-import@example.test", PasswordHash = "$2y$31$" + phpHash[7..] });
                    await db.SaveChangesAsync();
                }
                using var legacyClient = fixture.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
                await Login(legacyClient, "ptero-import", "Old7!");
                using var changed = await legacyClient.PostAsJsonAsync("/api/auth/change-password",
                    new { currentPassword = "Old7!", newPassword = "RenewedSynthetic983!" });
                Assert(changed.IsSuccessStatusCode, "Legacy password rotation failed");
                await Login(legacyClient, "ptero-import", "RenewedSynthetic983!");
                using var invalid = await legacyClient.PostAsJsonAsync("/api/auth/login", new { username = "invalid-import", password = "Old7!" });
                Assert(invalid.StatusCode == HttpStatusCode.Unauthorized, "Excessive bcrypt cost was not rejected");
            });
            await Check("Logout revokes the current session on the server", async () =>
            {
                Assert((await owner.PostAsync("/api/auth/logout", null)).StatusCode == HttpStatusCode.NoContent, "Logout failed");
                Assert((await owner.GetAsync("/api/auth/me")).StatusCode == HttpStatusCode.Unauthorized, "Logged-out JWT survived");
            });
            await Check("Inactive accounts and changed roles invalidate signed tokens", async () =>
            {
                using (var scope = fixture.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
                    (await db.Users.FindAsync(fixture.OtherId))!.IsActive = false;
                    (await db.Users.FindAsync(fixture.AdminId))!.Role = UserRole.User;
                    await db.SaveChangesAsync();
                }
                Assert((await other.GetAsync("/api/auth/me")).StatusCode == HttpStatusCode.Unauthorized, "Inactive account accepted");
                Assert((await admin.GetAsync("/api/admin/users")).StatusCode == HttpStatusCode.Unauthorized, "Stale admin role accepted");
            });
            await Check("Authentication limits are per IP and ignore spoofed forwarding headers", async () =>
            {
                async Task<int> Attempt(string ip)
                {
                    var response = await fixture.Server.SendAsync(context =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                        context.Request.Scheme = "https"; context.Request.Host = new HostString("localhost");
                        context.Request.Method = "POST"; context.Request.Path = "/api/auth/login";
                        context.Request.ContentType = "application/json";
                        context.Request.Headers["X-Forwarded-For"] = "198.51.100." + Random.Shared.Next(1, 255);
                        var bytes = Encoding.UTF8.GetBytes("{\"username\":\"nonexistent\",\"password\":\"SyntheticOnly123!\"}");
                        context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length;
                    });
                    // The login test measures the fixed window after each request releases its concurrency lease.
                    await fixture.WaitForRequestsAsync();
                    return response.Response.StatusCode;
                }
                for (var i = 1; i <= 20; i++)
                {
                    var status = await Attempt("192.0.2.20");
                    Assert(status == 401, $"Authentication request {i} returned HTTP {status}, expected HTTP 401 before reaching the rate limit");
                }
                Assert(await Attempt("192.0.2.20") == 429, "Auth IP limit missing");
                Assert(await Attempt("192.0.2.21") == 401, "One IP blocked an unrelated client");
            });
            Console.WriteLine(JsonSerializer.Serialize(new { date = "2026-11-16", mode = "secure-regression", passed = Results.Count, tests = Results,
                diagnosticSamples = fixture.DiagnosticLogs.Entries.Where(entry => entry.Code is "AN0201" or "AN0502").Take(5).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { await fixture.CloseAsync(); }
    }

    private sealed class PausedUploadContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly Task _resume;

        public PausedUploadContent(byte[] bytes, Task resume)
        {
            _bytes = bytes;
            _resume = resume;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            await stream.WriteAsync(_bytes.AsMemory(0, 65536), token);
            await stream.FlushAsync(token);
            await _resume.WaitAsync(token);
            await stream.WriteAsync(_bytes.AsMemory(65536), token);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }
    }

    private static async Task Login(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert(response.IsSuccessStatusCode, $"Synthetic login failed with {response.StatusCode}");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("token").GetString());
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
