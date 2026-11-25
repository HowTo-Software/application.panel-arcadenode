using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ArcadeNode.Backups;
using ArcadeNode.Transfers;
using Microsoft.Win32.SafeHandles;

namespace ArcadeNode.Node;

/// <summary>Opens game data relative to a trusted, dedicated Linux volume.</summary>
public sealed class LinuxGameFiles : IDisposable
{
    // Linux x86-64 and arm64 UAPI constants.
    private const int OpenAt2 = 437;
    private const int AccessAt2 = 439;
    private const int CurrentDirectory = -100;
    private const int CloseOnExec = 0x80000;
    private const int NoFollow = 0x20000;
    private const int PathOnly = 0x200000;
    private const int EmptyPath = 0x1000;
    private const int EffectiveAccess = 0x200;
    private const int ExecuteAccess = 1;
    private const int FollowLink = 0x400;
    private const int RemoveDirectory = 0x200;
    private const int Missing = 2;
    private const int PermissionDenied = 13;
    private const int AlreadyExists = 17;
    private const int SymbolicLinkLoop = 40;
    private const uint NoReplace = 1;
    private const uint StatType = 0x0001;
    private const uint StatMode = 0x0002;
    private const uint StatLinks = 0x0004;
    private const uint StatUser = 0x0008;
    private const uint StatModificationTime = 0x0040;
    private const uint StatChangeTime = 0x0080;
    private const uint StatInode = 0x0100;
    private const uint StatSize = 0x0200;
    private const uint StatBlocks = 0x0400;
    private const ulong NoMountCrossings = 0x01;
    private const ulong NoMagicLinks = 0x02;
    private const ulong NoSymlinks = 0x04;
    private const ulong Beneath = 0x08;
    private const int FileTypeMask = 0xf000;
    private const int DirectoryType = 0x4000;
    private const int RegularFileType = 0x8000;
    private const int SymbolicLinkType = 0xa000;
    private const int PermissionBits = 0x1ff;
    private const int AllModeBits = 0xfff;
    private const int GroupOrOtherWrite = 0x12;
    private const uint GameUserId = 10001;
    private const int MaximumCopyBytes = 512 * 1024 * 1024;
    private const int MaximumArchiveInputBytes = 512 * 1024 * 1024;
    private const int MaximumArchiveEntries = 20_000;
    private const int MaximumSelectedEntries = 40;
    private const int MaximumDeleteEntries = 20_000;
    private const int MaximumDeleteDepth = 64;
    private readonly SafeFileHandle _root;
    private readonly SemaphoreSlim _edits = new(1, 1);
    private readonly SemaphoreSlim _usageScan = new(1, 1);
    private readonly byte[] _directoryCursorKey = RandomNumberGenerator.GetBytes(32);
    private VolumeUsage? _cachedUsage;

    internal LinuxGameFiles(SafeFileHandle root) => _root = root;

    public LinuxGameFiles(string trustedVolumeDirectory)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Game volume access requires Linux x64 or arm64.");
        if (!Path.IsPathFullyQualified(trustedVolumeDirectory))
            throw new ArgumentException("The configured volume directory must be absolute.", nameof(trustedVolumeDirectory));

        var options = new OpenOptions { Flags = PathOnly | CloseOnExec, Resolve = NoSymlinks | NoMagicLinks };
        var descriptor = OpenAt2Native(OpenAt2, CurrentDirectory, trustedVolumeDirectory, in options, 24);
        if (descriptor < 0) throw OpenFailure();
        _root = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            if ((Describe(_root, StatType).Mode & FileTypeMask) != DirectoryType)
                throw new IOException("The configured game volume is not a directory.");
        }
        catch
        {
            _root.Dispose();
            throw;
        }
    }

    internal void RequireManagedVolumeRoot()
    {
        var details = Describe(_root, StatUser | StatMode);
        if (details.UserId != 0 || (details.Mode & GroupOrOtherWrite) != 0)
            throw new InvalidOperationException("Managed volume roots must be owned by root and protected from other writers.");
    }

    internal SafeFileHandle? OpenManagedVolume(Guid serverId, out ulong inode)
    {
        inode = 0;
        SafeFileHandle? handle = null;
        try
        {
            handle = Resolve($"arcadenode-{serverId:N}-data");
            var details = Describe(handle, StatType | StatUser | StatInode);
            if ((details.Mode & FileTypeMask) != DirectoryType || details.UserId != GameUserId) return null;
            inode = details.Inode;
            var result = handle;
            handle = null;
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally { handle?.Dispose(); }
    }

    public FileStream OpenRead(string relativePath)
    {
        using var target = Resolve(ValidatePath(relativePath));
        var details = Describe(target, StatType | StatLinks);
        if ((details.Mode & FileTypeMask) != RegularFileType || details.Links != 1)
            throw new IOException("Only regular game files with a single link may be opened.");

        // Reopen the held inode after validation. A pathname swap cannot redirect this read.
        return File.OpenRead(DescriptorPath(target));
    }

    public bool CanExecute(string relativePath)
    {
        try
        {
            using var file = OpenRead(relativePath);
            if (CheckAccess(AccessAt2, file.SafeFileHandle, "", ExecuteAccess, EmptyPath | EffectiveAccess) == 0)
                return true;
            if (Marshal.GetLastPInvokeError() == PermissionDenied) return false;
            throw OpenFailure();
        }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: Missing })
        {
            return false;
        }
    }

    public string? ReadContainedSymbolicLink(string relativePath)
    {
        var path = ValidatePath(relativePath);
        SafeFileHandle descriptor;
        try { descriptor = Resolve(_root, path, noFollow: true); }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: Missing }) { return null; }
        using var link = descriptor;
        var details = Describe(link, StatType | StatLinks);
        if ((details.Mode & FileTypeMask) != SymbolicLinkType) return null;
        if (details.Links != 1) throw new IOException("Expected a symbolic link with a single name.");

        var buffer = new byte[4096];
        var length = ReadLinkNative(link, "", buffer, (nuint)buffer.Length);
        if (length < 0) throw OpenFailure();
        if (length == 0 || length >= buffer.Length) throw new IOException("Invalid game volume link.");
        var target = new UTF8Encoding(false, true).GetString(buffer, 0, (int)length);
        ValidateContainedLinkTarget(path, target);
        return target;
    }

    internal void ValidateContainedLinkTarget(string relativePath, string target)
    {
        var path = ValidatePath(relativePath);
        if (target.Length == 0 || Encoding.UTF8.GetByteCount(target) >= 4096 || target.StartsWith('/') ||
            target.Contains('\\') || target.Any(char.IsControl) ||
            target.Split('/').Any(GameArchiveExtraction.IsStagingName))
            throw new IOException("Expected a relative game volume link.");

        // Resolve from the pinned root without following links or crossing a mount in the target path.
        using var resolved = Resolve(path[..(path.LastIndexOf('/') + 1)] + target);
        var destination = Describe(resolved, StatType | StatLinks);
        var type = destination.Mode & FileTypeMask;
        if (type != DirectoryType && (type != RegularFileType || destination.Links != 1))
            throw new IOException("Game volume links must refer to a regular file or directory.");
    }

    public DirectoryListing List(string relativePath, CancellationToken cancellationToken, string? cursor = null, string? filter = null)
    {
        const int limit = 200;
        const int scanLimit = 4096;
        if (!GameDirectoryQuery.ValidCursor(cursor) || !GameDirectoryQuery.ValidFilter(filter))
            throw new ArgumentException("Invalid game directory cursor or filter.");
        using var target = Resolve(relativePath.Length == 0 ? "." : ValidatePath(relativePath));
        var revision = ReadDirectoryRevision(target);
        var offset = 0L;
        if (cursor is not null)
        {
            var bytes = Convert.FromHexString(cursor);
            offset = BinaryPrimitives.ReadInt64BigEndian(bytes);
            if (offset <= 0 || !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(sizeof(long)),
                SignDirectoryOffset(offset, revision, relativePath, filter))) throw new DirectoryChangedException();
        }

        var options = new OpenOptions { Flags = CloseOnExec, Resolve = Beneath | NoSymlinks | NoMagicLinks | NoMountCrossings };
        var descriptor = OpenRelative(OpenAt2, target, ".", in options, 24);
        if (descriptor < 0) throw OpenFailure();
        using var reader = new SafeFileHandle((nint)descriptor, ownsHandle: true);

        var results = new List<DirectoryEntry>();
        var scanned = 0;
        var hasMore = false;
        var nextOffset = offset;
        foreach (var entry in LinuxDirectoryEntries.Read(reader, offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned == scanLimit || results.Count == limit)
            {
                hasMore = true;
                break;
            }
            scanned++;
            nextOffset = entry.NextOffset;
            var name = entry.Name;
            if (entry.IsLink || name is null or "." or ".." || GameArchiveExtraction.IsStagingName(name) ||
                name.Any(c => c == '\\' || char.IsControl(c)) ||
                (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;
            var isDirectory = entry.IsDirectory;
            string? mode = null;
            if (IsEntryName(name))
            {
                try
                {
                    using var item = Resolve(target, name);
                    var info = Describe(item, StatType | StatMode | StatLinks);
                    isDirectory = (info.Mode & FileTypeMask) == DirectoryType;
                    if (IsChangeableMode(info)) mode = Convert.ToString(info.Mode & PermissionBits, 8).PadLeft(3, '0');
                }
                catch (IOException) { /* A disappearing or unavailable entry has no editable mode. */ }
            }
            results.Add(new DirectoryEntry(name, isDirectory, false, mode));
        }
        if (ReadDirectoryRevision(target) != revision) throw new DirectoryChangedException();
        if (!hasMore) return new(results, false);
        if (nextOffset <= 0 || nextOffset == offset) throw new IOException("Directory traversal did not advance.");
        return new(results, true, nextOffset.ToString("X16", CultureInfo.InvariantCulture) +
            Convert.ToHexString(SignDirectoryOffset(nextOffset, revision, relativePath, filter)));
    }

    private static DirectoryRevision ReadDirectoryRevision(SafeFileHandle directory)
    {
        var data = Describe(directory, StatType | StatInode | StatModificationTime | StatChangeTime);
        if ((data.Mode & FileTypeMask) != DirectoryType) throw new IOException("Expected a game directory.");
        return new(data.Inode, data.ModifiedSeconds, data.ModifiedNanoseconds, data.ChangedSeconds, data.ChangedNanoseconds);
    }

    private byte[] SignDirectoryOffset(long offset, DirectoryRevision revision, string path, string? filter) =>
        HMACSHA256.HashData(_directoryCursorKey, Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{offset}:{revision.Inode}:{revision.ModifiedSeconds}:{revision.ModifiedNanoseconds}:{revision.ChangedSeconds}:{revision.ChangedNanoseconds}:{path}\0{filter}")));

    private readonly record struct DirectoryRevision(ulong Inode, long ModifiedSeconds, uint ModifiedNanoseconds,
        long ChangedSeconds, uint ChangedNanoseconds);

    public async Task<VolumeUsage> MeasureAsync(CancellationToken token)
    {
        await _usageScan.WaitAsync(token);
        try
        {
            if (_cachedUsage is { } cached && DateTimeOffset.UtcNow - cached.SampledAt < TimeSpan.FromSeconds(30))
                return cached;

            long logicalBytes = 0;
            long allocatedBytes = 0;
            var scanned = 0;
            var directories = new Stack<string>();
            var hardLinks = new HashSet<ulong>();
            directories.Push(".");
            const uint metadata = StatType | StatLinks | StatInode | StatSize | StatBlocks;
            while (directories.TryPop(out var relativeDirectory))
            {
                token.ThrowIfCancellationRequested();
                using var directory = Resolve(relativeDirectory);
                var directoryInfo = Describe(directory, metadata);
                if ((directoryInfo.Mode & FileTypeMask) != DirectoryType) throw new IOException("Game directory changed during measurement.");
                allocatedBytes = checked(allocatedBytes + (long)directoryInfo.Blocks * 512);
                foreach (var entry in Enumerate(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (++scanned > 250000) throw new IOException("Game volume measurement exceeded the scan budget.");
                    if (entry.IsLink || GameArchiveExtraction.IsStagingName(entry.Name)) continue;
                    var relative = relativeDirectory == "." ? entry.Name : relativeDirectory + "/" + entry.Name;
                    using var file = Resolve(ValidatePath(relative));
                    var details = Describe(file, metadata);
                    if ((details.Mode & FileTypeMask) == DirectoryType)
                    {
                        directories.Push(relative);
                    }
                    else if ((details.Mode & FileTypeMask) == RegularFileType && (details.Links <= 1 || hardLinks.Add(details.Inode)))
                    {
                        logicalBytes = checked(logicalBytes + (long)details.Size);
                        allocatedBytes = checked(allocatedBytes + (long)details.Blocks * 512);
                    }
                }
            }

            return _cachedUsage = new VolumeUsage(logicalBytes, allocatedBytes, DateTimeOffset.UtcNow);
        }
        finally
        {
            _usageScan.Release();
        }
    }

    public async Task WriteArchiveAsync(Stream destination, long maxBytes, CancellationToken token, string? explicitExclusions = null)
    {
        var exclusions = BackupIgnoreRules.Compile(string.IsNullOrWhiteSpace(explicitExclusions)
            ? await ReadServerExclusionsAsync(token) : explicitExclusions);
        await using var archive = new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true);
        var directories = new Stack<string>();
        directories.Push(".");
        var archived = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var links = new List<string>();
        var entries = 0;
        long bytes = 0;
        while (directories.TryPop(out var parent))
        {
            token.ThrowIfCancellationRequested();
            using var directory = Resolve(parent);
            if ((Describe(directory, StatType).Mode & FileTypeMask) != DirectoryType)
                throw new IOException("Game directory changed during backup.");
            foreach (var entry in Enumerate(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > GameBackupLimits.MaximumEntries) throw new IOException("Backup exceeded the entry budget.");
                if (GameArchiveExtraction.IsStagingName(entry.Name)) continue;
                var relative = ValidatePath(parent == "." ? entry.Name : parent + "/" + entry.Name);
                if (exclusions.Excludes(relative, entry.IsDirectory)) continue;
                if (entry.IsLink)
                {
                    if (!archived.TryAdd(relative, null)) throw new IOException("Game entry changed during backup.");
                    links.Add(relative);
                    continue;
                }
                using var handle = Resolve(relative);
                var info = Describe(handle, StatType | StatLinks | StatSize | StatModificationTime);
                var kind = info.Mode & FileTypeMask;
                if ((kind == DirectoryType) != entry.IsDirectory || !archived.TryAdd(relative, entry.IsDirectory))
                    throw new IOException("Game entry changed during backup.");
                if (kind == DirectoryType)
                {
                    directories.Push(relative);
                    await archive.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, relative)
                    {
                        Mode = (UnixFileMode)(info.Mode & 0x1ff), ModificationTime = ModificationTime(info)
                    }, token);
                    continue;
                }
                bytes += await WriteRegularArchiveEntryAsync(archive, handle, info, relative, maxBytes - bytes, token);
            }
        }
        var linkMetadata = 0;
        foreach (var relative in links)
        {
            token.ThrowIfCancellationRequested();
            if (ReadContainedSymbolicLink(relative) is not { } target)
                throw new IOException("Game link changed during backup.");
            linkMetadata = GameBackupRestore.CountLinkMetadata(linkMetadata, relative, target);
            GameBackupRestore.ValidateArchivedLinkTarget(archived, relative, target);
            await archive.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, relative) { LinkName = target }, token);
        }
        if (archived.Count == 0) await destination.WriteAsync(new byte[1024], token);
    }

    public async Task<bool> CompressAsync(string relativeDirectory, string[] entries, string destination, CancellationToken token)
    {
        if (!IsEntryName(destination) || !destination.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            entries.Length is < 1 or > MaximumSelectedEntries || entries.Any(name => !IsEntryName(name) || name == destination) ||
            entries.Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ArgumentException("Select distinct names and a new TAR/GZip name in the same directory.");
        var directoryPath = relativeDirectory.Length == 0 ? "." : ValidatePath(relativeDirectory);
        await _edits.WaitAsync(token);
        try
        {
            using var directory = Resolve(directoryPath);
            if ((Describe(directory, StatType).Mode & FileTypeMask) != DirectoryType)
                throw new DirectoryNotFoundException("Expected a game directory.");
            if (EntryExists(directory, destination)) return false;
            return await PublishAsync(directory, destination, (stream, request) => WriteSelectionAsync(stream, directory, entries, request),
                replacing: false, token);
        }
        finally { _edits.Release(); }
    }

    private static bool EntryExists(SafeFileHandle directory, string name)
    {
        try { using var existing = Resolve(directory, name); return true; }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: Missing }) { return false; }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: SymbolicLinkLoop }) { return true; }
    }

    private static async Task WriteSelectionAsync(Stream stream, SafeFileHandle directory, string[] selected, CancellationToken token)
    {
        await using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
        await using var archive = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        var pending = new Stack<string>(selected.Reverse());
        var entryCount = selected.Length;
        long bytes = 0;
        while (pending.TryPop(out var relative))
        {
            token.ThrowIfCancellationRequested();
            using var handle = Resolve(directory, relative);
            var info = Describe(handle, StatType | StatLinks | StatSize | StatModificationTime);
            if ((info.Mode & FileTypeMask) != DirectoryType)
            {
                bytes += await WriteRegularArchiveEntryAsync(archive, handle, info, relative, MaximumArchiveInputBytes - bytes, token);
                continue;
            }

            await archive.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, relative)
            {
                Mode = (UnixFileMode)(info.Mode & 0x1ff), ModificationTime = ModificationTime(info)
            }, token);
            foreach (var entry in Enumerate(handle))
            {
                token.ThrowIfCancellationRequested();
                if (++entryCount > MaximumArchiveEntries) throw new FileLimitExceededException();
                if (GameArchiveExtraction.IsStagingName(entry.Name)) continue;
                if (entry.IsLink) throw new IOException("An archive may contain only regular files and directories.");
                pending.Push(ValidatePath(relative + '/' + entry.Name));
            }
        }
    }

    private static async Task<long> WriteRegularArchiveEntryAsync(TarWriter archive, SafeFileHandle handle, StatData info,
        string relative, long bytesRemaining, CancellationToken token)
    {
        if ((info.Mode & FileTypeMask) != RegularFileType || info.Links != 1)
            throw new IOException("An archive may contain only regular files with a single link and directories.");
        if (bytesRemaining < 0 || info.Size > (ulong)bytesRemaining) throw new FileLimitExceededException();
        await using var input = new FileStream(DescriptorPath(handle), FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        if ((ulong)length != info.Size) throw new IOException("The file changed while archiving.");
        await archive.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, relative)
        {
            DataStream = input, Mode = (UnixFileMode)(info.Mode & 0x1ff), ModificationTime = ModificationTime(info)
        }, token);
        var current = Describe(handle, StatType | StatLinks | StatSize | StatModificationTime);
        if (input.Position != length || current.Size != info.Size || current.Links != 1 || current.Mode != info.Mode ||
            current.ModifiedSeconds != info.ModifiedSeconds || current.ModifiedNanoseconds != info.ModifiedNanoseconds)
            throw new IOException("The file changed while archiving.");
        return length;
    }

    private static DateTimeOffset ModificationTime(StatData info) =>
        DateTimeOffset.FromUnixTimeSeconds(info.ModifiedSeconds).AddTicks(info.ModifiedNanoseconds / 100);

    private static bool IsEntryName(string? name) => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") &&
        !name.Contains('/') && !name.Contains('\\') && !name.Any(char.IsControl) && Encoding.UTF8.GetByteCount(name) <= 255 &&
        !GameArchiveExtraction.IsStagingName(name);

    public async Task<bool> ExtractAsync(string source, string destination, CancellationToken token)
    {
        if (!GameArchiveExtraction.IsSupported(source) || source == destination)
            throw new ArgumentException("Choose a supported archive and a new destination directory.");
        await _edits.WaitAsync(token);
        try
        {
            using var parent = OpenParent(destination, out var name);
            if (EntryExists(parent, name)) return false;
            await using var archive = OpenRead(source);
            const uint metadata = StatType | StatLinks | StatSize | StatModificationTime;
            var original = Describe(archive.SafeFileHandle, metadata);
            var temporary = GameArchiveExtraction.StagingPrefix + Guid.NewGuid().ToString("N");
            if (MakeDirectoryNative(parent, temporary, 0x1c0) != 0) throw OpenFailure(); // 0700
            var staging = Path.Combine(DescriptorPath(parent), temporary);
            var published = false;
            try
            {
                await GameArchiveExtraction.UnpackAsync(archive, source, staging, token);
                var current = Describe(archive.SafeFileHandle, metadata);
                if (current.Size != original.Size || current.Links != 1 || current.Mode != original.Mode ||
                    current.ModifiedSeconds != original.ModifiedSeconds || current.ModifiedNanoseconds != original.ModifiedNanoseconds)
                    throw new IOException("Archive changed during extraction.");
                token.ThrowIfCancellationRequested();
                if (RenameNoReplaceNative(parent, temporary, parent, name, NoReplace) != 0)
                {
                    if (Marshal.GetLastPInvokeError() == AlreadyExists) return false;
                    throw OpenFailure();
                }
                published = true;
                return true;
            }
            finally
            {
                if (!published) Directory.Delete(staging, recursive: true);
            }
        }
        finally { _edits.Release(); }
    }

    private async Task<string?> ReadServerExclusionsAsync(CancellationToken token)
    {
        FileStream input;
        try { input = OpenRead(".pteroignore"); }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: Missing }) { return null; }
        await using (input) return await BackupIgnoreRules.ReadAsync(input, token);
    }

    private static FileSystemEnumerable<DirectoryEntry> Enumerate(SafeFileHandle directory) =>
        new(DescriptorPath(directory),
            static (ref FileSystemEntry entry) => new DirectoryEntry(entry.FileName.ToString(),
                entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0),
            new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false });

    public void ValidateDirectory(string relativePath) => InspectDirectory(relativePath, StatType);

    public ushort GetDirectoryPermissions(string relativePath) =>
        (ushort)(InspectDirectory(relativePath, StatType | StatMode) & 0x1ff);

    private ushort InspectDirectory(string relativePath, uint mask)
    {
        using var directory = Resolve(ValidatePath(relativePath));
        var mode = Describe(directory, mask).Mode;
        if ((mode & FileTypeMask) != DirectoryType)
            throw new IOException("Expected a game directory.");
        return mode;
    }

    public async Task<bool> UploadNewAsync(string relativePath, Stream source, long length, CancellationToken token)
    {
        await _edits.WaitAsync(token);
        try
        {
            using var directory = OpenParent(relativePath, out var name);
            if (EntryExists(directory, name)) return false;
            return await PublishAsync(directory, name, (file, request) =>
                GameFileTransfer.CopyExactlyAsync(source, file, length, request), replacing: false, token);
        }
        finally { _edits.Release(); }
    }

    public async Task<bool> CreateDirectoryAsync(string relativePath, CancellationToken token)
    {
        await _edits.WaitAsync(token);
        try
        {
            using var parent = OpenParent(relativePath, out var name);
            token.ThrowIfCancellationRequested();
            if (MakeDirectoryNative(parent, name, 0x1e8) == 0) return true; // 0750, subject to umask.
            if (Marshal.GetLastPInvokeError() == AlreadyExists) return false;
            throw OpenFailure();
        }
        finally { _edits.Release(); }
    }

    public async Task<bool> MoveAsync(string source, string destination, CancellationToken token)
    {
        if (destination == source || destination.StartsWith(source + '/', StringComparison.Ordinal))
            throw new ArgumentException("Choose a different destination outside the source entry.");

        await _edits.WaitAsync(token);
        try
        {
            using var from = OpenParent(source, out var sourceName);
            using var to = OpenParent(destination, out var destinationName);
            using var original = Resolve(from, sourceName);
            var info = Describe(original, StatType | StatLinks);
            if ((info.Mode & FileTypeMask) != DirectoryType && ((info.Mode & FileTypeMask) != RegularFileType || info.Links != 1))
                throw new FileNotFoundException("Only regular files and directories within the game volume may be moved.");
            token.ThrowIfCancellationRequested();
            if (RenameNoReplaceNative(from, sourceName, to, destinationName, NoReplace) == 0) return true;
            if (Marshal.GetLastPInvokeError() == AlreadyExists) return false;
            throw OpenFailure();
        }
        finally { _edits.Release(); }
    }

    public async Task<bool> DeleteEntriesAsync(string relativeDirectory, string[] entries, CancellationToken token)
    {
        if (entries.Length is < 1 or > MaximumSelectedEntries || entries.Any(name => !IsEntryName(name)) ||
            entries.Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ArgumentException("Select distinct existing names in a game directory.");
        var relative = relativeDirectory.Length == 0 ? "." : ValidatePath(relativeDirectory);

        await _edits.WaitAsync(token);
        try
        {
            using var directory = Resolve(relative);
            if ((Describe(directory, StatType).Mode & FileTypeMask) != DirectoryType)
                throw new DirectoryNotFoundException("Expected a game directory.");

            // Inspect every selected tree before unlinking any directory entry.
            var remaining = MaximumDeleteEntries;
            var plan = new List<RemovalEntry>(entries.Length);
            foreach (var name in entries) plan.Add(PlanRemoval(directory, name, 1, ref remaining, token));
            foreach (var entry in plan) RemoveEntry(directory, entry, token);
            return true;
        }
        finally { _edits.Release(); }
    }

    public async Task<bool> ChangePermissionsAsync(string relativeDirectory, string[] entries, string mode, CancellationToken token)
    {
        var digits = mode.Length == 4 && mode[0] == '0' ? mode[1..] : mode;
        if (digits.Length != 3 || digits.Any(digit => digit is < '0' or > '7'))
            throw new ArgumentException("Use three octal permission digits, with an optional leading zero.", nameof(mode));
        if (entries.Length is < 1 or > MaximumSelectedEntries || entries.Any(name => !IsEntryName(name)) ||
            entries.Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new ArgumentException("Select distinct existing names in a game directory.", nameof(entries));
        var requested = Convert.ToUInt32(digits, 8);
        var relative = relativeDirectory.Length == 0 ? "." : ValidatePath(relativeDirectory);

        await _edits.WaitAsync(token);
        var targets = new List<(SafeFileHandle Handle, uint Original)>(entries.Length);
        try
        {
            using var directory = Resolve(relative);
            if ((Describe(directory, StatType).Mode & FileTypeMask) != DirectoryType)
                throw new DirectoryNotFoundException("Expected a game directory.");

            // Validate and retain each descriptor before changing any member of the batch.
            foreach (var name in entries)
            {
                token.ThrowIfCancellationRequested();
                var handle = Resolve(directory, name);
                try
                {
                    var info = Describe(handle, StatType | StatMode | StatLinks);
                    if (!IsChangeableMode(info)) throw new IOException("Expected a regular file with one link or a directory.");
                    targets.Add((handle, (uint)(info.Mode & AllModeBits)));
                }
                catch { handle.Dispose(); throw; }
            }

            token.ThrowIfCancellationRequested();
            for (var index = 0; index < targets.Count; index++)
            {
                // chmod follows the process's descriptor link to the validated inode.
                if (ChangeModeNative(DescriptorPath(targets[index].Handle), requested) == 0) continue;
                var failure = OpenFailure();
                for (var rollback = index - 1; rollback >= 0; rollback--)
                    _ = ChangeModeNative(DescriptorPath(targets[rollback].Handle), targets[rollback].Original);
                throw failure;
            }
            return true;
        }
        finally
        {
            foreach (var (handle, _) in targets) handle.Dispose();
            _edits.Release();
        }
    }

    private static bool IsChangeableMode(StatData info) => (info.Mode & FileTypeMask) == DirectoryType ||
        ((info.Mode & FileTypeMask) == RegularFileType && info.Links == 1);

    private static RemovalEntry PlanRemoval(SafeFileHandle directory, string name, int depth, ref int remaining, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (--remaining < 0 || depth > MaximumDeleteDepth) throw new FileLimitExceededException();
        if (!IsEntryName(name)) throw new IOException("Only supported game entry names can be removed.");

        using var target = Resolve(directory, name, noFollow: true);
        var info = Describe(target, StatType | StatInode);
        var kind = info.Mode & FileTypeMask;
        if (kind is RegularFileType or SymbolicLinkType) return new RemovalEntry(name, info.Inode, kind, null);
        if (kind != DirectoryType) throw new IOException("Only files, symbolic links and directories can be removed.");

        var children = new List<RemovalEntry>();
        foreach (var entry in Enumerate(target))
        {
            if (entry.Name.Contains('\ufffd')) throw new IOException("The entry name cannot be verified as UTF-8.");
            children.Add(PlanRemoval(target, entry.Name, depth + 1, ref remaining, token));
        }
        return new RemovalEntry(name, info.Inode, kind, children);
    }

    private static void RemoveEntry(SafeFileHandle directory, RemovalEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var target = Resolve(directory, entry.Name, noFollow: true);
        var current = Describe(target, StatType | StatInode);
        if (current.Inode != entry.Inode || (current.Mode & FileTypeMask) != entry.Kind)
            throw new IOException("A game entry changed after the deletion was checked.");

        if (entry.Children is { } children)
            foreach (var child in children) RemoveEntry(target, child, token);
        token.ThrowIfCancellationRequested();
        if (UnlinkNative(directory, entry.Name, entry.Kind == DirectoryType ? RemoveDirectory : 0) != 0) throw OpenFailure();
    }

    private sealed record RemovalEntry(string Name, ulong Inode, int Kind, List<RemovalEntry>? Children);

    internal void DeleteRegularFileIfExists(string relativePath, CancellationToken token)
    {
        try
        {
            using var parent = OpenParent(relativePath, out var name);
            using var file = Resolve(parent, name);
            var info = Describe(file, StatType | StatLinks);
            if ((info.Mode & FileTypeMask) != RegularFileType || info.Links != 1)
                throw new IOException("Expected a regular backup archive with a single link.");
            token.ThrowIfCancellationRequested();
            if (UnlinkNative(parent, name, 0) != 0) throw OpenFailure();
        }
        catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: Missing }) { }
    }

    public async Task<bool> CopyFileAsync(string source, string destination, CancellationToken token)
    {
        if (source == destination) throw new ArgumentException("The copy needs a different destination.");
        await _edits.WaitAsync(token);
        try
        {
            using var handle = Resolve(ValidatePath(source));
            const uint metadata = StatType | StatLinks | StatSize | StatModificationTime;
            var original = Describe(handle, metadata);
            if ((original.Mode & FileTypeMask) != RegularFileType || original.Links != 1)
                throw new IOException("Only regular game files with a single link may be copied.");
            if (original.Size > MaximumCopyBytes) throw new FileLimitExceededException();

            await using var input = new FileStream(DescriptorPath(handle), FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await PublishAsync(destination, async (file, cancellationToken) =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    var remaining = (int)original.Size;
                    while (remaining > 0)
                    {
                        var chunk = buffer.AsMemory(0, Math.Min(remaining, 65536));
                        await input.ReadExactlyAsync(chunk, cancellationToken);
                        await file.WriteAsync(chunk, cancellationToken);
                        remaining -= chunk.Length;
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }

                var current = Describe(handle, metadata);
                if (current.Size != original.Size || current.ModifiedSeconds != original.ModifiedSeconds ||
                    current.ModifiedNanoseconds != original.ModifiedNanoseconds || current.Mode != original.Mode || current.Links != 1)
                    throw new IOException("The source file changed during the copy.");
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(file.SafeFileHandle, (UnixFileMode)(original.Mode & 0x1ff));
            }, replacing: false, token);
        }
        finally { _edits.Release(); }
    }

    internal sealed class FileLimitExceededException : IOException;

    public async Task<bool> EditTextAsync(string relativePath, string text, string expectedSha256, CancellationToken cancellationToken)
    {
        if (text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > 8192 ||
            expectedSha256.Length != 64 || !expectedSha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Invalid edit content or checksum.");
        if (Path.GetExtension(relativePath).ToLowerInvariant() is not (".cfg" or ".ini" or ".json" or ".properties" or ".txt" or ".yaml" or ".yml" or ".toml"))
            throw new ArgumentException("Unsupported text file extension.");
        await _edits.WaitAsync(cancellationToken);
        try
        {
            await using var current = OpenRead(relativePath);
            if (current.Length > 8192) throw new ArgumentException("Text file is too large.");
            var previous = new byte[(int)current.Length];
            await current.ReadExactlyAsync(previous, cancellationToken);
            if (!Convert.ToHexString(SHA256.HashData(previous)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                return false;
            var utf8 = new UTF8Encoding(false, true);
            if (utf8.GetString(previous).Contains('\0')) throw new ArgumentException("File is not UTF-8 text.");
            var content = utf8.GetBytes(text);
            return await PublishAsync(relativePath, (file, token) => file.WriteAsync(content, token).AsTask(), replacing: true, cancellationToken);
        }
        finally
        {
            _edits.Release();
        }
    }

    private async Task<bool> PublishAsync(string relativePath, Func<FileStream, CancellationToken, Task> writeContent,
        bool replacing, CancellationToken cancellationToken)
    {
        using var directory = OpenParent(relativePath, out var name);
        return await PublishAsync(directory, name, writeContent, replacing, cancellationToken);
    }

    private static async Task<bool> PublishAsync(SafeFileHandle directory, string name, Func<FileStream, CancellationToken, Task> writeContent,
        bool replacing, CancellationToken cancellationToken)
    {

        var directoryFlag = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 1UL << 14 : 1UL << 16;
        var options = new OpenOptions
        {
            Flags = CloseOnExec | (1UL << 22) | directoryFlag | 1,
            Mode = 0x1a0, // 0640
            Resolve = Beneath | NoSymlinks | NoMagicLinks | NoMountCrossings
        };
        var descriptor = OpenRelative(OpenAt2, directory, ".", in options, 24);
        if (descriptor < 0) throw OpenFailure();
        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        await using var file = new FileStream(handle, FileAccess.Write);
        await writeContent(file, cancellationToken);
        await file.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // A replacement stages a complete inode under a unique name, then renames it within the same directory.
        var staging = replacing ? $".arcadenode-edit-{Guid.NewGuid():N}" : name;
        if (LinkNative(CurrentDirectory, DescriptorPath(handle), directory, staging, FollowLink) != 0)
        {
            if (!replacing && Marshal.GetLastPInvokeError() == AlreadyExists) return false;
            throw OpenFailure();
        }
        if (!replacing) return true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RenameNative(directory, staging, directory, name) != 0) throw OpenFailure();
            return true;
        }
        finally
        {
            UnlinkNative(directory, staging, 0);
        }
    }

    private SafeFileHandle OpenParent(string relativePath, out string name)
    {
        var path = ValidatePath(relativePath);
        var slash = path.LastIndexOf('/');
        name = path[(slash + 1)..];
        var directory = Resolve(slash < 0 ? "." : path[..slash]);
        try
        {
            if ((Describe(directory, StatType).Mode & FileTypeMask) != DirectoryType)
                throw new DirectoryNotFoundException("Expected a parent directory within the game volume.");
            return directory;
        }
        catch { directory.Dispose(); throw; }
    }

    private SafeFileHandle Resolve(string relativePath) => Resolve(_root, relativePath);

    private static SafeFileHandle Resolve(SafeFileHandle root, string relativePath, bool noFollow = false)
    {
        var options = new OpenOptions
        {
            Flags = PathOnly | CloseOnExec | (noFollow ? NoFollow : 0UL),
            Resolve = Beneath | NoSymlinks | NoMagicLinks | NoMountCrossings
        };
        var descriptor = OpenRelative(OpenAt2, root, relativePath, in options, 24);
        if (descriptor < 0) throw OpenFailure();

        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static string ValidatePath(string relativePath) =>
        string.IsNullOrEmpty(relativePath) || relativePath.Length > 1024 || relativePath.Contains('\\') ||
        relativePath.Any(char.IsControl) || relativePath.Split('/').Any(part => part is "" or "." or ".." || GameArchiveExtraction.IsStagingName(part))
            ? throw new ArgumentException("Expected a relative game file path.", nameof(relativePath))
            : relativePath;

    private static string DescriptorPath(SafeFileHandle handle) =>
        "/proc/self/fd/" + handle.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);

    public sealed record DirectoryEntry(string Name, bool IsDirectory, bool IsLink, string? Mode = null);
    public sealed record DirectoryListing(IReadOnlyList<DirectoryEntry> Entries, bool Truncated, string? NextCursor = null);
    public sealed class DirectoryChangedException : IOException;
    public sealed record VolumeUsage(long LogicalBytes, long AllocatedBytes, DateTimeOffset SampledAt);

    public void Dispose() { _root.Dispose(); _edits.Dispose(); _usageScan.Dispose(); }

    private static StatData Describe(SafeFileHandle descriptor, uint mask)
    {
        if (StatNative(descriptor, "", EmptyPath, mask, out var data) != 0)
            throw OpenFailure();
        if ((data.Mask & mask) != mask) throw new IOException("Required file metadata is unavailable.");
        return data;
    }

    private static IOException OpenFailure() =>
        new("Game volume access was rejected by the node.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenOptions
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatData
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanoseconds;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long OpenAt2Native(long number, int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, in OpenOptions options, nuint size);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long OpenRelative(long number, SafeFileHandle directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, in OpenOptions options, nuint size);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long CheckAccess(long number, SafeFileHandle file,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatNative(SafeFileHandle descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatData data);

    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    private static extern nint ReadLinkNative(SafeFileHandle descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] buffer, nuint size);

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int ChangeModeNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
    private static extern int LinkNative(int sourceDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        SafeFileHandle destinationDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, int flags);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameNative(SafeFileHandle currentDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string currentName,
        SafeFileHandle newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newName);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameNoReplaceNative(SafeFileHandle currentDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string currentName,
        SafeFileHandle newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newName, uint flags);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryNative(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkNative(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
}
