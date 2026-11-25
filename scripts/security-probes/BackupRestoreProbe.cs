using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ArcadeNode.Node;

namespace SecurityRegression;

internal static class BackupRestoreProbe
{
    private static readonly DateTimeOffset SavedAt = new(2026, 10, 12, 14, 30, 0, TimeSpan.Zero);
    private const UnixFileMode SavedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static async Task RunAsync(string root)
    {
        var staging = Directory.CreateDirectory(Path.Combine(root, "restore-targets")).FullName;
        var existing = Directory.CreateDirectory(Path.Combine(staging, "existing-server")).FullName;
        var existingConfig = Path.Combine(existing, "world.cfg");
        await File.WriteAllTextAsync(existingConfig, "current live data");

        var archive = Path.Combine(root, "restore-test.tar.gz");
        var destination = Path.Combine(staging, "clone");
        var bytes = new byte[131073];
        new Random(27).NextBytes(bytes);
        using var payload = new MemoryStream(bytes);
        var hash = await WriteArchiveAsync(archive,
            new PaxTarEntry(TarEntryType.Directory, "world/") { Mode = SavedMode, ModificationTime = SavedAt },
            new PaxTarEntry(TarEntryType.RegularFile, "world/save.bin") { DataStream = payload, Mode = SavedMode, ModificationTime = SavedAt },
            new PaxTarEntry(TarEntryType.RegularFile, "world.cfg") { ModificationTime = SavedAt });
        var receipt = await GameBackupRestore.StageAsync(archive, hash, destination, CancellationToken.None);
        var restored = Path.Combine(destination, "world", "save.bin");
        Require(receipt == new GameBackupRestore.Receipt(2, 1, bytes.Length, hash) &&
            (await File.ReadAllBytesAsync(restored)).SequenceEqual(bytes) && new FileInfo(Path.Combine(destination, "world.cfg")).Length == 0 &&
            File.GetLastWriteTimeUtc(restored) == SavedAt.UtcDateTime &&
            Directory.GetLastWriteTimeUtc(Path.Combine(destination, "world")) == SavedAt.UtcDateTime,
            "Restore lost binary bytes, empty files or modification timestamps");
        if (OperatingSystem.IsLinux())
            Require(File.GetUnixFileMode(restored) == SavedMode && File.GetUnixFileMode(Path.Combine(destination, "world")) == SavedMode,
                "Restore lost Linux file or directory permissions");
        await ExpectRejectedAsync(archive, hash, existing);
        Require(await File.ReadAllTextAsync(existingConfig) == "current live data", "Restore overwrote existing game data");

        var noPublication = Path.Combine(staging, "failed-restore");
        await ExpectRejectedAsync(archive, new string('0', 64), noPublication);
        var invalidArchive = Path.Combine(root, "invalid-restore-test.tar.gz");
        var cases = new[]
        {
            new PaxTarEntry(TarEntryType.RegularFile, "../escaped.cfg"),
            new PaxTarEntry(TarEntryType.SymbolicLink, "alias") { LinkName = "../outside.cfg" },
            new PaxTarEntry(TarEntryType.HardLink, "hard-alias") { LinkName = "world.cfg" },
            new PaxTarEntry(TarEntryType.RegularFile, "world.cfg")
        };
        foreach (var invalid in cases)
        {
            var invalidHash = await WriteArchiveAsync(invalidArchive, new PaxTarEntry(TarEntryType.RegularFile, "world.cfg"), invalid);
            await ExpectRejectedAsync(invalidArchive, invalidHash, noPublication);
        }
        Require(!Path.Exists(noPublication) && !Path.Exists(Path.Combine(staging, "escaped.cfg")) &&
            !Directory.EnumerateFileSystemEntries(staging, ".arcadenode-restore-*").Any(), "A rejected restore left files behind");

        var concurrent = Path.Combine(staging, "concurrent");
        var completed = await Task.WhenAll(TryRestoreAsync(archive, hash, concurrent), TryRestoreAsync(archive, hash, concurrent));
        Require(completed.Count(success => success) == 1 &&
            (await File.ReadAllBytesAsync(Path.Combine(concurrent, "world", "save.bin"))).SequenceEqual(bytes),
            "Concurrent restores replaced or damaged the destination");

        var empty = Directory.CreateDirectory(Path.Combine(staging, "empty-source")).FullName;
        var emptyArchive = Path.Combine(root, "empty-backup.tar.gz");
        await using (var output = File.Create(emptyArchive))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            if (OperatingSystem.IsLinux())
            {
                using var volume = new LinuxGameFiles(empty);
                await volume.WriteArchiveAsync(gzip, 1, CancellationToken.None);
            }
            else await gzip.WriteAsync(new byte[1024]);
        }
        string emptyHash;
        await using (var input = File.OpenRead(emptyArchive)) emptyHash = Convert.ToHexString(await SHA256.HashDataAsync(input));
        var emptyDestination = Path.Combine(staging, "empty-restored");
        var emptyReceipt = await GameBackupRestore.StageAsync(emptyArchive, emptyHash, emptyDestination, CancellationToken.None);
        Require(emptyReceipt == new GameBackupRestore.Receipt(0, 0, 0, emptyHash) && Directory.Exists(emptyDestination) &&
            !Directory.EnumerateFileSystemEntries(emptyDestination).Any(), "An empty game volume could not round trip");
    }

    public static async Task RunPterodactylAsync(string root)
    {
        var staging = Directory.CreateDirectory(Path.Combine(root, "pterodactyl-backup-targets")).FullName;
        var archive = Path.Combine(root, "pterodactyl-backup.tar.gz");
        var bytes = new byte[131073];
        new Random(73).NextBytes(bytes);
        using var payload = new MemoryStream(bytes);
        var sha256 = await WriteArchiveAsync(archive,
            new UstarTarEntry(TarEntryType.RegularFile, "world/region/r.0.0.mca") { DataStream = payload, ModificationTime = SavedAt, Mode = SavedMode },
            new UstarTarEntry(TarEntryType.RegularFile, "server.properties") { ModificationTime = SavedAt },
            new UstarTarEntry(TarEntryType.Directory, "world/") { ModificationTime = SavedAt, Mode = SavedMode });
        var compressed = await File.ReadAllBytesAsync(archive);
        var legacyChecksum = "sha1:" + Convert.ToHexString(SHA1.HashData(compressed)).ToLowerInvariant();
        var destination = Path.Combine(staging, "restored");
        var receipt = await GameBackupRestore.StagePterodactylAsync(archive, legacyChecksum, compressed.Length, destination, CancellationToken.None);
        var region = Path.Combine(destination, "world", "region", "r.0.0.mca");
        Require(receipt == new GameBackupRestore.Receipt(2, 2, bytes.Length, sha256) &&
            (await File.ReadAllBytesAsync(region)).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(region) == SavedAt.UtcDateTime &&
            Directory.GetLastWriteTimeUtc(Path.Combine(destination, "world")) == SavedAt.UtcDateTime,
            "The Wings archive did not restore the implicit parent and the later explicit directory metadata");
        if (OperatingSystem.IsLinux())
            Require(File.GetUnixFileMode(region) == SavedMode && File.GetUnixFileMode(Path.Combine(destination, "world")) == SavedMode,
                "The legacy archive lost file or explicit directory modes");

        var strong = Path.Combine(staging, "sha256-restored");
        Require(await GameBackupRestore.StagePterodactylAsync(archive, "sha256:" + sha256, compressed.Length, strong, CancellationToken.None) == receipt,
            "The migration rejected a source recording SHA-256");

        var rejected = Path.Combine(staging, "rejected");
        await RejectAsync(archive, legacyChecksum, compressed.Length + 1, rejected);
        await RejectAsync(archive, "sha1:" + new string('0', 40), compressed.Length, rejected);
        await RejectAsync(archive, legacyChecksum, compressed.Length, destination);
        var unsafeArchive = Path.Combine(root, "pterodactyl-invalid-backup.tar.gz");
        var invalidDigest = await WriteArchiveAsync(unsafeArchive,
            new UstarTarEntry(TarEntryType.RegularFile, "world/config.json"),
            new UstarTarEntry(TarEntryType.SymbolicLink, "world/linked") { LinkName = "../../outside.cfg" });
        await RejectAsync(unsafeArchive, "sha256:" + invalidDigest, new FileInfo(unsafeArchive).Length, rejected);
        Require(!Path.Exists(rejected) && !Directory.EnumerateFileSystemEntries(staging, ".arcadenode-restore-*").Any() &&
            (await File.ReadAllBytesAsync(region)).SequenceEqual(bytes) && (await File.ReadAllBytesAsync(archive)).SequenceEqual(compressed),
            "Rejected Wings data left a partial output or modified the archive or live data");

        static async Task RejectAsync(string archive, string checksum, long length, string target)
        {
            try { await GameBackupRestore.StagePterodactylAsync(archive, checksum, length, target, CancellationToken.None); }
            catch (Exception error) when (error is IOException or InvalidDataException) { return; }
            throw new InvalidOperationException("Accepted a legacy archive with an invalid receipt, entry or destination");
        }
    }

    public static async Task RunContainedLinksAsync(string root)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "linked-backup-source")).FullName;
        var world = Directory.CreateDirectory(Path.Combine(source, "world")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(source, "data")).FullName;
        var save = Path.Combine(data, "save.bin");
        await File.WriteAllTextAsync(save, "linked game data");
        Directory.CreateSymbolicLink(Path.Combine(world, "shared"), "../data");
        File.CreateSymbolicLink(Path.Combine(world, "latest"), "../data/save.bin");
        var archive = Path.Combine(root, "linked-backup.tar.gz");
        using var volume = new LinuxGameFiles(source);
        var storage = Directory.CreateDirectory(Path.Combine(root, "linked-backup-storage")).FullName;
        var recoveries = Directory.CreateDirectory(Path.Combine(root, "linked-backup-recoveries")).FullName;
        var store = new NodeBackupStore(storage, recoveries);
        var serverId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        var stored = await store.CreateAsync(serverId, backupId, volume, null, CancellationToken.None);
        await using (var download = store.OpenArchive(serverId, backupId))
            Require(download.Length == stored.Size && Convert.ToHexString(await SHA256.HashDataAsync(download)) == stored.Sha256,
                "The node published a backup with an inconsistent receipt");
        var restored = await store.StageRestoreAsync(serverId, backupId, stored.Sha256, CancellationToken.None);
        Require(await store.StageRestoreAsync(serverId, backupId, stored.Sha256, CancellationToken.None) == restored,
            "The node could not return its persisted link receipt on a backup restore retry");
        var destination = Path.Combine(recoveries, serverId.ToString("N"), backupId.ToString("N"), GameBackupRestore.BundleData);
        using var restoredVolume = new LinuxGameFiles(destination);
        Require(restored is { Files: 1, Directories: 2, Links: 2 } &&
            restoredVolume.ReadContainedSymbolicLink("world/shared") == "../data" &&
            restoredVolume.ReadContainedSymbolicLink("world/latest") == "../data/save.bin" &&
            await File.ReadAllTextAsync(Path.Combine(destination, "world", "latest")) == "linked game data",
            "An internally linked game volume could not round trip");

        try
        {
            await WriteNodeBackup("/data/");
            throw new InvalidOperationException("A backup accepted links with an excluded target.");
        }
        catch (IOException) { }

        // Wings archives can list a link before the target and omit explicit parent entries.
        var hash = await WriteArchiveAsync(archive,
            new UstarTarEntry(TarEntryType.SymbolicLink, "world/latest") { LinkName = "../data/save.bin" },
            new UstarTarEntry(TarEntryType.RegularFile, "data/save.bin"));
        var wings = Path.Combine(root, "linked-wings-restored");
        var recovered = await GameBackupRestore.StagePterodactylAsync(archive, "sha256:" + hash, new FileInfo(archive).Length,
            wings, CancellationToken.None);
        using var wingsVolume = new LinuxGameFiles(wings);
        Require(recovered is { Files: 1, Directories: 2, Links: 1 } &&
            wingsVolume.ReadContainedSymbolicLink("world/latest") == "../data/save.bin", "The Wings link or its implicit parent was lost");

        var rejected = Path.Combine(root, "linked-backup-refused");
        foreach (var target in new[] { save, "../missing.bin", "first-alias" })
        {
            hash = await WriteArchiveAsync(archive,
                new PaxTarEntry(TarEntryType.RegularFile, "save.bin"),
                new PaxTarEntry(TarEntryType.SymbolicLink, "first-alias") { LinkName = "save.bin" },
                new PaxTarEntry(TarEntryType.SymbolicLink, "second-alias") { LinkName = target });
            await ExpectRejectedAsync(archive, hash, rejected);
        }
        hash = await WriteArchiveAsync(archive,
            new UstarTarEntry(TarEntryType.SymbolicLink, "alias") { LinkName = "data" },
            new UstarTarEntry(TarEntryType.RegularFile, "alias/child"),
            new UstarTarEntry(TarEntryType.Directory, "data"));
        try
        {
            await GameBackupRestore.StagePterodactylAsync(archive, hash, new FileInfo(archive).Length, rejected, CancellationToken.None);
            throw new InvalidOperationException("A Wings archive used a link as a restored parent directory.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { }
        Require(!Path.Exists(rejected) && !Directory.EnumerateFileSystemEntries(root, ".arcadenode-restore-*").Any(),
            "A rejected link left a partially restored volume");

        async Task WriteNodeBackup(string? exclusions = null)
        {
            await using var output = File.Create(archive);
            await using var gzip = new GZipStream(output, CompressionLevel.Fastest);
            await volume.WriteArchiveAsync(gzip, 4096, CancellationToken.None, exclusions);
        }
    }

    private static async Task<string> WriteArchiveAsync(string archive, params TarEntry[] entries)
    {
        await using (var output = File.Create(archive))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            foreach (var entry in entries) await tar.WriteEntryAsync(entry);
        await using var saved = File.OpenRead(archive);
        return Convert.ToHexString(await SHA256.HashDataAsync(saved));
    }

    private static async Task<bool> TryRestoreAsync(string archive, string hash, string target)
    {
        try
        {
            await GameBackupRestore.StageAsync(archive, hash, target, CancellationToken.None);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { return false; }
    }

    private static async Task ExpectRejectedAsync(string archive, string hash, string target) =>
        Require(!await TryRestoreAsync(archive, hash, target), "An invalid backup or destination was accepted");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
