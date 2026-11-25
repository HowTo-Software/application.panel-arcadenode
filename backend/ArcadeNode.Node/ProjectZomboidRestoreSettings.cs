using System.Text.Json.Serialization;

namespace ArcadeNode.Transfers;

public sealed record ProjectZomboidRestoreSettings(
    [property: JsonPropertyName("layout")] string Layout,
    [property: JsonPropertyName("save_name")] string SaveName,
    [property: JsonPropertyName("game_port")] int GamePort,
    [property: JsonPropertyName("direct_port")] int DirectPort,
    [property: JsonPropertyName("rcon_port")] int? RconPort)
{
    public const string HomeDirectory = "/home/pzserver";
    public const string ManagedLayout = "arcadenode";
    public const string PterodactylLayout = "pterodactyl";

    public static string? SaveNameFor(string? configured, string layout)
    {
        configured ??= "ArcadeNode_PZ_Server";
        if (configured.Length > 64 || configured.Any(char.IsControl)) return null;
        if (layout == PterodactylLayout)
            return configured is "" or "." or ".." || configured.Contains('/') || configured.Contains('\\') ? null : configured;
        if (layout != ManagedLayout) return null;

        var name = new string(configured.Replace(' ', '_')
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').ToArray());
        return name.Length == 0 ? "servertest" : name;
    }
}
