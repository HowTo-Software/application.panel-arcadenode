using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;

namespace ServerPanel.API.Controllers;

[ApiController]
[Authorize]
[Route("api/servers/{serverId:guid}/members")]
public sealed class ServerMembersController(PanelDbContext db, ServerAccess access, IServiceScopeFactory scopes) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid serverId, CancellationToken token)
    {
        var ownerId = await FindOwnerAsync(serverId, token);
        if (ownerId is null) return NotFound();
        var caller = await ReadCallerAsync(serverId, ownerId.Value, token);
        if (caller is null || !caller.Has(ServerPermission.MemberRead)) return Forbid();
        Response.Headers.CacheControl = "no-store";
        var members = await db.ServerMembers.AsNoTracking().Where(member => member.ServerId == serverId)
            .OrderBy(member => member.User.Username).ThenBy(member => member.UserId)
            .Select(member => new { member.UserId, member.User.Username, member.User.IsActive, member.User.Role, member.Permissions })
            .ToListAsync(token);
        return Ok(new
        {
            Members = members.Select(member => new
            {
                member.UserId, member.Username, member.IsActive, Permissions = ServerAccess.Describe(member.Permissions),
                CanEdit = member.IsActive && IsOrdinaryAccount(member.Role) && caller.Has(ServerPermission.MemberUpdate) &&
                    caller.CanChange(member.UserId, member.Role, member.Permissions),
                CanRemove = caller.Has(ServerPermission.MemberDelete) && caller.CanChange(member.UserId, member.Role, member.Permissions)
            }),
            AvailablePermissions = ServerAccess.Describe(caller.Permissions)
        });
    }

    [HttpPut("{userId:guid}")]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Set(Guid serverId, Guid userId, MemberPermissions input, CancellationToken token)
    {
        if (userId == Guid.Empty) return BadRequest();
        var permissions = ServerPermission.View;
        foreach (var name in input.Permissions)
        {
            if (name is null || !ServerAccess.TryPermission(name, out var flag) || ServerAccess.Allows(permissions, flag))
                return BadRequest(new { message = "Select only distinct server permissions." });
            permissions |= flag;
        }

        if (await CheckWriteAccessAsync(serverId, ServerPermission.MemberCreate | ServerPermission.MemberUpdate, token) is { } denied)
            return denied;
        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireMemberManagementAsync(serverId, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var ownerId = await FindOwnerAsync(serverId, token);
        if (ownerId is null) return NotFound();
        var caller = await ReadCallerAsync(serverId, ownerId.Value, token);
        if (caller is null || (!caller.Has(ServerPermission.MemberCreate) && !caller.Has(ServerPermission.MemberUpdate))) return Forbid();
        if (userId == ownerId || userId == caller.Id) return caller.IsManager ? BadRequest() : Forbid();
        if (!caller.Has(permissions)) return Forbid();

        var member = await db.ServerMembers.FindAsync([serverId, userId], token);
        var required = member is null ? ServerPermission.MemberCreate : ServerPermission.MemberUpdate;
        if (!caller.Has(required) || (member is not null && !caller.Has(member.Permissions))) return Forbid();
        if (!await db.Users.AnyAsync(user => user.Id == userId && user.IsActive &&
            (user.Role == UserRole.User || user.Role == UserRole.SubUser), token)) return NotFound();
        if (input.Operation == "create" && member is not null) return Conflict(new { message = "The user already shares this server." });
        if (input.Operation == "update" && member is null) return NotFound();
        if (member is null)
        {
            if (await db.ServerMembers.CountAsync(entry => entry.ServerId == serverId, token) >= ServerAccess.MaximumMembers)
                return Conflict(new { message = "The server has reached its member limit." });
            member = new ServerMember { ServerId = serverId, UserId = userId };
            db.ServerMembers.Add(member);
        }
        if (member.Permissions != permissions)
        {
            member.Permissions = permissions;
            member.Revision = Guid.NewGuid();
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return NoContent();
    }

    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> Remove(Guid serverId, Guid userId, CancellationToken token)
    {
        if (await CheckWriteAccessAsync(serverId, ServerPermission.MemberDelete, token) is { } denied) return denied;
        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireMemberManagementAsync(serverId, token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var ownerId = await FindOwnerAsync(serverId, token);
        if (ownerId is null) return NotFound();
        var caller = await ReadCallerAsync(serverId, ownerId.Value, token);
        if (caller is null || !caller.Has(ServerPermission.MemberDelete) || userId == ownerId || userId == caller.Id) return Forbid();
        var target = await db.ServerMembers.Include(member => member.User)
            .SingleOrDefaultAsync(member => member.ServerId == serverId && member.UserId == userId, token);
        if (target is null) return NoContent();
        if (!caller.CanChange(userId, target.User.Role, target.Permissions)) return Forbid();
        db.ServerMembers.Remove(target);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return NoContent();
    }

    private Task<Guid?> FindOwnerAsync(Guid id, CancellationToken token) => db.Servers.AsNoTracking()
        .Where(server => server.Id == id).Select(server => (Guid?)server.OwnerId).SingleOrDefaultAsync(token);

    private async Task<IActionResult?> CheckWriteAccessAsync(Guid serverId, ServerPermission anyRequired, CancellationToken token)
    {
        var ownerId = await FindOwnerAsync(serverId, token);
        if (ownerId is null) return NotFound();
        var caller = await ReadCallerAsync(serverId, ownerId.Value, token);
        return caller is not null && (caller.Permissions & anyRequired) != 0 ? null : Forbid();
    }

    private async Task<Caller?> ReadCallerAsync(Guid serverId, Guid ownerId, CancellationToken token)
    {
        var permissions = await access.ResolveAsync(User, serverId, ownerId, token);
        return permissions is { } allowed && ServerAccess.UserId(User) is { } id
            ? new(id, ownerId, allowed, ServerAccess.CanManage(User, ownerId)) : null;
    }

    private static bool IsOrdinaryAccount(UserRole role) => role is UserRole.User or UserRole.SubUser;

    private sealed record Caller(Guid Id, Guid OwnerId, ServerPermission Permissions, bool IsManager)
    {
        public bool Has(ServerPermission required) => ServerAccess.Allows(Permissions, required);
        public bool CanChange(Guid targetId, UserRole role, ServerPermission targetPermissions) =>
            targetId != Id && targetId != OwnerId && (IsManager || (IsOrdinaryAccount(role) && Has(targetPermissions)));
    }

    public sealed record MemberPermissions([Required, MaxLength(ServerAccess.PermissionCount)] string[] Permissions,
        [RegularExpression("^(create|update)$")] string? Operation = null);
}
