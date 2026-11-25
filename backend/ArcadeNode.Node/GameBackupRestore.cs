using System.Buffers;
using System.ComponentModel;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArcadeNode.Backups;

namespace ArcadeNode.Node;

public static class GameBackupRestore
{
    private const int MaximumLinkMetadata = 64 * 1024 * 1024;
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PermissionBits = (UnixFileMode)0x1ff;
    private static readonly char[] InvalidNameCharacters = Path.GetInvalidFileNameChars();

    public sealed record Receipt(int Files, int Directories, long Bytes, string ArchiveSha256,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Links = 0);

    internal const string BundleData = "data";
    internal const string BundleReceipt = "receipt.json";

    /// <summary>Verifies an ArcadeNode archive against its trusted receipt and publishes it in a new directory.</summary>
    public static Task<Receipt> StageAsync(string archivePath, string trustedSha256, string destination, CancellationToken token) =>
        StageCoreAsync(archivePath, trustedSha256, destination, bundle: false, null, token);

    internal static Task<Receipt> StageBundleAsync(string archivePath, string trustedSha256, string destination, CancellationToken token) =>
        StageCoreAsync(archivePath, trustedSha256, destination, bundle: true, null, token);

    /// <summary>Recovers a downloaded Wings archive using the recorded backup length and checksum.</summary>
    public static Task<Receipt> StagePterodactylAsync(string archivePath, string recordedChecksum, long recordedLength,
        string destination, CancellationToken token)
    {
        if (recordedLength is <= 0 or > GameBackupLimits.MaximumArchiveBytes)
            throw new ArgumentException("The backup receipt has an unsupported archive length.", nameof(recordedLength));
        return StageCoreAsync(archivePath, recordedChecksum, destination, bundle: false, recordedLength, token);
    }

    private static async Task<Receipt> StageCoreAsync(string archivePath, string checksum, string destination,
        bool bundle, long? pterodactylLength, CancellationToken token)
    {
        var legacy = pterodactylLength.HasValue;
        var verifySha1 = legacy && checksum.StartsWith("sha1:", StringComparison.OrdinalIgnoreCase);
        var hex = verifySha1 ? checksum[5..] : legacy && checksum.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? checksum[7..] : checksum;
        if (hex.Length != (verifySha1 ? 40 : 64) || !hex.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Expected a supported checksum from the backup receipt.", nameof(checksum));
        var expected = Convert.FromHexString(hex);
        var fullDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var parent = Path.GetDirectoryName(fullDestination);
        if (parent is null || (File.GetAttributes(parent) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException("Restore requires an existing private staging parent directory.");
        if (Path.Exists(fullDestination)) throw new IOException("The restore destination already exists.");
        await using var input = OpenRegularArchive(archivePath);
        var length = input.Length;
        if (length is <= 0 or > GameBackupLimits.MaximumArchiveBytes ||
            (pterodactylLength is { } recorded && length != recorded)) throw new InvalidDataException("Invalid backup archive length.");
        var validated = await ValidateArchiveAsync(input, expected, length, verifySha1, token);
        input.Position = 0;

        var staging = Path.Combine(parent, $".arcadenode-restore-{Guid.NewGuid():N}");
        CreatePrivateDirectory(staging);
        var directories = new List<(string Path, UnixFileMode Mode, DateTimeOffset Modified)>();
        var published = false;
        try
        {
            var data = bundle ? Path.Combine(staging, BundleData) : staging;
            if (bundle) CreatePrivateDirectory(data);
            var receipt = await ExtractAsync(input, validated, length, data, directories, legacy, token);
            if (legacy) directories.Sort((left, right) => left.Path.Length.CompareTo(right.Path.Length));
            for (var i = directories.Count - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                var directory = directories[i];
                Directory.SetLastWriteTimeUtc(directory.Path, directory.Modified.UtcDateTime);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory.Path, directory.Mode & PermissionBits);
            }
            if (bundle)
            {
                await using var output = new FileStream(Path.Combine(staging, BundleReceipt), FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, bufferSize: 4096, FileOptions.Asynchronous);
                await JsonSerializer.SerializeAsync(output, receipt, cancellationToken: token);
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            Publish(staging, fullDestination);
            published = true;
            return receipt;
        }
        finally
        {
            if (!published)
            {
                if (OperatingSystem.IsLinux())
                    foreach (var directory in directories)
                        File.SetUnixFileMode(directory.Path, PrivateDirectory);
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task<byte[]> ValidateArchiveAsync(FileStream input, byte[] expected, long length, bool verifySha1, CancellationToken token)
    {
        // SHA-1 is used only to compare older Wings receipts. SHA-256 binds extraction to the verified bytes.
        using var sha1 = verifySha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var chunk = buffer.AsMemory(0, (int)Math.Min(remaining, 65536));
                await input.ReadExactlyAsync(chunk, token);
                sha1?.AppendData(chunk.Span);
                sha256.AppendData(chunk.Span);
                remaining -= chunk.Length;
            }
            var archiveSha256 = sha256.GetHashAndReset();
            if (input.Position != length || input.Length != length ||
                !CryptographicOperations.FixedTimeEquals(sha1?.GetHashAndReset() ?? archiveSha256, expected))
                throw new InvalidDataException("The archive differs from the backup receipt.");
            return archiveSha256;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static FileStream OpenRegularArchive(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        if (OperatingSystem.IsLinux())
        {
            using var archives = new LinuxGameFiles(Path.GetDirectoryName(fullPath) ?? throw new IOException("Expected a backup path."));
            return archives.OpenRead(Path.GetFileName(fullPath));
        }
        if ((File.GetAttributes(fullPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException("The backup archive must be a regular file.");
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static async Task<Receipt> ExtractAsync(FileStream input, byte[] expected, long archiveLength, string staging,
        List<(string Path, UnixFileMode Mode, DateTimeOffset Modified)> directories, bool legacy, CancellationToken token)
    {
        using var hash = SHA256.Create();
        await using var hashing = new CryptoStream(input, hash, CryptoStreamMode.Read, leaveOpen: true);
        await using var gzip = new GZipStream(hashing, CompressionMode.Decompress, leaveOpen: true);
        await using var reader = new TarReader(gzip, leaveOpen: true);
        var inferred = legacy ? new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) : null;
        Dictionary<string, string>? links = null;
        var linkMetadata = 0;
        var entries = 0;
        var files = 0;
        long bytes = 0;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: token) is { } entry)
        {
            if (++entries > GameBackupLimits.MaximumEntries) throw new InvalidDataException("Backup exceeded the entry budget.");
            var isDirectory = entry.EntryType == TarEntryType.Directory;
            var isLink = entry.EntryType == TarEntryType.SymbolicLink && OperatingSystem.IsLinux() && entry.Length == 0;
            if (!isDirectory && !isLink && entry.EntryType != TarEntryType.RegularFile)
                throw new InvalidDataException("Backup contains an unsupported entry type.");
            var relative = ValidateEntryName(isDirectory && entry.Name.EndsWith('/') ? entry.Name[..^1] : entry.Name);
            var path = Path.Combine(staging, relative);
            if (links?.ContainsKey(relative) is true) throw new InvalidDataException("Backup contains a duplicate entry.");
            if (isDirectory && inferred?.Remove(path) is true)
            {
                directories.Add((path, entry.Mode, entry.ModificationTime));
                continue;
            }
            if (Path.Exists(path)) throw new InvalidDataException("Backup contains a duplicate entry.");
            var parent = Path.GetDirectoryName(path)!;
            if (inferred is not null)
                InferDirectories(parent, inferred, GameBackupLimits.MaximumEntries - files - directories.Count - (links?.Count ?? 0) - 1, token);
            else if (!Directory.Exists(parent)) throw new InvalidDataException("Backup is missing a parent directory.");

            if (isDirectory)
            {
                CreatePrivateDirectory(path);
                directories.Add((path, entry.Mode, entry.ModificationTime));
                continue;
            }

            if (isLink)
            {
                linkMetadata = CountLinkMetadata(linkMetadata, relative, entry.LinkName);
                (links ??= new(StringComparer.Ordinal)).Add(relative, entry.LinkName);
                continue;
            }

            if (entry.Length < 0 || entry.Length > GameBackupLimits.MaximumDataBytes - bytes)
                throw new InvalidDataException("Backup exceeded the data budget.");
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (entry.DataStream is { } content) await content.CopyToAsync(output, token);
                if (output.Length != entry.Length) throw new InvalidDataException("Backup entry length differs from its header.");
                await output.FlushAsync(token);
            }
            File.SetLastWriteTimeUtc(path, entry.ModificationTime.UtcDateTime);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, entry.Mode & PermissionBits);
            files++;
            bytes += entry.Length;
        }

        // TAR can finish before the compressed stream's footer is consumed.
        // A generated archive has at most one trailing zero block after the reader finishes.
        var tail = new byte[513];
        var tailLength = await gzip.ReadAtLeastAsync(tail, tail.Length, false, token);
        if (tailLength > 512 || tail.AsSpan(0, tailLength).ContainsAnyExcept((byte)0))
            throw new InvalidDataException("Backup contains extra data after the TAR entries.");
        await hashing.CopyToAsync(Stream.Null, token);
        if (input.Position != archiveLength || input.Length != archiveLength ||
            !CryptographicOperations.FixedTimeEquals(hash.Hash!, expected))
            throw new InvalidDataException("The archive changed while being restored.");

        if (links is not null)
        {
            using var volume = new LinuxGameFiles(staging);
            // Validate the entire set before creating any links. A TAR entry cannot be extracted through a link.
            foreach (var (relative, target) in links)
            {
                token.ThrowIfCancellationRequested();
                if (Path.Exists(Path.Combine(staging, relative)))
                    throw new InvalidDataException("A backup link conflicts with an extracted path.");
                volume.ValidateContainedLinkTarget(relative, target);
            }
            foreach (var (relative, target) in links)
            {
                token.ThrowIfCancellationRequested();
                File.CreateSymbolicLink(Path.Combine(staging, relative), target);
            }
        }
        return new Receipt(files, directories.Count + (inferred?.Count ?? 0), bytes, Convert.ToHexString(expected), links?.Count ?? 0);
    }

    internal static int CountLinkMetadata(int current, string name, string target)
    {
        var targetBytes = Encoding.UTF8.GetByteCount(target);
        var entryBytes = Encoding.UTF8.GetByteCount(name) + targetBytes;
        if (targetBytes is 0 or >= 4096 || entryBytes > MaximumLinkMetadata - current)
            throw new InvalidDataException("Backup exceeded the symbolic link metadata budget.");
        return current + entryBytes;
    }

    internal static void ValidateArchivedLinkTarget(IReadOnlyDictionary<string, bool?> entries, string name, string target)
    {
        var directory = name[..Math.Max(0, name.LastIndexOf('/'))];
        var parts = target.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part is "" or ".") continue;
            if (part == ".." && directory.Length > 0)
            {
                directory = directory[..Math.Max(0, directory.LastIndexOf('/'))];
                continue;
            }
            directory = directory.Length == 0 ? part : $"{directory}/{part}";
            if (!entries.TryGetValue(directory, out var isDirectory) || isDirectory is null ||
                (!isDirectory.Value && i < parts.Length - 1))
                throw new IOException("A backup link must point to a file or directory included in the backup.");
        }
    }

    private static void InferDirectories(string parent, HashSet<string> inferred, int limit, CancellationToken token)
    {
        if (inferred.Count > limit) throw new InvalidDataException("Backup exceeded the entry budget.");
        if (Directory.Exists(parent)) return;
        var missing = new Stack<string>();
        while (!Directory.Exists(parent))
        {
            if (missing.Count >= limit - inferred.Count) throw new InvalidDataException("Backup exceeded the entry budget.");
            missing.Push(parent);
            parent = Path.GetDirectoryName(parent) ?? throw new InvalidDataException("Expected an archive parent directory.");
        }
        while (missing.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            CreatePrivateDirectory(directory);
            inferred.Add(directory);
        }
    }

    internal static string ValidateEntryName(string name)
    {
        if (name.Length is 0 or > 1024 || name.Contains('\\') || name.Any(char.IsControl))
            throw new InvalidDataException("Backup contains an invalid entry path.");
        foreach (var part in name.Split('/'))
        {
            if (part is "" or "." or ".." || part.AsSpan().ContainsAny(InvalidNameCharacters) ||
                (OperatingSystem.IsWindows() && IsWindowsAlias(part)))
                throw new InvalidDataException("Backup contains an invalid entry path.");
        }
        return name;
    }

    private static bool IsWindowsAlias(string part)
    {
        var stem = part.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return part.EndsWith('.') || part.EndsWith(' ') || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(stem[3]));
    }

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(path, PrivateDirectory);
        else Directory.CreateDirectory(path);
    }

    private static void Publish(string staging, string destination)
    {
        if (!OperatingSystem.IsLinux())
        {
            Directory.Move(staging, destination);
            return;
        }

        const int currentDirectory = -100;
        const uint noReplace = 1;
        if (RenameNoReplace(currentDirectory, staging, currentDirectory, destination, noReplace) != 0)
            throw new IOException("Cannot publish the new backup directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameNoReplace(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
}
