using System.Buffers.Text;

namespace ArcadeNode.Node;

// Bounds and validates physical TAR records before the TAR reader materializes their metadata.
internal sealed class BoundedTarStream(Stream input, long maxDataBytes, int maxEntries) : Stream
{
    private const int BlockSize = 512;
    private const int MaxMetadataRecord = 64 * 1024;
    private const int MaxMetadataBytes = 32 * 1024 * 1024;
    private readonly byte[] _header = new byte[BlockSize];
    private byte[]? _metadata;
    private int _headerOffset = BlockSize;
    private int _metadataLength;
    private long _remaining;
    private long _dataBytes;
    private int _metadataBytes;
    private int _headers;
    private int _endBlocks;
    private long? _paxSize;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_headerOffset == BlockSize && _remaining == 0 && !await ReadHeaderAsync(token)) return 0;
        if (_headerOffset < BlockSize)
        {
            var count = Math.Min(buffer.Length, BlockSize - _headerOffset);
            _header.AsMemory(_headerOffset, count).CopyTo(buffer);
            _headerOffset += count;
            return count;
        }

        var maximum = (int)Math.Min(buffer.Length, _remaining);
        if (_metadataLength > 0)
        {
            _metadata!.AsMemory(_metadataLength - (int)_remaining, maximum).CopyTo(buffer);
            _remaining -= maximum;
            return maximum;
        }
        var read = await input.ReadAsync(buffer[..maximum], token);
        if (read == 0) throw new InvalidDataException("Truncated TAR entry.");
        _remaining -= read;
        return read;
    }

    private async Task<bool> ReadHeaderAsync(CancellationToken token)
    {
        var read = await input.ReadAtLeastAsync(_header, BlockSize, false, token);
        if (read == 0 && _endBlocks >= 2) return false;
        if (read != BlockSize) throw new InvalidDataException("Truncated TAR header or footer.");
        _headerOffset = 0;
        _metadataLength = 0;
        if (!_header.AsSpan().ContainsAnyExcept((byte)0))
        {
            if (_paxSize.HasValue || ++_endBlocks > 128)
                throw new InvalidDataException("Invalid TAR footer.");
            return true;
        }
        if (_endBlocks != 0) throw new InvalidDataException("Data follows the end of the TAR archive.");
        if (++_headers > maxEntries * 3 + 64) throw new LinuxGameFiles.FileLimitExceededException();
        ValidateHeaderChecksum();
        var length = ReadSize(_header.AsSpan(124, 12));
        _remaining = (length + BlockSize - 1) / BlockSize * BlockSize;
        var kind = _header[156];
        if (kind is (byte)'x' or (byte)'g' or (byte)'L')
        {
            if (length <= 0 || length > MaxMetadataRecord || _remaining > MaxMetadataBytes - _metadataBytes)
                throw new LinuxGameFiles.FileLimitExceededException();
            _metadataBytes += (int)_remaining;
            _metadataLength = (int)_remaining;
            _metadata ??= new byte[MaxMetadataRecord];
            await input.ReadExactlyAsync(_metadata.AsMemory(0, _metadataLength), token);
            if (kind is (byte)'x' or (byte)'g') ReadPax(_metadata.AsSpan(0, (int)length), kind == (byte)'g');
            return true;
        }
        if (kind is not (0 or (byte)'0' or (byte)'5') || (kind == (byte)'5' && length != 0) ||
            (_paxSize is { } pax && pax != length))
            throw new InvalidDataException("Unsupported TAR entry or inconsistent size.");
        _paxSize = null;
        if (length > maxDataBytes - _dataBytes) throw new LinuxGameFiles.FileLimitExceededException();
        _dataBytes += length;
        return true;
    }

    private void ReadPax(ReadOnlySpan<byte> content, bool global)
    {
        while (!content.IsEmpty)
        {
            var separator = content.IndexOf((byte)' ');
            if (separator <= 0 || !Utf8Parser.TryParse(content[..separator], out int size, out var consumed) ||
                consumed != separator || size <= separator + 2 || size > content.Length || content[size - 1] != (byte)'\n')
                throw new InvalidDataException("Invalid PAX record.");
            var attribute = content.Slice(separator + 1, size - separator - 2);
            var equals = attribute.IndexOf((byte)'=');
            if (equals < 1) throw new InvalidDataException("Invalid PAX attribute.");
            var key = attribute[..equals];
            if (key.StartsWith("GNU.sparse"u8) || key.SequenceEqual("SCHILY.realsize"u8))
                throw new InvalidDataException("Sparse TAR entries are unsupported.");
            if (key.SequenceEqual("size"u8))
            {
                var value = attribute[(equals + 1)..];
                if (global || _paxSize.HasValue || !Utf8Parser.TryParse(value, out long length, out consumed) ||
                    consumed != value.Length || length < 0 || length > maxDataBytes)
                    throw new InvalidDataException("Unsupported PAX size.");
                _paxSize = length;
            }
            content = content[size..];
        }
    }

    private void ValidateHeaderChecksum()
    {
        var expected = ReadOctal(_header.AsSpan(148, 8));
        var unsigned = 8 * (byte)' ';
        var signed = unsigned;
        for (var index = 0; index < _header.Length; index++)
        {
            if (index is >= 148 and < 156) continue;
            unsigned += _header[index];
            signed += unchecked((sbyte)_header[index]);
        }
        if (expected != unsigned && expected != signed) throw new InvalidDataException("Invalid TAR header checksum.");
    }

    private long ReadSize(ReadOnlySpan<byte> field)
    {
        if (field[0] < 128) return ReadOctal(field);
        if (field[0] != 128) throw new InvalidDataException("Invalid TAR size.");
        long value = 0;
        foreach (var digit in field[1..])
        {
            value = value * 256 + digit;
            if (value > maxDataBytes) throw new LinuxGameFiles.FileLimitExceededException();
        }
        return value;
    }

    private static long ReadOctal(ReadOnlySpan<byte> field)
    {
        while (!field.IsEmpty && field[0] == (byte)' ') field = field[1..];
        while (!field.IsEmpty && field[^1] is 0 or (byte)' ') field = field[..^1];
        long value = 0;
        foreach (var digit in field)
        {
            if (digit is < (byte)'0' or > (byte)'7') throw new InvalidDataException("Invalid TAR number.");
            value = value * 8 + digit - (byte)'0';
        }
        return value;
    }
}
