using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BCrypt.Net;

namespace Groovra.Auth.Microservice.Services;

public class TwoFactorService
{
    private static readonly char[] Base32Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    public string GenerateSecretKey()
    {
        byte[] bytes = new byte[20];
        RandomNumberGenerator.Fill(bytes);
        StringBuilder sb = new StringBuilder(32);
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(Base32Chars[bytes[i] % Base32Chars.Length]);
        }
        return sb.ToString();
    }

    public string GenerateOtpAuthUri(string email, string secret)
    {
        string encodedEmail = Uri.EscapeDataString(email);
        string encodedIssuer = Uri.EscapeDataString("Groovra");
        return $"otpauth://totp/{encodedIssuer}:{encodedEmail}?secret={secret}&issuer={encodedIssuer}&algorithm=SHA1&digits=6&period=30";
    }

    public bool VerifyTotpCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
            return false;

        code = code.Trim().Replace(" ", "").Replace("-", "");
        if (code.Length != 6 || !code.All(char.IsDigit))
            return false;

        byte[] secretBytes = Base32Decode(secret);
        if (secretBytes.Length == 0) return false;

        long unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long currentStep = unixTimestamp / 30;

        for (long step = currentStep - 1; step <= currentStep + 1; step++)
        {
            string expectedCode = GenerateTotpForStep(secretBytes, step);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expectedCode), 
                    Encoding.UTF8.GetBytes(code)))
            {
                return true;
            }
        }

        return false;
    }

    public (List<string> PlainCodes, string HashedJson) GenerateRecoveryCodes(int count = 8)
    {
        var plainCodes = new List<string>();
        var hashedCodes = new List<string>();

        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[8];
            RandomNumberGenerator.Fill(bytes);
            var sb = new StringBuilder(8);
            for (int j = 0; j < 8; j++)
            {
                sb.Append(chars[bytes[j] % chars.Length]);
            }
            string rawCode = sb.ToString();
            plainCodes.Add(rawCode);
            hashedCodes.Add(BCrypt.Net.BCrypt.HashPassword(rawCode));
        }

        string json = JsonSerializer.Serialize(hashedCodes);
        return (plainCodes, json);
    }

    public bool VerifyAndConsumeRecoveryCode(Models.User user, string code)
    {
        if (string.IsNullOrWhiteSpace(user.TwoFactorRecoveryCodesJson) || string.IsNullOrWhiteSpace(code))
            return false;

        code = code.Trim().ToUpperInvariant();
        List<string>? hashedCodes;
        try
        {
            hashedCodes = JsonSerializer.Deserialize<List<string>>(user.TwoFactorRecoveryCodesJson);
        }
        catch
        {
            return false;
        }

        if (hashedCodes is null || hashedCodes.Count == 0) return false;

        for (int i = 0; i < hashedCodes.Count; i++)
        {
            if (BCrypt.Net.BCrypt.Verify(code, hashedCodes[i]))
            {
                hashedCodes.RemoveAt(i);
                user.TwoFactorRecoveryCodesJson = JsonSerializer.Serialize(hashedCodes);
                return true;
            }
        }

        return false;
    }

    private static string GenerateTotpForStep(byte[] secretBytes, long step)
    {
        byte[] stepBytes = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(stepBytes);
        }

        using var hmac = new HMACSHA1(secretBytes);
        byte[] hash = hmac.ComputeHash(stepBytes);

        int offset = hash[hash.Length - 1] & 0x0F;
        int binaryCode = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);

        int otp = binaryCode % 1_000_000;
        return otp.ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        input = input.TrimEnd('=').ToUpperInvariant();
        if (string.IsNullOrEmpty(input)) return Array.Empty<byte>();

        List<byte> bytes = new List<byte>();
        int buffer = 0;
        int bitsLeft = 0;

        foreach (char c in input)
        {
            int val = Array.IndexOf(Base32Chars, c);
            if (val < 0) continue;

            buffer = (buffer << 5) | val;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        return bytes.ToArray();
    }
}
