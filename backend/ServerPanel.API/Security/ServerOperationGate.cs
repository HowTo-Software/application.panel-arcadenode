using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ServerPanel.API.Data;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Security;

// Coordinates writes across API replicas in SQL Server. No business changes are
// saved inside this transaction; disposing it releases the application lock.
public sealed class ServerOperationGate(PanelDbContext db, IWebHostEnvironment environment, ILogger<ServerOperationGate> logger)
{
    private static readonly SemaphoreSlim[] TestLocks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim[] TestRestoreLocks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim[] TestScheduleLocks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim[] TestMemberLocks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private sealed class Lease(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }

    public Task<IAsyncDisposable> AcquireAsync(Guid serverId, CancellationToken token) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.ServerLock, () => AcquireCoreAsync($"arcadenode.server.{serverId:N}", TestLocks, token), serverId);

    public Task<IAsyncDisposable> AcquireBackupRestoreAsync(Guid serverId, CancellationToken token) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.ServerLock, () => AcquireCoreAsync($"arcadenode.backup.restore.{serverId:N}", TestRestoreLocks, token), serverId);

    public Task<IAsyncDisposable> AcquireScheduleClaimAsync(Guid serverId, CancellationToken token) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.ServerLock, () => AcquireCoreAsync($"arcadenode.schedule.claim.{serverId:N}", TestScheduleLocks, token), serverId);

    public Task<IAsyncDisposable> AcquireMemberManagementAsync(Guid serverId, CancellationToken token) =>
        DiagnosticLog.RunAsync(logger, DiagnosticPoints.ServerLock, () => AcquireCoreAsync($"arcadenode.server.members.{serverId:N}", TestMemberLocks, token), serverId);

    private async Task<IAsyncDisposable> AcquireCoreAsync(string resourceName, SemaphoreSlim[] testLocks, CancellationToken token)
    {
        if (!db.Database.IsSqlServer())
        {
            if (!environment.IsEnvironment("Testing")) throw new InvalidOperationException("Server operation locking requires SQL Server.");
            var gate = testLocks[(uint)StringComparer.Ordinal.GetHashCode(resourceName) % (uint)testLocks.Length];
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), token)) throw new InvalidOperationException("Another server operation is still running.");
            return new Lease(() => { gate.Release(); return ValueTask.CompletedTask; });
        }

        var transaction = await db.Database.BeginTransactionAsync(token);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandTimeout = 10;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive',
                  @LockOwner = 'Transaction', @LockTimeout = 5000, @DbPrincipal = 'public';
                SELECT @result;
                """;
            var resource = command.CreateParameter(); resource.ParameterName = "@resource";
            resource.DbType = DbType.String; resource.Size = 100; resource.Value = resourceName;
            command.Parameters.Add(resource);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0)
                throw new InvalidOperationException("Another server operation is still running.");
            return new Lease(transaction.DisposeAsync);
        }
        catch { await transaction.DisposeAsync(); throw; }
    }
}
