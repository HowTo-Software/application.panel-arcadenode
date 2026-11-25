using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ArcadeNode.Backups;
using ArcadeNode.Transfers;
using Microsoft.AspNetCore.WebUtilities;

namespace ServerPanel.API.Services;

public sealed class NodeFileClient(IConfiguration configuration) : IDisposable
{
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private readonly ConcurrentDictionary<Guid, Lazy<Connection>> _connections = new();

    public bool IsConfigured(Guid nodeId) => configuration.GetSection($"FileNodes:{nodeId:D}").Exists();
    public bool IsRestoreConfigured(Guid nodeId) =>
        bool.TryParse(configuration[$"FileNodes:{nodeId:D}:RestoreEnabled"], out var enabled) && enabled;

    public async Task<(HttpStatusCode Status, BackupReceipt? Receipt)> CreateBackupAsync(Guid nodeId, Guid serverId, Guid backupId,
        string? exclusions, CancellationToken token)
    {
        var connection = GetConnection(nodeId);
        if (connection is null) return (HttpStatusCode.ServiceUnavailable, null);
        // An older node must reject the route instead of creating an unfiltered archive.
        var target = new Uri(connection.Origin, $"/v1/servers/{serverId:D}/backups/{backupId:D}/with-exclusions");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(GameBackupLimits.CreationTimeout + GameBackupLimits.ResponseGrace);
        using var request = new HttpRequestMessage(HttpMethod.Post, target);
        if (!string.IsNullOrWhiteSpace(exclusions)) request.Content = new StringContent(exclusions, Encoding.UTF8, "text/plain");
        using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        if (response.Content.Headers.ContentLength > 4096) return (HttpStatusCode.BadGateway, null);
        await response.Content.LoadIntoBufferAsync(4096, timeout.Token);
        var receipt = await response.Content.ReadFromJsonAsync<BackupReceipt>(cancellationToken: timeout.Token);
        return receipt is { Size: > 0 and <= GameBackupLimits.MaximumArchiveBytes, Sha256.Length: 64 } &&
            receipt.Sha256.All(char.IsAsciiHexDigit)
            ? (HttpStatusCode.OK, receipt) : (HttpStatusCode.BadGateway, null);
    }

    public sealed record BackupReceipt(long Size, string Sha256);

    public async Task<bool> DeleteBackupAsync(Guid nodeId, Guid serverId, Guid backupId, CancellationToken token)
    {
        var connection = GetConnection(nodeId);
        if (connection is null) return false;
        var target = new Uri(connection.Origin, $"/v1/servers/{serverId:D}/backups/{backupId:D}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Delete, target);
        using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        return response.StatusCode == HttpStatusCode.NoContent;
    }

    public async Task<(HttpStatusCode Status, RestoreReceipt? Receipt)> RestoreBackupAsync(Guid nodeId, Guid serverId,
        Guid backupId, string sha256, CancellationToken token)
    {
        if (!IsRestoreConfigured(nodeId) || GetConnection(nodeId) is not { } connection)
            return (HttpStatusCode.ServiceUnavailable, null);
        var target = new Uri(connection.Origin, $"/v1/servers/{serverId:D}/backups/{backupId:D}/restore");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(GameBackupLimits.RestoreTimeout + GameBackupLimits.ResponseGrace);
        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { sha256 }), Encoding.UTF8, "application/json")
        };
        using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        if (response.Content.Headers.ContentLength > 4096) return (HttpStatusCode.BadGateway, null);
        await response.Content.LoadIntoBufferAsync(4096, timeout.Token);
        var receipt = await response.Content.ReadFromJsonAsync<RestoreReceipt>(cancellationToken: timeout.Token);
        return receipt is { Files: >= 0, Directories: >= 0, Links: >= 0, Bytes: >= 0 and <= GameBackupLimits.MaximumDataBytes } &&
            (long)receipt.Files + receipt.Directories + receipt.Links <= GameBackupLimits.MaximumEntries &&
            sha256.Equals(receipt.ArchiveSha256, StringComparison.OrdinalIgnoreCase)
            ? (HttpStatusCode.OK, receipt) : (HttpStatusCode.BadGateway, null);
    }

    public sealed record RestoreReceipt(int Files, int Directories, long Bytes, string ArchiveSha256, int Links = 0);

    public async Task<HttpResponseMessage?> OpenBackupAsync(Guid nodeId, Guid serverId, Guid backupId, CancellationToken token)
    {
        var connection = GetConnection(nodeId);
        if (connection is null) return null;
        var target = new Uri(connection.Origin, $"/v1/servers/{serverId:D}/backups/{backupId:D}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await connection.Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    public Task<(HttpStatusCode Status, byte[]? Bytes)> DownloadAsync(Guid nodeId, Guid serverId, string path, CancellationToken cancellationToken) =>
        ReadAsync(nodeId, $"/v1/servers/{serverId:D}/files/download", path, "application/octet-stream", MaxFileBytes, cancellationToken);

    public async Task<HttpResponseMessage?> OpenFileAsync(Guid nodeId, Guid serverId, string path, HttpMethod method,
        CancellationToken token)
    {
        var connection = GetConnection(nodeId);
        if (connection is null) return null;
        var target = new Uri(connection.Origin, QueryHelpers.AddQueryString($"/v1/servers/{serverId:D}/files/stream", "path", path));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(method, target);
        return await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    public Task<(HttpStatusCode Status, byte[]? Bytes)> ListAsync(Guid nodeId, Guid serverId, string path, string? cursor, string? filter,
        CancellationToken cancellationToken)
    {
        var route = $"/v1/servers/{serverId:D}/files";
        if (cursor is not null || filter is not null)
            route = QueryHelpers.AddQueryString(route + "/page", new Dictionary<string, string?> { ["cursor"] = cursor, ["filter"] = filter });
        return ReadAsync(nodeId, route, path, "application/json", 512 * 1024, cancellationToken);
    }

    public Task<(HttpStatusCode Status, byte[]? Bytes)> StorageAsync(Guid nodeId, Guid serverId, CancellationToken cancellationToken) =>
        ReadAsync(nodeId, $"/v1/servers/{serverId:D}/storage", null, "application/json", 4096, cancellationToken);

    public Task<HttpStatusCode> CreateDirectoryAsync(Guid nodeId, Guid serverId, string path, CancellationToken token) =>
        ChangeEntryAsync(nodeId, $"/v1/servers/{serverId:D}/files/directories", new { path }, token);

    public Task<HttpStatusCode> MoveAsync(Guid nodeId, Guid serverId, string source, string destination, CancellationToken token) =>
        ChangeEntryAsync(nodeId, $"/v1/servers/{serverId:D}/files/move", new { source, destination }, token);

    public Task<HttpStatusCode> ChangePermissionsAsync(Guid nodeId, Guid serverId, string directory, string[] entries, string mode,
        CancellationToken token) => ChangeEntryAsync(nodeId, $"/v1/servers/{serverId:D}/files/chmod", new { directory, entries, mode }, token);

    public Task<HttpStatusCode> CopyAsync(Guid nodeId, Guid serverId, string source, string destination, CancellationToken token) =>
        ChangeEntryAsync(nodeId, $"/v1/servers/{serverId:D}/files/copy", new { source, destination }, token, timeoutSeconds: 30);

    public Task<HttpStatusCode> CompressAsync(Guid nodeId, Guid serverId, string directory, string[] entries, string destination,
        CancellationToken token) => ChangeEntryAsync(nodeId, $"/v1/servers/{serverId:D}/files/compress",
        new { directory, entries, destination }, token, timeoutSeconds: 30);

    public Task<HttpStatusCode> ExtractAsync(Guid nodeId, Guid serverId, string source, string destination, CancellationToken token) =>
        ChangeEntryAsync(nodeId, $"/v1/servers/{serverId:D}/files/extract", new { source, destination }, token, timeoutSeconds: 30);

    public Task<HttpStatusCode> DeleteEntriesAsync(Guid nodeId, Guid serverId, string directory, string[] entries, CancellationToken token) =>
        ChangeEntryAsync(nodeId, $"/v1/servers/{serverId:D}/files/delete", new { directory, entries }, token, timeoutSeconds: 30);

    private async Task<HttpStatusCode> ChangeEntryAsync(Guid nodeId, string route, object input, CancellationToken token, int timeoutSeconds = 15)
    {
        var connection = GetConnection(nodeId);
        if (connection is null) return HttpStatusCode.ServiceUnavailable;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(connection.Origin, route))
        {
            Content = new StringContent(JsonSerializer.Serialize(input), Encoding.UTF8, "application/json")
        };
        using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        return response.StatusCode switch
        {
            HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.Conflict or HttpStatusCode.MethodNotAllowed or
                HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or
                HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestEntityTooLarge => response.StatusCode,
            _ => HttpStatusCode.BadGateway
        };
    }

    public async Task<HttpStatusCode> UploadAsync(Guid nodeId, Guid serverId, string path, Stream source, long length,
        CancellationToken cancellationToken)
    {
        var connection = GetConnection(nodeId);
        if (connection is null) return HttpStatusCode.ServiceUnavailable;
        var target = new Uri(connection.Origin, QueryHelpers.AddQueryString($"/v1/servers/{serverId:D}/files/upload", "path", path));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(GameFileTransfer.PanelUploadTimeoutSeconds));
        using var content = new UploadContent(source, length, timeout.Token);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };
        request.Headers.ExpectContinue = length > 0;
        try
        {
            using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.StatusCode switch
            {
                HttpStatusCode.Created or HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.MethodNotAllowed or
                HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests or
                HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.GatewayTimeout => response.StatusCode,
                _ => HttpStatusCode.BadGateway
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HttpStatusCode.GatewayTimeout;
        }
    }

    private sealed class UploadContent(Stream source, long length, CancellationToken requestToken) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            GameFileTransfer.CopyExactlyAsync(source, stream, length, requestToken);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            GameFileTransfer.CopyExactlyAsync(source, stream, length, cancellationToken);

        protected override bool TryComputeLength(out long value)
        {
            value = length;
            return true;
        }
    }

    public async Task<HttpStatusCode> EditTextAsync(Guid nodeId, Guid serverId, string path, string content, string sha256, CancellationToken token)
    {
        var connection = GetConnection(nodeId);
        if (connection is null) return HttpStatusCode.ServiceUnavailable;
        var target = new Uri(connection.Origin, QueryHelpers.AddQueryString($"/v1/servers/{serverId:D}/files/text", "path", path));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Put, target) { Content = JsonContent.Create(new { content, sha256 }) };
        using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        return response.StatusCode switch
        {
            HttpStatusCode.NoContent or HttpStatusCode.PreconditionFailed or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound or
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests => response.StatusCode,
            _ => HttpStatusCode.BadGateway
        };
    }

    private async Task<(HttpStatusCode Status, byte[]? Bytes)> ReadAsync(Guid nodeId, string route, string? path,
        string mediaType, int limit, CancellationToken cancellationToken)
    {
        var connection = GetConnection(nodeId);
        if (connection is null) return (HttpStatusCode.ServiceUnavailable, null);
        var target = new Uri(connection.Origin, path is null ? route : QueryHelpers.AddQueryString(route, "path", path));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await connection.Client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.TooManyRequests or HttpStatusCode.GatewayTimeout
                    or HttpStatusCode.ServiceUnavailable
                    => response.StatusCode,
                _ => HttpStatusCode.BadGateway
            }, null);
        }
        var length = response.Content.Headers.ContentLength;
        if (length < 0 || length > limit || response.Content.Headers.ContentType?.MediaType != mediaType)
            return (HttpStatusCode.BadGateway, null);

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        if (length is { } expected)
        {
            var bytes = new byte[(int)expected];
            await body.ReadExactlyAsync(bytes, timeout.Token);
            return (HttpStatusCode.OK, bytes);
        }
        if (mediaType != "application/json") return (HttpStatusCode.BadGateway, null);

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await body.ReadAsync(buffer, timeout.Token);
            if (count == 0) return (HttpStatusCode.OK, output.ToArray());
            if (output.Length + count > limit) return (HttpStatusCode.BadGateway, null);
            output.Write(buffer, 0, count);
        }
    }

    private Connection? GetConnection(Guid nodeId)
    {
        var settings = configuration.GetSection($"FileNodes:{nodeId:D}");
        return settings.Exists() ? _connections.GetOrAdd(nodeId, _ => new Lazy<Connection>(() => Connect(settings))).Value : null;
    }

    private static Connection Connect(IConfiguration settings)
    {
        if (!Uri.TryCreate(settings["Endpoint"], UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 || origin.PathAndQuery != "/" || origin.Fragment.Length != 0)
            throw new InvalidOperationException("Configure a fixed HTTPS file node origin.");

        var pin = Convert.FromHexString(settings["ServerCertificateSha256"] ?? "");
        if (pin.Length != 32) throw new InvalidOperationException("Configure the file node certificate fingerprint.");
        var certificatePath = settings["ClientCertificatePath"] ?? throw new InvalidOperationException("Configure the panel certificate.");
        var keyPath = settings["ClientKeyPath"] ?? throw new InvalidOperationException("Configure the panel certificate key.");
        using var pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        // Schannel needs a key container when the panel runs on Windows.
        var pkcs12 = pem.Export(X509ContentType.Pkcs12);
        X509Certificate2 panel;
        try { panel = X509CertificateLoader.LoadPkcs12(pkcs12, null); }
        finally { CryptographicOperations.ZeroMemory(pkcs12); }
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { panel },
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    certificate is not null &&
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                    CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256)) &&
                    IsCurrent(certificate)
            }
        };
        return new Connection(origin, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, panel);
    }

    private static bool IsCurrent(X509Certificate certificate)
    {
        using var x509 = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var now = DateTime.UtcNow;
        return x509.NotBefore.ToUniversalTime() <= now && x509.NotAfter.ToUniversalTime() >= now;
    }

    public void Dispose()
    {
        foreach (var value in _connections.Values)
            if (value.IsValueCreated) value.Value.Dispose();
    }

    private sealed record Connection(Uri Origin, HttpClient Client, X509Certificate2 Certificate) : IDisposable
    {
        public void Dispose() { Client.Dispose(); Certificate.Dispose(); }
    }
}
