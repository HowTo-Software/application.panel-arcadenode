namespace ArcadeNode.Transfers;

internal static class GameDirectoryQuery
{
    public static bool ValidCursor(string? cursor) => cursor is null || cursor.Length == 80 && cursor.All(char.IsAsciiHexDigit);

    public static bool ValidFilter(string? filter) => filter is null ||
        filter.Length is > 0 and <= 128 && !string.IsNullOrWhiteSpace(filter) && !filter.Any(char.IsControl);
}
