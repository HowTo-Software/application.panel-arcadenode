using Microsoft.AspNetCore.Authorization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Common;
using ServerPanel.API.Data;
using ServerPanel.API.DTOs;
using ServerPanel.API.Models;
using ServerPanel.API.Services;

namespace ServerPanel.API.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class AdminController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IServerService _serverService;
    private readonly INodeService _nodeService;
    private readonly PanelDbContext _context;

    public AdminController(
        IAuthService authService,
        IServerService serverService,
        INodeService nodeService,
        PanelDbContext context)
    {
        _authService = authService;
        _serverService = serverService;
        _nodeService = nodeService;
        _context = context;
    }

    /// <summary>
    /// Get dashboard statistics
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(DashboardStats), 200)]
    public async Task<IActionResult> GetDashboardStats()
    {
        var stats = new DashboardStats
        {
            TotalUsers = await _context.Users.CountAsync(),
            TotalServers = await _context.Servers.CountAsync(),
            TotalNodes = await _context.Nodes.CountAsync(),
            RunningServers = await _context.Servers.CountAsync(s => s.Status == Models.ServerStatus.Running),
            OnlineNodes = await _context.Nodes.CountAsync(n => n.IsOnline),
            TotalAllocatedMemory = await _context.Servers.SumAsync(s => s.MemoryLimit),
            TotalAllocatedDisk = await _context.Servers.SumAsync(s => s.DiskLimit)
        };

        return Ok(stats);
    }

    /// <summary>
    /// Get all users
    /// </summary>
    [HttpGet("users")]
    [ProducesResponseType(typeof(IEnumerable<UserDto>), 200)]
    public async Task<IActionResult> GetAllUsers([FromQuery] int? page = null, [FromQuery] int? pageSize = null)
    {
        var (p, ps) = Pagination.Resolve(page, pageSize);
        var result = await _authService.GetAllUsersAsync(p, ps);

        Response.ApplyPaginationHeaders(result);
        return Ok(result.Items);
    }

    /// <summary>
    /// Get user by ID
    /// </summary>
    [HttpGet("users/{id:guid}")]
    [ProducesResponseType(typeof(UserDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var user = await _authService.GetUserByIdAsync(id);
        if (user == null) return NotFound();

        return Ok(user);
    }

    /// <summary>
    /// Delete user
    /// </summary>
    [HttpDelete("users/{id:guid}")]
    [Authorize(Roles = "SuperAdmin")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        // Check if user has servers
        var hasServers = await _context.Servers.AnyAsync(s => s.OwnerId == id);
        if (hasServers)
        {
            return BadRequest(new { message = "Cannot delete user with existing servers" });
        }

        var success = await _authService.DeleteUserAsync(id);
        if (!success) return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Get all nests (categories)
    /// </summary>
    [HttpGet("nests")]
    [ProducesResponseType(typeof(IEnumerable<NestDto>), 200)]
    public async Task<IActionResult> GetNests(int? page = null, int? pageSize = null)
    {
        var paging = ServerPanel.API.Common.Pagination.Resolve(page, pageSize);
        var total = await _context.Nests.CountAsync();
        var nests = await _context.Nests
            .OrderBy(item => item.Id)
            .Skip(ServerPanel.API.Common.Pagination.Skip(paging.Page, paging.PageSize))
            .Take(paging.PageSize)
            .Include(n => n.Eggs)
            .Select(n => new NestDto(
                n.Id,
                n.Name,
                n.Description,
                n.Author,
                n.Eggs.Count(e => e.PterodactylSha256 == null),
                n.CreatedAt
            ))
            .ToListAsync();

        Response.ApplyPaginationHeaders(new PagedResult<NestDto>(nests, paging.Page, paging.PageSize, total));
        return Ok(nests);
    }

    /// <summary>
    /// Get eggs for a nest
    /// </summary>
    [HttpGet("nests/{nestId:guid}/eggs")]
    [ProducesResponseType(typeof(IEnumerable<EggDto>), 200)]
    public async Task<IActionResult> GetEggs(Guid nestId, int? page = null, int? pageSize = null)
    {
        var paging = ServerPanel.API.Common.Pagination.Resolve(page, pageSize);
        var query = _context.Eggs.Where(e => e.NestId == nestId && e.PterodactylSha256 == null);
        var total = await query.CountAsync();
        var eggs = await query
            .OrderBy(item => item.Id)
            .Skip(ServerPanel.API.Common.Pagination.Skip(paging.Page, paging.PageSize))
            .Take(paging.PageSize)
            .Include(e => e.Nest)
            .Select(e => new EggDto(
                e.Id,
                e.Name,
                e.Description,
                e.DockerImage,
                e.StartupCommand,
                e.NestId,
                e.Nest.Name,
                null,
                e.CreatedAt
            ))
            .ToListAsync();

        Response.ApplyPaginationHeaders(new PagedResult<EggDto>(eggs, paging.Page, paging.PageSize, total));
        return Ok(eggs);
    }

    /// <summary>
    /// Get all eggs
    /// </summary>
    [HttpGet("eggs")]
    [ProducesResponseType(typeof(IEnumerable<EggDto>), 200)]
    public async Task<IActionResult> GetAllEggs(int? page = null, int? pageSize = null)
    {
        var paging = ServerPanel.API.Common.Pagination.Resolve(page, pageSize);
        var query = _context.Eggs.Where(e => e.PterodactylSha256 == null);
        var total = await query.CountAsync();
        var eggs = await query
            .OrderBy(item => item.Id)
            .Skip(ServerPanel.API.Common.Pagination.Skip(paging.Page, paging.PageSize))
            .Take(paging.PageSize)
            .Include(e => e.Nest)
            .Select(e => new EggDto(
                e.Id,
                e.Name,
                e.Description,
                e.DockerImage,
                e.StartupCommand,
                e.NestId,
                e.Nest.Name,
                null,
                e.CreatedAt
            ))
            .ToListAsync();

        Response.ApplyPaginationHeaders(new PagedResult<EggDto>(eggs, paging.Page, paging.PageSize, total));
        return Ok(eggs);
    }

    // ── Audit Log ──────────────────────────────────────────────────

    /// <summary>
    /// Get audit logs (most recent first), paginated. Optional filters by user and method.
    /// </summary>
    [HttpGet("audit-logs")]
    [ProducesResponseType(typeof(PagedResult<AuditLogDto>), 200)]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? method = null)
    {
        page = Math.Clamp(page, 1, 1000000);
        if (pageSize is < 1 or > 200) pageSize = 50;

        var query = _context.AuditLogs.AsQueryable();

        if (userId.HasValue)
        {
            query = query.Where(a => a.UserId == userId.Value);
        }

        if (!string.IsNullOrWhiteSpace(method))
        {
            var normalized = method.ToUpperInvariant();
            query = query.Where(a => a.Method == normalized);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditLogDto(
                a.Id,
                a.Timestamp,
                a.UserId,
                a.Username,
                a.Method,
                a.Path,
                a.StatusCode,
                a.IpAddress,
                a.DurationMs))
            .ToListAsync();

        return Ok(new PagedResult<AuditLogDto>(items, page, pageSize, totalCount));
    }

    // ── Invite Code Management ─────────────────────────────────────

    /// <summary>
    /// List all invite codes
    /// </summary>
    [HttpGet("invites")]
    [ProducesResponseType(typeof(IEnumerable<InviteCodeDto>), 200)]
    public async Task<IActionResult> GetInviteCodes(int? page = null, int? pageSize = null)
    {
        var paging = ServerPanel.API.Common.Pagination.Resolve(page, pageSize);
        var total = await _context.InviteCodes.CountAsync();
        var invites = await _context.InviteCodes
            .Include(i => i.CreatedByUser)
            .OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
            .Skip(ServerPanel.API.Common.Pagination.Skip(paging.Page, paging.PageSize)).Take(paging.PageSize)
            .Select(i => new InviteCodeDto(
                i.Id,
                i.Code,
                i.MaxUses,
                i.TimesUsed,
                i.ExpiresAt,
                i.IsRevoked,
                i.IsValid,
                i.CreatedAt,
                i.CreatedByUser != null ? i.CreatedByUser.Username : "unknown"
            ))
            .ToListAsync();

        Response.ApplyPaginationHeaders(new PagedResult<InviteCodeDto>(invites, paging.Page, paging.PageSize, total));
        return Ok(invites);
    }

    /// <summary>
    /// Create a new invite code
    /// </summary>
    [HttpPost("invites")]
    [ProducesResponseType(typeof(InviteCodeDto), 201)]
    public async Task<IActionResult> CreateInviteCode([FromBody] CreateInviteCodeRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (request.MaxUses is < 1 or > 100 || request.ExpiresAt <= DateTime.UtcNow)
            return BadRequest(new { message = "Set a valid invitation usage limit and expiration." });
        var code = GenerateInviteCode();

        var invite = new InviteCode
        {
            Code = code,
            MaxUses = request.MaxUses,
            ExpiresAt = request.ExpiresAt ?? DateTime.UtcNow.AddDays(7),
            CreatedByUserId = userId.Value
        };

        _context.InviteCodes.Add(invite);
        await _context.SaveChangesAsync();

        var creator = await _context.Users.FindAsync(userId.Value);

        var dto = new InviteCodeDto(
            invite.Id,
            invite.Code,
            invite.MaxUses,
            invite.TimesUsed,
            invite.ExpiresAt,
            invite.IsRevoked,
            invite.IsValid,
            invite.CreatedAt,
            creator?.Username ?? "unknown"
        );

        return CreatedAtAction(nameof(GetInviteCodes), dto);
    }

    /// <summary>
    /// Revoke an invite code
    /// </summary>
    [HttpDelete("invites/{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RevokeInviteCode(Guid id)
    {
        var invite = await _context.InviteCodes.FindAsync(id);
        if (invite == null) return NotFound();

        invite.IsRevoked = true;
        invite.ConcurrencyVersion = Guid.NewGuid();
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private Guid? GetUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private static string GenerateInviteCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        var code = new char[16];
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        return new string(code);
    }
}
