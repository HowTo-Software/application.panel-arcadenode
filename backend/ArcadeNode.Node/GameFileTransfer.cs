using System.Buffers;

namespace ArcadeNode.Transfers;

internal static class GameFileTransfer
{
    public const int MaximumBytes = 512 * 1024 * 1024;
    public const int NodeUploadTimeoutSeconds = 300;
    public const int PanelUploadTimeoutSeconds = NodeUploadTimeoutSeconds + 5;
    public const int DownloadTimeoutSeconds = 600;
    private const int BufferBytes = 64 * 1024;

    public static async Task CopyExactlyAsync(Stream source, Stream destination, long length, CancellationToken token)
    {
        if (length is < 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            while (length > 0)
            {
                var chunk = buffer.AsMemory(0, (int)Math.Min(BufferBytes, length));
                await source.ReadExactlyAsync(chunk, token);
                await destination.WriteAsync(chunk, token);
                length -= chunk.Length;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
