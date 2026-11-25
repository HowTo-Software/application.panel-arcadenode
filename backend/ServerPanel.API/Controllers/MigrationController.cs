using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArcadeNode.Transfers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Common;
using ServerPanel.API.Data;
using ServerPanel.API.DTOs;
using ServerPanel.API.Models;
using ServerPanel.API.Security;
using ServerPanel.API.Services;

namespace ServerPanel.API.Controllers;

[ApiController]
[Route("api/admin/migrations/servers")]
[Authorize(Roles = "Admin,SuperAdmin")]
public sealed class MigrationController(PanelDbContext db, IDockerService docker, IServiceScopeFactory scopes, DockerEnrollment enrollment) : ControllerBase
{
    private const int PermissionReceiptVersion = 2;
    private const int ContainedLinksReceiptVersion = 3;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize,
        [FromQuery, StringLength(100)] string? query, CancellationToken token)
    {
        var (currentPage, size) = Pagination.Resolve(page, pageSize);
        var servers = db.Servers.AsNoTracking().Where(server => server.MigrationPending || server.RestoredVolumeSha256 != null);
        if (query?.Trim() is { Length: > 0 } term)
        {
            servers = Guid.TryParse(term, out var serverId)
                ? servers.Where(server => server.Id == serverId)
                : servers.Where(server => server.Name.Contains(term) || server.Identifier.Contains(term));
        }
        var total = await servers.CountAsync(token);
        var migrations = await servers.OrderByDescending(server => server.CreatedAt).ThenBy(server => server.Id)
            .Skip(Pagination.Skip(currentPage, size)).Take(size)
            .Select(server => new
            {
                server.Id, server.Name, server.Identifier, NodeName = server.Node.Name,
                server.MigrationPending, server.MigrationVolumeVerifiedAt, server.MigrationReleasedAt,
                server.RestoredVolumeSha256
            }).ToListAsync(token);
        var items = migrations.Select(server => new MigrationStatus(server.Id, server.Name, server.Identifier, server.NodeName,
            server.MigrationReleasedAt.HasValue ? "Released" :
                server.MigrationPending || !server.MigrationVolumeVerifiedAt.HasValue ? "AwaitingVerification" : "Prepared",
            server.RestoredVolumeSha256, AsUtc(server.MigrationVolumeVerifiedAt), AsUtc(server.MigrationReleasedAt))).ToArray();
        Response.ApplyPaginationHeaders(new PagedResult<MigrationStatus>(items, currentPage, size, total));
        return Ok(items);
    }

    [HttpPost("{id:guid}/prepare-restored")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> PrepareRestored(Guid id, [FromBody] VolumeApproval approval, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (ValidateVerification(approval, id, now) is not { } verification)
            return BadRequest(new { message = "Submit the matching source and destination verification report for this server UUID." });
        var source = verification.Source;
        var process = source.SourceProcess!;
        var digest = source.TreeSha256!.ToUpperInvariant();

        // Isolate the SQL application lock transaction from the update transaction.
        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireAsync(id, token);
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, token);
        if (server is null) return NotFound();
        if (server.MigrationReleasedAt is null && (server.MigrationPending || server.RestoredVolumeSha256 is not null) &&
            GameConfigurationError(server, approval) is { } configurationError)
            return BadRequest(new { message = configurationError });
        if (server.MigrationPending)
        {
            if (source.SchemaVersion < PermissionReceiptVersion)
                return BadRequest(new { message = "Capture and verify the Linux volumes with the current migration CLI to include file permissions." });
            if (!server.IsSuspended || server.ContainerId is not null || server.Status != ServerStatus.Suspended) return Conflict();
            var containerId = await docker.PrepareRestoredContainerAsync(server, digest);
            if (string.IsNullOrWhiteSpace(containerId)) throw new InvalidOperationException("The node did not confirm a stopped container.");
            server.ContainerId = containerId;
            server.RestoredVolumeSha256 = digest;
            server.MigrationPending = false;
        }
        else
        {
            if (server.RestoredVolumeSha256 is null || string.IsNullOrEmpty(server.ContainerId)) return Conflict();
            if (server.MigrationReleasedAt is not null)
                return server.RestoredVolumeSha256 == digest ? NoContent() : Conflict();
            if (!server.IsSuspended || server.Status != ServerStatus.Suspended) return Conflict();
            if (server.RestoredVolumeSha256 != digest ||
                !string.Equals(server.MigrationSourceContainerId, process.ContainerId, StringComparison.OrdinalIgnoreCase) ||
                server.MigrationSourceStoppedAt != process.StoppedAt?.UtcDateTime)
            {
                if (source.SchemaVersion < PermissionReceiptVersion || server.MigrationVolumeVerifiedAt is not { } verifiedAt ||
                    source.CapturedAt!.Value.UtcDateTime < verifiedAt.AddMinutes(-2) ||
                    !await docker.IsContainerStoppedAsync(id, token))
                    return Conflict(new { message = "Capture a fresh source snapshot and verify it against the prepared, stopped destination volume." });
                server.RestoredVolumeSha256 = digest;
            }
        }

        server.MigrationSourceContainerId = process.ContainerId!.ToLowerInvariant();
        server.MigrationSourceStoppedAt = process.StoppedAt?.UtcDateTime;
        server.MigrationVolumeVerifiedAt = now.UtcDateTime;
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    [HttpPost("{id:guid}/release-prepared")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> ReleasePrepared(Guid id, [FromBody] ReleaseApproval approval, CancellationToken token)
    {
        if (approval is not { SchemaVersion: 2, WritesFenced: true, SourceProcess: { } process } || approval.ServerId != id ||
            !IsSha256(approval.TreeSha256) || !IsProcessReceipt(process, approval.ObservedAt) ||
            ValidateVerification(approval.DestinationVerification, id, DateTimeOffset.UtcNow) is not { } verification ||
            !string.Equals(verification.Source.TreeSha256, approval.TreeSha256, StringComparison.OrdinalIgnoreCase) ||
            !SameProcess(verification.Source.SourceProcess!, process))
            return BadRequest(new { message = "Submit the confirmation of the fenced source and the fresh verification report for the destination volume." });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var operatorId)) return Unauthorized();

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireAsync(id, token);
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, token);
        if (server is null) return NotFound();
        if (server.MigrationPending || string.IsNullOrEmpty(server.ContainerId) || server.MigrationVolumeVerifiedAt is null ||
            !string.Equals(server.RestoredVolumeSha256, approval.TreeSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(server.MigrationSourceContainerId, process.ContainerId, StringComparison.OrdinalIgnoreCase) ||
            server.MigrationSourceStoppedAt != process.StoppedAt?.UtcDateTime)
            return Conflict(new { message = "The source instance or volume no longer matches the prepared migration." });
        if (server.MigrationReleasedAt is not null) return NoContent();
        if (GameConfigurationError(server, approval.DestinationVerification!) is { } configurationError)
            return BadRequest(new { message = configurationError });
        var now = DateTimeOffset.UtcNow;
        if (approval.ObservedAt < now.AddMinutes(-15) || approval.ObservedAt > now.AddMinutes(2) ||
            approval.ObservedAt.UtcDateTime < server.MigrationVolumeVerifiedAt.Value.AddMinutes(-2) ||
            verification.Destination.CapturedAt!.Value < now.AddMinutes(-15) ||
            verification.Destination.CapturedAt.Value.UtcDateTime < server.MigrationVolumeVerifiedAt.Value.AddMinutes(-2))
            return BadRequest(new { message = "Reverify the destination and confirm the fenced source after preparation, within 15 minutes of release." });
        if (!server.IsSuspended || server.Status != ServerStatus.Suspended || !await docker.IsContainerStoppedAsync(id, token))
            return Conflict(new { message = "The destination must still be suspended and its container stopped." });

        server.MigrationReleasedAt = now.UtcDateTime;
        server.MigrationReleasedById = operatorId;
        server.IsSuspended = false;
        server.Status = ServerStatus.Stopped;
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    private string? GameConfigurationError(GameServer server, VolumeApproval approval) =>
        enrollment.RequireImage(server.DockerImage).DataPath switch
        {
            ProjectZomboidRestoreSettings.HomeDirectory when !MatchesZomboid(server, approval.ProjectZomboid) =>
                "Verify the copied Zomboid volume with --project-zomboid and match its save name and ports to the destination configuration.",
            BedrockRestoreSettings.HomeDirectory when !MatchesBedrock(server, approval.MinecraftBedrock) =>
                "Verify the copied Bedrock volume with --minecraft-bedrock and match its IPv4 and IPv6 ports to the destination configuration.",
            ReforgerRestoreSettings.HomeDirectory when !MatchesReforger(server, approval.ArmaReforger) =>
                "Verify the copied Reforger volume with --arma-reforger and match its game, A2S and RCON listeners to the destination allocations.",
            _ => null
        };

    private static bool MatchesBedrock(GameServer server, BedrockRestoreSettings? settings)
    {
        if (settings is not { Layout: RestoredGameLayout.Managed or RestoredGameLayout.Pterodactyl, Ipv6Port: >= 1024 and <= 65535 } ||
            settings.GamePort != server.Port || settings.Ipv6Port == server.Port) return false;
        var configured = ReadEnvironment(server).GetValueOrDefault("SERVER_PORTV6");
        var ipv6 = BedrockRestoreSettings.DefaultIpv6Port;
        if (configured is not null && !int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out ipv6)) return false;
        return ipv6 == settings.Ipv6Port &&
            (ipv6 == BedrockRestoreSettings.DefaultIpv6Port || ReadSecondaryPorts(server).Contains(ipv6));
    }

    private static bool MatchesReforger(GameServer server, ReforgerRestoreSettings? settings)
    {
        if (settings is not { Layout: RestoredGameLayout.Managed or RestoredGameLayout.Pterodactyl } ||
            settings.GamePort != server.Port || settings.PublicPort != server.Port ||
            (settings.A2sPort.HasValue && settings.A2sPort == settings.RconPort)) return false;
        var secondary = ReadSecondaryPorts(server);
        return IsReserved(settings.A2sPort) && IsReserved(settings.RconPort);

        bool IsReserved(int? port) => port is null ||
            (port is >= 1024 and <= 65535 && port.Value != server.Port && secondary.Contains(port.Value));
    }

    private static bool MatchesZomboid(GameServer server, ProjectZomboidRestoreSettings? settings)
    {
        if (settings is null || settings.GamePort != server.Port || settings.DirectPort == server.Port) return false;

        var environment = ReadEnvironment(server);
        var secondary = ReadSecondaryPorts(server);
        if (ProjectZomboidRestoreSettings.SaveNameFor(environment.GetValueOrDefault("SERVER_NAME"), settings.Layout) is not { } name ||
            settings.SaveName != name ||
            !secondary.Contains(settings.DirectPort) || !int.TryParse(environment.GetValueOrDefault("STEAM_PORT") ??
                environment.GetValueOrDefault("DIRECT_PORT") ?? "16262", NumberStyles.None, CultureInfo.InvariantCulture, out var direct) ||
            direct != settings.DirectPort) return false;

        if (settings.RconPort is not { } rcon) return true;
        if (rcon is < 1024 or > 65535 ||
            !int.TryParse(environment.GetValueOrDefault("RCON_PORT") ?? "27015", NumberStyles.None, CultureInfo.InvariantCulture, out var configuredRcon))
            return false;
        return rcon == configuredRcon || (!IsPublished(rcon) && !IsPublished(configuredRcon));

        bool IsPublished(int port) => port == server.Port || secondary.Contains(port);
    }

    private static Dictionary<string, string> ReadEnvironment(GameServer server) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(server.EnvironmentVariables ?? "{}") ?? new();

    private static int[] ReadSecondaryPorts(GameServer server) =>
        JsonSerializer.Deserialize<int[]>(server.AdditionalPorts ?? "[]") ?? [];

    private static (VolumeReceipt Source, VolumeReceipt Destination)? ValidateVerification(VolumeApproval? approval, Guid id, DateTimeOffset now)
    {
        if (approval is not { Mode: "verify_volume", Matches: true, Source: { SourceProcess: { } process } source, Destination: { } destination } ||
            !IsVolumeReceipt(source, id, now) || !IsVolumeReceipt(destination, id, now) || destination.SourceProcess is not null ||
            source.SchemaVersion != destination.SchemaVersion || !IsProcessReceipt(process, source.CapturedAt!.Value) ||
            destination.CapturedAt < source.CapturedAt.Value.AddMinutes(-2) ||
            source.Files != destination.Files || source.Directories != destination.Directories || source.Bytes != destination.Bytes ||
            (source.Links ?? 0) != (destination.Links ?? 0) ||
            !source.TreeSha256!.Equals(destination.TreeSha256, StringComparison.OrdinalIgnoreCase)) return null;
        return (source, destination);
    }

    private static bool IsVolumeReceipt(VolumeReceipt receipt, Guid id, DateTimeOffset now) =>
        receipt.SchemaVersion is 1 or PermissionReceiptVersion or ContainedLinksReceiptVersion && receipt.ServerId == id && IsSha256(receipt.TreeSha256) &&
        (receipt.SchemaVersion == ContainedLinksReceiptVersion ? receipt.Links is >= 0 : receipt.Links is null or 0) &&
        receipt.Files is >= 0 && receipt.Directories is >= 0 && receipt.Bytes is >= 0 &&
        receipt.CapturedAt is { } captured && captured > DateTimeOffset.UnixEpoch && captured <= now.AddMinutes(2);

    private static bool IsProcessReceipt(SourceProcessReceipt process, DateTimeOffset at) =>
        IsSha256(process.ContainerId) && at > DateTimeOffset.UnixEpoch &&
        (process.StoppedAt is null || (process.StoppedAt > DateTimeOffset.UnixEpoch && process.StoppedAt <= at));

    private static bool SameProcess(SourceProcessReceipt source, SourceProcessReceipt confirmation) =>
        string.Equals(source.ContainerId, confirmation.ContainerId, StringComparison.OrdinalIgnoreCase) && source.StoppedAt == confirmation.StoppedAt;

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static DateTime? AsUtc(DateTime? value) => value is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null;

    private sealed record MigrationStatus(Guid Id, string Name, string Identifier, string NodeName, string Stage,
        string? VolumeSha256, DateTime? VerifiedAt, DateTime? ReleasedAt);

    public sealed record VolumeApproval(string? Mode, bool Matches, VolumeReceipt? Source, VolumeReceipt? Destination,
        [property: JsonPropertyName("project_zomboid")] ProjectZomboidRestoreSettings? ProjectZomboid = null,
        [property: JsonPropertyName("minecraft_bedrock")] BedrockRestoreSettings? MinecraftBedrock = null,
        [property: JsonPropertyName("arma_reforger")] ReforgerRestoreSettings? ArmaReforger = null);
    public sealed record VolumeReceipt(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("server_id")] Guid ServerId,
        long? Files, long? Directories, long? Bytes,
        [property: JsonPropertyName("tree_sha256")] string? TreeSha256,
        [property: JsonPropertyName("captured_at")] DateTimeOffset? CapturedAt,
        [property: JsonPropertyName("source_process")] SourceProcessReceipt? SourceProcess,
        long? Links = null);
    public sealed record SourceProcessReceipt(
        [property: JsonPropertyName("container_id")] string? ContainerId,
        [property: JsonPropertyName("stopped_at")] DateTimeOffset? StoppedAt);
    public sealed record ReleaseApproval(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("server_id")] Guid ServerId,
        [property: JsonPropertyName("tree_sha256")] string? TreeSha256,
        [property: JsonPropertyName("source_process")] SourceProcessReceipt? SourceProcess,
        [property: JsonPropertyName("observed_at")] DateTimeOffset ObservedAt,
        [property: JsonPropertyName("writes_fenced")] bool WritesFenced,
        [property: JsonPropertyName("destination_verification")] VolumeApproval? DestinationVerification);
}
