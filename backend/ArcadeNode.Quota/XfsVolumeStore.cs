using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcadeNode.Quota;

internal sealed class QuotaException(string message) : Exception(message);

[SupportedOSPlatform("linux")]
internal sealed class XfsVolumeStore : IAsyncDisposable
{
    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode ManagedRootMode =
        PrivateDirectory | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    private const int ProjectInherit = 0x00000200;
    private const int QuotaBlockBytes = 512;
    private static readonly Regex VolumeName = new(@"\Aarcadenode-[0-9a-f]{32}-data\z", RegexOptions.NonBacktracking);
    private static readonly Regex HostPath = new(@"\A(/[a-zA-Z0-9_.-]+)+\z", RegexOptions.NonBacktracking);

    private readonly string _root;
    private readonly string _volumes;
    private readonly string _metadata;
    private readonly FileStream _ownership;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _projectId = 9999;

    private sealed record VolumeState(int Project, long Size, bool Ready, bool Removed);
    internal sealed record Volume(string Name, string Mountpoint);

    private XfsVolumeStore(string root, string volumes, string metadata, FileStream ownership)
    {
        _root = root;
        _volumes = volumes;
        _metadata = metadata;
        _ownership = ownership;
    }

    public static async Task<XfsVolumeStore> OpenAsync(string root)
    {
        RequireHostPath(root);
        await RequireRootDirectoryAsync(root, privateDirectory: false, CancellationToken.None);
        var mount = File.ReadLines("/proc/self/mountinfo").FirstOrDefault(line =>
            line.Split(' ', 7) is [_, _, _, "/", var path, _, _] && path == root);
        if (mount is null || !mount.Contains(" - xfs ", StringComparison.Ordinal))
            throw new QuotaException("The configured quota root must be a dedicated XFS mount.");

        var metadata = Path.Combine(root, ".arcadenode-quota");
        var volumes = Path.Combine(root, "volumes");
        var store = new XfsVolumeStore(root, volumes, metadata, await OpenMetadataAsync(metadata));
        try
        {
            await store.RequireEnforcementAsync(CancellationToken.None);
            Directory.CreateDirectory(volumes, ManagedRootMode);
            await RequireRootDirectoryAsync(volumes, privateDirectory: false, CancellationToken.None);
            File.SetUnixFileMode(volumes, ManagedRootMode);
            var projects = new HashSet<int>();
            foreach (var name in store.MetadataNames())
            {
                var state = store.ReadState(name) ?? throw new QuotaException("Quota storage metadata is unavailable.");
                if (!projects.Add(state.Project))
                    throw new QuotaException("Quota storage contains duplicate project identifiers.");
                store._projectId = Math.Max(store._projectId, state.Project);
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(volumes))
            {
                RequireVolumeDirectory(path);
                var name = RequireVolumeName(Path.GetFileName(path));
                if (store.ReadState(name) is not { Removed: false })
                    throw new QuotaException("A quota volume directory has no matching metadata.");
            }
            return store;
        }
        catch
        {
            await store.DisposeAsync();
            throw;
        }
    }

    private static async Task<FileStream> OpenMetadataAsync(string path)
    {
        Directory.CreateDirectory(path, PrivateDirectory);
        await RequireRootDirectoryAsync(path, privateDirectory: true, CancellationToken.None);
        return new FileStream(Path.Combine(path, "driver.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static async Task RequirePrivateSocketDirectoryAsync(string socket)
    {
        RequireHostPath(socket);
        await RequireRootDirectoryAsync(Path.GetDirectoryName(socket)!, privateDirectory: true, CancellationToken.None);
    }

    public async Task CreateAsync(string? input, Dictionary<string, string>? options, CancellationToken token)
    {
        var name = RequireVolumeName(input);
        if (options is not { Count: 1 } || !options.TryGetValue("size", out var requested) ||
            !long.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size < 1024 * 1024 || size % QuotaBlockBytes != 0)
            throw new QuotaException("Supply one aligned size option in bytes.");

        await _operations.WaitAsync(token);
        try
        {
            await RequireEnforcementAsync(token);
            var path = VolumePath(name);
            var staging = StagingPath(name);
            var state = ReadState(name);
            if (state is null || state.Removed)
            {
                if (EntryExists(path) || EntryExists(staging))
                    throw new QuotaException("An unmanaged path already exists for this volume.");
                state = new VolumeState(checked(++_projectId), size, Ready: false, Removed: false);
                WriteState(name, state);
            }
            if (state.Size != size)
                throw new QuotaException("The volume was already assigned a different quota.");

            if (!state.Ready)
            {
                if (EntryExists(path))
                {
                    if (EntryExists(staging)) throw new QuotaException("Conflicting paths exist for an unfinished volume.");
                    RequireVolumeDirectory(path);
                    if (!Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        // Resume directories left by older drivers that initialized them in the public root.
                        Directory.Move(path, staging);
                        SyncDirectory(_volumes);
                        SyncDirectory(_metadata);
                    }
                    else
                    {
                        // A published directory can acquire data before the ready metadata is committed.
                        await RequireQuotaAsync(path, state, token);
                        await RunAsync("chown", token, "10001:10001", path);
                        File.SetUnixFileMode(path, PrivateDirectory);
                    }
                }
                if (!EntryExists(path))
                {
                    Directory.CreateDirectory(staging, PrivateDirectory);
                    RequireVolumeDirectory(staging);
                    if (Directory.EnumerateFileSystemEntries(staging).Any())
                        throw new QuotaException("An unfinished volume must be empty before quota initialization.");
                    await XfsQuotaAsync(token, $"project -s -d 0 -p {staging} {state.Project}", $"limit -p bhard={size} {state.Project}");
                    await RequireQuotaAsync(staging, state, token);
                    await RunAsync("chown", token, "10001:10001", staging);
                    File.SetUnixFileMode(staging, PrivateDirectory);
                    // The file node can discover the directory as soon as it enters the managed root.
                    Directory.Move(staging, path);
                }
                SyncDirectory(_volumes);
                WriteState(name, state with { Ready = true });
                return;
            }

            await RequireQuotaAsync(path, state, token);
        }
        finally { _operations.Release(); }
    }

    public async Task<Volume?> GetAsync(string? input, CancellationToken token)
    {
        if (input is null || !VolumeName.IsMatch(input)) return null;
        var name = input;
        await _operations.WaitAsync(token);
        try
        {
            if (ReadState(name) is not { Ready: true, Removed: false } state) return null;
            var path = VolumePath(name);
            await RequireEnforcementAsync(token);
            await RequireQuotaAsync(path, state, token);
            return new Volume(name, path);
        }
        finally { _operations.Release(); }
    }

    public async Task<string> GetMountpointAsync(string? name, CancellationToken token) =>
        (await GetAsync(name, token))?.Mountpoint ?? throw new QuotaException("The quota volume was not found.");

    public async Task<IReadOnlyList<Volume>> ListAsync(CancellationToken token)
    {
        await _operations.WaitAsync(token);
        try
        {
            return MetadataNames().Where(name => ReadState(name) is { Ready: true, Removed: false })
                .Select(name => new Volume(name, VolumePath(name))).ToArray();
        }
        finally { _operations.Release(); }
    }

    public async Task RemoveAsync(string? input, CancellationToken token)
    {
        var name = RequireVolumeName(input);
        await _operations.WaitAsync(token);
        try
        {
            if (ReadState(name) is not { Removed: false } state) return;
            var path = VolumePath(name);
            var staging = StagingPath(name);
            if (EntryExists(path) && EntryExists(staging))
                throw new QuotaException("Conflicting paths exist for a quota volume.");
            if (EntryExists(staging)) path = staging;
            if (EntryExists(path))
            {
                RequireVolumeDirectory(path);
                if (Directory.EnumerateFileSystemEntries(path).Any())
                    throw new QuotaException("The volume contains data and must be retained.");
                Directory.Delete(path, recursive: false);
                SyncDirectory(Path.GetDirectoryName(path)!);
            }
            // Keep the project identifier reserved across restarts, including an interrupted removal.
            WriteState(name, state with { Ready = false, Removed = true });
        }
        finally { _operations.Release(); }
    }

    private static void RequireHostPath(string path)
    {
        if (!HostPath.IsMatch(path) || Path.GetFullPath(path) != path)
            throw new QuotaException("Use a canonical Linux path without spaces for quota storage and sockets.");
    }

    private static async Task RequireRootDirectoryAsync(string path, bool privateDirectory, CancellationToken token)
    {
        RequireVolumeDirectory(path);
        var mode = File.GetUnixFileMode(path);
        var forbidden = privateDirectory
            ? UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
              UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
            : UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if ((mode & forbidden) != 0 || (await RunAsync("stat", token, "-c", "%u", path)).Trim() != "0")
            throw new QuotaException("Quota storage and socket parents must be controlled by root.");
    }

    private static void RequireVolumeDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || directory.LinkTarget is not null)
            throw new QuotaException("The expected quota directory is unavailable.");
    }

    private static string RequireVolumeName(string? name) =>
        name is not null && VolumeName.IsMatch(name)
            ? name : throw new QuotaException("Expected the ArcadeNode volume name for one server UUID.");

    private string VolumePath(string name) => Path.Combine(_volumes, name);
    private string StagingPath(string name) => Path.Combine(_metadata, name + ".pending");
    private string MetadataPath(string name) => Path.Combine(_metadata, name + ".json");
    private static bool EntryExists(string path) => Path.Exists(path) || new DirectoryInfo(path).LinkTarget is not null;

    private IEnumerable<string> MetadataNames() =>
        Directory.EnumerateFiles(_metadata, "*.json").Select(path => RequireVolumeName(Path.GetFileNameWithoutExtension(path)));

    private VolumeState? ReadState(string name)
    {
        var path = MetadataPath(name);
        if (!File.Exists(path)) return null;
        var state = JsonSerializer.Deserialize<VolumeState>(File.ReadAllText(path));
        if (state is null || state.Project < 10000 || state.Size <= 0 || state.Size % QuotaBlockBytes != 0 ||
            (state.Ready && state.Removed))
            throw new QuotaException("Invalid quota storage metadata.");
        return state;
    }

    private void WriteState(string name, VolumeState state)
    {
        var temporary = Path.Combine(_metadata, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, state);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, MetadataPath(name), overwrite: true);
            SyncDirectory(_metadata);
        }
        finally { File.Delete(temporary); }
    }

    private async Task RequireEnforcementAsync(CancellationToken token)
    {
        var state = await XfsQuotaAsync(token, "state -p");
        if (!state.Contains($"Project quota state on {_root} (", StringComparison.Ordinal) ||
            !state.Split('\n').Any(line => line.Trim() == "Accounting: ON") ||
            !state.Split('\n').Any(line => line.Trim() == "Enforcement: ON"))
            throw new QuotaException("XFS project quota accounting and enforcement must be enabled.");
    }

    private async Task RequireQuotaAsync(string path, VolumeState state, CancellationToken token)
    {
        RequireVolumeDirectory(path);
        var attributes = await RunAsync("xfs_io", token, "-c", "stat", path);
        var fields = attributes.Split('\n').Select(line => line.Trim()).ToArray();
        var prefix = "fsxattr.xflags = 0x";
        var flags = fields.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2);
        if (!fields.Contains($"fsxattr.projid = {state.Project}", StringComparer.Ordinal) || flags is not { Length: > 2 } ||
            !uint.TryParse(flags.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var settings) ||
            (settings & ProjectInherit) == 0)
            throw new QuotaException("The volume directory does not inherit its assigned XFS project quota.");

        var quota = await XfsQuotaAsync(token, $"dump -p -L {state.Project} -U {state.Project}");
        var row = quota.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .SingleOrDefault(parts => parts.Length == 5 && parts[0] == state.Project.ToString(CultureInfo.InvariantCulture));
        if (row is null ||
            !long.TryParse(row[2], NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ||
            limit != state.Size / QuotaBlockBytes)
            throw new QuotaException("The volume hard limit does not match the configured size.");
    }

    private Task<string> XfsQuotaAsync(CancellationToken token, params string[] commands)
    {
        var arguments = new List<string> { "-x", "-D", "/dev/null", "-P", "/dev/null" };
        foreach (var command in commands)
        {
            arguments.Add("-c");
            arguments.Add(command);
        }
        arguments.Add(_root);
        return RunAsync("xfs_quota", token, arguments.ToArray());
    }

    private static async Task<string> RunAsync(string tool, CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new QuotaException("A quota utility could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("A quota utility did not complete in time.");
        }
        var result = await output;
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(await errors))
            throw new QuotaException($"The {tool} utility could not apply or verify the volume quota.");
        return result;
    }

    private static void SyncDirectory(string path)
    {
        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle((nint)Open(path, 0), ownsHandle: true);
        if (handle.IsInvalid || Sync(handle) != 0)
            throw new IOException("Could not sync quota metadata to disk.");
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Sync(Microsoft.Win32.SafeHandles.SafeFileHandle handle);

    public async ValueTask DisposeAsync()
    {
        await _ownership.DisposeAsync();
        _operations.Dispose();
    }
}
