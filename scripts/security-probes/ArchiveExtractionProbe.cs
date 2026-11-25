using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ArcadeNode.Node;

namespace SecurityRegression;

internal static class ArchiveExtractionProbe
{
    public static async Task RunAsync(string root)
    {
        root = Directory.CreateDirectory(Path.Combine(root, "file-archive-tests")).FullName;
        var data = new byte[131073];
        new Random(78).NextBytes(data);
        foreach (var name in new[] { "game.zip", "game.tar", "game.tar.gz", "game.tgz" })
        {
            var destination = Directory.CreateDirectory(Path.Combine(root, name + ".output")).FullName;
            await using var input = name.EndsWith(".zip") ? Zip("world/data.bin", data, includeEmpty: true)
                : Tar("world/data.bin", data, gzip: !name.EndsWith(".tar"), includeEmpty: true);
            await GameArchiveExtraction.UnpackAsync(input, name, destination, CancellationToken.None);
            if (!(await File.ReadAllBytesAsync(Path.Combine(destination, "world", "data.bin"))).SequenceEqual(data) ||
                !Directory.Exists(Path.Combine(destination, "empty")) ||
                !File.Exists(Path.Combine(destination, "world", "empty.txt")))
                throw new InvalidOperationException("Extraction lost regular, empty or implicitly nested entries.");
        }

        var rejected = Directory.CreateDirectory(Path.Combine(root, "rejected")).FullName;
        var marker = Path.Combine(root, "keep.cfg");
        await File.WriteAllTextAsync(marker, "preserve");
        await using (var zip = Zip("../keep.cfg", data))
            await RejectAsync(() => GameArchiveExtraction.UnpackAsync(zip, "bad.zip", rejected, CancellationToken.None));
        await using (var tar = Tar("../keep.cfg", data, gzip: true))
            await RejectAsync(() => GameArchiveExtraction.UnpackAsync(tar, "bad.tgz", rejected, CancellationToken.None));
        await using (var link = Zip("linked", data, symbolicLink: true))
            await RejectAsync(() => GameArchiveExtraction.UnpackAsync(link, "links.zip", rejected, CancellationToken.None));

        if (await File.ReadAllTextAsync(marker) != "preserve")
            throw new InvalidOperationException("An archive escaped the staging directory.");
        await using var valid = Zip("bytes.bin", data);
        var truncated = valid.ToArray()[..^2];
        await using (var broken = new MemoryStream(truncated))
            await RejectAsync(() => GameArchiveExtraction.UnpackAsync(broken, "truncated.zip", rejected, CancellationToken.None));

        await using var oversizedZip = Zip("entry", data);
        await RejectAsync(async () => { using var rejectedZip = await BoundedZipStream.OpenAsync(oversizedZip, 8, 20, CancellationToken.None); });
        await using var oversizedTar = Tar("entry", data, gzip: false);
        using var bounded = new BoundedTarStream(oversizedTar, 8, 20);
        await RejectAsync(() => bounded.CopyToAsync(Stream.Null));

        using var crcFixture = Zip("corrupt.cfg", Encoding.UTF8.GetBytes("unique probe text"), store: true);
        var corrupted = crcFixture.ToArray();
        var offset = corrupted.AsSpan().IndexOf("unique probe text"u8);
        if (offset < 0) throw new InvalidOperationException("Expected a stored ZIP fixture.");
        corrupted[offset] ^= 1;
        await using var invalidCrc = new MemoryStream(corrupted);
        await RejectAsync(() => GameArchiveExtraction.UnpackAsync(invalidCrc, "crc.zip", rejected, CancellationToken.None));
    }

    private static MemoryStream Tar(string name, byte[] data, bool gzip, bool includeEmpty = false)
    {
        var result = new MemoryStream();
        using (var compressed = gzip ? new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true) : null)
        using (var archive = new TarWriter((Stream?)compressed ?? result, TarEntryFormat.Pax, leaveOpen: true))
        {
            using var input = new MemoryStream(data);
            archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = input });
            if (includeEmpty)
            {
                archive.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./empty/"));
                archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./world/empty.txt"));
            }
        }
        result.Position = 0;
        return result;
    }

    private static MemoryStream Zip(string name, byte[] data, bool includeEmpty = false, bool symbolicLink = false, bool store = false)
    {
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            var file = archive.CreateEntry(name, store ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
            if (symbolicLink) file.ExternalAttributes = 0xa000 << 16;
            using (var content = file.Open()) content.Write(data);
            if (includeEmpty)
            {
                archive.CreateEntry("empty/");
                archive.CreateEntry("world/empty.txt");
            }
        }
        result.Position = 0;
        return result;
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid or over-budget archive was accepted.");
    }
}
