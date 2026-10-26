using System.Security.Cryptography;

namespace App.Core.Recipes;

public static class ShareTokenGenerator
{
    public const int TokenByteLength = 24;
    public const int MinTokenLength = 16;
    public const int MaxTokenLength = 64;

    public static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[TokenByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static bool IsWellFormedToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (trimmed.Length < MinTokenLength || trimmed.Length > MaxTokenLength)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            var isValid =
                (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-'
                || c == '_';
            if (!isValid)
            {
                return false;
            }
        }

        return true;
    }
}
