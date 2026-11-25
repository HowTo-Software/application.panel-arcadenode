using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ArcadeNode.Node;

internal static class LinuxDirectoryEntries
{
    private const int BufferSize = 16 * 1024;
    private const int NameOffset = 19;
    private static readonly UTF8Encoding Names = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal readonly record struct Entry(string? Name, byte Type, long NextOffset)
    {
        public bool IsDirectory => Type == 4; // DT_DIR
        public bool IsLink => Type == 10; // DT_LNK
    }

    public static IEnumerable<Entry> Read(SafeFileHandle directory, long offset)
    {
        if (offset != 0 && Seek(directory, offset, 0) != offset) throw ReadFailure();
        var syscall = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 217 : 61;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var bytes = ReadNative(syscall, directory, buffer, BufferSize);
                if (bytes == 0) yield break;
                if (bytes < 0 || bytes > BufferSize) throw ReadFailure();
                for (var index = 0; index < bytes;)
                {
                    var entry = Parse(buffer.AsSpan(index, (int)bytes - index), out var recordLength);
                    index += recordLength;
                    yield return entry;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static Entry Parse(ReadOnlySpan<byte> record, out int recordLength)
    {
        // Linux x64 and arm64 use the same little-endian linux_dirent64 layout.
        if (record.Length <= NameOffset) throw new IOException("Invalid Linux directory entry.");
        recordLength = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        if (recordLength <= NameOffset || recordLength > record.Length) throw new IOException("Invalid Linux directory entry length.");
        var name = record[NameOffset..recordLength];
        var terminator = name.IndexOf((byte)0);
        if (terminator <= 0) throw new IOException("Invalid Linux directory entry name.");
        if (terminator > 255) return new(null, record[18], BinaryPrimitives.ReadInt64LittleEndian(record[8..]));
        string? decoded;
        try { decoded = Names.GetString(name[..terminator]); }
        catch (DecoderFallbackException) { decoded = null; }
        return new(decoded, record[18], BinaryPrimitives.ReadInt64LittleEndian(record[8..]));
    }

    private static IOException ReadFailure() =>
        new("Could not read the game directory.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    private static extern long Seek(SafeFileHandle directory, long offset, int whence);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long ReadNative(long syscall, SafeFileHandle directory, [Out] byte[] buffer, nuint size);
}
