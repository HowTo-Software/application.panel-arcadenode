using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcadeNode.Backups;

internal sealed class BackupIgnoreRules(IReadOnlyList<BackupIgnoreRules.Rule> rules)
{
    public const int MaximumBytes = 32 * 1024;
    private const int MaximumPatterns = 256;
    private const string PathCharacter = "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^/\\uD800-\\uDFFF])";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal sealed record Rule(Regex Pattern, bool DirectoryOnly, bool Include);

    public static bool IsValid(string? text)
    {
        try { Compile(text); return true; }
        catch (ArgumentException) { return false; }
    }

    public static string? ForStorage(string? text) => string.IsNullOrWhiteSpace(text) ? null :
        JsonSerializer.Serialize(text.Split('\n').Select(line => line.TrimEnd('\r')));

    public static string? FromStorage(string? json) => json is null ? null :
        string.Join('\n', JsonSerializer.Deserialize<string[]>(json) ?? []);

    public static async Task<string?> ReadAsync(Stream source, CancellationToken token)
    {
        var buffer = new byte[MaximumBytes + 1];
        var length = await source.ReadAtLeastAsync(buffer, buffer.Length, false, token);
        if (length > MaximumBytes) throw new ArgumentException("Backup exclusions exceed 32 KiB.");
        var start = buffer.AsSpan(0, length).StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return length == start ? null : StrictUtf8.GetString(buffer, start, length - start);
    }

    public static BackupIgnoreRules Compile(string? text)
    {
        if (text is null) return new([]);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new ArgumentException("Backup exclusions exceed 32 KiB.");
        var rules = new List<Rule>();
        foreach (var raw in text.Split('\n'))
        {
            var line = TrimTrailingSpaces(raw.TrimEnd('\r'));
            if (line.Length == 0 || line[0] == '#') continue;
            if (rules.Count >= MaximumPatterns || line.Any(char.IsControl)) throw new ArgumentException("Invalid backup exclusion list.");
            var include = line[0] == '!';
            if (include) line = line[1..];
            var directoryOnly = line.EndsWith('/');
            if (directoryOnly) line = line[..^1];
            var rooted = line.StartsWith('/');
            if (rooted) line = line[1..];
            if (line.Split('/').Any(component => component is "" or "." or ".."))
                throw new ArgumentException("Invalid backup exclusion pattern.");

            var pattern = new StringBuilder(rooted || line.Contains('/') ? "\\A" : "(?:\\A|/)");
            AppendGlob(pattern, line);
            pattern.Append("\\z");
            try
            {
                // Archive traversal bounds path length and checks its deadline before each entry.
                // A per-match wall-clock limit can reject valid rules during cold startup or CPU throttling.
                rules.Add(new Rule(new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    Regex.InfiniteMatchTimeout), directoryOnly, include));
            }
            catch (NotSupportedException error) { throw new ArgumentException("A backup exclusion pattern is too complex.", error); }
        }
        return new(rules);
    }

    public bool Excludes(string relativePath, bool directory)
    {
        for (var index = rules.Count - 1; index >= 0; index--)
        {
            var rule = rules[index];
            if ((!rule.DirectoryOnly || directory) && rule.Pattern.IsMatch(relativePath)) return !rule.Include;
        }
        return false;
    }

    private static string TrimTrailingSpaces(string line)
    {
        var length = line.Length;
        while (length > 0 && line[length - 1] == ' ')
        {
            var backslashes = 0;
            for (var index = length - 2; index >= 0 && line[index] == '\\'; index--) backslashes++;
            if (backslashes % 2 != 0) break;
            length--;
        }
        return line[..length];
    }

    private static void AppendGlob(StringBuilder pattern, string line)
    {
        var stars = 0;
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            switch (current)
            {
                case '\\':
                    if (++index == line.Length || line[index] == '/') throw new ArgumentException("Invalid escape in a backup exclusion.");
                    pattern.Append(Regex.Escape(line[index].ToString()));
                    break;
                case '*':
                    var start = index;
                    while (index + 1 < line.Length && line[index + 1] == '*') index++;
                    stars += index - start + 1;
                    if (stars > 16) throw new ArgumentException("A backup exclusion may contain at most 16 unescaped asterisks.");
                    var wholeSegment = (start == 0 || line[start - 1] == '/') && (index + 1 == line.Length || line[index + 1] == '/');
                    if (index > start && wholeSegment)
                    {
                        if (index + 1 == line.Length) pattern.Append(".+");
                        else { pattern.Append("(?:[^/]+/)*"); index++; }
                    }
                    else pattern.Append("[^/]*");
                    break;
                case '?': pattern.Append(PathCharacter); break;
                case '[': index = AppendClass(pattern, line, index + 1); break;
                default: pattern.Append(Regex.Escape(current.ToString())); break;
            }
        }
    }

    private static int AppendClass(StringBuilder pattern, string line, int index)
    {
        pattern.Append('[');
        if (index < line.Length && line[index] is '!' or '^') { pattern.Append("^/"); index++; }
        var first = true;
        for (; index < line.Length; index++)
        {
            var current = line[index];
            if (current == ']' && !first) { pattern.Append("-[/]]"); return index; }
            var literal = current == '\\';
            if (literal && ++index < line.Length) current = line[index];
            if (index >= line.Length || current == '/') throw new ArgumentException("Invalid backup exclusion character class.");
            if (!literal && current == '[' && index + 1 < line.Length && line[index + 1] == ':')
            {
                var end = line.IndexOf(":]", index + 2, StringComparison.Ordinal);
                if (end < 0) throw new ArgumentException("Invalid backup exclusion character class.");
                pattern.Append(PosixClass(line[(index + 2)..end]));
                index = end + 1;
            }
            else if (!literal && current == '-' && !first && index + 1 < line.Length && line[index + 1] != ']') pattern.Append('-');
            else pattern.Append($"\\u{(int)current:X4}");
            first = false;
        }
        throw new ArgumentException("Unterminated backup exclusion character class.");
    }

    private static string PosixClass(string name) => name switch
    {
        "alnum" => "0-9A-Za-z", "alpha" => "A-Za-z", "blank" => "\\t ", "cntrl" => "\\u0000-\\u001f\\u007f",
        "digit" => "0-9", "graph" => "!-\\u007e", "lower" => "a-z", "print" => " -\\u007e",
        "punct" => "!-\\u002f:-@\\u005b-\\u0060\\u007b-\\u007e", "space" => "\\t-\\r ",
        "upper" => "A-Z", "xdigit" => "0-9A-Fa-f",
        _ => throw new ArgumentException("Unknown backup exclusion character class.")
    };
}
