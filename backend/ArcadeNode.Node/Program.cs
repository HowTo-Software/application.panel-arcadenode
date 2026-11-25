using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using ArcadeNode.Backups;
using ArcadeNode.Transfers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace ArcadeNode.Node;

internal static class NodeProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("ArcadeNode file service requires Linux.");
        var config = builder.Configuration.GetSection("Files");
        var certificatePath = config["CertificatePath"] ?? throw new InvalidOperationException("Configure the node certificate.");
        var keyPath = config["KeyPath"] ?? throw new InvalidOperationException("Configure the node certificate key.");
        using var serverCertificate = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        var panelPins = (config.GetSection("PanelCertificateSha256").Get<string[]>() ?? [])
            .Select(Convert.FromHexString).ToArray();
        if (panelPins.Length == 0 || panelPins.Any(pin => pin.Length != 32))
            throw new InvalidOperationException("Configure at least one SHA-256 panel certificate fingerprint.");

        bool IsApproved(X509Certificate2? certificate)
        {
            if (certificate is null) return false;
            var now = DateTime.UtcNow;
            if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() < now)
                return false;
            var fingerprint = certificate.GetCertHash(HashAlgorithmName.SHA256);
            return panelPins.Any(pin => CryptographicOperations.FixedTimeEquals(pin, fingerprint));
        }

        var volumePaths = new Dictionary<Guid, string>();
        foreach (var item in config.GetSection("Volumes").GetChildren())
        {
            if (!Guid.TryParseExact(item.Key, "D", out var id) || item.Value is not { Length: > 0 } path ||
                !volumePaths.TryAdd(id, path))
                throw new InvalidOperationException("Each volume configuration requires a unique server UUID and directory.");
        }
        using var volumes = new NodeVolumes(volumePaths, config["ManagedVolumesDirectory"]);
        string? StorageDirectory(string key)
        {
            if (config[key] is not { Length: > 0 } configured) return null;
            if (!Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException("Configure absolute backup and restore directories.");
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            if (volumes.Overlaps(fullPath))
                throw new InvalidOperationException("Backup and restore storage must be separate from game volumes.");
            return fullPath;
        }
        var backupPath = StorageDirectory("BackupDirectory");
        var restorePath = StorageDirectory("RestoreDirectory");
        if (restorePath is not null)
        {
            if (backupPath is null || IsWithin(restorePath, backupPath) || IsWithin(backupPath, restorePath))
                throw new InvalidOperationException("Restore staging requires separate storage from the backup directory.");
            using var restoreDirectory = new LinuxGameFiles(restorePath);
        }
        var backups = backupPath is null ? null : new NodeBackupStore(backupPath, restorePath,
            config.GetValue("BackupMaxDataGiB", GameBackupLimits.DefaultCreationGiB));
        using var backupSlot = new SemaphoreSlim(1, 1);
        using var uploadSlots = new SemaphoreSlim(2, 2);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 65536;
            kestrel.Limits.MaxConcurrentConnections = 32;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.ListenAnyIP(config.GetValue("Port", 8443), listener => listener.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => IsApproved(certificate);
            }));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("downloads", _ => new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 }));
        });

        const int maxFileBytes = 8 * 1024 * 1024;
        var uploadsEnabled = config.GetValue("AllowUploads", false);
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/v1/servers/{id:guid}/backups/{backupId:guid}", (Guid id, Guid backupId, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (backups is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            using var lease = volumes.Open(id);
            if (backupId == Guid.Empty || lease is null) return Results.NotFound();

            try { return Results.File(backups.OpenArchive(id, backupId), "application/gzip"); }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Results.NotFound(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapDelete("/v1/servers/{id:guid}/backups/{backupId:guid}", (Guid id, Guid backupId, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (backups is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            using var lease = volumes.Open(id);
            if (backupId == Guid.Empty || lease is null) return Results.NotFound();
            try
            {
                backups.DeleteArchive(id, backupId, context.RequestAborted);
                return Results.NoContent();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
        });
        async Task<IResult> CreateBackup(Guid id, Guid backupId, HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (backups is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            using var lease = volumes.Open(id);
            if (backupId == Guid.Empty || lease is null) return Results.NotFound();
            if (!await backupSlot.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(GameBackupLimits.CreationTimeout);
            try
            {
                if (context.Request.ContentLength > BackupIgnoreRules.MaximumBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                var exclusions = await BackupIgnoreRules.ReadAsync(context.Request.Body, timeout.Token);
                if (exclusions is not null && !string.Equals(context.Request.ContentType?.Split(';', 2)[0].Trim(), "text/plain", StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
                return Results.Json(await backups.CreateAsync(id, backupId, lease.Volume, exclusions, timeout.Token));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or
                System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
            finally { backupSlot.Release(); }
        }
        app.MapPost("/v1/servers/{id:guid}/backups/{backupId:guid}", CreateBackup);
        app.MapPost("/v1/servers/{id:guid}/backups/{backupId:guid}/with-exclusions", CreateBackup);
        app.MapPost("/v1/servers/{id:guid}/backups/{backupId:guid}/restore", async (Guid id, Guid backupId, RestoreInput input, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (backups is not { CanRestore: true }) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            using var lease = volumes.Open(id);
            if (backupId == Guid.Empty || lease is null) return Results.NotFound();
            if (input.Sha256 is not { Length: 64 } sha256 || !sha256.All(char.IsAsciiHexDigit)) return Results.BadRequest();
            if (!await backupSlot.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(GameBackupLimits.RestoreTimeout);
            try { return Results.Json(await backups.StageRestoreAsync(id, backupId, sha256, timeout.Token)); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Results.NotFound(); }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or
                System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
            finally { backupSlot.Release(); }
        });
        app.MapGet("/v1/servers/{id:guid}/storage", async (Guid id, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var lease = volumes.Open(id);
            if (lease is null) return Results.NotFound();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { return Results.Json(await lease.Volume.MeasureAsync(timeout.Token)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
        });
        async Task<IResult> ChangeEntryAsync(Guid id, HttpContext context, int successStatus,
            Func<LinuxGameFiles, CancellationToken, Task<bool>> operation, int timeoutSeconds = 10)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!uploadsEnabled) return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            using var lease = volumes.Open(id);
            if (lease is null) return Results.NotFound();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                return await operation(lease.Volume, timeout.Token) ? Results.StatusCode(successStatus) : Results.Conflict();
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (LinuxGameFiles.FileLimitExceededException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (Exception error) when (error is InvalidDataException or FormatException or OverflowException)
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
            // ENOENT, EXDEV, ENOTDIR and ELOOP indicate a path unavailable inside the volume.
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException ||
                error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 18 or 20 or 40 })
            {
                return Results.NotFound();
            }
            catch (IOException) { return Results.StatusCode(StatusCodes.Status422UnprocessableEntity); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
        }

        app.MapPost("/v1/servers/{id:guid}/files/directories", (Guid id, DirectoryInput input, HttpContext context) =>
            ChangeEntryAsync(id, context, StatusCodes.Status201Created, (volume, token) =>
                volume.CreateDirectoryAsync(input.Path ?? "", token)));
        app.MapPost("/v1/servers/{id:guid}/files/move", (Guid id, FileTransferInput input, HttpContext context) =>
            ChangeEntryAsync(id, context, StatusCodes.Status204NoContent, (volume, token) =>
                volume.MoveAsync(input.Source ?? "", input.Destination ?? "", token)));
        app.MapPost("/v1/servers/{id:guid}/files/chmod", (Guid id, FileModeInput input, HttpContext context) =>
            ChangeEntryAsync(id, context, StatusCodes.Status204NoContent, (volume, token) =>
                volume.ChangePermissionsAsync(input.Directory ?? "", input.Entries ?? [], input.Mode ?? "", token)));
        app.MapPost("/v1/servers/{id:guid}/files/copy", (Guid id, FileTransferInput input, HttpContext context) =>
            ChangeEntryAsync(id, context, StatusCodes.Status201Created, (volume, token) =>
                volume.CopyFileAsync(input.Source ?? "", input.Destination ?? "", token), timeoutSeconds: 25));
        app.MapPost("/v1/servers/{id:guid}/files/compress", (Guid id, FileArchiveInput input, HttpContext context) =>
            ChangeEntryAsync(id, context, StatusCodes.Status201Created, (volume, token) =>
                volume.CompressAsync(input.Directory ?? "", input.Entries ?? [], input.Destination ?? "", token), timeoutSeconds: 25));
        app.MapPost("/v1/servers/{id:guid}/files/extract", (Guid id, FileTransferInput input, HttpContext context) =>
            ChangeEntryAsync(id, context, StatusCodes.Status201Created, (volume, token) =>
                volume.ExtractAsync(input.Source ?? "", input.Destination ?? "", token), timeoutSeconds: 25));
        app.MapPost("/v1/servers/{id:guid}/files/delete", (Guid id, FileDeleteInput input, HttpContext context) =>
            ChangeEntryAsync(id, context, StatusCodes.Status204NoContent, (volume, token) =>
                volume.DeleteEntriesAsync(input.Directory ?? "", input.Entries ?? [], token), timeoutSeconds: 25));

        app.MapPut("/v1/servers/{id:guid}/files/text", async (Guid id, string path, TextEdit edit, HttpContext context) =>
        {
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!uploadsEnabled) return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            using var lease = volumes.Open(id);
            if (lease is null) return Results.NotFound();
            if (edit.Content is null || edit.Sha256 is null) return Results.BadRequest();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                return await lease.Volume.EditTextAsync(path, edit.Content, edit.Sha256, timeout.Token)
                    ? Results.NoContent() : Results.StatusCode(StatusCodes.Status412PreconditionFailed);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (IOException) { return Results.StatusCode(StatusCodes.Status422UnprocessableEntity); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
        });
        app.MapPost("/v1/servers/{id:guid}/files/upload", async (Guid id, string path, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!uploadsEnabled) return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            using var lease = volumes.Open(id);
            if (lease is null) return Results.NotFound();
            if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (context.Request.ContentLength is not { } length) return Results.StatusCode(StatusCodes.Status411LengthRequired);
            if (length > GameFileTransfer.MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = GameFileTransfer.MaximumBytes;
            if (!uploadSlots.Wait(0)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(GameFileTransfer.NodeUploadTimeoutSeconds));
            try
            {
                return await lease.Volume.UploadNewAsync(path, context.Request.Body, length, timeout.Token)
                    ? Results.StatusCode(StatusCodes.Status201Created) : Results.Conflict();
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (IOException error) when (error is DirectoryNotFoundException or FileNotFoundException ||
                error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 18 or 20 or 40 })
            {
                return Results.NotFound();
            }
            catch (IOException) { return Results.StatusCode(StatusCodes.Status422UnprocessableEntity); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
            finally { uploadSlots.Release(); }
        });
        IResult ListDirectory(Guid id, string? path, string? cursor, string? filter, HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!GameDirectoryQuery.ValidCursor(cursor) || !GameDirectoryQuery.ValidFilter(filter)) return Results.BadRequest();
            using var lease = volumes.Open(id);
            if (lease is null) return Results.NotFound();
            try
            {
                return Results.Json(lease.Volume.List(path ?? "", context.RequestAborted, cursor, filter));
            }
            catch (LinuxGameFiles.DirectoryChangedException) { return Results.Conflict(); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
        }
        app.MapGet("/v1/servers/{id:guid}/files", ListDirectory);
        app.MapGet("/v1/servers/{id:guid}/files/page", ListDirectory);
        app.MapGet("/v1/servers/{id:guid}/files/download", async (Guid id, string path, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var lease = volumes.Open(id);
            if (lease is null) return Results.NotFound();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await using var file = lease.Volume.OpenRead(path);
                var length = file.Length;
                if (length > maxFileBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                var bytes = new byte[(int)length];
                await file.ReadExactlyAsync(bytes, timeout.Token);
                return Results.File(bytes, "application/octet-stream", Path.GetFileName(path));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
        });
        app.MapMethods("/v1/servers/{id:guid}/files/stream", [HttpMethods.Head, HttpMethods.Get],
            async Task<IResult> (Guid id, string path, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (!context.Request.IsHttps || !IsApproved(context.Connection.ClientCertificate))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            using var lease = volumes.Open(id);
            if (lease is null) return Results.NotFound();

            FileStream file;
            try { file = lease.Volume.OpenRead(path); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.NotFound();
            }

            await using (file)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(GameFileTransfer.DownloadTimeoutSeconds));
                try
                {
                    var length = file.Length;
                    if (length > GameFileTransfer.MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    context.Response.ContentLength = length;
                    context.Response.ContentType = "application/octet-stream";
                    if (HttpMethods.IsGet(context.Request.Method))
                        await GameFileTransfer.CopyExactlyAsync(file, context.Response.Body, length, deadline.Token);
                    return Results.Empty;
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                {
                    if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
                    {
                        context.Abort();
                        return Results.Empty;
                    }
                    context.Response.ContentLength = null;
                    context.Response.ContentType = null;
                    return Results.StatusCode(error is OperationCanceledException
                        ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status422UnprocessableEntity);
                }
            }
        });
        await app.RunAsync();
    }

    private sealed record TextEdit(string? Content, string? Sha256);
    private sealed record DirectoryInput(string? Path);
    private sealed record FileTransferInput(string? Source, string? Destination);
    private sealed record FileArchiveInput(string? Directory, string[]? Entries, string? Destination);
    private sealed record FileDeleteInput(string? Directory, string[]? Entries);
    private sealed record FileModeInput(string? Directory, string[]? Entries, string? Mode);
    private sealed record RestoreInput(string? Sha256);

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
