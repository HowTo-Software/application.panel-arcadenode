using System.Buffers.Binary;

namespace ArcadeNode.Node;

// Supplies the ZIP reader with a checked, immutable central directory and a bounded payload stream.
internal sealed class BoundedZipStream(Stream input, long metadataStart, byte[] metadata) : Stream
{
    private const int EndRecordSize = 22;
    private const int DirectoryRecordSize = 46;
    private const int MaxDirectoryBytes = 24 * 1024 * 1024;
    private long _position;

    public static async Task<BoundedZipStream> OpenAsync(Stream input, long maxDataBytes, int maxEntries, CancellationToken token)
    {
        var length = input.Length;
        if (length < EndRecordSize) throw new InvalidDataException("Missing ZIP directory.");
        var tail = new byte[(int)Math.Min(length, EndRecordSize + ushort.MaxValue)];
        input.Position = length - tail.Length;
        await input.ReadExactlyAsync(tail, token);
        var endOffset = FindEndRecord(tail);
        var (directoryOffset, directoryBytes, entries) = ReadEndRecord(tail.AsSpan(endOffset));
        if (entries > maxEntries) throw new LinuxGameFiles.FileLimitExceededException();
        if ((long)directoryOffset + directoryBytes != length - tail.Length + endOffset)
            throw new InvalidDataException("Invalid ZIP directory bounds.");

        var snapshot = new byte[directoryBytes + tail.Length - endOffset];
        tail.AsSpan(endOffset).CopyTo(snapshot.AsSpan(directoryBytes));
        input.Position = directoryOffset;
        await input.ReadExactlyAsync(snapshot.AsMemory(0, directoryBytes), token);
        ValidateDirectory(snapshot.AsSpan(0, directoryBytes), entries, directoryOffset, maxDataBytes, token);
        return new BoundedZipStream(input, directoryOffset, snapshot);
    }

    private static int FindEndRecord(ReadOnlySpan<byte> tail)
    {
        for (var index = tail.Length - EndRecordSize; index >= 0; index--)
            if (Read32(tail[index..]) == 0x06054b50 &&
                Read16(tail[(index + 20)..]) == tail.Length - index - EndRecordSize)
                return index;
        throw new InvalidDataException("Missing ZIP directory.");
    }

    private static (uint Offset, int Bytes, int Entries) ReadEndRecord(ReadOnlySpan<byte> end)
    {
        var entries = Read16(end[10..]);
        var bytes = Read32(end[12..]);
        var offset = Read32(end[16..]);
        if (Read16(end[4..]) != 0 || Read16(end[6..]) != 0 || Read16(end[8..]) != entries ||
            entries == ushort.MaxValue || bytes == uint.MaxValue || offset == uint.MaxValue)
            throw new InvalidDataException("Expected a standard single-volume ZIP archive.");
        if (bytes > MaxDirectoryBytes) throw new LinuxGameFiles.FileLimitExceededException();
        return (offset, (int)bytes, entries);
    }

    private static void ValidateDirectory(ReadOnlySpan<byte> directory, int entries, uint payloadLength, long maxDataBytes,
        CancellationToken token)
    {
        long bytes = 0;
        for (var index = 0; index < entries; index++)
        {
            token.ThrowIfCancellationRequested();
            if (directory.Length < DirectoryRecordSize || Read32(directory) != 0x02014b50 ||
                (Read16(directory[8..]) & 0x2041) != 0 || Read16(directory[34..]) != 0)
                throw new InvalidDataException("Unsupported ZIP directory entry.");
            var compressed = Read32(directory[20..]);
            var uncompressed = Read32(directory[24..]);
            var offset = Read32(directory[42..]);
            var nameLength = Read16(directory[28..]);
            var recordLength = DirectoryRecordSize + nameLength + Read16(directory[30..]) + Read16(directory[32..]);
            if (nameLength is 0 or > 4096 || recordLength > directory.Length || offset >= payloadLength ||
                compressed > payloadLength - offset || uncompressed == uint.MaxValue)
                throw new InvalidDataException("Invalid ZIP entry bounds.");
            bytes += uncompressed;
            if (bytes > maxDataBytes) throw new LinuxGameFiles.FileLimitExceededException();
            directory = directory[recordLength..];
        }
        if (!directory.IsEmpty) throw new InvalidDataException("ZIP directory does not match the entry count.");
    }

    private static uint Read32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    private static ushort Read16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => metadataStart + metadata.Length;
    public override long Position
    {
        get => _position;
        set => _position = value >= 0 && value <= Length ? value : throw new IOException("Invalid ZIP stream position.");
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = checked(offset + (origin switch
    {
        SeekOrigin.Begin => 0,
        SeekOrigin.Current => Position,
        SeekOrigin.End => Length,
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    }));
    public override void Flush() => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

    public override int Read(Span<byte> buffer)
    {
        if (_position >= metadataStart) return ReadMetadata(buffer);
        input.Position = _position;
        var read = input.Read(buffer[..(int)Math.Min(buffer.Length, metadataStart - _position)]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_position >= metadataStart) return ReadMetadata(buffer.Span);
        input.Position = _position;
        var read = await input.ReadAsync(buffer[..(int)Math.Min(buffer.Length, metadataStart - _position)], token);
        _position += read;
        return read;
    }

    private int ReadMetadata(Span<byte> buffer)
    {
        var count = (int)Math.Min(Length - _position, buffer.Length);
        metadata.AsSpan((int)(_position - metadataStart), count).CopyTo(buffer);
        _position += count;
        return count;
    }
}
