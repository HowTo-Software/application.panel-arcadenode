using System.Net;
using ArcadeNode.Backups;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;

namespace ServerPanel.API.Services;

public sealed class BackupWorker(IServiceScopeFactory scopes, ILogger<BackupWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        DrainAsync(RunOneAsync, stoppingToken), DrainAsync(RunOneRestoreAsync, stoppingToken), DrainAsync(RunOneDeletionAsync, stoppingToken));

    private async Task DrainAsync(Func<IServiceScopeFactory, CancellationToken, Task<bool>> runOne, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await runOne(scopes, stoppingToken); }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Backup worker could not process its queue ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    public static async Task<bool> RunOneAsync(IServiceScopeFactory scopes, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<PanelDbContext>();
        var now = DateTime.UtcNow;
        var next = await db.Backups.AsNoTracking().Where(backup => backup.IsQueued && (backup.NextAttemptAt == null || backup.NextAttemptAt <= now))
            .OrderBy(backup => backup.CreatedAt).ThenBy(backup => backup.Id)
            .Select(backup => new { backup.Id, backup.ServerId }).FirstOrDefaultAsync(token);
        if (next is null) return false;

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireAsync(next.ServerId, token);
        var backup = await db.Backups.Include(item => item.Server).ThenInclude(server => server.Owner)
            .SingleOrDefaultAsync(item => item.Id == next.Id && item.IsQueued && (item.NextAttemptAt == null || item.NextAttemptAt <= now), token);
        if (backup is null) return false;

        NodeFileClient.BackupReceipt? receipt = null;
        try
        {
            var server = backup.Server;
            var docker = services.GetRequiredService<IDockerService>();
            if (!server.IsSuspended && server.Owner.IsActive && server.Status == ServerStatus.Stopped &&
                !string.IsNullOrEmpty(server.ContainerId) && await docker.IsContainerStoppedAsync(server.Id, token))
            {
                var result = await services.GetRequiredService<NodeFileClient>()
                    .CreateBackupAsync(server.NodeId, server.Id, backup.Id, BackupIgnoreRules.FromStorage(backup.IgnoredFiles), token);
                if (result.Status == HttpStatusCode.TooManyRequests && backup.CreatedAt > now.AddMinutes(-10))
                {
                    backup.NextAttemptAt = DateTime.UtcNow.AddSeconds(15);
                    await db.SaveChangesAsync(token);
                    return false;
                }
                receipt = result.Receipt;
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsOperationFailure(error))
        {
            services.GetRequiredService<ILogger<BackupWorker>>()
                .LogWarning("Backup {BackupId} failed ({ErrorType}).", backup.Id, error.GetType().Name);
        }
        backup.IsQueued = false;
        backup.NextAttemptAt = null;
        backup.IsSuccessful = receipt is not null;
        backup.CompletedAt = DateTime.UtcNow;
        backup.Size = receipt?.Size ?? 0;
        backup.Checksum = receipt?.Sha256;
        await db.SaveChangesAsync(token);
        return true;
    }

    internal static async Task<bool> HasQueueRoomAsync(PanelDbContext db, Guid serverId, CancellationToken token)
    {
        var backups = db.Backups.Where(backup => backup.ServerId == serverId);
        return !await backups.AnyAsync(backup => backup.IsQueued, token) && await backups.CountAsync(token) < 20;
    }

    public static async Task<bool> RunOneDeletionAsync(IServiceScopeFactory scopes, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<PanelDbContext>();
        var next = await db.Backups.AsNoTracking().Where(b => b.DeletionStatus == BackupDeletionStatus.Queued)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).Select(b => new { b.Id, b.ServerId }).FirstOrDefaultAsync(token);
        if (next is null) return false;

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireBackupRestoreAsync(next.ServerId, token);
        var backup = await db.Backups.Include(b => b.Server)
            .SingleOrDefaultAsync(b => b.Id == next.Id && b.DeletionStatus == BackupDeletionStatus.Queued, token);
        if (backup is null) return false;

        var deleted = false;
        try
        {
            if (!backup.IsLocked && !backup.IsQueued && backup.RestoreStatus == BackupRestoreStatus.None &&
                backup.DeletionNodeId == backup.Server.NodeId &&
                !await db.Schedules.AnyAsync(s => s.ServerId == backup.ServerId && s.PendingBackupId == backup.Id, token))
                deleted = await services.GetRequiredService<NodeFileClient>()
                    .DeleteBackupAsync(backup.Server.NodeId, backup.ServerId, backup.Id, token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsOperationFailure(error))
        {
            services.GetRequiredService<ILogger<BackupWorker>>()
                .LogWarning("Deletion for backup {BackupId} failed ({ErrorType}).", backup.Id, error.GetType().Name);
        }

        // Repeating the request after a node acknowledgement is safe if the database commit was interrupted.
        if (deleted) db.Backups.Remove(backup);
        else backup.DeletionStatus = BackupDeletionStatus.Failed;
        await db.SaveChangesAsync(token);
        return true;
    }

    public static async Task<bool> RunOneRestoreAsync(IServiceScopeFactory scopes, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<PanelDbContext>();
        var next = await db.Backups.AsNoTracking().Where(b => b.RestoreStatus == BackupRestoreStatus.Queued)
            .OrderBy(b => b.RestoreRequestedAt).ThenBy(b => b.Id).Select(b => new { b.Id, b.ServerId })
            .FirstOrDefaultAsync(token);
        if (next is null) return false;

        await using var lockScope = scopes.CreateAsyncScope();
        var gate = lockScope.ServiceProvider.GetRequiredService<ServerOperationGate>();
        await using var lease = await gate.AcquireBackupRestoreAsync(next.ServerId, token);
        var backup = await db.Backups.Include(item => item.Server).ThenInclude(server => server.Owner)
            .SingleOrDefaultAsync(b => b.Id == next.Id && b.RestoreStatus == BackupRestoreStatus.Queued, token);
        if (backup is null) return false;

        NodeFileClient.RestoreReceipt? receipt = null;
        try
        {
            var server = backup.Server;
            if (server.Owner.IsActive && backup.RestoreNodeId == server.NodeId && backup.IsSuccessful && backup.Checksum is not null)
            {
                var result = await services.GetRequiredService<NodeFileClient>()
                    .RestoreBackupAsync(server.NodeId, server.Id, backup.Id, backup.Checksum, token);
                if (result.Status == HttpStatusCode.TooManyRequests) return false;
                receipt = result.Receipt;
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsOperationFailure(error))
        {
            services.GetRequiredService<ILogger<BackupWorker>>()
                .LogWarning("Restore for backup {BackupId} failed ({ErrorType}).", backup.Id, error.GetType().Name);
        }

        backup.RestoreStatus = receipt is null ? BackupRestoreStatus.Failed : BackupRestoreStatus.Prepared;
        backup.RestoreCompletedAt = DateTime.UtcNow;
        backup.RestoredFiles = receipt?.Files;
        backup.RestoredDirectories = receipt?.Directories;
        backup.RestoredBytes = receipt?.Bytes;
        await db.SaveChangesAsync(token);
        return true;
    }

    private static bool IsOperationFailure(Exception error) => error is IOException or HttpRequestException or
        System.Text.Json.JsonException or OperationCanceledException or Docker.DotNet.DockerApiException or InvalidOperationException or
        FormatException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException;
}
