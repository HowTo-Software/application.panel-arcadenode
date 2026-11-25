using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal static class PterodactylSource
{
    private const string InspectFormat = """
        {"id":{{json .Id}},"name":{{json .Name}},"service":{{json (index .Config.Labels "Service")}},"type":{{json (index .Config.Labels "ContainerType")}},"state":{{json .State}},"mounts":{{json .Mounts}}}
        """;

    internal sealed record ProcessReceipt(string ContainerId, DateTimeOffset? StoppedAt);
    internal sealed record Observation(ProcessReceipt Process, string DataDirectory);

    public static async Task<Observation> ObserveAsync(Guid serverId, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run on the Linux Pterodactyl node.");
        var socket = Environment.GetEnvironmentVariable("PTERODACTYL_DOCKER_SOCKET") ?? "/var/run/docker.sock";
        if (!Path.IsPathFullyQualified(socket) || socket.Any(char.IsControl)) throw new InvalidDataException("Expected an absolute local Docker socket path.");
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment.Remove("DOCKER_CONTEXT");
        start.Environment.Remove("DOCKER_HOST");
        foreach (var argument in new[] { "--host", $"unix://{socket}", "inspect", "--type", "container", "--format", InspectFormat, serverId.ToString("D") })
            start.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(start) ?? throw new IOException("Could not inspect the local source container.");
        try
        {
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var json = new StringBuilder();
            var buffer = new char[1024];
            int length;
            while ((length = await process.StandardOutput.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (json.Length + length > 32_768) throw new InvalidDataException("Docker inspection exceeded its size limit.");
                json.Append(buffer, 0, length);
            }
            await process.WaitForExitAsync(timeout.Token);
            await stderr;
            if (process.ExitCode != 0) throw new IOException("Could not inspect the local source container.");
            return Parse(serverId, json.ToString());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("Docker inspection timed out.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) when (process.HasExited) { }
            }
        }
    }

    private static Observation Parse(Guid serverId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var container = root.GetProperty("id").GetString();
        if (container is not { Length: 64 } || !container.All(char.IsAsciiHexDigit) ||
            root.GetProperty("name").GetString() != $"/{serverId:D}" || root.GetProperty("service").GetString() != "Pterodactyl" ||
            root.GetProperty("type").GetString() != "server_process")
            throw new InvalidDataException("The inspected container is not the requested Pterodactyl server.");

        var state = root.GetProperty("state");
        var status = state.GetProperty("Status").GetString();
        if (status is not ("created" or "exited") || state.GetProperty("Running").GetBoolean() || state.GetProperty("Paused").GetBoolean() ||
            state.GetProperty("Restarting").GetBoolean() || state.GetProperty("Dead").GetBoolean())
            throw new InvalidDataException("The Pterodactyl source is not stopped.");
        var stoppedAt = status == "exited" ? state.GetProperty("FinishedAt").GetDateTimeOffset() : (DateTimeOffset?)null;
        if (stoppedAt is { } finished && (finished <= DateTimeOffset.UnixEpoch || finished > DateTimeOffset.UtcNow))
            throw new InvalidDataException("The source stop time is invalid.");

        var mounts = root.GetProperty("mounts").EnumerateArray().ToArray();
        if (mounts.Any(mount => mount.GetProperty("Destination").GetString()?.StartsWith("/home/container/", StringComparison.Ordinal) == true))
            throw new InvalidDataException("Nested game-data mounts must be migrated separately.");
        var dataMounts = mounts
            .Where(mount => mount.GetProperty("Destination").GetString() == "/home/container").ToArray();
        if (dataMounts is not [var mount] || mount.GetProperty("Type").GetString() != "bind" ||
            mount.GetProperty("Source").GetString() is not { Length: > 0 } path || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The source data bind mount could not be identified.");
        return new Observation(new ProcessReceipt(container, stoppedAt), Path.GetFullPath(path));
    }
}
