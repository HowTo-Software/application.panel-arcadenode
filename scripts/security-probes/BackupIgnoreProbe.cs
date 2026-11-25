using System.Text;
using ArcadeNode.Backups;

namespace SecurityRegression;

internal static class BackupIgnoreProbe
{
    public static async Task RunAsync()
    {
        var rules = BackupIgnoreRules.Compile("# exclusions\r\n/cache/\r\nlogs/**\r\n*.tmp\r\n!world/keep.tmp\r\n" +
            "**/snapshots/**/*.bak\r\n\\#local\r\n\\!saved\r\nspaces\\ \r\ntrailing  \r\njob[!a-c].log\r\n" +
            "[[:digit:]].cfg\r\nemoji?.txt\r\na[[:print:]]b\r\n");
        var samples = new (string Path, bool Directory, bool Excluded)[]
        {
            ("cache", true, true), ("cache", false, false), ("world/cache", true, false),
            ("logs", true, false), ("logs/old/secret", false, true), ("world/logs/info", false, false),
            ("temp.tmp", false, true), ("world/data.tmp", false, true), ("world/keep.tmp", false, false),
            ("snapshots/world.bak", false, true), ("root/snapshots/daily/world.bak", false, true),
            ("world/#local", false, true), ("!saved", false, true), ("spaces ", false, true),
            ("spaces", false, false), ("trailing", false, true), ("trailing ", false, false),
            ("jobd.log", false, true), ("jobb.log", false, false), ("0.cfg", false, true), ("A.cfg", false, false),
            ("emoji😀.txt", false, true), ("azb", false, true), ("a/b", false, false)
        };
        foreach (var (path, directory, excluded) in samples)
            if (rules.Excludes(path, directory) != excluded) throw new InvalidOperationException($"Unexpected exclusion result for {path}.");

        if (BackupIgnoreRules.IsValid("bad[") || BackupIgnoreRules.IsValid(string.Join('\n', Enumerable.Repeat("*.tmp", 257))) ||
            BackupIgnoreRules.IsValid(new string('*', 17)) || BackupIgnoreRules.IsValid("private/../save") ||
            BackupIgnoreRules.IsValid(new string('ç', 17 * 1024))) throw new InvalidOperationException("Invalid exclusion list was accepted.");

        const string explicitRules = "logs/\r\n!logs/.keep";
        if (BackupIgnoreRules.FromStorage(BackupIgnoreRules.ForStorage(explicitRules)) != "logs/\n!logs/.keep" ||
            BackupIgnoreRules.ForStorage(" \r\n") is not null) throw new InvalidOperationException("Exclusion storage changed its fallback or rules.");

        using var bom = new MemoryStream([0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("*.tmp")]);
        if (await BackupIgnoreRules.ReadAsync(bom, CancellationToken.None) != "*.tmp")
            throw new InvalidOperationException("A UTF-8 BOM became part of the exclusion pattern.");
    }
}
