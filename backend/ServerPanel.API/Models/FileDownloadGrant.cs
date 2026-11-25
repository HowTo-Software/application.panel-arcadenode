namespace ServerPanel.API.Models;

public sealed class FileDownloadGrant
{
    public string TokenHash { get; set; } = null!;
    public Guid ServerId { get; set; }
    public GameServer Server { get; set; } = null!;
    public string Path { get; set; } = null!;
    public Guid SessionId { get; set; }
    public AuthSession Session { get; set; } = null!;
    public string SessionVersion { get; set; } = null!;
    public Guid? MemberRevision { get; set; }
    public UserRole Role { get; set; }
    public DateTime ExpiresAt { get; set; }
}
