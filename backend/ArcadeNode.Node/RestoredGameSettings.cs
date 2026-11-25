using System.Text.Json.Serialization;

namespace ArcadeNode.Transfers;

public static class RestoredGameLayout
{
    public const string Managed = "arcadenode";
    public const string Pterodactyl = "pterodactyl";
}

public sealed record BedrockRestoreSettings(
    [property: JsonPropertyName("layout")] string Layout,
    [property: JsonPropertyName("game_port")] int GamePort,
    [property: JsonPropertyName("ipv6_port")] int Ipv6Port)
{
    public const string HomeDirectory = "/home/bedrock";
    public const int DefaultIpv6Port = 19133;
}

public sealed record ReforgerRestoreSettings(
    [property: JsonPropertyName("layout")] string Layout,
    [property: JsonPropertyName("game_port")] int GamePort,
    [property: JsonPropertyName("public_port")] int PublicPort,
    [property: JsonPropertyName("a2s_port")] int? A2sPort,
    [property: JsonPropertyName("rcon_port")] int? RconPort)
{
    public const string HomeDirectory = "/home/reforger";
}
