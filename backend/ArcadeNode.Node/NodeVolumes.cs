using System.Diagnostics;

namespace ArcadeNode.Node;

internal sealed class NodeVolumes : IDisposable
{
    private const int ManagedCacheCapacity = 256;
    private readonly Dictionary<Guid, LinuxGameFiles> _volumes = [];
    private readonly List<string> _paths = [];
    private readonly LinuxGameFiles? _managedRoot;
    private readonly Dictionary<Guid, ManagedVolume> _managed = [];
    private readonly object _gate = new();

    public NodeVolumes(IReadOnlyDictionary<Guid, string> volumePaths, string? managedDirectory)
    {
        if ((volumePaths.Count == 0 && managedDirectory is null) || volumePaths.ContainsKey(Guid.Empty))
            throw new InvalidOperationException("Configure the game volumes assigned to this node.");

        string Register(string path)
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Expected an absolute game volume directory.");
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Overlaps(fullPath)) throw new InvalidOperationException("Each game volume and managed root must have a separate path.");
            _paths.Add(fullPath);
            return fullPath;
        }

        try
        {
            foreach (var (id, path) in volumePaths)
                _volumes.Add(id, new LinuxGameFiles(Register(path)));

            if (managedDirectory is not null)
            {
                _managedRoot = new LinuxGameFiles(Register(managedDirectory));
                _managedRoot.RequireManagedVolumeRoot();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool Overlaps(string path) => _paths.Any(existing =>
        existing == path || IsDescendant(path, existing) || IsDescendant(existing, path));

    private static bool IsDescendant(string path, string directory) =>
        path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + '/', StringComparison.Ordinal);

    public Lease? Open(Guid id)
    {
        if (_volumes.TryGetValue(id, out var configured)) return new Lease(configured);
        if (_managedRoot is null || id == Guid.Empty) return null;

        lock (_gate)
        {
            var directory = _managedRoot.OpenManagedVolume(id, out var inode);
            try
            {
                _managed.TryGetValue(id, out var entry);
                // The root descriptor and same-mount resolution keep inode comparisons on one filesystem.
                if (entry is not null && (directory is null || entry.Inode != inode))
                {
                    if (entry.Active != 0) return null;
                    Drop(entry);
                    entry = null;
                }
                if (directory is null) return null;

                if (entry is null)
                {
                    if (_managed.Count == ManagedCacheCapacity)
                    {
                        var oldestIdle = _managed.Values.Where(volume => volume.Active == 0).MinBy(volume => volume.LastUsed);
                        if (oldestIdle is null) return null;
                        Drop(oldestIdle);
                    }

                    entry = new ManagedVolume(id, new LinuxGameFiles(directory), inode);
                    directory = null;
                    _managed.Add(id, entry);
                }

                entry.Active++;
                entry.LastUsed = Stopwatch.GetTimestamp();
                return new Lease(entry.Files, () => { lock (_gate) entry.Active--; });
            }
            finally { directory?.Dispose(); }
        }
    }

    private void Drop(ManagedVolume volume)
    {
        _managed.Remove(volume.Id);
        volume.Files.Dispose();
    }

    public void Dispose()
    {
        foreach (var volume in _volumes.Values) volume.Dispose();
        lock (_gate)
        {
            foreach (var volume in _managed.Values) volume.Files.Dispose();
            _managedRoot?.Dispose();
        }
    }

    private sealed class ManagedVolume(Guid id, LinuxGameFiles files, ulong inode)
    {
        public Guid Id { get; } = id;
        public LinuxGameFiles Files { get; } = files;
        public ulong Inode { get; } = inode;
        public int Active { get; set; }
        public long LastUsed { get; set; }
    }

    internal sealed class Lease(LinuxGameFiles volume, Action? release = null) : IDisposable
    {
        private Action? _release = release;
        public LinuxGameFiles Volume { get; } = volume;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
