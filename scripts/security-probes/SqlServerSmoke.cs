using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ServerPanel.API.Data;
using ServerPanel.API.DTOs;
using ServerPanel.API.Security;
using ServerPanel.API.Services;

namespace SecurityRegression;

internal static class SqlServerSmoke
{
    public static async Task RunAsync()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("ARCADENODE_SQL_SMOKE_PORT"), out var port) || port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARCADENODE_SQL_SMOKE_PASSWORD")))
            throw new InvalidOperationException("Set the loopback SQL smoke port and synthetic password.");

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{port}",
            InitialCatalog = "ArcadeNodeSmoke_" + Guid.NewGuid().ToString("N"),
            UserID = "sa",
            Password = Environment.GetEnvironmentVariable("ARCADENODE_SQL_SMOKE_PASSWORD"),
            TrustServerCertificate = true,
            ConnectTimeout = 8,
            Pooling = false
        };
        var options = new DbContextOptionsBuilder<PanelDbContext>().UseSqlServer(connection.ConnectionString).Options;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await using var db = new PanelDbContext(options);
        var migrations = db.Database.GetMigrations().ToArray();
        Require(migrations.Length > 2, "The migration chain was not discovered.");
        await db.GetService<IMigrator>().MigrateAsync(migrations[1], token);

        var seedId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [Eggs] SET [Description] = {"Synthetic legacy save"} WHERE [Id] = {seedId}", token);
        await db.Database.MigrateAsync(token);
        Require((await db.Database.GetAppliedMigrationsAsync(token)).SequenceEqual(migrations), "Not all migrations were applied.");
        Require(await db.Eggs.AsNoTracking().AnyAsync(egg => egg.Id == seedId && egg.Description == "Synthetic legacy save", token),
            "The in-place SQL migration did not preserve the existing egg.");

        var login = "arcadenode_smoke_" + Guid.NewGuid().ToString("N");
        var runtimePassword = "Smoke9!" + RandomNumberGenerator.GetHexString(48);
        // Both values come from generators with fixed alphabets. CREATE LOGIN requires a password literal.
        var createLogin = $"CREATE LOGIN [{login}] WITH PASSWORD = '{runtimePassword}', CHECK_POLICY = OFF";
        await db.Database.ExecuteSqlRawAsync(createLogin, token);
        try
        {
            await ApplyRuntimeGrantsAsync(db, login, token);
            var runtimeConnection = new SqlConnectionStringBuilder(connection.ConnectionString) { UserID = login, Password = runtimePassword };
            var runtimeOptions = new DbContextOptionsBuilder<PanelDbContext>().UseSqlServer(runtimeConnection.ConnectionString).Options;
            await using var runtime = new PanelDbContext(runtimeOptions);
            await using var otherRuntime = new PanelDbContext(runtimeOptions);
            await VerifyRuntimeGrantsAsync(runtime, token);
            await VerifyImportedLoginAsync(db, runtime, token);

            var environment = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
                new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Testing" }).Environment;
            var first = new ServerOperationGate(runtime, environment, NullLogger<ServerOperationGate>.Instance);
            var second = new ServerOperationGate(otherRuntime, environment, NullLogger<ServerOperationGate>.Instance);
            var server = Guid.NewGuid();
            await VerifyExclusiveAsync(first.AcquireAsync, second.AcquireAsync, server, token);
            await VerifyExclusiveAsync(first.AcquireMemberManagementAsync, second.AcquireMemberManagementAsync, server, token);
            await using (var members = await first.AcquireMemberManagementAsync(server, token))
            await using (var power = await second.AcquireAsync(server, token)) { }
        }
        finally
        {
            var dropLogin = $"DROP LOGIN [{login}]";
            await db.Database.ExecuteSqlRawAsync(dropLogin, CancellationToken.None);
        }

        Console.WriteLine("SQL Server preserved legacy data, authenticated an imported Pterodactyl password and TOTP with runtime grants and serialized operations across runtime connections.");
    }

    private static async Task ApplyRuntimeGrantsAsync(PanelDbContext db, string login, CancellationToken token)
    {
        using var resource = typeof(SqlServerSmoke).Assembly.GetManifestResourceStream("ArcadeNode.RuntimeGrants.sql")
            ?? throw new InvalidOperationException("The runtime grants template was not embedded.");
        using var reader = new StreamReader(resource);
        // The smoke connection already selects its uniquely named disposable database.
        var template = (await reader.ReadToEndAsync(token)).Replace("USE [ArcadeNode];", "", StringComparison.Ordinal)
            .Replace("[arcadenode_runtime]", $"[{login}]", StringComparison.Ordinal)
            .Replace("N'arcadenode_runtime'", $"N'{login}'", StringComparison.Ordinal);
        foreach (var batch in Regex.Split(template, @"(?m)^GO[ \t]*\r?$"))
            if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch, token);
    }

    private static async Task VerifyRuntimeGrantsAsync(PanelDbContext runtime, CancellationToken token)
    {
        await runtime.Database.OpenConnectionAsync(token);
        try
        {
            await using var check = runtime.Database.GetDbConnection().CreateCommand();
            check.CommandText = """
                SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 0 AND IS_MEMBER('db_owner') = 0
                  AND IS_MEMBER('db_ddladmin') = 0 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL') = 0
                  AND HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'ALTER') = 0 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 0
                  THEN 1 ELSE 0 END
                """;
            Require(Convert.ToInt32(await check.ExecuteScalarAsync(token)) == 1, "The serving SQL identity has administrative privileges.");
        }
        finally { await runtime.Database.CloseConnectionAsync(); }
    }

    private static async Task VerifyImportedLoginAsync(PanelDbContext migration, PanelDbContext runtime, CancellationToken token)
    {
        const string phpHash = "$2y$10$RkI0LBo9/q7Pn1L1DE4Hv.EQQEDUPu29SjIYScVFBBXQzzr4gHdki";
        const string totpSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        var identity = Guid.NewGuid();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1,
            users = new[] { new { uuid = identity, username = "imported-sql-owner", email = "imported-sql-owner@example.test",
                name_first = "Imported", name_last = "Owner", password = phpHash, root_admin = false, use_totp = true, totp_secret = totpSecret } }
        });
        using var input = new MemoryStream(manifest);
        var keyDirectory = Directory.CreateTempSubdirectory("arcadenode-sql-keys-");
        try
        {
            var importerKeys = DataProtectionProvider.Create(keyDirectory, keys => keys.SetApplicationName("ArcadeNode"));
            var imported = await PterodactylAccountImport.RunAsync(migration, input, apply: true,
                importerKeys.CreateProtector(Totp.ProtectionPurpose), cancellationToken: token);
            Require(imported.Imported == 1 && imported.Issues.Count == 0, "SQL Server did not stage the Pterodactyl account.");

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = RandomNumberGenerator.GetHexString(64)
            }).Build();
            var servingKeys = DataProtectionProvider.Create(keyDirectory, keys => keys.SetApplicationName("ArcadeNode"));
            var auth = new AuthService(runtime, configuration, NullLogger<AuthService>.Instance, servingKeys);
            Require(await auth.LoginAsync(new LoginRequest("imported-sql-owner", "Old7!")) is null,
                "An imported TOTP account could sign in with its password alone.");
            var authenticated = await auth.LoginAsync(new LoginRequest("imported-sql-owner", "Old7!", CurrentTotpCode()));
            Require(authenticated?.User.Id == identity, "The runtime SQL identity could not authenticate the imported password and TOTP.");
            Require(await runtime.AuthSessions.AsNoTracking().AnyAsync(session => session.UserId == identity, token),
                "The imported user did not establish a persisted SQL Server session.");
        }
        finally { keyDirectory.Delete(recursive: true); }
    }

    private static string CurrentTotpCode()
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData("12345678901234567890"u8, counter);
        var number = (BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(hash[^1] & 15)) & 0x7fff_ffff) % 1_000_000;
        return number.ToString("D6", CultureInfo.InvariantCulture);
    }

    private static async Task VerifyExclusiveAsync(Func<Guid, CancellationToken, Task<IAsyncDisposable>> acquireFirst,
        Func<Guid, CancellationToken, Task<IAsyncDisposable>> acquireSecond, Guid server, CancellationToken token)
    {
        var lease = await acquireFirst(server, token);
        var waiting = acquireSecond(server, token);
        try
        {
            var completed = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(300), token));
            if (completed == waiting)
            {
                await using var unexpected = await waiting;
                throw new InvalidOperationException("Two SQL connections acquired the same exclusive server lock.");
            }
        }
        finally
        {
            await lease.DisposeAsync();
        }
        await using var granted = await waiting;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
