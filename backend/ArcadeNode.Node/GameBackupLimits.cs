namespace ArcadeNode.Backups;

internal static class GameBackupLimits
{
    public const long BytesPerGiB = 1024L * 1024 * 1024;
    public const int DefaultCreationGiB = 16;
    public const int MaximumDataGiB = 64;
    public const long MaximumDataBytes = MaximumDataGiB * BytesPerGiB;
    public const long MaximumArchiveBytes = (MaximumDataGiB + 4) * BytesPerGiB;
    public const int MaximumEntries = 250_000;

    public static readonly TimeSpan CreationTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan ResponseGrace = TimeSpan.FromSeconds(5);

    public static long CreationBytes(int gibibytes)
    {
        if (gibibytes is < 1 or > MaximumDataGiB)
            throw new InvalidOperationException($"Configure Files:BackupMaxDataGiB between 1 and {MaximumDataGiB}.");
        return gibibytes * BytesPerGiB;
    }
}
