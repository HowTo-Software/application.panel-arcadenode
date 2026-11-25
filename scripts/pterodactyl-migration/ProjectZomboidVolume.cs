using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ArcadeNode.Node;
using ArcadeNode.Transfers;

internal static class ProjectZomboidVolume
{
    public static async Task<ProjectZomboidRestoreSettings> ReadAsync(string directory, string serverName, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidDataException("Run the Zomboid verification on Linux with the configured server name.");

        using var volume = new LinuxGameFiles(Path.GetFullPath(directory));
        var managed = volume.CanExecute("server/start-server.sh");
        if (!managed && (!volume.CanExecute("ProjectZomboid64") || !volume.CanExecute("jre64/bin/java")))
            throw new InvalidDataException("The expected Zomboid launcher is missing or lacks execute permission.");
        var layout = managed ? ProjectZomboidRestoreSettings.ManagedLayout : ProjectZomboidRestoreSettings.PterodactylLayout;
        var saveName = ProjectZomboidRestoreSettings.SaveNameFor(serverName, layout)
            ?? throw new InvalidDataException("The configured Zomboid save name is invalid.");

        using var launch = JsonDocument.Parse(await RestoredGameVolumes.ReadConfigurationAsync(volume,
            managed ? "server/ProjectZomboid64.json" : "ProjectZomboid64.json", 65_536, token));
        if (launch.RootElement.ValueKind != JsonValueKind.Object || !launch.RootElement.TryGetProperty("vmArgs", out var args) ||
            args.ValueKind != JsonValueKind.Array || args.EnumerateArray().Any(option => option.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("Zomboid launcher options are invalid.");

        var saves = StateDirectory("Saves");
        var databases = StateDirectory("db");
        var configs = StateDirectory("Server");
        if (managed) StateDirectory("Logs");
        volume.ValidateDirectory($"{saves}/Multiplayer/{saveName}");
        await using var database = volume.OpenRead($"{databases}/{saveName}.db");
        var header = new byte[100];
        await database.ReadExactlyAsync(header, token);
        var encodedPageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16, 2));
        var pageSize = encodedPageSize == 1 ? 65_536 : encodedPageSize;
        if (!header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) ||
            pageSize < 512 || !System.Numerics.BitOperations.IsPow2(pageSize) ||
            header[18] is not (1 or 2) || header[19] is not (1 or 2) ||
            database.Length < pageSize || database.Length % pageSize != 0)
            throw new InvalidDataException("The restored Zomboid player database lacks a valid SQLite header and page layout.");

        var config = new UTF8Encoding(false, true).GetString(await RestoredGameVolumes.ReadConfigurationAsync(volume,
            $"{configs}/{saveName}.ini", 256 * 1024, token));
        return ReadSettings(layout, saveName, config);

        string StateDirectory(string component)
        {
            if (!managed) return $".cache/{component}";
            var path = $"Zomboid/{component}";
            if (volume.ReadContainedSymbolicLink(path) is not { } target) return path;
            var stored = $"data/{component}";
            if (target != $"../{stored}") throw new InvalidDataException("A managed Zomboid link must refer to its data directory.");
            volume.ValidateDirectory(stored);
            return stored;
        }
    }

    internal static ProjectZomboidRestoreSettings ReadSettings(string layout, string saveName, string config)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in config.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator];
            if ((key is "DefaultPort" or "UDPPort" or "RCONPort" or "RCONPassword") &&
                !options.TryAdd(key, line[(separator + 1)..].TrimEnd('\r')))
                throw new InvalidDataException("The Zomboid configuration has duplicate port or RCON settings.");
        }

        var game = Port("DefaultPort");
        var direct = Port("UDPPort");
        if (game == direct) throw new InvalidDataException("Zomboid uses separate game and direct UDP ports.");

        int? rcon = null;
        if (options.GetValueOrDefault("RCONPassword") is { Length: > 0 } password)
        {
            if (password.EnumerateRunes().Count() < 16 || password == "changeme")
                throw new InvalidDataException("The existing Zomboid RCON password fails the restore policy.");
            rcon = Port("RCONPort");
        }
        return new(layout, saveName, game, direct, rcon);

        int Port(string name) => int.TryParse(options.GetValueOrDefault(name), NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            port is >= 1024 and <= 65535 ? port : throw new InvalidDataException("A required Zomboid port is missing or invalid.");
    }
}
