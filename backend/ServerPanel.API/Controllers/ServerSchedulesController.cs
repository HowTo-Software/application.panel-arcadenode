using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Services;
using ServerPanel.API.Security;

namespace ServerPanel.API.Controllers;

[ApiController]
[Authorize]
[Route("api/servers/{serverId:guid}/schedules")]
public sealed class ServerSchedulesController(PanelDbContext db, ServerAccess access, IServiceScopeFactory scopes) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid serverId, CancellationToken token)
    {
        var (denied, _, _, _) = await CheckAccessAsync(serverId, ServerPermission.ScheduleRead, token);
        if (denied is not null) return denied;
        var schedules = await db.Schedules.AsNoTracking().Where(s => s.ServerId == serverId)
            .Include(s => s.Tasks).OrderBy(s => s.Name).ThenBy(s => s.Id).Take(50).ToListAsync(token);
        return Ok(schedules.Select(ToResponse));
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid serverId, [FromBody] ScheduleInput input, CancellationToken token)
    {
        var (denied, permissions, memberId, memberRevision) = await CheckAccessAsync(serverId, ServerPermission.ScheduleCreate, token);
        if (denied is not null) return denied;
        if (!IsValid(input))
            return BadRequest(new { message = "Invalid schedule or task." });
        if (input.Tasks.Any(task => !ServerAccess.Allows(permissions, ServerAccess.TaskPermission(task.Action, task.Payload)))) return Forbid();

        var next = ScheduleRules.NextRun(input.CronExpression, DateTime.UtcNow);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        if (await db.Schedules.CountAsync(s => s.ServerId == serverId, token) >= 50)
            return Conflict(new { message = "The server has reached its schedule limit." });
        var schedule = new Schedule
        {
            ServerId = serverId, Name = input.Name.Trim(), CronExpression = input.CronExpression.Trim(),
            IsActive = input.IsActive, OnlyWhenOnline = input.OnlyWhenOnline, NextRunAt = input.IsActive ? next : null,
            DelegatedUserId = memberId, DelegatedMemberRevision = memberRevision
        };
        schedule.Tasks = BuildTasks(schedule.Id, input.Tasks);
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return StatusCode(StatusCodes.Status201Created, ToResponse(schedule));
    }

    [HttpPut("{scheduleId:guid}")]
    public async Task<IActionResult> Update(Guid serverId, Guid scheduleId, [FromBody] ScheduleInput input,
        [FromHeader(Name = "If-Match")] string? expectedRevision, CancellationToken token)
    {
        var (initialDenial, _, _, _) = await CheckAccessAsync(serverId, ServerPermission.ScheduleUpdate, token);
        if (initialDenial is not null) return initialDenial;
        if (!IsValid(input)) return BadRequest(new { message = "Invalid schedule or task." });
        if (string.IsNullOrEmpty(expectedRevision)) return StatusCode(StatusCodes.Status428PreconditionRequired);
        var next = ScheduleRules.NextRun(input.CronExpression, DateTime.UtcNow);

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireScheduleClaimAsync(serverId, token);
        var (denied, permissions, memberId, memberRevision) = await CheckAccessAsync(serverId, ServerPermission.ScheduleUpdate, token);
        if (denied is not null) return denied;
        if (input.Tasks.Any(task => !ServerAccess.Allows(permissions, ServerAccess.TaskPermission(task.Action, task.Payload)))) return Forbid();

        var query = db.Schedules.Where(s => s.ServerId == serverId && s.Id == scheduleId);
        var schedule = await query.AsNoTracking().Include(s => s.Tasks).SingleOrDefaultAsync(token);
        if (schedule is null) return NotFound();
        if (schedule.IsProcessing || schedule.PendingBackupId.HasValue) return Conflict();
        if (!string.Equals(expectedRevision, $"\"{Revision(schedule, OrderedTasks(schedule))}\"", StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status412PreconditionFailed);

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var changed = await query.Where(s => !s.IsProcessing && s.PendingBackupId == null).ExecuteUpdateAsync(update => update
            .SetProperty(s => s.Name, input.Name.Trim()).SetProperty(s => s.CronExpression, input.CronExpression.Trim())
            .SetProperty(s => s.IsActive, input.IsActive).SetProperty(s => s.OnlyWhenOnline, input.OnlyWhenOnline)
            .SetProperty(s => s.NextRunAt, input.IsActive ? next : (DateTime?)null)
            .SetProperty(s => s.DelegatedUserId, memberId).SetProperty(s => s.DelegatedMemberRevision, memberRevision), token);
        if (changed != 1) return Conflict();
        await db.ScheduleTasks.Where(task => task.ScheduleId == scheduleId).ExecuteDeleteAsync(token);
        db.ScheduleTasks.AddRange(BuildTasks(scheduleId, input.Tasks));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return NoContent();
    }

    [HttpDelete("{scheduleId:guid}")]
    public async Task<IActionResult> Delete(Guid serverId, Guid scheduleId, CancellationToken token)
    {
        var (denied, _, _, _) = await CheckAccessAsync(serverId, ServerPermission.ScheduleDelete, token);
        if (denied is not null) return denied;
        var query = db.Schedules.Where(s => s.ServerId == serverId && s.Id == scheduleId);
        if (await query.Where(s => !s.IsProcessing).ExecuteDeleteAsync(token) == 1) return NoContent();
        return await query.AnyAsync(token) ? Conflict() : NotFound();
    }

    [HttpPut("{scheduleId:guid}/activity")]
    public async Task<IActionResult> SetActivity(Guid serverId, Guid scheduleId, [FromBody] ScheduleActivity input, CancellationToken token)
    {
        var (denied, permissions, memberId, memberRevision) = await CheckAccessAsync(serverId, ServerPermission.ScheduleUpdate, token);
        if (denied is not null) return denied;
        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireScheduleClaimAsync(serverId, token);
        var query = db.Schedules.Where(s => s.ServerId == serverId && s.Id == scheduleId);
        var schedule = await query.AsNoTracking().Include(item => item.Tasks).SingleOrDefaultAsync(token);
        if (schedule is null) return NotFound();
        if (input.IsActive && schedule.Tasks.Any(task => !ServerAccess.Allows(permissions, ServerAccess.TaskPermission(task.Action, task.Payload))))
            return Forbid();
        DateTime? next = input.IsActive ? ScheduleRules.NextRun(schedule.CronExpression, DateTime.UtcNow) : null;
        var delegatedUserId = input.IsActive ? memberId : schedule.DelegatedUserId;
        var delegatedMemberRevision = input.IsActive ? memberRevision : schedule.DelegatedMemberRevision;
        var changed = await query.Where(s => !s.IsProcessing && (s.IsActive != input.IsActive || s.NextRunAt == null))
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.IsActive, input.IsActive).SetProperty(s => s.NextRunAt, next)
                .SetProperty(s => s.DelegatedUserId, delegatedUserId).SetProperty(s => s.DelegatedMemberRevision, delegatedMemberRevision), token);
        if (changed == 1 || await query.AnyAsync(s => !s.IsProcessing && s.IsActive == input.IsActive, token)) return NoContent();
        return Conflict();
    }

    private async Task<(IActionResult? Denied, ServerPermission Permissions, Guid? MemberId, Guid? Revision)> CheckAccessAsync(Guid id,
        ServerPermission required, CancellationToken token)
    {
        var server = await db.Servers.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.OwnerId }).SingleOrDefaultAsync(token);
        if (server is null) return (NotFound(), ServerPermission.View, null, null);
        if (ServerAccess.CanManage(User, server.OwnerId)) return (null, ServerPermission.All, null, null);
        var userId = ServerAccess.UserId(User);
        var member = userId is { } actor ? await access.ResolveMemberAsync(id, actor, token) : null;
        return member is not null && ServerAccess.Allows(member.Permissions, required)
            ? (null, member.Permissions, userId, member.Revision) : (Forbid(), ServerPermission.View, null, null);
    }

    private static bool IsValid(ScheduleInput input) => !string.IsNullOrWhiteSpace(input.Name) &&
        input.Tasks.All(task => task is not null && ScheduleRules.ValidTask(task.Action, task.Payload, task.TimeOffset));

    private static List<ScheduleTask> BuildTasks(Guid scheduleId, ScheduleTaskInput[] tasks) => tasks.Select((task, index) => new ScheduleTask
    {
        ScheduleId = scheduleId, SequenceId = index + 1, Action = task.Action, Payload = task.Payload,
        TimeOffset = task.TimeOffset, ContinueOnFailure = task.ContinueOnFailure
    }).ToList();

    private static ScheduleTaskInput[] OrderedTasks(Schedule schedule) => schedule.Tasks.OrderBy(task => task.SequenceId)
        .Select(task => new ScheduleTaskInput(task.Action, task.Payload, task.TimeOffset, task.ContinueOnFailure)).ToArray();

    private static string Revision(Schedule schedule, ScheduleTaskInput[] tasks) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schedule.Name, schedule.CronExpression, schedule.IsActive, schedule.OnlyWhenOnline,
            schedule.DelegatedUserId, schedule.DelegatedMemberRevision, Tasks = tasks
        })));

    private static object ToResponse(Schedule schedule)
    {
        var tasks = OrderedTasks(schedule);
        return new
        {
            schedule.Id, schedule.Name, schedule.CronExpression, schedule.IsActive, schedule.IsProcessing,
            WaitingForBackup = schedule.PendingBackupId.HasValue,
            schedule.OnlyWhenOnline, NextRunAt = AsUtc(schedule.NextRunAt), LastRunAt = AsUtc(schedule.LastRunAt),
            Tasks = tasks, Revision = Revision(schedule, tasks)
        };
    }

    private static DateTime? AsUtc(DateTime? value) =>
        value is { } time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : null;

    public sealed record ScheduleInput([Required, StringLength(100)] string Name, [Required, StringLength(100)] string CronExpression,
        bool IsActive, bool OnlyWhenOnline, [Required, MinLength(1), MaxLength(5)] ScheduleTaskInput[] Tasks);

    public sealed record ScheduleTaskInput(TaskAction Action, string? Payload, int TimeOffset, bool ContinueOnFailure);
    public sealed record ScheduleActivity(bool IsActive);
}
