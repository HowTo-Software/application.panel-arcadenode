namespace ServerPanel.API.Models;

public sealed class ServerMember
{
    public Guid ServerId { get; set; }
    public Guid UserId { get; set; }
    public ServerPermission Permissions { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public User User { get; set; } = null!;
}

[Flags]
public enum ServerPermission
{
    View = 0,
    ConsoleRead = 1 << 0,
    ConsoleCommand = 1 << 1,
    PowerStart = 1 << 2,
    PowerStop = 1 << 3,
    PowerRestart = 1 << 4,
    FileList = 1 << 5,
    FileRead = 1 << 6,
    FileCreate = 1 << 7,
    FileUpdate = 1 << 8,
    ScheduleRead = 1 << 9,
    ScheduleCreate = 1 << 10,
    ScheduleUpdate = 1 << 11,
    ScheduleDelete = 1 << 12,
    BackupRead = 1 << 13,
    BackupCreate = 1 << 14,
    BackupDelete = 1 << 15,
    BackupDownload = 1 << 16,
    BackupRestore = 1 << 17,
    Rename = 1 << 18,
    Assistant = 1 << 19,
    MemberRead = 1 << 20,
    MemberCreate = 1 << 21,
    MemberUpdate = 1 << 22,
    MemberDelete = 1 << 23,
    FileArchive = 1 << 24,
    FileDelete = 1 << 25,
    All = (1 << 26) - 1
}
