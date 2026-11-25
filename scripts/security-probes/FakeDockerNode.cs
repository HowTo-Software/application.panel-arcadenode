using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace SecurityRegression;

// Synthetic Docker HTTP upgrade; never runs Docker, commands or games.
public sealed class FakeDockerNode(SecurityFixture fixture, X509Certificate2? serverCertificate = null) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, JsonElement> _containers = new();
    private readonly ConcurrentDictionary<string, object> _volumes = new();
    private readonly ConcurrentBag<Task> _connections = new();
    private Task? _accept;
    public readonly ConcurrentQueue<(string Path, string Body)> Requests = new();
    public readonly ConcurrentQueue<string> Commands = new();
    public readonly ConcurrentQueue<string> Signals = new();
    public bool WrongOwnership;
    public bool RefuseConsoleUpgrade;
    public bool LegacyImage;
    public bool RestoreUnsupported;
    public bool ReportStopped;
    public bool ContainerListUnavailable;
    public readonly ConcurrentDictionary<string, ContainerState> RuntimeOverrides = new();
    public bool CorruptBackupArchive;
    public bool BackupBusy;
    public bool BackupDeletionUnavailable;
    public bool RestoreBusy;
    public bool WrongRestoreChecksum;
    public long RestoreBytes = 130000;
    public TaskCompletionSource? RestoreEntered, ReleaseRestore;
    public static readonly byte[] BackupArchive = MakeArchive();
    public static readonly string BackupChecksum = Convert.ToHexString(SHA256.HashData(BackupArchive));
    public string? VolumeFailure;
    public bool HangHandshake;
    public readonly TaskCompletionSource HandshakeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource HandshakeDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource? StartEntered, ReleaseStart;
    public TaskCompletionSource? UploadChunkReceived;
    public TaskCompletionSource? UploadHeadersReceived;
    public TaskCompletionSource? ReleaseFileDownload;
    public static readonly byte[] LargeFile = Enumerable.Range(0, 9 * 1024 * 1024).Select(index => (byte)(index % 241)).ToArray();
    public string Endpoint => $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
    public Task StartAsync() { _listener.Start(); _accept = AcceptAsync(); return Task.CompletedTask; }
    public void Seed(string containerId, Guid serverId, Guid nodeId, Guid ownerId) =>
        _containers[containerId] = JsonSerializer.SerializeToElement(new { Labels = new Dictionary<string, string> {
            ["panel.server.id"] = serverId.ToString(), ["panel.node.id"] = nodeId.ToString(), ["panel.owner.id"] = ownerId.ToString() } });
    public void SeedRestoredVolume(Guid serverId, int megabytes, string digest)
    {
        var name = $"arcadenode-{serverId:N}-data";
        _volumes[name] = new
        {
            Name = name, Driver = "synthetic-quota",
            Options = new Dictionary<string, string> { ["size"] = ((long)megabytes * 1024 * 1024).ToString() },
            Labels = new Dictionary<string, string> { ["panel.server.id"] = serverId.ToString(), ["panel.restore.sha256"] = digest }
        };
    }
    private async Task AcceptAsync()
    {
        try { while (!_stop.IsCancellationRequested) _connections.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
        catch (OperationCanceledException) { }
    }
    private async Task HandleAsync(TcpClient tcp)
    {
        using (tcp)
        using (var tls = new SslStream(tcp.GetStream(), false))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10)); var ct = timeout.Token;
            try
            {
                if (HangHandshake)
                {
                    var discard = new byte[4096];
                    while (await tcp.GetStream().ReadAsync(discard, ct) != 0) HandshakeStarted.TrySetResult();
                    HandshakeDisconnected.TrySetResult();
                    return;
                }
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = serverCertificate ?? fixture.ServerCertificate, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && Trusts(cert)
                }, ct);
                var header = new StringBuilder(); var one = new byte[1];
                while (header.Length < 16384 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                { if (await tls.ReadAsync(one, ct) == 0) return; header.Append((char)one[0]); }
                var lines = header.ToString().Split("\r\n"); var request = lines[0].Split(' '); var path = request[1].Split('?')[0];
                var contentLength = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = contentLength is null ? 0 : int.Parse(contentLength.Split(':')[1].Trim());
                var upload = path.EndsWith("/files/upload", StringComparison.Ordinal);
                if (length > (upload ? 1024 * 1024 : 65536)) throw new InvalidOperationException("Synthetic request exceeded its bound");
                if (upload) UploadHeadersReceived?.TrySetResult();
                if (upload && lines.Any(line => line.Equals("Expect: 100-continue", StringComparison.OrdinalIgnoreCase)))
                {
                    await tls.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);
                    await tls.FlushAsync(ct);
                }
                var bodyBytes = new byte[length];
                if (upload && UploadChunkReceived is { } firstChunk && length > 65536)
                {
                    await tls.ReadExactlyAsync(bodyBytes.AsMemory(0, 65536), ct);
                    firstChunk.TrySetResult();
                    await tls.ReadExactlyAsync(bodyBytes.AsMemory(65536), ct);
                }
                else await tls.ReadExactlyAsync(bodyBytes, ct);
                var body = Encoding.UTF8.GetString(bodyBytes); Requests.Enqueue((path, body));
                if (path.EndsWith("/files/stream", StringComparison.Ordinal))
                {
                    var query = request[1][path.Length..];
                    await SendFileAsync(tls, request[0], QueryHelpers.ParseQuery(query)["path"].ToString(), ct);
                    return;
                }
                if (path.EndsWith("/start") && StartEntered is not null && ReleaseStart is not null)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(25));
                    StartEntered.TrySetResult();
                    await ReleaseStart.Task.WaitAsync(ct);
                }
                if (path.EndsWith("/restore") && RestoreEntered is not null && ReleaseRestore is not null)
                {
                    RestoreEntered.TrySetResult();
                    await ReleaseRestore.Task.WaitAsync(ct);
                }
                if (path.EndsWith("/attach"))
                {
                    if (RefuseConsoleUpgrade || request[0] != "POST" ||
                        !lines.Any(line => line.Equals("Connection: Upgrade", StringComparison.OrdinalIgnoreCase)) ||
                        !lines.Any(line => line.Equals("Upgrade: tcp", StringComparison.OrdinalIgnoreCase)) ||
                        !request[1].EndsWith("?stream=1&stdin=1", StringComparison.Ordinal))
                    {
                        await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
                        return;
                    }
                    await tls.WriteAsync("HTTP/1.1 101 UPGRADED\r\nContent-Type: application/vnd.docker.raw-stream\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n"u8.ToArray(), ct);
                    await tls.FlushAsync(ct);
                    var command = new StringBuilder();
                    while (command.Length < 1024) { if (await tls.ReadAsync(one, ct) == 0) break; command.Append((char)one[0]); if (one[0] == 10) break; }
                    Commands.Enqueue(command.ToString()); return;
                }
                var (status, response) = Response(path, request[0], body, request[1][path.Length..]);
                var payload = response switch
                {
                    byte[] binary => binary,
                    string raw => Encoding.UTF8.GetBytes(raw),
                    _ => JsonSerializer.SerializeToUtf8Bytes(response)
                };
                var mediaType = response is byte[] ? "application/gzip" : "application/json";
                await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Synthetic\r\nContent-Type: {mediaType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), ct);
                await tls.WriteAsync(payload, ct); await tls.FlushAsync(ct);
            }
            catch (IOException) { if (HangHandshake) HandshakeDisconnected.TrySetResult(); } // Bounded readers can close early.
            catch (AuthenticationException) { } // Invalid client certificate tests.
            catch (OperationCanceledException) { }
        }
    }
    private bool Trusts(X509Certificate cert) { using var leaf = X509CertificateLoader.LoadCertificate(cert.GetRawCertData()); return SecurityFixture.Trusts(leaf, fixture.Ca); }

    private async Task SendFileAsync(Stream stream, string method, string file, CancellationToken token)
    {
        var (status, length) = file switch
        {
            "world/mapa São Paulo & gelo.bin" or "truncated.bin" => (200, LargeFile.Length),
            "empty.dat" => (200, 0),
            "oversized.dat" => (413, 0),
            _ => (404, 0)
        };
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Synthetic\r\nContent-Type: application/octet-stream\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n"), token);
        if (method != "HEAD" && length > 0)
        {
            if (file == "truncated.bin")
            {
                await stream.WriteAsync(LargeFile.AsMemory(0, 32), token);
            }
            else
            {
                await stream.WriteAsync(LargeFile.AsMemory(0, 65536), token);
                await stream.FlushAsync(token);
                if (ReleaseFileDownload is { } release) await release.Task.WaitAsync(token);
                await stream.WriteAsync(LargeFile.AsMemory(65536), token);
            }
        }
        await stream.FlushAsync(token);
    }

    private static byte[] MakeArchive()
    {
        var payload = new byte[130000];
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.NoCompression, leaveOpen: true)) gzip.Write(payload);
        return output.ToArray();
    }

    private (int, object) Response(string path, string method, string body, string query)
    {
        if (method == "GET" && path.StartsWith("/v1/servers/", StringComparison.Ordinal) && path.EndsWith("/files/page", StringComparison.Ordinal))
        {
            var options = QueryHelpers.ParseQuery(query);
            if (options["cursor"] == new string('B', 80)) return (409, "");
            return (200, new { entries = Array.Empty<object>(), truncated = false, nextCursor = (string?)null,
                path = options["path"].ToString(), cursor = options["cursor"].ToString(), filter = options["filter"].ToString() });
        }
        if (method == "GET" && path.StartsWith("/v1/servers/", StringComparison.Ordinal) && path.EndsWith("/files", StringComparison.Ordinal))
            return (200, new { entries = Array.Empty<object>(), truncated = false });
        if (method == "POST" && path.StartsWith("/v1/servers/", StringComparison.Ordinal))
        {
            if (path.EndsWith("/files/upload", StringComparison.Ordinal)) return (201, "");
            if (path.EndsWith("/files/directories", StringComparison.Ordinal)) return (201, "");
            if (path.EndsWith("/files/chmod", StringComparison.Ordinal))
            {
                using var request = JsonDocument.Parse(body);
                return request.RootElement.GetProperty("entries").EnumerateArray().Any(entry => entry.GetString() == "missing")
                    ? (404, new { }) : (204, "");
            }
            if (path.EndsWith("/files/delete", StringComparison.Ordinal))
            {
                using var request = JsonDocument.Parse(body);
                var names = request.RootElement.GetProperty("entries").EnumerateArray().Select(item => item.GetString()).ToArray();
                return names switch
                {
                    ["oversized"] => (413, new { }),
                    ["missing"] => (404, new { }),
                    _ => (204, "")
                };
            }
            if (path.EndsWith("/files/extract", StringComparison.Ordinal))
            {
                using var archive = JsonDocument.Parse(body);
                return archive.RootElement.GetProperty("source").GetString() == "invalid.zip" ? (422, new { }) : (201, "");
            }
            if (path.EndsWith("/files/compress", StringComparison.Ordinal))
            {
                using var archive = JsonDocument.Parse(body);
                return archive.RootElement.GetProperty("destination").GetString() switch
                {
                    "existing.tar.gz" => (409, new { }),
                    "oversized.tar.gz" => (413, new { }),
                    _ => (201, "")
                };
            }
            if (path.EndsWith("/files/copy", StringComparison.Ordinal))
            {
                using var copy = JsonDocument.Parse(body);
                if (copy.RootElement.GetProperty("source").GetString() == "world/oversized.bin") return (413, new { });
                if (copy.RootElement.GetProperty("destination").GetString() == "world/existing.cfg") return (409, new { });
                return (201, "");
            }
            if (path.EndsWith("/files/move", StringComparison.Ordinal))
            {
                using var move = JsonDocument.Parse(body);
                if (move.RootElement.GetProperty("destination").GetString() == "world/existing.cfg") return (409, new { });
                return (204, "");
            }
        }
        if (path.StartsWith("/v1/servers/") && path.Contains("/backups/"))
        {
            if (method == "DELETE") return (BackupDeletionUnavailable ? 503 : 204, "");
            if (method == "POST" && path.EndsWith("/restore"))
            {
                if (RestoreBusy) return (429, new { });
                using var requested = JsonDocument.Parse(body);
                if (requested.RootElement.GetProperty("sha256").GetString() != BackupChecksum) return (422, new { });
                return (200, new { files = 3, directories = 2, bytes = RestoreBytes, archiveSha256 = WrongRestoreChecksum ? new string('B', 64) : BackupChecksum });
            }
            if (method == "POST") return BackupBusy ? (429, new { }) : (200, new { size = BackupArchive.Length, sha256 = BackupChecksum });
            if (method == "GET")
            {
                if (!CorruptBackupArchive) return (200, BackupArchive);
                var changed = (byte[])BackupArchive.Clone();
                changed[^1] ^= 1;
                return (200, changed);
            }
        }
        if (path.Contains("/exec")) return (500, new { message = "Exec forbidden" });
        if (path.EndsWith("/info")) return (200, new { OperatingSystem = "Synthetic Linux", MemTotal = 64L * 1024 * 1024 * 1024, NCPU = 12, Containers = 7 });
        if (path.EndsWith("/version")) return (200, new { Version = "synthetic" });
        if (path.Contains("/images/") && path.EndsWith("/json")) return (200, new { Config = new
        {
            User = "10001:10001",
            Labels = new Dictionary<string, string>
            {
                ["arcadenode.managed"] = LegacyImage ? "1" : "2",
                ["arcadenode.restore"] = RestoreUnsupported ? "0" : "1"
            },
            Volumes = new { }
        } });
        if (path.EndsWith("/volumes/create"))
        {
            if (VolumeFailure is not null) return (500, new { message = VolumeFailure });
            var volume = JsonDocument.Parse(body).RootElement;
            return (201, new { Name = volume.GetProperty("Name").GetString(), Driver = volume.GetProperty("Driver").GetString(), Options = volume.GetProperty("DriverOpts"), Labels = volume.GetProperty("Labels") });
        }
        if (path.Contains("/volumes/") && method == "GET")
            return _volumes.TryGetValue(path.Split('/')[^1], out var restored) ? (200, restored) : (404, new { });
        if (path.EndsWith("/containers/create"))
        {
            var config = JsonDocument.Parse(body).RootElement.Clone(); var id = "synthetic-" + Guid.NewGuid().ToString("N"); _containers[id] = config; return (201, new { Id = id });
        }
        if (path.EndsWith("/containers/json", StringComparison.Ordinal) && method == "GET")
        {
            if (ContainerListUnavailable) return (503, new { message = "Temporary fixture outage" });
            var parameters = QueryHelpers.ParseQuery(query);
            if (parameters["all"].ToString() is not ("true" or "1") || !parameters.TryGetValue("filters", out var filters))
                return (400, new { message = "Expected all managed containers" });
            using var filter = JsonDocument.Parse(filters.ToString());
            var labels = filter.RootElement.GetProperty("label");
            var expected = labels.EnumerateObject().Single().Name;
            var source = _containers.Where(row => $"panel.node.id={row.Value.GetProperty("Labels").GetProperty("panel.node.id").GetString()}" == expected);
            return (200, source.Select(row => new { Id = row.Key, State = ReportRuntime(row.Key).Status,
                Labels = WrongOwnership ? JsonSerializer.SerializeToElement(new Dictionary<string, string>()) : row.Value.GetProperty("Labels") }).ToArray());
        }
        if (path.Contains("/containers/") && path.EndsWith("/json"))
        {
            var id = path.Split('/')[^2];
            if (!_containers.TryGetValue(id, out var config)) return (404, new { });
            var labels = WrongOwnership ? JsonSerializer.SerializeToElement(new Dictionary<string, string>()) : config.GetProperty("Labels");
            return (200, new { Config = new { Tty = true, Labels = labels }, State = ReportRuntime(id) });
        }
        if (path.EndsWith("/stats")) return (200, new
        {
            cpu_stats = new { cpu_usage = new { total_usage = 200, percpu_usage = new[] { 100, 100 } }, system_cpu_usage = 2000 },
            precpu_stats = new { cpu_usage = new { total_usage = 100 }, system_cpu_usage = 1000 },
            memory_stats = new { usage = 1000, stats = new { inactive_file = 300, cache = 600 } },
            blkio_stats = new { io_service_bytes_recursive = new[] { new { op = "Read", value = 500 }, new { op = "Write", value = 250 } } }
        });
        if (path.EndsWith("/logs")) return (200, new string('x', 400000));
        if (path.EndsWith("/kill", StringComparison.Ordinal))
        {
            var signal = QueryHelpers.ParseQuery(query)["signal"].ToString();
            if (method != "POST" || signal is not ("SIGKILL" or "SIGINT"))
                return (400, new { message = "Expected an explicit managed signal" });
            Signals.Enqueue(signal);
            return ReportStopped ? (409, new { message = "Container is not running" }) : (204, "");
        }
        if (path.EndsWith("/start") || path.EndsWith("/stop") || path.EndsWith("/restart") || method == "DELETE") return (204, "");
        return (404, new { });
    }

    private ContainerState ReportRuntime(string id) => RuntimeOverrides.TryGetValue(id, out var state) ? state : new ContainerState
    {
        Status = ReportStopped ? "exited" : "running", Running = !ReportStopped
    };
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop(); if (_accept is not null) await _accept;
        await Task.WhenAll(_connections); _stop.Dispose();
    }
}
