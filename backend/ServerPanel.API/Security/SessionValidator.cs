using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ServerPanel.API.Data;

namespace ServerPanel.API.Security;

public sealed class SessionValidator(PanelDbContext db)
{
    public async Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(principal.FindFirstValue("sid"), out var sessionId)) return false;
        var stamp = principal.FindFirstValue("session_version");
        var role = principal.FindFirstValue(ClaimTypes.Role);
        var session = await db.AuthSessions.AsNoTracking().Include(s => s.User)
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);
        return session is not null && session.ExpiresAt > DateTime.UtcNow && session.User.IsActive &&
            session.User.SecurityStamp == stamp && session.User.Role.ToString() == role;
    }
}
