using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using ArcadeNode.Backups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;
using ServerPanel.API.Services;

namespace ServerPanel.API.Controllers;

[ApiController]
[Authorize]
[Route("api/servers/{serverId:guid}/backups")]
public sealed class ServerBackupsController(PanelDbContext db, NodeFileClient nodes, ServerAccess access) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid serverId, CancellationToken token)
    {
        var server = await LoadServerAsync(serverId, token);
        if (server is null) return NotFound();
        if (!await AllowsAsync(server, ServerPermission.BackupRead, token)) return Forbid();

        Response.Headers.CacheControl = "no-store";
        var backups = await db.Backups.AsNoTracking().Where(b => b.ServerId == serverId)
            .OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id).Take(20).ToListAsync(token);
        var pending = await db.Schedules.AsNoTracking().Where(s => s.ServerId == serverId && s.PendingBackupId.HasValue)
            .Select(s => s.PendingBackupId!.Value).ToArrayAsync(token);
        var restoreAvailable = nodes.IsRestoreConfigured(server.NodeId);
        return Ok(backups.Select(backup => Describe(backup, restoreAvailable, pending.Contains(backup.Id))));
    }

    [HttpPut("{backupId:guid}/protection")]
    public Task<IActionResult> SetProtection(Guid serverId, Guid backupId, BackupProtection input,
        [FromServices] IServiceScopeFactory scopes, CancellationToken token) =>
        ChangeBackupAsync(serverId, backupId, scopes, async (_, backup) =>
        {
            if (input.IsLocked is not { } isLocked) return BadRequest();
            if (backup.IsQueued || backup.DeletionStatus != BackupDeletionStatus.None) return Conflict();
            backup.IsLocked = isLocked;
            await db.SaveChangesAsync(token);
            return NoContent();
        }, token);

    [HttpDelete("{backupId:guid}")]
    public Task<IActionResult> QueueDeletion(Guid serverId, Guid backupId,
        [FromServices] IServiceScopeFactory scopes, CancellationToken token) =>
        ChangeBackupAsync(serverId, backupId, scopes, async (server, backup) =>
        {
            if (backup.DeletionStatus == BackupDeletionStatus.Queued) return Accepted();
            var pendingSchedule = await db.Schedules.AnyAsync(s => s.ServerId == serverId && s.PendingBackupId == backupId, token);
            if (!CanDelete(backup, pendingSchedule)) return Conflict(new { message = "This backup is protected or in use." });
            if (!nodes.IsConfigured(server.NodeId)) return StatusCode(StatusCodes.Status503ServiceUnavailable);
            backup.DeletionStatus = BackupDeletionStatus.Queued;
            backup.DeletionNodeId = server.NodeId;
            await db.SaveChangesAsync(token);
            return Accepted();
        }, token);

    private async Task<IActionResult> ChangeBackupAsync(Guid serverId, Guid backupId, IServiceScopeFactory scopes,
        Func<GameServer, Backup, Task<IActionResult>> operation, CancellationToken token)
    {
        var server = await LoadServerAsync(serverId, token);
        if (server is null) return NotFound();
        if (!await AllowsAsync(server, ServerPermission.BackupDelete, token)) return Forbid();

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireBackupRestoreAsync(serverId, token);
        server = await LoadServerAsync(serverId, token);
        if (server is null) return NotFound();
        if (!await AllowsAsync(server, ServerPermission.BackupDelete, token)) return Forbid();
        var backup = await db.Backups.SingleOrDefaultAsync(b => b.Id == backupId && b.ServerId == serverId, token);
        return backup is null ? NotFound() : await operation(server, backup);
    }

    [HttpPost("{backupId:guid}/restore")]
    public async Task<IActionResult> EnqueueRestore(Guid serverId, Guid backupId,
        [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        var server = await LoadServerAsync(serverId, token);
        if (server is null) return NotFound();
        if (!await AllowsAsync(server, ServerPermission.BackupRestore, token)) return Forbid();
        var backups = db.Backups.Where(backup => backup.ServerId == serverId);
        var current = await backups.AsNoTracking().SingleOrDefaultAsync(backup => backup.Id == backupId, token);
        if (current is null) return NotFound();
        if (!CanDownload(current)) return Conflict();
        var available = nodes.IsRestoreConfigured(server.NodeId);
        if (current.RestoreStatus == BackupRestoreStatus.Prepared) return Ok(Describe(current, available));
        if (current.RestoreStatus == BackupRestoreStatus.Queued) return Accepted(Describe(current, available));
        if (!available) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (await backups.AnyAsync(backup => backup.RestoreStatus == BackupRestoreStatus.Queued, token)) return Conflict();

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireBackupRestoreAsync(serverId, token);
        server = await LoadServerAsync(serverId, token);
        if (server is null) return NotFound();
        if (!await AllowsAsync(server, ServerPermission.BackupRestore, token)) return Forbid();
        available = nodes.IsRestoreConfigured(server.NodeId);
        var backup = await backups.SingleOrDefaultAsync(backup => backup.Id == backupId, token);
        if (backup is null) return NotFound();
        if (!CanDownload(backup)) return Conflict();
        if (backup.RestoreStatus == BackupRestoreStatus.Prepared) return Ok(Describe(backup, available));
        if (backup.RestoreStatus == BackupRestoreStatus.Queued) return Accepted(Describe(backup, available));
        if (!available) return StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (await backups.AnyAsync(item => item.RestoreStatus == BackupRestoreStatus.Queued, token)) return Conflict();
        backup.RestoreStatus = BackupRestoreStatus.Queued;
        backup.RestoreNodeId = server.NodeId;
        backup.RestoreRequestedAt = DateTime.UtcNow;
        backup.RestoreCompletedAt = null;
        await db.SaveChangesAsync(token);
        return Accepted(Describe(backup, available));
    }

    [HttpPost("{backupId:guid}/download-grant")]
    [EnableRateLimiting("backup-grant")]
    public async Task<IActionResult> IssueDownloadGrant(Guid serverId, Guid backupId, CancellationToken token)
    {
        var server = await LoadServerAsync(serverId, token);
        if (server is null) return NotFound();
        Guid? memberRevision = null;
        if (!ServerAccess.CanManage(User, server.OwnerId))
        {
            var member = ServerAccess.UserId(User) is { } id ? await access.ResolveMemberAsync(serverId, id, token) : null;
            if (member is null || !ServerAccess.Allows(member.Permissions, ServerPermission.BackupDownload)) return Forbid();
            memberRevision = member.Revision;
        }

        var backup = await db.Backups.AsNoTracking().SingleOrDefaultAsync(b => b.Id == backupId && b.ServerId == serverId, token);
        if (backup is null) return NotFound();
        if (!CanDownload(backup)) return Conflict();
        if (!nodes.IsConfigured(server.NodeId)) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (!Guid.TryParse(User.FindFirstValue("sid"), out var sessionId) ||
            !Enum.TryParse<UserRole>(User.FindFirstValue(ClaimTypes.Role), out var role) ||
            User.FindFirstValue("session_version") is not { Length: 32 } sessionVersion) return Unauthorized();

        var now = DateTime.UtcNow;
        await db.BackupDownloadGrants.Where(grant => grant.ExpiresAt <= now).ExecuteDeleteAsync(token);
        var secret = RandomNumberGenerator.GetBytes(32);
        db.BackupDownloadGrants.Add(new BackupDownloadGrant
        {
            TokenHash = Convert.ToHexString(SHA256.HashData(secret)), BackupId = backupId,
            SessionId = sessionId, SessionVersion = sessionVersion, MemberRevision = memberRevision, Role = role, ExpiresAt = now.AddMinutes(1)
        });
        await db.SaveChangesAsync(token);
        return Ok(new { ticket = Convert.ToHexString(secret) });
    }

    [HttpPost("{backupId:guid}/download")]
    [AllowAnonymous]
    [EnableRateLimiting("backup-transfer")]
    [Consumes("application/x-www-form-urlencoded")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> Download(Guid serverId, Guid backupId, [FromForm] string? ticket, CancellationToken token)
    {
        if (ticket is not { Length: 64 } || !ticket.All(char.IsAsciiHexDigit)) return Unauthorized();

        var hash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(ticket)));
        var now = DateTime.UtcNow;
        var validGrant = db.BackupDownloadGrants.Where(grant => grant.TokenHash == hash && grant.BackupId == backupId &&
            grant.ExpiresAt > now && grant.Backup.ServerId == serverId && grant.Session.ExpiresAt > now &&
            grant.Session.User.IsActive && grant.Session.User.Role == grant.Role && grant.Session.User.SecurityStamp == grant.SessionVersion &&
            ((grant.MemberRevision == null && (grant.Backup.Server.OwnerId == grant.Session.UserId ||
                grant.Role == UserRole.Admin || grant.Role == UserRole.SuperAdmin)) ||
             db.ServerMembers.Any(member => member.ServerId == serverId && member.UserId == grant.Session.UserId && member.Revision == grant.MemberRevision &&
                (member.Permissions & ServerPermission.BackupDownload) == ServerPermission.BackupDownload)));
        var download = await validGrant.AsNoTracking().Select(grant => new
        {
            grant.Backup, grant.Backup.Server.NodeId, grant.Session.UserId, grant.Session.User.Username
        }).SingleOrDefaultAsync(token);
        if (download is null || !CanDownload(download.Backup) || await validGrant.ExecuteDeleteAsync(token) != 1)
            return Unauthorized();

        HttpContext.Items["AuditUserId"] = download.UserId;
        HttpContext.Items["AuditUsername"] = download.Username;
        var backup = download.Backup;
        try
        {
            using var nodeResponse = await nodes.OpenBackupAsync(download.NodeId, serverId, backupId, token);
            if (nodeResponse is null || nodeResponse.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (nodeResponse.StatusCode == HttpStatusCode.NotFound) return NotFound();
            if (nodeResponse.StatusCode != HttpStatusCode.OK || nodeResponse.Content.Headers.ContentType?.MediaType != "application/gzip" ||
                nodeResponse.Content.Headers.ContentEncoding.Count != 0 || nodeResponse.Content.Headers.ContentLength != backup.Size)
                return StatusCode(StatusCodes.Status502BadGateway);

            Response.ContentLength = backup.Size;
            Response.ContentType = "application/gzip";
            Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                { FileName = $"arcadenode-{backupId:N}.tar.gz" }.ToString();
            Response.Headers["X-Accel-Buffering"] = "no";
            await using var source = await nodeResponse.Content.ReadAsStreamAsync(token);
            await CopyArchiveAsync(source, backup, token);
            return new EmptyResult();
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is IOException or HttpRequestException or OperationCanceledException)
        {
            if (Response.HasStarted)
            {
                HttpContext.Items["AuditStatus"] = StatusCodes.Status502BadGateway;
                HttpContext.Abort();
                return new EmptyResult();
            }
            Response.Clear();
            Response.Headers.CacheControl = "no-store";
            Response.Headers.XContentTypeOptions = "nosniff";
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private async Task CopyArchiveAsync(Stream source, Backup backup, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var idleTimeout = TimeSpan.FromSeconds(30);
        var buffer = new byte[65536];
        var left = backup.Size;
        while (left > 0)
        {
            deadline.CancelAfter(idleTimeout);
            var bytes = buffer.AsMemory(0, (int)Math.Min(left, buffer.Length));
            await source.ReadExactlyAsync(bytes, deadline.Token);
            digest.AppendData(bytes.Span);
            left -= bytes.Length;
            // Withhold the last chunk so a corrupted archive cannot finish downloading.
            if (left == 0 && !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(backup.Checksum!)))
                throw new IOException("Backup archive checksum mismatch.");
            deadline.CancelAfter(idleTimeout);
            await Response.Body.WriteAsync(bytes, deadline.Token);
        }
    }

    private static bool CanDownload(Backup backup) => backup is
        { IsQueued: false, IsSuccessful: true, DeletionStatus: BackupDeletionStatus.None, Size: > 0, Checksum.Length: 64 } &&
        backup.Checksum.All(char.IsAsciiHexDigit);

    private static bool CanDelete(Backup backup, bool pendingSchedule) => !pendingSchedule &&
        backup is { IsLocked: false, IsQueued: false, RestoreStatus: BackupRestoreStatus.None, DeletionStatus: not BackupDeletionStatus.Queued };

    [HttpPost]
    public async Task<IActionResult> Enqueue(Guid serverId, BackupInput input,
        [FromServices] IServiceScopeFactory scopes, CancellationToken token)
    {
        var server = await LoadServerAsync(serverId, token);
        if (server is null) return NotFound();
        if (!await AllowsAsync(server, ServerPermission.BackupCreate, token)) return Forbid();
        if (string.IsNullOrWhiteSpace(input.Name) || !BackupIgnoreRules.IsValid(input.Exclusions))
            return BadRequest(new { message = "Invalid backup name or exclusion list." });

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireAsync(serverId, token);
        server = await LoadServerAsync(serverId, token);
        if (server is null) return NotFound();
        if (!await AllowsAsync(server, ServerPermission.BackupCreate, token)) return Forbid();
        if (server.IsSuspended || server.Status != ServerStatus.Stopped || string.IsNullOrEmpty(server.ContainerId))
            return Conflict(new { message = "Stop the container before creating a backup." });
        if (!nodes.IsConfigured(server.NodeId)) return StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (!await BackupWorker.HasQueueRoomAsync(db, serverId, token))
            return Conflict(new { message = "A backup is already queued or the server reached its backup limit." });

        var backup = new Backup { ServerId = serverId, Name = input.Name.Trim(), IsQueued = true,
            IgnoredFiles = BackupIgnoreRules.ForStorage(input.Exclusions) };
        db.Backups.Add(backup);
        await db.SaveChangesAsync(token);
        return Accepted(Describe(backup, nodes.IsRestoreConfigured(server.NodeId)));
    }

    private Task<GameServer?> LoadServerAsync(Guid id, CancellationToken token) =>
        db.Servers.AsNoTracking().SingleOrDefaultAsync(server => server.Id == id, token);

    private Task<bool> AllowsAsync(GameServer server, ServerPermission permission, CancellationToken token) =>
        access.AllowsAsync(User, server.Id, server.OwnerId, permission, token);

    private static object Describe(Backup backup, bool restoreAvailable, bool pendingSchedule = false) => new
    {
        backup.Id, backup.Name, backup.Size, backup.Checksum, backup.IsSuccessful, backup.IsQueued,
        backup.IsLocked, DeletionStatus = backup.DeletionStatus.ToString(), CanDelete = CanDelete(backup, pendingSchedule),
        RestoreAvailable = restoreAvailable, RestoreStatus = backup.RestoreStatus.ToString(),
        backup.RestoreNodeId, backup.RestoredFiles, backup.RestoredDirectories, backup.RestoredBytes,
        RestoreCompletedAt = backup.RestoreCompletedAt is { } restored ? DateTime.SpecifyKind(restored, DateTimeKind.Utc) : (DateTime?)null,
        CreatedAt = DateTime.SpecifyKind(backup.CreatedAt, DateTimeKind.Utc),
        CompletedAt = backup.CompletedAt is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : (DateTime?)null
    };

    public sealed record BackupInput([Required, StringLength(100)] string Name, string? Exclusions);
    public sealed record BackupProtection(bool? IsLocked);
}
