using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;

namespace ServerPanel.API.Services;

public static class PterodactylScheduleImport
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    public static async Task<Report> RunAsync(PanelDbContext db, Stream input, bool apply, CancellationToken token = default)
    {
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(input, InputOptions, token);
        if (manifest is not { SchemaVersion: 1, Schedules.Length: <= 10000 })
            throw new ValidationException("Expected a version 1 Pterodactyl schedule manifest.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var issues = new List<Issue>();
        var pending = new List<Schedule>();
        var existingCounts = new Dictionary<Guid, int>();
        var seen = new HashSet<Guid>();
        var alreadyPresent = 0;
        foreach (var entry in manifest.Schedules)
        {
            if (entry is null) throw new ValidationException("The manifest contains an invalid schedule.");
            if (entry.Id <= 0 || entry.ServerUuid == Guid.Empty || entry.Name is not { Length: > 0 and <= 100 } ||
                string.IsNullOrWhiteSpace(entry.Name) || entry.CronExpression is null || entry.Tasks is not { Length: >= 1 and <= 5 })
            {
                issues.Add(new(entry.ServerUuid, entry.Id, "invalid_schedule"));
                continue;
            }
            var id = StableId(entry.ServerUuid, entry.Id);
            if (!seen.Add(id))
            {
                issues.Add(new(entry.ServerUuid, entry.Id, "duplicate_schedule"));
                continue;
            }
            try { ScheduleRules.NextRun(entry.CronExpression, DateTime.UtcNow); }
            catch (ValidationException)
            {
                issues.Add(new(entry.ServerUuid, entry.Id, "invalid_cron"));
                continue;
            }
            if (entry.Tasks.Any(t => t is null || t.SequenceId < 1 || !TryAction(t, out _)) ||
                entry.Tasks.Select(t => t!.SequenceId).Distinct().Count() != entry.Tasks.Length)
            {
                issues.Add(new(entry.ServerUuid, entry.Id, "unsupported_or_duplicate_task"));
                continue;
            }
            if (!existingCounts.TryGetValue(entry.ServerUuid, out var count))
            {
                count = await db.Servers.Where(s => s.Id == entry.ServerUuid).Select(s => (int?)s.Schedules.Count).SingleOrDefaultAsync(token) ?? -1;
                existingCounts.Add(entry.ServerUuid, count);
            }
            if (count < 0)
            {
                issues.Add(new(entry.ServerUuid, entry.Id, "destination_server_missing"));
                continue;
            }
            var expected = entry.Tasks.Select(t => t!).OrderBy(t => t.SequenceId).Select(t =>
            {
                TryAction(t, out var action);
                return new ScheduleTask { SequenceId = t.SequenceId, Action = action, Payload = t.Payload,
                    TimeOffset = t.TimeOffset, ContinueOnFailure = t.ContinueOnFailure };
            }).ToList();
            var existing = await db.Schedules.AsNoTracking().Include(s => s.Tasks).SingleOrDefaultAsync(s => s.Id == id, token);
            if (existing is not null)
            {
                if (existing.ServerId == entry.ServerUuid && existing.Name == entry.Name && existing.CronExpression == entry.CronExpression &&
                    existing.OnlyWhenOnline == entry.OnlyWhenOnline && existing.Tasks.OrderBy(t => t.SequenceId).Select(TaskValues).SequenceEqual(expected.Select(TaskValues)))
                    alreadyPresent++;
                else
                    issues.Add(new(entry.ServerUuid, entry.Id, "existing_schedule_conflict"));
                continue;
            }
            if (count >= 50)
            {
                issues.Add(new(entry.ServerUuid, entry.Id, "destination_schedule_limit"));
                continue;
            }
            existingCounts[entry.ServerUuid] = count + 1;
            pending.Add(new Schedule { Id = id, ServerId = entry.ServerUuid, Name = entry.Name, CronExpression = entry.CronExpression,
                OnlyWhenOnline = entry.OnlyWhenOnline, IsActive = false, Tasks = expected });
        }

        if (apply && issues.Count == 0)
        {
            db.Schedules.AddRange(pending);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        return new(pending.Count, alreadyPresent, apply && issues.Count == 0 ? pending.Count : 0, issues);
    }

    private static bool TryAction(TaskEntry task, out TaskAction action)
    {
        action = task.Action switch
        {
            "power" => TaskAction.Power,
            "command" => TaskAction.Command,
            "backup" => TaskAction.Backup,
            _ => (TaskAction)(-1)
        };
        return ScheduleRules.ValidTask(action, task.Payload, task.TimeOffset);
    }

    private static Guid StableId(Guid serverId, long sourceId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        "arcadenode:pterodactyl:schedule:" + serverId.ToString("D") + ":" + sourceId.ToString(CultureInfo.InvariantCulture))).AsSpan(0, 16));

    private static (int, TaskAction, string?, int, bool) TaskValues(ScheduleTask task) =>
        (task.SequenceId, task.Action, task.Payload, task.TimeOffset, task.ContinueOnFailure);

    private sealed record Manifest(int SchemaVersion, ScheduleEntry?[] Schedules);
    private sealed record ScheduleEntry(long Id, Guid ServerUuid, string? Name, string? CronExpression, bool OnlyWhenOnline, TaskEntry?[] Tasks);
    private sealed record TaskEntry(int SequenceId, string? Action, string? Payload, int TimeOffset, bool ContinueOnFailure);
    public sealed record Issue(Guid ServerUuid, long SourceId, string Reason);
    public sealed record Report(int Pending, int AlreadyPresent, int Imported, IReadOnlyList<Issue> Issues);
}
