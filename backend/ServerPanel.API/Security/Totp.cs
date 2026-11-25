using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ServerPanel.API.Security;

public static class Totp
{
    public const string ProtectionPurpose = "ArcadeNode.Login.Totp.v1";
    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[]? DecodeSecret(string? value)
    {
        if (value is null || value.Length > 128) return null;
        var input = value.AsSpan().TrimEnd('=');
        if (input.Length is < 16 or > 104 || input.Length % 8 is 1 or 3 or 6 ||
            (input.Length != value.Length && value.Length % 8 != 0))
            return null;

        var result = new byte[input.Length * 5 / 8];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var character in input)
        {
            var digit = Base32.IndexOf(char.ToUpperInvariant(character));
            if (digit < 0)
            {
                CryptographicOperations.ZeroMemory(result);
                return null;
            }
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            result[index++] = (byte)(buffer >> bits);
            buffer &= (1 << bits) - 1;
        }
        if (buffer == 0) return result;
        CryptographicOperations.ZeroMemory(result);
        return null;
    }

    public static long? MatchStep(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        if (secret.Length is < 10 or > 65 || code is not { Length: 6 } || !code.All(char.IsAsciiDigit))
            return null;

        var current = now.ToUnixTimeSeconds() / 30;
        Span<byte> provided = stackalloc byte[6];
        Encoding.ASCII.GetBytes(code, provided);
        Span<byte> counter = stackalloc byte[8];
        Span<byte> hash = stackalloc byte[20];
        Span<byte> expected = stackalloc byte[6];
        for (var step = current + 1; step >= Math.Max(0, current - 1); step--)
        {
            if (step <= lastUsedStep) break;
            BinaryPrimitives.WriteInt64BigEndian(counter, step);
            HMACSHA1.HashData(secret, counter, hash);
            var offset = hash[^1] & 15;
            var number = (BinaryPrimitives.ReadUInt32BigEndian(hash[offset..]) & 0x7fff_ffff) % 1_000_000;
            number.TryFormat(expected, out _, "D6", CultureInfo.InvariantCulture);
            if (CryptographicOperations.FixedTimeEquals(provided, expected)) return step;
        }
        return null;
    }
}
