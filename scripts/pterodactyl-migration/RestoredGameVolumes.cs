using System.Globalization;
using System.Text;
using System.Text.Json;
using ArcadeNode.Node;
using ArcadeNode.Transfers;

internal static class RestoredGameVolumes
{
    public static async Task<BedrockRestoreSettings> ReadBedrockAsync(string directory, CancellationToken token)
    {
        using var volume = new LinuxGameFiles(Path.GetFullPath(directory));
        var classic = volume.CanExecute("bedrock_server");
        if (!classic && !volume.CanExecute("server/bedrock_server"))
            throw new InvalidDataException("The Bedrock executable is missing or cannot execute.");

        var worldDirectory = classic ? "worlds" : "server/worlds";
        if (!classic && volume.ReadContainedSymbolicLink(worldDirectory) is { } target)
        {
            if (target != "../data/worlds")
                throw new InvalidDataException("The managed Bedrock world link must point to its data directory.");
            worldDirectory = "data/worlds";
        }
        volume.ValidateDirectory(worldDirectory);
        var config = await ReadConfigurationAsync(volume, classic ? "server.properties" : "server/server.properties", 256 * 1024, token);
        return ReadBedrockSettings(Layout(classic), new UTF8Encoding(false, true).GetString(config));
    }

    public static async Task<ReforgerRestoreSettings> ReadReforgerAsync(string directory, CancellationToken token)
    {
        using var volume = new LinuxGameFiles(Path.GetFullPath(directory));
        var classic = volume.CanExecute("ArmaReforgerServer");
        if (!classic && !volume.CanExecute("server/ArmaReforgerServer"))
            throw new InvalidDataException("The Reforger executable is missing or cannot execute.");

        if (classic)
        {
            using var project = volume.OpenRead("addons/data/ArmaReforger.gproj");
            if (volume.ReadContainedSymbolicLink("tmp") is not null)
                throw new InvalidDataException("The Reforger addon temporary directory must be a regular directory.");
        }
        volume.ValidateDirectory(classic ? "profile" : "data/profile");
        volume.ValidateDirectory(classic ? "addons" : "data/addons");
        var config = await ReadConfigurationAsync(volume, classic ? "config.json" : "data/configs/server.json", 256 * 1024, token);
        using var json = JsonDocument.Parse(config, new JsonDocumentOptions { AllowDuplicateProperties = false });
        return ReadReforgerSettings(Layout(classic), json.RootElement);
    }

    internal static BedrockRestoreSettings ReadBedrockSettings(string layout, string config)
    {
        int? game = null, ipv6 = null;
        foreach (var line in config.Split('\n'))
        {
            var value = line.AsSpan().TrimEnd('\r');
            if (value.StartsWith("server-port="))
                game = game is null ? ParsePort(value["server-port=".Length..]) : throw InvalidPorts();
            else if (value.StartsWith("server-portv6="))
                ipv6 = ipv6 is null ? ParsePort(value["server-portv6=".Length..]) : throw InvalidPorts();
        }
        if (game is null || ipv6 is null || game == ipv6) throw InvalidPorts();
        return new(layout, game.Value, ipv6.Value);
    }

    internal static ReforgerRestoreSettings ReadReforgerSettings(string layout, JsonElement config)
    {
        var game = Property(config, "game");
        var query = Property(config, "a2s");
        var rcon = Property(config, "rcon");
        var bind = Property(config, "bindPort") ?? Property(config, "gameHostBindPort") ??
            Property(config, "publicPort") ?? Property(config, "gameHostRegisterPort");
        var gamePort = bind is { } bindValue ? ParsePort(bindValue) : 2001;
        var advertised = Property(config, "publicPort") ?? Property(config, "gameHostRegisterPort");
        var publicPort = advertised is { } publicValue ? ParsePort(publicValue) : gamePort;
        var queryValue = Property(query, "port");
        int? queryPort = queryValue is { } a2sValue ? ParsePort(a2sValue) :
            Property(config, "a2sQueryEnabled") is { ValueKind: JsonValueKind.True }
                ? Property(config, "steamQueryPort") is { } steam ? ParsePort(steam) : 17777
                : null;
        int? rconPort = rcon is null ? null : Property(rcon, "port") is { } remote ? ParsePort(remote) : 19999;

        if (publicPort != gamePort || queryPort == gamePort || rconPort == gamePort ||
            (rconPort.HasValue && rconPort == queryPort))
            throw InvalidPorts();
        ValidateBind(Property(config, "bindAddress") ?? Property(config, "gameHostBindAddress"));
        if (queryPort.HasValue) ValidateBind(Property(query, "address"));
        if (rconPort.HasValue) ValidateBind(Property(rcon, "address"));
        ValidatePassword(Property(game, "passwordAdmin") ?? Property(config, "adminPassword"), optional: true);
        if (rconPort.HasValue) ValidatePassword(Property(rcon, "password"), optional: false);
        return new(layout, gamePort, publicPort, queryPort, rconPort);
    }

    private static JsonElement? Property(JsonElement? parent, string name)
    {
        if (parent is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a Reforger configuration object.");
        return value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property : null;
    }

    private static void ValidateBind(JsonElement? address)
    {
        if (address is { } value && (value.ValueKind != JsonValueKind.String || value.GetString() is not ("" or "0.0.0.0")))
            throw new InvalidDataException("The Reforger listener must use the container's wildcard address.");
    }

    private static void ValidatePassword(JsonElement? entry, bool optional)
    {
        var password = entry is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
        if ((optional && (entry is null || password == "")) || password?.EnumerateRunes().Count() >= 16) return;
        throw new InvalidDataException("A restored Reforger credential fails the restore policy.");
    }

    private static int ParsePort(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) && port is >= 1024 and <= 65535
            ? port : throw InvalidPorts();

    private static int ParsePort(ReadOnlySpan<char> value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
        port is >= 1024 and <= 65535 && value.Length == port.ToString(CultureInfo.InvariantCulture).Length
            ? port : throw InvalidPorts();

    private static InvalidDataException InvalidPorts() => new("A restored game listener is missing, repeated or invalid.");

    private static string Layout(bool classic) => classic ? RestoredGameLayout.Pterodactyl : RestoredGameLayout.Managed;

    internal static async Task<byte[]> ReadConfigurationAsync(LinuxGameFiles volume, string path, int limit, CancellationToken token)
    {
        await using var file = volume.OpenRead(path);
        var length = file.Length;
        if (length < 1 || length > limit) throw new InvalidDataException("The game configuration has an invalid size.");
        var bytes = new byte[(int)length];
        await file.ReadExactlyAsync(bytes, token);
        if (file.ReadByte() >= 0) throw new InvalidDataException("The game configuration changed while reading.");
        return bytes;
    }
}
