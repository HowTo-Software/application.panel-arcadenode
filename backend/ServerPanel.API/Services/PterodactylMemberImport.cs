using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;

namespace ServerPanel.API.Services;

public static class PterodactylMemberImport
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    public static async Task<Report> RunAsync(PanelDbContext db, Stream input, bool apply, CancellationToken token = default)
    {
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(input, InputOptions, token);
        if (manifest is not { SchemaVersion: 1, Members.Length: <= 10000 })
            throw new ValidationException("Expected a version 1 Pterodactyl subuser manifest.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var issues = new List<Issue>();
        var unsupported = new List<Unsupported>();
        var existingCounts = new Dictionary<Guid, int>();
        var seen = new HashSet<(Guid ServerId, Guid UserId)>();
        var pending = new List<ServerMember>();
        var alreadyPresent = 0;
        for (var index = 0; index < manifest.Members.Length; index++)
        {
            var position = index + 1;
            var row = manifest.Members[index];
            if (row is null || row.ServerUuid == Guid.Empty || row.UserUuid == Guid.Empty || row.Permissions is not { Length: <= 80 } ||
                row.Permissions.Any(name => name is not { Length: > 0 and <= 80 } || name.Any(c => !char.IsAsciiLetterLower(c) && c is not ('.' or '-' or '_'))) ||
                row.Permissions.Distinct(StringComparer.Ordinal).Count() != row.Permissions.Length)
            {
                issues.Add(new(position, "invalid_subuser"));
                continue;
            }
            if (!seen.Add((row.ServerUuid, row.UserUuid)))
            {
                issues.Add(new(position, "duplicate_subuser"));
                continue;
            }

            var serverOwner = await db.Servers.AsNoTracking().Where(server => server.Id == row.ServerUuid)
                .Select(server => (Guid?)server.OwnerId).SingleOrDefaultAsync(token);
            if (serverOwner is null || serverOwner == row.UserUuid || !await db.Users.AnyAsync(user => user.Id == row.UserUuid &&
                (user.Role == UserRole.User || user.Role == UserRole.SubUser), token))
            {
                issues.Add(new(position, "destination_account_or_server_unavailable"));
                continue;
            }

            var permissions = ServerPermission.View;
            foreach (var name in row.Permissions)
            {
                if (ServerAccess.TryPermission(name, out var flag)) permissions |= flag;
                else unsupported.Add(new(position, name));
            }
            var existing = await db.ServerMembers.AsNoTracking().SingleOrDefaultAsync(member =>
                member.ServerId == row.ServerUuid && member.UserId == row.UserUuid, token);
            if (existing is not null)
            {
                if (existing.Permissions == permissions) alreadyPresent++;
                else issues.Add(new(position, "existing_subuser_conflict"));
                continue;
            }
            if (!existingCounts.TryGetValue(row.ServerUuid, out var count))
                count = await db.ServerMembers.CountAsync(member => member.ServerId == row.ServerUuid, token);
            if (count >= ServerAccess.MaximumMembers)
            {
                issues.Add(new(position, "destination_member_limit"));
                continue;
            }
            existingCounts[row.ServerUuid] = count + 1;
            pending.Add(new ServerMember { ServerId = row.ServerUuid, UserId = row.UserUuid, Permissions = permissions });
        }

        if (apply && issues.Count == 0)
        {
            db.ServerMembers.AddRange(pending);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        return new(pending.Count, alreadyPresent, apply && issues.Count == 0 ? pending.Count : 0, issues, unsupported);
    }

    public sealed record Report(int Proposed, int AlreadyPresent, int Imported, List<Issue> Issues, List<Unsupported> UnsupportedPermissions);
    public sealed record Issue(int Position, string Reason);
    public sealed record Unsupported(int Position, string Permission);
    private sealed record Manifest(int SchemaVersion, Entry[] Members);
    private sealed record Entry(Guid ServerUuid, Guid UserUuid, string[] Permissions);
}
