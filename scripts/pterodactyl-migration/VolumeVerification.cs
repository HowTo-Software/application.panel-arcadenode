using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArcadeNode.Node;
using ArcadeNode.Transfers;

internal static class VolumeVerification
{
    private const int PermissionsVersion = 2;
    internal const int ContainedLinksVersion = 3;

    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions json)
    {
        var verify = args[0] == "verify-volume";
        var game = verify ? args switch
        {
            [_, _, _, _, "--project-zomboid", _] => "--project-zomboid",
            [_, _, _, _, "--minecraft-bedrock"] => "--minecraft-bedrock",
            [_, _, _, _, "--arma-reforger"] => "--arma-reforger",
            _ => null
        } : null;
        var restore = args[0] == "restore-backup";
        var restorePterodactyl = args[0] == "restore-pterodactyl-backup";
        var captureSource = args[0] == "capture-pterodactyl-volume";
        var confirmSource = args[0] == "confirm-pterodactyl-source";
        var requiredArguments = restorePterodactyl || game == "--project-zomboid" ? 6 :
            restore || game is not null ? 5 : verify || confirmSource ? 4 : captureSource ? 2 : 3;
        if (args.Length != requiredArguments || !Guid.TryParseExact(args[1], "D", out var serverId) || serverId == Guid.Empty)
        {
            Console.Error.WriteLine("Usage: Preflight capture-volume <server-uuid> <snapshot-directory>");
            Console.Error.WriteLine("       Preflight verify-volume <server-uuid> <source-receipt.json|-> <destination-directory> [--project-zomboid <server-name> | --minecraft-bedrock | --arma-reforger]");
            Console.Error.WriteLine("       Preflight restore-backup <server-uuid> <backup.tar.gz> <trusted-sha256> <new-staging-directory>");
            Console.Error.WriteLine("       Preflight restore-pterodactyl-backup <server-uuid> <backup-uuid> <backup-receipts.json> <backup.tar.gz> <new-staging-directory>");
            Console.Error.WriteLine("       Preflight capture-pterodactyl-volume <server-uuid>");
            Console.Error.WriteLine("       Preflight confirm-pterodactyl-source <server-uuid> <source-receipt.json> --writes-fenced");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var token = cancellation.Token;
            if (captureSource)
            {
                var before = await PterodactylSource.ObserveAsync(serverId, token);
                var receipt = await CaptureAsync(serverId, before.DataDirectory, ContainedLinksVersion, token);
                if (before != await PterodactylSource.ObserveAsync(serverId, token))
                    throw new InvalidDataException("The source changed during capture.");
                Console.WriteLine(JsonSerializer.Serialize(receipt with { SourceProcess = before.Process }, json));
                return 0;
            }
            if (confirmSource)
            {
                if (args[3] != "--writes-fenced") throw new InvalidDataException("Confirm the source write fence before release.");
                var receipt = await ReadReceiptAsync(args[2], serverId, json, token);
                var confirmation = await ConfirmSourceAsync(receipt, PterodactylSource.ObserveAsync, token);
                Console.WriteLine(JsonSerializer.Serialize(confirmation, json));
                return 0;
            }
            if (restore)
            {
                var recovered = await GameBackupRestore.StageAsync(args[2], args[3], args[4], token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Mode = "restore_backup", ServerId = serverId, recovered.Files, recovered.Directories, recovered.Links,
                    recovered.Bytes, recovered.ArchiveSha256
                }, json));
                return 0;
            }
            if (restorePterodactyl)
            {
                if (!Guid.TryParseExact(args[2], "D", out var backupId) || backupId == Guid.Empty)
                    throw new InvalidDataException("Expected a Pterodactyl backup UUID.");
                var receipt = await ReadBackupReceiptAsync(args[3], serverId, backupId, json, token);
                var recovered = await GameBackupRestore.StagePterodactylAsync(args[4], receipt.Checksum, receipt.ArchiveBytes, args[5], token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Mode = "restore_pterodactyl_backup", ServerId = serverId, BackupId = backupId, receipt.Disk,
                    recovered.Files, recovered.Directories, recovered.Links, recovered.Bytes, recovered.ArchiveSha256
                }, json));
                return 0;
            }
            if (!verify)
            {
                var source = await CaptureAsync(serverId, args[2], OperatingSystem.IsLinux() ? ContainedLinksVersion : 1, token);
                Console.WriteLine(JsonSerializer.Serialize(source, json));
                return 0;
            }

            var expected = await ReadReceiptAsync(args[2], serverId, json, token);
            var destination = await CaptureAsync(serverId, args[3], expected.SchemaVersion, token);
            var matches = Matches(expected, destination);
            var report = new VerificationReport("verify_volume", matches, expected, destination);
            if (matches)
                report = game switch
                {
                    "--project-zomboid" => report with { ProjectZomboid = await ProjectZomboidVolume.ReadAsync(args[3], args[5], token) },
                    "--minecraft-bedrock" => report with { MinecraftBedrock = await RestoredGameVolumes.ReadBedrockAsync(args[3], token) },
                    "--arma-reforger" => report with { ArmaReforger = await RestoredGameVolumes.ReadReforgerAsync(args[3], token) },
                    _ => report
                };
            Console.WriteLine(JsonSerializer.Serialize(report, json));
            return matches ? 0 : 2;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException
            or UnauthorizedAccessException or NotSupportedException or OverflowException or InvalidOperationException
            or System.ComponentModel.Win32Exception or KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine(restorePterodactyl
                ? "Could not stage the Pterodactyl backup. Check the server and backup UUIDs, source receipt, archive and destination."
                : restore
                ? "Could not stage the backup. Check the trusted checksum, archive format, destination and permissions."
                : confirmSource ? "Could not confirm the stopped Pterodactyl source. Check whether its container, files or permissions changed since capture."
                : captureSource ? "Could not capture the stopped Pterodactyl source. Check the local Docker socket, container state and game volume."
                : game == "--project-zomboid" ? "Could not verify the Zomboid volume. Check the receipt, installed launcher, save name, SQLite database and port settings."
                : game == "--minecraft-bedrock" ? "Could not verify the Bedrock volume. Check the receipt, executable, world directory and listener settings."
                : game == "--arma-reforger" ? "Could not verify the Reforger volume. Check the receipt, executable, profile, addons and listener settings."
                : "Could not verify the volume. Check the receipt, snapshot paths, permissions and file types.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<BackupSourceReceipt> ReadBackupReceiptAsync(string path, Guid serverId, Guid backupId,
        JsonSerializerOptions json, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException();
        var source = await JsonSerializer.DeserializeAsync<BackupSourceReceipts>(input, json, token);
        if (source is not { SchemaVersion: 1 } || source.ServerUuid != serverId) throw new InvalidDataException();
        var receipt = source.Backups.SingleOrDefault(entry => entry?.BackupUuid == backupId);
        if (receipt is not { ArchiveBytes: > 0, Disk: "wings" or "s3", Checksum.Length: > 0 }) throw new InvalidDataException();
        return receipt;
    }

    private static async Task<VolumeReceipt> ReadReceiptAsync(string path, Guid serverId, JsonSerializerOptions json, CancellationToken token)
    {
        const int receiptLimit = 4096;
        await using var input = path == "-" ? Console.OpenStandardInput() : File.OpenRead(path);
        var buffer = new byte[receiptLimit + 1];
        var length = await input.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: token);
        if (length > receiptLimit) throw new InvalidDataException();
        var document = buffer.AsSpan(0, length);
        if (document.StartsWith("\uFEFF"u8)) document = document[3..];
        var receipt = JsonSerializer.Deserialize<VolumeReceipt>(document, json);
        if (receipt is not { SchemaVersion: 1 or PermissionsVersion or ContainedLinksVersion, TreeSha256.Length: 64 } || receipt.ServerId != serverId ||
            (receipt.SchemaVersion == ContainedLinksVersion ? receipt.Links is null or < 0 : receipt.Links is not (null or 0)) ||
            !receipt.TreeSha256.All(char.IsAsciiHexDigit) || receipt.Files < 0 || receipt.Directories < 0 || receipt.Bytes < 0)
            throw new InvalidDataException();
        return receipt;
    }

    internal static async Task<SourceFenceReceipt> ConfirmSourceAsync(VolumeReceipt receipt,
        Func<Guid, CancellationToken, Task<PterodactylSource.Observation>> observe, CancellationToken token)
    {
        var before = await observe(receipt.ServerId, token);
        if (receipt.SourceProcess != before.Process)
            throw new InvalidDataException("The source is not in the same stopped instance as the snapshot.");
        var current = await CaptureAsync(receipt.ServerId, before.DataDirectory, receipt.SchemaVersion, token);
        if (!Matches(receipt, current) || before != await observe(receipt.ServerId, token))
            throw new InvalidDataException("The stopped source changed since capture.");
        return new(2, receipt.ServerId, current.TreeSha256, before.Process, DateTimeOffset.UtcNow, true);
    }

    private static bool Matches(VolumeReceipt expected, VolumeReceipt actual) =>
        expected.SchemaVersion == actual.SchemaVersion && expected.ServerId == actual.ServerId && expected.Files == actual.Files &&
        expected.Directories == actual.Directories && expected.Bytes == actual.Bytes && (expected.Links ?? 0) == (actual.Links ?? 0) &&
        expected.TreeSha256.Equals(actual.TreeSha256, StringComparison.OrdinalIgnoreCase);

    internal static async Task<VolumeReceipt> CaptureAsync(Guid serverId, string root, int version, CancellationToken token)
    {
        var includeLinks = version == ContainedLinksVersion;
        var includePermissions = version is PermissionsVersion or ContainedLinksVersion;
        if (includePermissions && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Run verification of Linux permissions on Linux.");
        root = Path.GetFullPath(root);
        if ((File.GetAttributes(root) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new InvalidDataException();
        using var linux = OperatingSystem.IsLinux() ? new LinuxGameFiles(root) : null;
        using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        tree.AppendData(version switch
        {
            1 => "arcadenode-volume-v1\0"u8,
            PermissionsVersion => "arcadenode-volume-v2\0"u8,
            ContainedLinksVersion => "arcadenode-volume-v3\0"u8,
            _ => throw new ArgumentOutOfRangeException(nameof(version))
        });
        long directories = 0, files = 0, bytes = 0, links = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            var entries = Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains('\\') || relative.Any(char.IsControl)) throw new InvalidDataException();
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (!includeLinks || linux!.ReadContainedSymbolicLink(relative) is not { } target)
                        throw new InvalidDataException();
                    var targetBytes = Encoding.UTF8.GetBytes(target);
                    AppendEntry(tree, relative, (byte)'L', targetBytes.Length, SHA256.HashData(targetBytes), null);
                    links++;
                    continue;
                }
                if ((attributes & FileAttributes.Device) != 0) throw new InvalidDataException();
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // On Linux validate even empty directories against mount crossing and invalid path components.
                    ushort? directoryPermissions = null;
                    if (includePermissions) directoryPermissions = linux!.GetDirectoryPermissions(relative);
                    else linux?.ValidateDirectory(relative);
                    pending.Push(entry);
                    AppendEntry(tree, relative, (byte)'D', 0, [], directoryPermissions);
                    directories++;
                    continue;
                }

                await using var file = linux is null ? File.OpenRead(entry) : linux.OpenRead(relative);
                var length = file.Length;
                var permissions = ReadFilePermissions(file, includePermissions);
                var hash = await SHA256.HashDataAsync(file, token);
                if (file.Position != length || file.Length != length || permissions != ReadFilePermissions(file, includePermissions))
                    throw new InvalidDataException();
                AppendEntry(tree, relative, (byte)'F', length, hash, permissions);
                files++;
                bytes = checked(bytes + length);
            }
        }
        return new VolumeReceipt(version, serverId, files, directories, bytes, Convert.ToHexString(tree.GetHashAndReset()),
            DateTimeOffset.UtcNow, Links: includeLinks ? links : null);
    }

    private static ushort? ReadFilePermissions(FileStream file, bool includePermissions) =>
        includePermissions && OperatingSystem.IsLinux() ? (ushort)((int)File.GetUnixFileMode(file.SafeFileHandle) & 0x1ff) : null;

    private static void AppendEntry(IncrementalHash tree, string path, byte kind, long length, ReadOnlySpan<byte> hash, ushort? permissions)
    {
        var name = Encoding.UTF8.GetBytes(path);
        Span<byte> header = stackalloc byte[15];
        header[0] = kind;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], name.Length);
        BinaryPrimitives.WriteInt64BigEndian(header[5..], length);
        if (permissions is { } mode) BinaryPrimitives.WriteUInt16BigEndian(header[13..], mode);
        tree.AppendData(permissions.HasValue ? header : header[..13]);
        tree.AppendData(name);
        tree.AppendData(hash);
    }

    internal sealed record VolumeReceipt(int SchemaVersion, Guid ServerId, long Files, long Directories, long Bytes, string TreeSha256,
        DateTimeOffset? CapturedAt = null, PterodactylSource.ProcessReceipt? SourceProcess = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Links = null);
    internal sealed record SourceFenceReceipt(int SchemaVersion, Guid ServerId, string TreeSha256,
        PterodactylSource.ProcessReceipt SourceProcess, DateTimeOffset ObservedAt, bool WritesFenced);
    private sealed record VerificationReport(string Mode, bool Matches, VolumeReceipt Source, VolumeReceipt Destination,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectZomboidRestoreSettings? ProjectZomboid = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BedrockRestoreSettings? MinecraftBedrock = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReforgerRestoreSettings? ArmaReforger = null);
    private sealed record BackupSourceReceipts(int SchemaVersion, Guid ServerUuid, BackupSourceReceipt[] Backups);
    private sealed record BackupSourceReceipt(Guid BackupUuid, string Disk, long ArchiveBytes, string Checksum);
}
