using System.ComponentModel.DataAnnotations;
using ArcadeNode.Backups;
using Cronos;
using ServerPanel.API.Models;

namespace ServerPanel.API.Services;

internal static class ScheduleRules
{
    public static DateTime NextRun(string expression, DateTime from)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (expression.Length > 100 || fields.Length != 5)
            throw new ValidationException("Use a five-field cron expression in UTC.");
        try
        {
            // Pterodactyl fires when either restricted day field matches.
            if (fields[2] != "*" && fields[4] != "*")
            {
                var byWeekday = (string[])fields.Clone();
                byWeekday[2] = "*";
                fields[4] = "*";
                var a = CronExpression.Parse(string.Join(' ', fields)).GetNextOccurrence(from);
                var b = CronExpression.Parse(string.Join(' ', byWeekday)).GetNextOccurrence(from);
                return (a is not null && (b is null || a < b) ? a : b)
                    ?? throw new ValidationException("The schedule has no future occurrence.");
            }

            return CronExpression.Parse(expression).GetNextOccurrence(from)
                ?? throw new ValidationException("The schedule has no future occurrence.");
        }
        catch (CronFormatException)
        {
            throw new ValidationException("Invalid cron expression.");
        }
    }

    public static bool ValidTask(TaskAction action, string? payload, int delay) =>
        delay is >= 0 and <= 60 && action switch
        {
            TaskAction.Power => payload is "start" or "stop" or "restart" or "kill",
            TaskAction.Command => !string.IsNullOrWhiteSpace(payload) && payload.Length <= 512 && !payload.Any(char.IsControl),
            TaskAction.Backup => BackupIgnoreRules.IsValid(payload),
            _ => false
        };
}
