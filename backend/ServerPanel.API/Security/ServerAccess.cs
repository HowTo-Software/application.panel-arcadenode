using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;

namespace ServerPanel.API.Security;

public sealed class ServerAccess(PanelDbContext db)
{
    private static readonly Dictionary<string, ServerPermission> Names = new(StringComparer.Ordinal)
    {
        ["websocket.connect"] = ServerPermission.ConsoleRead,
        ["control.console"] = ServerPermission.ConsoleCommand,
        ["control.start"] = ServerPermission.PowerStart,
        ["control.stop"] = ServerPermission.PowerStop,
        ["control.restart"] = ServerPermission.PowerRestart,
        ["file.read"] = ServerPermission.FileList,
        ["file.read-content"] = ServerPermission.FileRead,
        ["file.create"] = ServerPermission.FileCreate,
        ["file.update"] = ServerPermission.FileUpdate,
        ["file.archive"] = ServerPermission.FileArchive,
        ["file.delete"] = ServerPermission.FileDelete,
        ["schedule.read"] = ServerPermission.ScheduleRead,
        ["schedule.create"] = ServerPermission.ScheduleCreate,
        ["schedule.update"] = ServerPermission.ScheduleUpdate,
        ["schedule.delete"] = ServerPermission.ScheduleDelete,
        ["backup.read"] = ServerPermission.BackupRead,
        ["backup.create"] = ServerPermission.BackupCreate,
        ["backup.delete"] = ServerPermission.BackupDelete,
        ["backup.download"] = ServerPermission.BackupDownload,
        ["backup.restore"] = ServerPermission.BackupRestore,
        ["settings.rename"] = ServerPermission.Rename,
        ["integration.ai"] = ServerPermission.Assistant,
        ["user.read"] = ServerPermission.MemberRead,
        ["user.create"] = ServerPermission.MemberCreate,
        ["user.update"] = ServerPermission.MemberUpdate,
        ["user.delete"] = ServerPermission.MemberDelete
    };

    public const int MaximumMembers = 200;
    public const int PermissionCount = 26;

    public static Guid? UserId(ClaimsPrincipal actor) =>
        Guid.TryParse(actor.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static bool CanManage(ClaimsPrincipal actor, Guid ownerId) =>
        actor.IsInRole("Admin") || actor.IsInRole("SuperAdmin") || UserId(actor) == ownerId;

    public async Task<ServerPermission?> ResolveAsync(ClaimsPrincipal actor, Guid serverId, Guid ownerId, CancellationToken token) =>
        CanManage(actor, ownerId) ? ServerPermission.All
        : UserId(actor) is { } id ? (await ResolveMemberAsync(serverId, id, token))?.Permissions : null;

    public Task<MemberAccess?> ResolveMemberAsync(Guid serverId, Guid userId, CancellationToken token) =>
        db.ServerMembers.AsNoTracking().Where(member => member.ServerId == serverId && member.UserId == userId && member.User.IsActive)
            .Select(member => new MemberAccess(member.Permissions, member.Revision)).SingleOrDefaultAsync(token);

    public sealed record MemberAccess(ServerPermission Permissions, Guid Revision);

    public async Task<bool> AllowsAsync(ClaimsPrincipal actor, Guid serverId, Guid ownerId, ServerPermission permission,
        CancellationToken token) => Allows(await ResolveAsync(actor, serverId, ownerId, token), permission);

    public static bool Allows(ServerPermission? permissions, ServerPermission required) =>
        permissions is { } granted && (granted & required) == required;

    public static bool TryPermission(string name, out ServerPermission permission) => Names.TryGetValue(name, out permission);

    public static string[] Describe(ServerPermission permissions) =>
        [.. Names.Where(entry => Allows(permissions, entry.Value)).Select(entry => entry.Key)];

    public static ServerPermission? Power(string action) => action.ToLowerInvariant() switch
    {
        "start" => ServerPermission.PowerStart,
        "stop" or "kill" => ServerPermission.PowerStop,
        "restart" => ServerPermission.PowerRestart,
        _ => null
    };

    public static ServerPermission TaskPermission(TaskAction action, string? payload) => action switch
    {
        TaskAction.Command => ServerPermission.ConsoleCommand,
        TaskAction.Power when payload is not null => Power(payload) ?? ServerPermission.All,
        TaskAction.Backup => ServerPermission.BackupCreate,
        _ => ServerPermission.All
    };
}
