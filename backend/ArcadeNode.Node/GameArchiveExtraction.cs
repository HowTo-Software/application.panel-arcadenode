using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace ArcadeNode.Node;

internal static class GameArchiveExtraction
{
    internal const string StagingPrefix = ".arcadenode-unpack-";
    private const int MaximumArchiveBytes = 512 * 1024 * 1024;
    private const int MaximumDataBytes = 512 * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const int BufferSize = 64 * 1024;
    private static readonly uint[] ZipCrcTable = BuildZipCrcTable();

    internal static bool IsStagingName(string name) => name.Length == StagingPrefix.Length + 32 &&
        name.StartsWith(StagingPrefix, StringComparison.Ordinal) &&
        name.AsSpan(StagingPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static bool IsSupported(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);

    // The caller creates a private, empty staging directory and publishes it only after success.
    internal static async Task UnpackAsync(Stream source, string archiveName, string staging, CancellationToken token)
    {
        if (source.Length > MaximumArchiveBytes) throw new LinuxGameFiles.FileLimitExceededException();
        if (source.Length == 0) throw new InvalidDataException("Empty archive file.");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var output = new StagingContents(staging, buffer.AsMemory(0, BufferSize));
            if (archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await UnpackZipAsync(source, output, token);
                return;
            }
            if (archiveName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                await UnpackTarAsync(source, output, token);
                return;
            }
            if (!IsSupported(archiveName)) throw new ArgumentException("Unsupported archive format.");
            await using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            await UnpackTarAsync(gzip, output, token);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static async Task UnpackZipAsync(Stream source, StagingContents output, CancellationToken token)
    {
        using var snapshot = await BoundedZipStream.OpenAsync(source, MaximumDataBytes, MaximumEntries, token);
        using var archive = new ZipArchive(snapshot, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var mode = (uint)entry.ExternalAttributes >> 16;
            var type = mode & 0xf000;
            if ((entry.ExternalAttributes & 0x440) != 0 || (type != 0 && type != (directory ? 0x4000u : 0x8000u)) ||
                (!directory && (entry.ExternalAttributes & 0x10) != 0))
                throw new InvalidDataException("The ZIP contains a link or a special file.");
            var path = output.Add(entry.FullName, directory);
            if (directory)
            {
                if (entry.Length != 0 || entry.Crc32 != 0) throw new InvalidDataException("Invalid ZIP directory data.");
                continue;
            }
            await using var content = entry.Open();
            await output.WriteFileAsync(path, content, entry.Length, entry.LastWriteTime, (UnixFileMode)(mode & 0x1ff), entry.Crc32, token);
        }
    }

    private static async Task UnpackTarAsync(Stream source, StagingContents output, CancellationToken token)
    {
        using var bounded = new BoundedTarStream(source, MaximumDataBytes, MaximumEntries);
        await using var archive = new TarReader(bounded, leaveOpen: true);
        while (await archive.GetNextEntryAsync(copyData: false, cancellationToken: token) is { } entry)
        {
            var directory = entry.EntryType == TarEntryType.Directory;
            if (!directory && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("The TAR contains a link or a special file.");
            var path = output.Add(entry.Name, directory);
            if (!directory)
                await output.WriteFileAsync(path, entry.DataStream ?? Stream.Null, entry.Length, entry.ModificationTime, entry.Mode, null, token);
        }
        await bounded.CopyToAsync(Stream.Null, token);
    }

    private sealed class StagingContents(string root, Memory<byte> buffer)
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private readonly string _rootPrefix = Path.GetFullPath(root + Path.DirectorySeparatorChar);
        private readonly HashSet<string> _entries = new(PathComparer);
        private readonly HashSet<string> _directories = new(PathComparer);
        private int _files;
        private long _bytes;

        public string Add(string name, bool directory)
        {
            if (directory && name.EndsWith('/')) name = name[..^1];
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (directory && name == ".")
            {
                if (!_entries.Add(".")) throw new InvalidDataException("Duplicate archive root.");
                return _rootPrefix;
            }
            if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':')
                throw new InvalidDataException("Archive entries must use relative paths.");
            var relative = GameBackupRestore.ValidateEntryName(name);
            if (relative.Split('/').Any(part => Encoding.UTF8.GetByteCount(part) > 255 || IsStagingName(part)))
                throw new InvalidDataException("The archive contains an unsupported path.");
            if (!_entries.Add(relative)) throw new InvalidDataException("Duplicate archive entry.");
            if (_entries.Count > MaximumEntries) throw new LinuxGameFiles.FileLimitExceededException();
            var path = ResolveOutputPath(relative);
            if (directory)
            {
                EnsureDirectories(relative);
                return path;
            }
            if (_directories.Contains(relative)) throw new InvalidDataException("An archive entry collides with a directory.");
            CheckEntryBudget();
            _files++;
            EnsureDirectories(Parent(relative));
            return path;
        }

        private void EnsureDirectories(string relative)
        {
            if (relative.Length == 0 || _directories.Contains(relative)) return;
            var pending = new Stack<string>();
            while (relative.Length > 0 && !_directories.Contains(relative))
            {
                pending.Push(relative);
                relative = Parent(relative);
            }
            while (pending.TryPop(out var directory))
            {
                CheckEntryBudget();
                GameBackupRestore.CreatePrivateDirectory(ResolveOutputPath(directory));
                _directories.Add(directory);
            }
        }

        private string ResolveOutputPath(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(_rootPrefix, relative));
            if (!path.StartsWith(_rootPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The archive entry is outside the staging directory.");
            return path;
        }

        private void CheckEntryBudget()
        {
            if (_files + _directories.Count >= MaximumEntries) throw new LinuxGameFiles.FileLimitExceededException();
        }

        private static string Parent(string name) => name[..Math.Max(0, name.LastIndexOf('/'))];

        public async Task WriteFileAsync(string path, Stream input, long length, DateTimeOffset modified,
            UnixFileMode mode, uint? expectedCrc, CancellationToken token)
        {
            if (length < 0 || length > MaximumDataBytes - _bytes) throw new LinuxGameFiles.FileLimitExceededException();
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var remaining = length;
                var crc = uint.MaxValue;
                while (remaining > 0)
                {
                    var chunk = buffer[..(int)Math.Min(remaining, buffer.Length)];
                    await input.ReadExactlyAsync(chunk, token);
                    if (expectedCrc.HasValue) crc = AppendZipCrc(crc, chunk.Span);
                    await output.WriteAsync(chunk, token);
                    remaining -= chunk.Length;
                }
                if (await input.ReadAsync(buffer[..1], token) != 0 || (expectedCrc.HasValue && ~crc != expectedCrc.Value))
                    throw new InvalidDataException("Archive entry differs from its recorded size or checksum.");
                await output.FlushAsync(token);
            }
            _bytes += length;
            File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | (mode & UnixFileMode.UserExecute));
        }
    }

    private static uint AppendZipCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data) crc = ZipCrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildZipCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xedb88320);
            table[index] = value;
        }
        return table;
    }
}
