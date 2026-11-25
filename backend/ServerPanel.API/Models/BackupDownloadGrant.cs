namespace ServerPanel.API.Models;

public sealed class BackupDownloadGrant
{
    public string TokenHash { get; set; } = null!;
    public Guid BackupId { get; set; }
    public Backup Backup { get; set; } = null!;
    public Guid SessionId { get; set; }
    public AuthSession Session { get; set; } = null!;
    public string SessionVersion { get; set; } = null!;
    public Guid? MemberRevision { get; set; }
    public UserRole Role { get; set; }
    public DateTime ExpiresAt { get; set; }
}
