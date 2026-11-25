using ArcadeNode.Node;

namespace SecurityRegression;

internal static class FileDeletionProbe
{
    public static async Task RunAsync(string root)
    {
        var data = Directory.CreateDirectory(Path.Combine(root, "delete-volume"));
        var outside = Directory.CreateDirectory(Path.Combine(root, "delete-outside"));
        var nested = Directory.CreateDirectory(Path.Combine(data.FullName, "world", "nested"));
        var log = Path.Combine(data.FullName, "old.log");
        var keep = Path.Combine(outside.FullName, "keep.dat");
        await File.WriteAllTextAsync(log, "discard");
        await File.WriteAllTextAsync(keep, "outside volume");
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "save.dat"), "old world");
        Directory.CreateSymbolicLink(Path.Combine(nested.FullName, "directory-link"), outside.FullName);
        File.CreateSymbolicLink(Path.Combine(data.FullName, "file-link"), keep);
        Directory.CreateSymbolicLink(Path.Combine(data.FullName, "parent-link"), outside.FullName);
        using var files = new LinuxGameFiles(data.FullName);

        await RejectAsync<IOException>(() => files.DeleteEntriesAsync("", ["old.log", "missing"], default));
        await RejectAsync<ArgumentException>(() => files.DeleteEntriesAsync("", ["old.log", ".."], default));
        await RejectAsync<IOException>(() => files.DeleteEntriesAsync("parent-link", ["keep.dat"], default));
        await RejectAsync<OperationCanceledException>(() => files.DeleteEntriesAsync("", ["old.log"], new CancellationToken(true)));
        var deep = Directory.CreateDirectory(Path.Combine(data.FullName, "too-deep"));
        for (var level = 0; level < 65; level++) deep = deep.CreateSubdirectory("d");
        await RejectAsync<LinuxGameFiles.FileLimitExceededException>(() => files.DeleteEntriesAsync("", ["old.log", "too-deep"], default));
        Require(File.Exists(log) && deep.Exists && File.Exists(keep), "A rejected selection changed the game tree or a link target.");

        Require(await files.DeleteEntriesAsync("", ["old.log", "world", "file-link", "parent-link"], default),
            "Linux did not remove the checked selection.");
        Require(!File.Exists(log) && !Directory.Exists(nested.FullName) && !File.Exists(Path.Combine(data.FullName, "file-link")) &&
            !Directory.Exists(Path.Combine(data.FullName, "parent-link")) && await File.ReadAllTextAsync(keep) == "outside volume",
            "The bounded deletion left selected entries or followed their links.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException("Linux deletion accepted an invalid selection.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
