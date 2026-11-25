using System.ComponentModel.DataAnnotations;
using System.Globalization;
using ArcadeNode.Backups;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;

namespace ServerPanel.API.Services;

public sealed class ScheduleWorker(IServiceScopeFactory scopes, ILogger<ScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunOneAsync(scope.ServiceProvider, DateTime.UtcNow, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Schedule worker failed ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    public static async Task<bool> RunOneAsync(IServiceProvider services, DateTime now, CancellationToken token)
    {
        var db = services.GetRequiredService<PanelDbContext>();
        var logger = services.GetRequiredService<ILogger<ScheduleWorker>>();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var abandonedBefore = now.AddMinutes(-10);
        await db.Schedules.Where(s => s.IsProcessing && s.PendingBackupId == null &&
                ((s.ProcessingStartedAt ?? s.LastRunAt) == null || (s.ProcessingStartedAt ?? s.LastRunAt) <= abandonedBefore))
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.IsProcessing, false)
                .SetProperty(s => s.ProcessingStartedAt, (DateTime?)null), token);
        var run = await ResumeAfterBackupAsync(db, now, token) ?? await ClaimDueAsync(db, scopes, logger, now, token);
        if (run is null) return false;
        var waitingForBackup = false;

        try
        {
            if (!run.Continue) return true;
            var schedule = await db.Schedules.AsNoTracking().Include(s => s.Tasks).SingleOrDefaultAsync(s => s.Id == run.Id, token);
            if (schedule is null) return true;
            var initialState = await GetStateAsync(db, schedule.ServerId, token);
            if (initialState is null) return true;
            var docker = services.GetRequiredService<IDockerService>();
            if (run.AfterSequence == 0 && schedule.OnlyWhenOnline &&
                await docker.ObserveRuntimeAsync(schedule.ServerId, token) is not { Status: ServerStatus.Running }) return true;

            var servers = services.GetRequiredService<IServerService>();
            var access = services.GetRequiredService<ServerAccess>();
            foreach (var task in schedule.Tasks.OrderBy(t => t.SequenceId).Take(5).Where(t => t.SequenceId > run.AfterSequence))
            {
                if (!ScheduleRules.ValidTask(task.Action, task.Payload, task.TimeOffset)) break;
                if (task.TimeOffset != 0) await Task.Delay(TimeSpan.FromSeconds(task.TimeOffset), token);
                if (await GetStateAsync(db, schedule.ServerId, token) is null) break;
                if (schedule.DelegatedUserId is { } memberId)
                {
                    var current = await access.ResolveMemberAsync(schedule.ServerId, memberId, token);
                    if (current is null || current.Revision != schedule.DelegatedMemberRevision ||
                        (!ServerAccess.Allows(current.Permissions, ServerPermission.ScheduleCreate) &&
                         !ServerAccess.Allows(current.Permissions, ServerPermission.ScheduleUpdate)) ||
                        !ServerAccess.Allows(current.Permissions, ServerAccess.TaskPermission(task.Action, task.Payload)))
                    {
                        await db.Schedules.Where(s => s.Id == schedule.Id && s.ProcessingStartedAt == run.ClaimedAt)
                            .ExecuteUpdateAsync(update => update.SetProperty(s => s.IsActive, false)
                                .SetProperty(s => s.NextRunAt, (DateTime?)null), token);
                        break;
                    }
                }
                bool succeeded;
                try
                {
                    succeeded = task.Action switch
                    {
                        TaskAction.Command => await docker.SendCommandAsync(schedule.ServerId, task.Payload!),
                        TaskAction.Power => await PowerAsync(docker, servers, schedule.ServerId, task.Payload!),
                        TaskAction.Backup => await EnqueueBackupAsync(scopes, schedule, task, run, token),
                        _ => false
                    };
                    // The durable backup receipt decides whether the remaining steps may run.
                    if (succeeded && task.Action == TaskAction.Backup)
                    {
                        waitingForBackup = true;
                        return true;
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    succeeded = false;
                    logger.LogWarning("Schedule {ScheduleId} task {TaskId} failed ({ErrorType}).",
                        run.Id, task.Id, error.GetType().Name);
                    // A failed save can leave the queue outcome unknown to this worker.
                    if (task.Action == TaskAction.Backup) return true;
                }
                if (!succeeded && !task.ContinueOnFailure) break;
            }
            return true;
        }
        finally
        {
            if (!waitingForBackup)
                await db.Schedules.Where(s => s.Id == run.Id && s.ProcessingStartedAt == run.ClaimedAt && s.PendingBackupId == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(s => s.IsProcessing, false)
                        .SetProperty(s => s.ProcessingStartedAt, (DateTime?)null), CancellationToken.None);
        }
    }

    private static async Task<ScheduleRun?> ClaimDueAsync(PanelDbContext db, IServiceScopeFactory scopes,
        ILogger logger, DateTime now, CancellationToken token)
    {
        var candidates = db.Schedules.Where(s => s.IsActive && !s.IsProcessing && s.NextRunAt <= now &&
            !db.Schedules.Any(other => other.ServerId == s.ServerId && other.IsProcessing));
        var due = await candidates.AsNoTracking().OrderBy(s => s.NextRunAt).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.ServerId, s.NextRunAt, s.CronExpression }).FirstOrDefaultAsync(token);
        if (due is null) return null;

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireScheduleClaimAsync(due.ServerId, token);
        var claim = candidates.Where(s => s.Id == due.Id && s.NextRunAt == due.NextRunAt && s.CronExpression == due.CronExpression);
        DateTime next;
        try { next = ScheduleRules.NextRun(due.CronExpression, now); }
        catch (ValidationException)
        {
            await claim.ExecuteUpdateAsync(update => update.SetProperty(s => s.IsActive, false).SetProperty(s => s.NextRunAt, (DateTime?)null), token);
            logger.LogWarning("Disabled invalid schedule {ScheduleId}.", due.Id);
            return null;
        }

        // Claim non-idempotent commands or power operations before sending them to a node.
        return await claim.ExecuteUpdateAsync(update => update.SetProperty(s => s.IsProcessing, true)
            .SetProperty(s => s.ProcessingStartedAt, now).SetProperty(s => s.LastRunAt, now).SetProperty(s => s.NextRunAt, next), token) == 1
            ? new ScheduleRun(due.Id, now, now, AfterSequence: 0, Continue: true) : null;
    }

    private static async Task<ScheduleRun?> ResumeAfterBackupAsync(PanelDbContext db, DateTime now, CancellationToken token)
    {
        var ready = await (from schedule in db.Schedules.AsNoTracking()
            join backup in db.Backups.AsNoTracking() on schedule.PendingBackupId equals (Guid?)backup.Id
            join task in db.ScheduleTasks.AsNoTracking() on schedule.PendingBackupTaskId equals (Guid?)task.Id
            where schedule.IsProcessing && schedule.LastRunAt != null && backup.ServerId == schedule.ServerId &&
                task.ScheduleId == schedule.Id && !backup.IsQueued
            orderby backup.CompletedAt, schedule.Id
            select new { schedule.Id, schedule.LastRunAt, BackupId = backup.Id, task.SequenceId, Continue = backup.IsSuccessful || task.ContinueOnFailure })
            .FirstOrDefaultAsync(token);
        if (ready is null) return null;

        return await db.Schedules.Where(s => s.Id == ready.Id && s.IsProcessing && s.PendingBackupId == ready.BackupId)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.PendingBackupId, (Guid?)null)
                .SetProperty(s => s.PendingBackupTaskId, (Guid?)null).SetProperty(s => s.ProcessingStartedAt, now), token) == 1
            ? new ScheduleRun(ready.Id, ready.LastRunAt!.Value, now, ready.SequenceId, ready.Continue) : null;
    }

    private static async Task<bool> EnqueueBackupAsync(IServiceScopeFactory scopes, Schedule schedule, ScheduleTask task,
        ScheduleRun run, CancellationToken token)
    {
        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireAsync(schedule.ServerId, token);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        var nodeId = await db.Servers.AsNoTracking().Where(s => s.Id == schedule.ServerId && !s.IsSuspended && s.Owner.IsActive &&
            s.Status == ServerStatus.Stopped && s.ContainerId != null && s.ContainerId != "")
            .Select(s => (Guid?)s.NodeId).SingleOrDefaultAsync(token);
        if (nodeId is null || !scope.ServiceProvider.GetRequiredService<NodeFileClient>().IsConfigured(nodeId.Value) ||
            !await BackupWorker.HasQueueRoomAsync(db, schedule.ServerId, token)) return false;

        var current = await db.Schedules.SingleOrDefaultAsync(s => s.Id == run.Id && s.IsProcessing && s.PendingBackupId == null &&
            s.ProcessingStartedAt == run.ClaimedAt && s.LastRunAt == run.OccurredAt, token);
        if (current is null) return false;
        var suffix = run.OccurredAt.ToString(" [yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + $" #{task.SequenceId}]";
        var backup = new Backup { ServerId = schedule.ServerId, IsQueued = true,
            Name = schedule.Name[..Math.Min(schedule.Name.Length, 100 - suffix.Length)] + suffix,
            IgnoredFiles = BackupIgnoreRules.ForStorage(task.Payload) };
        db.Backups.Add(backup);
        current.PendingBackupId = backup.Id;
        current.PendingBackupTaskId = task.Id;
        await db.SaveChangesAsync(token);
        return true;
    }

    private sealed record ScheduleRun(Guid Id, DateTime OccurredAt, DateTime ClaimedAt, int AfterSequence, bool Continue);

    private static Task<ServerStatus?> GetStateAsync(PanelDbContext db, Guid serverId, CancellationToken token) =>
        db.Servers.AsNoTracking().Where(s => s.Id == serverId && !s.IsSuspended && s.Owner.IsActive &&
            s.ContainerId != null && s.ContainerId != "").Select(s => (ServerStatus?)s.Status).SingleOrDefaultAsync(token);

    private static async Task<bool> PowerAsync(IDockerService docker, IServerService servers, Guid id, string action)
    {
        var result = action switch
        {
            "start" => await docker.StartContainerAsync(id),
            "stop" => await docker.StopContainerAsync(id),
            "restart" => await docker.RestartContainerAsync(id),
            "kill" => await docker.KillContainerAsync(id),
            _ => false
        };
        if (result) await servers.UpdateServerStatusAsync(id, action is "stop" or "kill" ? ServerStatus.Stopped : ServerStatus.Running);
        return result;
    }
}
