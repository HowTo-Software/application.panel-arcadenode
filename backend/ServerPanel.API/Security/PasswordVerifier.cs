using System.Text;

namespace ServerPanel.API.Security;

public static class PasswordVerifier
{
    public static bool IsSupportedHash(string? hash) =>
        hash is { Length: 60 } &&
        hash[0] == '$' && hash[1] == '2' && hash[2] is 'a' or 'b' or 'y' && hash[3] == '$' &&
        hash[4] is '0' or '1' && char.IsAsciiDigit(hash[5]) && hash[6] == '$' &&
        (hash[4] - '0') * 10 + hash[5] - '0' is >= 4 and <= 14 &&
        hash.AsSpan(7).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789./") < 0;

    public static bool IsVerifiablePassword(string? password) =>
        password is not null && !password.Contains('\0') && Encoding.UTF8.GetByteCount(password) is >= 1 and <= 72;

    public static bool Verify(string password, string hash)
    {
        if (!IsVerifiablePassword(password) || !IsSupportedHash(hash)) return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
