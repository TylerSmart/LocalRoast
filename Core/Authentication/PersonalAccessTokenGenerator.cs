using System.Security.Cryptography;
using System.Text;

namespace App.Core.Authentication;

public readonly record struct GeneratedPersonalAccessToken(
    string PlaintextToken,
    string TokenPrefix,
    string TokenHash
);

public static class PersonalAccessTokenGenerator
{
    public const string Prefix = "lr_pat_";
    public const int SecretByteLength = 32;
    public const int DisplayPrefixLength = 15;
    public const int MinTokenLength = 32;
    public const int MaxTokenLength = 128;

    public static GeneratedPersonalAccessToken GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[SecretByteLength];
        RandomNumberGenerator.Fill(bytes);
        var secret = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var plaintextToken = $"{Prefix}{secret}";
        var tokenPrefix = plaintextToken[..Math.Min(DisplayPrefixLength, plaintextToken.Length)];
        var tokenHash = ComputeSha256Hex(plaintextToken);

        return new GeneratedPersonalAccessToken(plaintextToken, tokenPrefix, tokenHash);
    }

    public static string ComputeSha256Hex(string plaintextToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextToken);
        var trimmed = plaintextToken.Trim();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        return Convert.ToHexStringLower(hashBytes);
    }

    public static bool IsWellFormedToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (
            trimmed.Length < MinTokenLength
            || trimmed.Length > MaxTokenLength
            || !trimmed.StartsWith(Prefix, StringComparison.Ordinal)
        )
        {
            return false;
        }

        for (var i = Prefix.Length; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
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

    public static bool FixedTimeHashEquals(string expectedHexHash, string actualHexHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHexHash) || string.IsNullOrWhiteSpace(actualHexHash))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expectedHexHash.Trim().ToLowerInvariant());
        var actualBytes = Encoding.UTF8.GetBytes(actualHexHash.Trim().ToLowerInvariant());

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
