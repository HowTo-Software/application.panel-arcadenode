using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using ServerPanel.API.Data;
using ServerPanel.API.DTOs;
using ServerPanel.API.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ServerPanel.API.Security;
using ServerPanel.API.Diagnostics;

namespace ServerPanel.API.Services;

public class AuthService : IAuthService
{
    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly PanelDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;
    private readonly IDataProtector _totpProtector;

    public AuthService(PanelDbContext context, IConfiguration configuration, ILogger<AuthService> logger,
        IDataProtectionProvider dataProtection)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _totpProtector = dataProtection.CreateProtector(Totp.ProtectionPurpose);
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request)
    {
        if (!PasswordVerifier.IsVerifiablePassword(request.Password)) return null;
        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Username == request.Username || u.Email == request.Username);

        if (user == null)
        {
            return null;
        }

        // Reject while the account is inside an active lockout window.
        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
        {
            _logger.LogWarning("Login attempt for locked-out user {UserId} (until {LockoutEnd})",
                user.Id, user.LockoutEnd.Value);
            return null;
        }

        if (!user.IsActive) return null;

        if (!PasswordVerifier.Verify(request.Password, user.PasswordHash))
        {
            await RecordFailedLoginAsync(user.Id);
            return null;
        }

        if (user.ProtectedTotpSecret is { } protectedSecret)
        {
            var step = VerifyTotp(user, protectedSecret, request.TotpCode);
            if (step is null)
            {
                await RecordFailedLoginAsync(user.Id);
                return null;
            }

            var now = DateTime.UtcNow;
            var consumed = await _context.Users.Where(u => u.Id == user.Id && u.IsActive &&
                u.SecurityStamp == user.SecurityStamp && u.ProtectedTotpSecret == protectedSecret &&
                (u.LockoutEnd == null || u.LockoutEnd <= now) &&
                (u.LastTotpStep == null || u.LastTotpStep < step.Value))
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastTotpStep, step.Value));
            if (consumed != 1) return null;
        }

        // A successful login clears any prior failure / lockout state.
        if (user.FailedLoginAttempts != 0 || user.LockoutEnd != null)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
        }

        user.LastLoginAt = DateTime.UtcNow;
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());

        return await CreateSessionAsync(user);
    }

    private long? VerifyTotp(User user, string protectedSecret, string? code)
    {
        byte[]? secret;
        try
        {
            secret = Totp.DecodeSecret(_totpProtector.Unprotect(protectedSecret));
        }
        catch (CryptographicException)
        {
            return null;
        }
        if (secret is null) return null;
        try
        {
            return Totp.MatchStep(secret, code, DateTimeOffset.UtcNow, user.LastTotpStep);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private Task RecordFailedLoginAsync(Guid userId)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(LockoutDuration);
        return _context.Users.Where(u => u.Id == userId && (u.LockoutEnd == null || u.LockoutEnd <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(u => u.LockoutEnd, u => u.FailedLoginAttempts + 1 >= MaxFailedLoginAttempts ? until : u.LockoutEnd)
                .SetProperty(u => u.FailedLoginAttempts, u => u.FailedLoginAttempts + 1 >= MaxFailedLoginAttempts ? 0 : u.FailedLoginAttempts + 1));
    }

    public async Task<(AuthResponse? Response, string? Error)> RegisterAsync(RegisterRequest request)
    {
        if (!new ServerPanel.API.Validation.PasswordComplexityAttribute().IsValid(request.Password))
            return (null, "Password does not meet the security policy.");
        // Validate invite code
        var invite = await _context.InviteCodes
            .FirstOrDefaultAsync(i => i.Code == request.InviteCode);

        if (invite == null || !invite.IsValid)
        {
            return (null, "Invalid or expired invite code");
        }

        // Check if username or email already exists
        if (await _context.Users.AnyAsync(u => u.Username == request.Username))
        {
            return (null, "Username already exists");
        }

        if (await _context.Users.AnyAsync(u => u.Email == request.Email))
        {
            return (null, "Email already exists");
        }

        var user = new User
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName ?? "",
            LastName = request.LastName ?? "",
            Role = UserRole.User,
            IsActive = true
        };

        _context.Users.Add(user);

        // Increment invite code usage
        invite.TimesUsed++;
        invite.ConcurrencyVersion = Guid.NewGuid();

        try { await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync()); }
        catch (DbUpdateException exception)
        {
            DiagnosticLog.Write(_logger, DiagnosticPoints.DatabaseWrite, exception);
            _context.ChangeTracker.Clear();
            return (null, "Registration could not be completed. The invitation may have been consumed.");
        }

        return (await CreateSessionAsync(user), null);
    }

    public async Task<UserDto?> GetUserByIdAsync(Guid id)
    {
        var user = await _context.Users.FindAsync(id);
        return user == null ? null : MapToDto(user);
    }

    public async Task<PagedResult<UserDto>> GetAllUsersAsync(int page = 1, int pageSize = 50)
    {
        (page, pageSize) = ServerPanel.API.Common.Pagination.Resolve(page, pageSize);
        var query = _context.Users.OrderByDescending(u => u.CreatedAt);

        var total = await query.CountAsync();
        var skip = ServerPanel.API.Common.Pagination.Skip(page, pageSize);
        var users = await query.Skip(skip).Take(pageSize).ToListAsync();

        return new PagedResult<UserDto>(users.Select(MapToDto).ToList(), page, pageSize, total);
    }

    public async Task<bool> UpdateUserAsync(Guid id, string? firstName, string? lastName, string? email)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return false;

        if (!string.IsNullOrEmpty(firstName)) user.FirstName = firstName;
        if (!string.IsNullOrEmpty(lastName)) user.LastName = lastName;
        if (!string.IsNullOrEmpty(email))
        {
            if (await _context.Users.AnyAsync(u => u.Email == email && u.Id != id))
            {
                return false;
            }
            user.Email = email;
        }

        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    public async Task<bool> ChangePasswordAsync(Guid id, string currentPassword, string newPassword)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return false;

        if (!PasswordVerifier.Verify(currentPassword, user.PasswordHash))
        {
            return false;
        }

        if (!new ServerPanel.API.Validation.PasswordComplexityAttribute().IsValid(newPassword)) return false;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        var sessions = await _context.AuthSessions.Where(session => session.UserId == id).ToListAsync();
        _context.AuthSessions.RemoveRange(sessions);
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    public async Task<bool> DeleteUserAsync(Guid id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return false;

        _context.Users.Remove(user);
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return true;
    }

    public async Task LogoutAsync(Guid userId, Guid sessionId)
    {
        await _context.AuthSessions.Where(s => s.UserId == userId && s.Id == sessionId).ExecuteDeleteAsync();
    }

    private async Task<AuthResponse> CreateSessionAsync(User user)
    {
        var old = await _context.AuthSessions.Where(s => s.UserId == user.Id).OrderByDescending(s => s.CreatedAt).ToListAsync();
        _context.AuthSessions.RemoveRange(old.Where(s => s.ExpiresAt <= DateTime.UtcNow).Concat(old.Where(s => s.ExpiresAt > DateTime.UtcNow).Skip(9)).Distinct());
        var session = new AuthSession { UserId = user.Id, ExpiresAt = DateTime.UtcNow.AddMinutes(30) };
        _context.AuthSessions.Add(session);
        await DiagnosticLog.RunAsync(_logger, DiagnosticPoints.DatabaseWrite, () => _context.SaveChangesAsync());
        return new AuthResponse(GenerateJwtToken(user, session), MapToDto(user), session.ExpiresAt);
    }

    private string GenerateJwtToken(User user, AuthSession session)
    {
        var jwtKey = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT signing key is not configured.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("sid", session.Id.ToString()),
            new Claim("session_version", user.SecurityStamp),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("firstName", user.FirstName),
            new Claim("lastName", user.LastName)
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"] ?? "ArcadeNode",
            audience: _configuration["Jwt:Audience"] ?? "ArcadeNodeUsers",
            claims: claims,
            expires: session.ExpiresAt,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static UserDto MapToDto(User user) => new(
        user.Id,
        user.Username,
        user.Email,
        user.FirstName,
        user.LastName,
        user.Role,
        user.IsActive,
        user.CreatedAt,
        user.LastLoginAt
    );
}
