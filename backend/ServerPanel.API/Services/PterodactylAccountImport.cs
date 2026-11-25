using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;
using ServerPanel.API.Models;
using ServerPanel.API.Security;

namespace ServerPanel.API.Services;

public static class PterodactylAccountImport
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    public static async Task<ImportReport> RunAsync(PanelDbContext db, Stream input, bool apply,
        IDataProtector? totpProtector = null, IReadOnlySet<Guid>? approvedAdministrators = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await JsonSerializer.DeserializeAsync<AccountManifest>(input, InputOptions, cancellationToken);
        if (manifest is not { SchemaVersion: 1, Users.Length: > 0 and <= 10000 })
            throw new ValidationException("Expected a version 1 account manifest with 1 to 10000 accounts.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var issues = new List<AccountIssue>();
        var pending = new List<User>();
        var ids = new HashSet<Guid>();
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alreadyPresent = 0;
        for (var index = 0; index < manifest.Users.Length; index++)
        {
            var source = manifest.Users[index];
            if (source is null || source.Uuid == Guid.Empty || source.Username is not { Length: > 0 and <= 100 } ||
                source.Username.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) ||
                source.Email is not { Length: > 0 and <= 255 } || !new EmailAddressAttribute().IsValid(source.Email) ||
                source.Email != source.Email.Trim() || source.NameFirst?.Length > 50 || source.NameLast?.Length > 50)
            {
                issues.Add(new(index + 1, "invalid_identity"));
                continue;
            }
            if (!ids.Add(source.Uuid) || !logins.Add(source.Username) || !logins.Add(source.Email))
            {
                issues.Add(new(index + 1, "duplicate_identity_in_manifest"));
                continue;
            }
            if (source.RootAdmin && approvedAdministrators?.Contains(source.Uuid) != true)
            {
                issues.Add(new(index + 1, "administrator_requires_review"));
                continue;
            }
            if (source.RootAdmin && !source.UseTotp)
            {
                issues.Add(new(index + 1, "administrator_requires_totp"));
                continue;
            }
            if (!PasswordVerifier.IsSupportedHash(source.Password))
            {
                issues.Add(new(index + 1, "unsupported_password_hash"));
                continue;
            }
            var role = source.RootAdmin ? UserRole.Admin : UserRole.User;
            string? normalizedSecret = null;
            if (source.UseTotp)
            {
                var decoded = Totp.DecodeSecret(source.TotpSecret);
                if (decoded is null || totpProtector is null)
                {
                    if (decoded is not null) CryptographicOperations.ZeroMemory(decoded);
                    issues.Add(new(index + 1, "totp_requires_preservation"));
                    continue;
                }
                CryptographicOperations.ZeroMemory(decoded);
                normalizedSecret = source.TotpSecret!.TrimEnd('=').ToUpperInvariant();
            }
            else if (source.TotpSecret is not null)
            {
                issues.Add(new(index + 1, "unexpected_totp_secret"));
                continue;
            }

            var matches = await db.Users.AsNoTracking()
                .Where(u => u.Id == source.Uuid || u.Username == source.Username || u.Email == source.Email ||
                    u.Username == source.Email || u.Email == source.Username)
                .Take(2).ToListAsync(cancellationToken);
            if (matches.Count != 0)
            {
                if (matches is [var user] && user.Role == role && user.Id == source.Uuid &&
                    user.Username == source.Username && user.Email == source.Email && user.PasswordHash == source.Password &&
                    TotpMatches(user.ProtectedTotpSecret, normalizedSecret, totpProtector))
                    alreadyPresent++;
                else
                    issues.Add(new(index + 1, "existing_identity_conflict"));
                continue;
            }

            pending.Add(new User
            {
                Id = source.Uuid,
                Username = source.Username,
                Email = source.Email,
                FirstName = source.NameFirst ?? "",
                LastName = source.NameLast ?? "",
                PasswordHash = source.Password!,
                ProtectedTotpSecret = apply && normalizedSecret is not null ? totpProtector!.Protect(normalizedSecret) : null,
                Role = role
            });
        }

        if (apply && issues.Count == 0)
        {
            db.Users.AddRange(pending);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return new ImportReport(pending.Count, alreadyPresent, apply && issues.Count == 0 ? pending.Count : 0, issues);
    }

    private static bool TotpMatches(string? stored, string? source, IDataProtector? protector)
    {
        if (stored is null || source is null) return stored == source;
        try { return protector?.Unprotect(stored) == source; }
        catch (CryptographicException) { return false; }
    }

    private sealed record AccountManifest(int SchemaVersion, AccountEntry?[] Users);
    private sealed record AccountEntry(Guid Uuid, string? Username, string? Email, string? NameFirst, string? NameLast,
        string? Password, bool RootAdmin, bool UseTotp, string? TotpSecret = null);
    public sealed record AccountIssue(int Row, string Reason);
    public sealed record ImportReport(int Pending, int AlreadyPresent, int Imported, IReadOnlyList<AccountIssue> Issues);
}
