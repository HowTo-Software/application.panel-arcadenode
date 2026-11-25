using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ServerPanel.API.Data;
using ServerPanel.API.Middleware;
using ServerPanel.API.Models;
using ServerPanel.API.Security;
using ServerPanel.API.Services;
using ServerPanel.API.Diagnostics;

using var bootstrapLogging = LoggerFactory.Create(logging => logging.AddSimpleConsole(options =>
{
    options.SingleLine = true; options.IncludeScopes = true; options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
}));
var startupPoint = DiagnosticPoints.Configuration;
try
{
    var importCommand = args.Where(arg => arg is "--plan-pterodactyl-accounts" or "--import-pterodactyl-accounts"
        or "--plan-pterodactyl-schedules" or "--import-pterodactyl-schedules"
        or "--plan-pterodactyl-subusers" or "--import-pterodactyl-subusers"
        or "--plan-pterodactyl-servers" or "--import-pterodactyl-servers").ToArray();
    if (importCommand.Length > 1 || (importCommand.Length == 1 && args.Contains("--migrate")))
        throw new InvalidOperationException("Choose one maintenance command per invocation.");
    var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--migrate" && !importCommand.Contains(arg)).ToArray());
    builder.Logging.AddSimpleConsole(options => { options.SingleLine = true; options.IncludeScopes = true; options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz "; });
    var nodeConfig = builder.Configuration["NodeConfigurationPath"];
    if (!string.IsNullOrWhiteSpace(nodeConfig)) builder.Configuration.AddJsonFile(nodeConfig, optional: false, reloadOnChange: false);
    var connection = builder.Configuration.GetConnectionString("DefaultConnection");
    if (builder.Configuration["Database:ConnectionStringFile"] is { Length: > 0 } connectionFile)
        connection = File.ReadAllText(connectionFile).Trim();
    if (builder.Configuration["Jwt:KeyFile"] is { Length: > 0 } jwtFile)
        builder.Configuration["Jwt:Key"] = File.ReadAllText(jwtFile).Trim();

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.AddServerHeader = false;
        options.Limits.MaxRequestBodySize = 65536;
        options.Limits.MaxConcurrentConnections = 256;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddControllers().AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.AddExceptionHandler<ApiExceptionHandler>();
    builder.Services.AddProblemDetails();
    builder.Services.AddDbContext<PanelDbContext>(options => options.UseSqlServer(connection,
        sql => sql.CommandTimeout(30)));
    builder.Services.AddSingleton<ICartridgeService, CartridgeService>();
    var dataProtection = builder.Services.AddDataProtection().SetApplicationName("ArcadeNode");
    if (builder.Configuration["Security:DataProtectionKeyDirectory"] is { Length: > 0 } keyDirectory)
        dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    {
        var sql = new SqlConnectionStringBuilder(connection);
        if (string.Equals(sql.UserID, "sa", StringComparison.OrdinalIgnoreCase) || sql.TrustServerCertificate ||
            sql.Encrypt == SqlConnectionEncryptOption.Optional)
            throw new InvalidOperationException("Production requires a dedicated SQL identity and verified encrypted database connections.");
    }

    if (importCommand.Length == 1)
    {
        await using var maintenance = builder.Build();
        await using var scope = maintenance.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        var isScheduleImport = importCommand[0].EndsWith("-schedules", StringComparison.Ordinal);
        var isServerImport = importCommand[0].EndsWith("-servers", StringComparison.Ordinal);
        var isSubuserImport = importCommand[0].EndsWith("-subusers", StringComparison.Ordinal);
        var path = builder.Configuration[isServerImport ? "Migration:PterodactylServersFile"
            : isScheduleImport ? "Migration:PterodactylSchedulesFile" : isSubuserImport ? "Migration:PterodactylSubusersFile"
            : "Migration:PterodactylAccountsFile"];
        if (string.IsNullOrWhiteSpace(path) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidOperationException("Set the path to a migration manifest of at most 16 MiB.");
        await using var input = File.OpenRead(path);
        if (isServerImport)
        {
            var enrollment = new DockerEnrollment(builder.Configuration, builder.Environment);
            var cartridges = scope.ServiceProvider.GetRequiredService<ICartridgeService>();
            var mappingsPath = builder.Configuration["Migration:PterodactylMappingsFile"];
            await using var mappings = string.IsNullOrWhiteSpace(mappingsPath) ? null : File.OpenRead(mappingsPath);
            if (mappings?.Length > PterodactylServerMappings.MaximumBytes)
                throw new InvalidOperationException("Set a migration mapping file of at most 256 KiB.");
            var serverReport = await PterodactylServerImport.RunAsync(db, enrollment, cartridges, input,
                importCommand[0] == "--import-pterodactyl-servers", mappingInput: mappings,
                eggRuntimes: new EggRuntimeProfiles(builder.Configuration, cartridges));
            Console.WriteLine(JsonSerializer.Serialize(serverReport));
            Environment.ExitCode = serverReport.Issues.Count == 0 ? 0 : 2;
            return;
        }
        if (isScheduleImport)
        {
            var scheduleReport = await PterodactylScheduleImport.RunAsync(db, input, importCommand[0] == "--import-pterodactyl-schedules");
            Console.WriteLine(JsonSerializer.Serialize(scheduleReport));
            Environment.ExitCode = scheduleReport.Issues.Count == 0 ? 0 : 2;
            return;
        }
        if (isSubuserImport)
        {
            var memberReport = await PterodactylMemberImport.RunAsync(db, input, importCommand[0] == "--import-pterodactyl-subusers");
            Console.WriteLine(JsonSerializer.Serialize(memberReport));
            Environment.ExitCode = memberReport.Issues.Count == 0 ? 0 : 2;
            return;
        }
        var totpProtector = string.IsNullOrWhiteSpace(builder.Configuration["Security:DataProtectionKeyDirectory"]) ? null :
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Totp.ProtectionPurpose);
        var administratorIds = new HashSet<Guid>();
        foreach (var value in (builder.Configuration["Migration:PterodactylAdministratorIds"] ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || !administratorIds.Add(id))
                throw new InvalidOperationException("Administrator approvals must be unique, nonempty UUIDs.");
        }
        var report = await PterodactylAccountImport.RunAsync(db, input, importCommand[0] == "--import-pterodactyl-accounts", totpProtector, administratorIds);
        Console.WriteLine(JsonSerializer.Serialize(report));
        Environment.ExitCode = report.Issues.Count == 0 ? 0 : 2;
        return;
    }

    // Explicit maintenance command. The serving identity never applies migrations.
    if (args.Contains("--migrate"))
    {
        await using var maintenance = builder.Build();
        using var scope = maintenance.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        startupPoint = DiagnosticPoints.Migration;
        await db.Database.MigrateAsync();
        if (!await db.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin))
        {
            var password = builder.Configuration["Seed:AdminPasswordFile"] is { Length: > 0 } seedFile
                ? File.ReadAllText(seedFile).Trim() : builder.Configuration["Seed:AdminPassword"];
            if (!new ServerPanel.API.Validation.PasswordComplexityAttribute().IsValid(password))
                throw new InvalidOperationException("Set a strong seed password in a secret file before initializing an administrator.");
            var username = builder.Configuration["Seed:AdminUsername"] ?? "admin";
            var email = builder.Configuration["Seed:AdminEmail"] ?? "admin@panel.local";
            if (username.Length is < 1 or > 100 || username.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')) ||
                email.Length is < 1 or > 255 || email != email.Trim() || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
                throw new InvalidOperationException("Set a valid username and email for the initial administrator.");
            if (await db.Users.AnyAsync(user => user.Username == username || user.Email == email ||
                user.Username == email || user.Email == username))
                throw new InvalidOperationException("The initial administrator must have a username and email distinct from existing accounts.");
            db.Users.Add(new User { Username = username, Email = email, Role = UserRole.SuperAdmin,
                FirstName = "Admin", PasswordHash = BCrypt.Net.BCrypt.HashPassword(password!) });
            await db.SaveChangesAsync();
        }
        return;
    }

    var jwtKey = builder.Configuration["Jwt:Key"];
    if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 48 || jwtKey.StartsWith("change-", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Set a random JWT key of at least 48 bytes using a secret file.");
    builder.Services.AddScoped<SessionValidator>();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "ArcadeNode",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "ArcadeNodeUsers",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30), ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            NameClaimType = ClaimTypes.Name, RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var validator = context.HttpContext.RequestServices.GetRequiredService<SessionValidator>();
                if (context.Principal is null || !await validator.IsValidAsync(context.Principal, context.HttpContext.RequestAborted))
                {
                    context.HttpContext.Items[DiagnosticLog.ContextKey] = DiagnosticPoints.Session.Code;
                    context.Fail("Session invalid or revoked.");
                }
            }
        };
    });
    builder.Services.AddAuthorization();

    static string ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetConcurrencyLimiter("application", _ =>
                new ConcurrencyLimiterOptions { PermitLimit = 64, QueueLimit = 0 })),
            PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })),
            PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetConcurrencyLimiter(ClientIp(context), _ =>
                new ConcurrencyLimiterOptions { PermitLimit = 10, QueueLimit = 0 })));
        options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        options.AddPolicy("sensitive", context => RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        options.AddPolicy("backup-grant", context => RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        options.AddConcurrencyLimiter("backup-transfer", settings => { settings.PermitLimit = 8; settings.QueueLimit = 0; });
        options.AddPolicy("file-grant", context => RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        options.AddConcurrencyLimiter("file-transfer", settings => { settings.PermitLimit = 8; settings.QueueLimit = 0; });
        options.AddPolicy("assistant", context => RateLimitPartition.GetFixedWindowLimiter(ClientIp(context), _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });

    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<IServerService, ServerService>();
    builder.Services.AddScoped<INodeService, NodeService>();
    builder.Services.AddSingleton<DockerEnrollment>();
    builder.Services.AddSingleton<IDockerClientFactory, DockerClientFactory>();
    builder.Services.AddSingleton<NodeFileClient>();
    if (builder.Configuration.GetValue("Backups:Enabled", !builder.Environment.IsEnvironment("Testing")))
        builder.Services.AddHostedService<BackupWorker>();
    builder.Services.AddSingleton<EggRuntimeProfiles>();
    builder.Services.AddSingleton<LocalGameAssistant>();
    builder.Services.AddHttpClient("LocalAssistant").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(4)
    });
    if (builder.Configuration.GetValue("Schedules:Enabled", !builder.Environment.IsEnvironment("Testing")))
        builder.Services.AddHostedService<ScheduleWorker>();
    if (builder.Configuration.GetValue("RuntimeMonitoring:Enabled", !builder.Environment.IsEnvironment("Testing")))
        builder.Services.AddHostedService<ServerRuntimeMonitor>();
    builder.Services.AddScoped<IDockerService, DockerService>();
    builder.Services.AddScoped<ServerOperationGate>();
    builder.Services.AddScoped<ServerAccess>();
    builder.Services.AddSingleton<IGameTemplateService, GameTemplateService>();

    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? (builder.Environment.IsDevelopment() ? new[] { "http://localhost:3000" } : Array.Empty<string>());
    builder.Services.AddCors(options => options.AddPolicy("Frontend", policy =>
    {
        if (origins.Length > 0) policy.WithOrigins(origins).WithMethods("GET", "POST", "PUT", "DELETE")
            .WithHeaders("Content-Type", "Authorization", "If-Match").WithExposedHeaders("X-Total-Count", "X-Page", "X-Page-Size", "X-Trace-Id", "X-Diagnostic-Code");
    }));
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear(); options.KnownIPNetworks.Clear();
        foreach (var address in builder.Configuration.GetSection("Security:TrustedProxies").Get<string[]>() ?? Array.Empty<string>())
            options.KnownProxies.Add(IPAddress.Parse(address));
        // A nonempty list ensures unknown peers cannot supply trusted forwarded headers.
        if (options.KnownProxies.Count == 0) options.KnownProxies.Add(IPAddress.IPv6Loopback);
    });

    var app = builder.Build();
    if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    {
        // Validate the effective SQL grants, not merely the login's name.
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        startupPoint = DiagnosticPoints.DatabaseConnection;
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = """
            SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1 OR IS_MEMBER('db_owner') = 1
              OR IS_MEMBER('db_ddladmin') = 1 OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL') = 1
              OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') = 1 THEN 1 ELSE 0 END
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException("The serving SQL identity must not hold database administration permissions.");
    }
    startupPoint = DiagnosticPoints.Startup;
    app.UseMiddleware<RequestDiagnosticMiddleware>();
    app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
    app.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await next(context);
    });
    app.UseForwardedHeaders();
    if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    {
        app.UseHsts();
        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps) { context.Response.StatusCode = 400; return; }
            await next(context);
        });
    }
    app.UseCors("Frontend");
    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuditLogging();
    app.UseAuthorization();
    // Panel sessions use explicit bearer credentials. Backup downloads redeem a single-use form grant.
    app.MapControllers();
    app.Lifetime.ApplicationStarted.Register(() => DiagnosticLog.Write(app.Logger, DiagnosticPoints.Ready));
    await app.RunAsync();

}
catch (HostAbortedException) { throw; }
catch (Exception exception)
{
    DiagnosticLog.Write(bootstrapLogging.CreateLogger("ArcadeNode.Startup"), DiagnosticLog.Find(exception) ?? startupPoint, exception);
    Environment.ExitCode = 1;
}

public partial class Program;
