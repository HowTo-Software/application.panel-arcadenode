using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ArcadeNode.Backups;

namespace ArcadeNode.Node;

internal sealed class NodeBackupStore(string root, string? restoreRoot, int creationGiB = GameBackupLimits.DefaultCreationGiB)
{
    private readonly long _creationLimit = GameBackupLimits.CreationBytes(creationGiB);
    public bool CanRestore => restoreRoot is not null;

    private string ArchivePath(Guid serverId, Guid backupId) =>
        Path.Combine(root, serverId.ToString("N"), backupId.ToString("N") + ".tar.gz");

    public FileStream OpenArchive(Guid serverId, Guid backupId) => new(ArchivePath(serverId, backupId), FileMode.Open,
        FileAccess.Read, FileShare.Read, bufferSize: 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void DeleteArchive(Guid serverId, Guid backupId, CancellationToken token)
    {
        using var storage = new LinuxGameFiles(root);
        storage.DeleteRegularFileIfExists($"{serverId:N}/{backupId:N}.tar.gz", token);
    }

    public async Task<GameBackupRestore.Receipt> StageRestoreAsync(Guid serverId, Guid backupId, string sha256, CancellationToken token)
    {
        var parent = Path.Combine(restoreRoot!, serverId.ToString("N"));
        GameBackupRestore.CreatePrivateDirectory(parent);
        var destination = Path.Combine(parent, backupId.ToString("N"));
        if (!Directory.Exists(destination))
        {
            try { return await GameBackupRestore.StageBundleAsync(ArchivePath(serverId, backupId), sha256, destination, token); }
            catch (IOException) when (Directory.Exists(destination)) { }
        }

        using var bundle = new LinuxGameFiles(destination);
        bundle.ValidateDirectory(GameBackupRestore.BundleData);
        await using var input = bundle.OpenRead(GameBackupRestore.BundleReceipt);
        if (input.Length > 4096) throw new InvalidDataException("Invalid restore receipt.");
        var receipt = await JsonSerializer.DeserializeAsync<GameBackupRestore.Receipt>(input, cancellationToken: token);
        if (receipt is null || !sha256.Equals(receipt.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared restore differs from the requested backup.");
        return receipt;
    }

    public async Task<BackupReceipt> CreateAsync(Guid serverId, Guid backupId, LinuxGameFiles volume, string? exclusions, CancellationToken token)
    {
        var directory = Path.Combine(root, serverId.ToString("N"));
        Directory.CreateDirectory(directory);
        var final = ArchivePath(serverId, backupId);
        if (!File.Exists(final))
        {
            var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                long size;
                string sha256;
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 65536, useAsync: true))
                {
                    using var hasher = SHA256.Create();
                    await using var hashing = new CryptoStream(output, hasher, CryptoStreamMode.Write, leaveOpen: true);
                    await using (var compressed = new GZipStream(hashing, CompressionLevel.Fastest, leaveOpen: true))
                        await volume.WriteArchiveAsync(compressed, _creationLimit, token, exclusions);
                    await hashing.FlushFinalBlockAsync(token);
                    await output.FlushAsync(token);
                    size = output.Length;
                    if (size is <= 0 or > GameBackupLimits.MaximumArchiveBytes)
                        throw new IOException("The compressed backup exceeded the archive budget.");
                    sha256 = Convert.ToHexString(hasher.Hash!);
                }
                token.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, final);
                    return new BackupReceipt(size, sha256);
                }
                catch (IOException) when (File.Exists(final)) { }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        await using var saved = File.OpenRead(final);
        var digest = await SHA256.HashDataAsync(saved, token);
        return new BackupReceipt(saved.Length, Convert.ToHexString(digest));
    }

    public sealed record BackupReceipt(long Size, string Sha256);
}
