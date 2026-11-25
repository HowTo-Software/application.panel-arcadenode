using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using ArcadeNode.Transfers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;
using ServerPanel.API.Services;

namespace ServerPanel.API.Controllers;

[ApiController]
[Authorize]
[Route("api/servers/{serverId:guid}/files")]
public sealed class FileDownloadsController(PanelDbContext db, NodeFileClient nodes, ServerAccess access) : ControllerBase
{
    [HttpPost("download-grant")]
    [EnableRateLimiting("file-grant")]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Issue(Guid serverId, FileDownloadRequest request, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!SecurityLimits.ValidGamePath(request.Path)) return BadRequest();
        var server = await db.Servers.AsNoTracking().Where(row => row.Id == serverId)
            .Select(row => new { row.NodeId, row.OwnerId, row.IsSuspended }).SingleOrDefaultAsync(token);
        if (server is null) return NotFound();
        Guid? memberRevision = null;
        if (!ServerAccess.CanManage(User, server.OwnerId))
        {
            var member = ServerAccess.UserId(User) is { } id ? await access.ResolveMemberAsync(serverId, id, token) : null;
            if (member is null || !ServerAccess.Allows(member.Permissions, ServerPermission.FileRead)) return Forbid();
            memberRevision = member.Revision;
        }
        if (server.IsSuspended) return Conflict();
        if (!Guid.TryParse(User.FindFirstValue("sid"), out var sessionId) ||
            !Enum.TryParse<UserRole>(User.FindFirstValue(ClaimTypes.Role), out var role) ||
            User.FindFirstValue("session_version") is not { Length: 32 } sessionVersion) return Unauthorized();

        try
        {
            using var probe = await nodes.OpenFileAsync(server.NodeId, serverId, request.Path, HttpMethod.Head, token);
            var status = DescribeNodeResponse(probe, out _);
            if (status != StatusCodes.Status200OK) return StatusCode(status);
        }
        catch (HttpRequestException) { return StatusCode(StatusCodes.Status502BadGateway); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return StatusCode(StatusCodes.Status504GatewayTimeout); }

        var now = DateTime.UtcNow;
        await db.FileDownloadGrants.Where(grant => grant.ExpiresAt <= now).ExecuteDeleteAsync(token);
        var secret = RandomNumberGenerator.GetBytes(32);
        db.FileDownloadGrants.Add(new FileDownloadGrant
        {
            TokenHash = Convert.ToHexString(SHA256.HashData(secret)), ServerId = serverId, Path = request.Path,
            SessionId = sessionId, SessionVersion = sessionVersion, MemberRevision = memberRevision, Role = role,
            ExpiresAt = now.AddMinutes(1)
        });
        await db.SaveChangesAsync(token);
        return Ok(new { ticket = Convert.ToHexString(secret) });
    }

    [HttpPost("download")]
    [AllowAnonymous]
    [EnableRateLimiting("file-transfer")]
    [Consumes("application/x-www-form-urlencoded")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> Download(Guid serverId, [FromForm] string? ticket, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (ticket is not { Length: 64 } || !ticket.All(char.IsAsciiHexDigit)) return Unauthorized();

        var hash = Convert.ToHexString(SHA256.HashData(Convert.FromHexString(ticket)));
        var now = DateTime.UtcNow;
        var valid = db.FileDownloadGrants.Where(grant => grant.TokenHash == hash && grant.ServerId == serverId &&
            grant.ExpiresAt > now && !grant.Server.IsSuspended && grant.Session.ExpiresAt > now &&
            grant.Session.User.IsActive && grant.Session.User.Role == grant.Role && grant.Session.User.SecurityStamp == grant.SessionVersion &&
            ((grant.MemberRevision == null && (grant.Server.OwnerId == grant.Session.UserId ||
                grant.Role == UserRole.Admin || grant.Role == UserRole.SuperAdmin)) ||
             db.ServerMembers.Any(member => member.ServerId == serverId && member.UserId == grant.Session.UserId && member.Revision == grant.MemberRevision &&
                (member.Permissions & ServerPermission.FileRead) == ServerPermission.FileRead)));
        var download = await valid.AsNoTracking().Select(grant => new
        {
            grant.Path, grant.Server.NodeId, grant.Session.UserId, grant.Session.User.Username
        }).SingleOrDefaultAsync(token);
        if (download is null || await valid.ExecuteDeleteAsync(token) != 1) return Unauthorized();

        HttpContext.Items["AuditUserId"] = download.UserId;
        HttpContext.Items["AuditUsername"] = download.Username;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(GameFileTransfer.DownloadTimeoutSeconds));
        try
        {
            using var response = await nodes.OpenFileAsync(download.NodeId, serverId, download.Path, HttpMethod.Get, deadline.Token);
            var status = DescribeNodeResponse(response, out var length);
            if (status != StatusCodes.Status200OK) return StatusCode(status);

            var attachment = new ContentDispositionHeaderValue("attachment");
            attachment.SetHttpFileName(download.Path[(download.Path.LastIndexOf('/') + 1)..]);
            Response.Headers.ContentDisposition = attachment.ToString();
            Response.Headers["X-Accel-Buffering"] = "no";
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = length;
            await using var source = await response!.Content.ReadAsStreamAsync(deadline.Token);
            await GameFileTransfer.CopyExactlyAsync(source, Response.Body, length, deadline.Token);
            return new EmptyResult();
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            var status = error is OperationCanceledException ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status502BadGateway;
            if (Response.HasStarted || token.IsCancellationRequested)
            {
                HttpContext.Items["AuditStatus"] = status;
                HttpContext.Abort();
                return new EmptyResult();
            }
            Response.Clear();
            Response.Headers.CacheControl = "no-store";
            Response.Headers.XContentTypeOptions = "nosniff";
            return StatusCode(status);
        }
    }

    private static int DescribeNodeResponse(HttpResponseMessage? response, out long length)
    {
        length = 0;
        if (response is null) return StatusCodes.Status503ServiceUnavailable;
        if (response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength is >= 0 and <= GameFileTransfer.MaximumBytes &&
            response.Content.Headers.ContentType?.MediaType == "application/octet-stream" && response.Content.Headers.ContentEncoding.Count == 0)
        {
            length = response.Content.Headers.ContentLength.Value;
            return StatusCodes.Status200OK;
        }
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.TooManyRequests or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => (int)response.StatusCode,
            _ => StatusCodes.Status502BadGateway
        };
    }

    public sealed record FileDownloadRequest(string Path);
}
