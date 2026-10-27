using System.Text.RegularExpressions;
using App.Core.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class PersonalAccessTokenGeneratorTests
{
    [TestMethod]
    public void GenerateToken_ReturnsPrefixed50CharacterToken()
    {
        var generated = PersonalAccessTokenGenerator.GenerateToken();

        Assert.IsNotNull(generated.PlaintextToken);
        Assert.IsTrue(generated.PlaintextToken.StartsWith("lr_pat_", StringComparison.Ordinal));
        Assert.AreEqual(50, generated.PlaintextToken.Length);
        Assert.AreEqual(15, generated.TokenPrefix.Length);
        Assert.IsTrue(generated.TokenPrefix.StartsWith("lr_pat_", StringComparison.Ordinal));
        Assert.AreEqual(64, generated.TokenHash.Length);
        Assert.IsTrue(PersonalAccessTokenGenerator.IsWellFormedToken(generated.PlaintextToken));
        StringAssert.DoesNotMatch(generated.PlaintextToken, new Regex(@"[+/=]"));
    }

    [TestMethod]
    public void GenerateToken_GeneratesDistinctTokensAndHashes()
    {
        var tokens = new HashSet<string>();
        var hashes = new HashSet<string>();

        for (var i = 0; i < 50; i++)
        {
            var generated = PersonalAccessTokenGenerator.GenerateToken();
            Assert.IsTrue(
                tokens.Add(generated.PlaintextToken),
                $"Duplicate token: {generated.PlaintextToken}"
            );
            Assert.IsTrue(
                hashes.Add(generated.TokenHash),
                $"Duplicate hash: {generated.TokenHash}"
            );
        }
    }

    [TestMethod]
    public void ComputeSha256Hex_MatchesExpectedDeterministicHash()
    {
        const string token = "lr_pat_TestSecretKey1234567890abcdefghijklmnopqrstuv";
        var hash1 = PersonalAccessTokenGenerator.ComputeSha256Hex(token);
        var hash2 = PersonalAccessTokenGenerator.ComputeSha256Hex(token);

        Assert.AreEqual(hash1, hash2);
        Assert.AreEqual(64, hash1.Length);
        StringAssert.Matches(hash1, new Regex("^[0-9a-f]{64}$"));
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow("lr_pat_", false)] // too short
    [DataRow("lr_pat_short", false)] // 12 chars (< 32)
    [DataRow("lr_pat_1234567890123456789012345", true)] // 32 chars (min)
    [DataRow("lr_pat_1234567890123456789012345678901234567890123", true)] // 50 chars
    [DataRow("not_lr_pat_1234567890123456789012345", false)] // wrong prefix
    [DataRow("lr_pat_with+plus12345678901234567890", false)]
    [DataRow("lr_pat_with/slash1234567890123456789", false)]
    [DataRow("lr_pat_with=equal1234567890123456789", false)]
    [DataRow("lr_pat_with space1234567890123456789", false)]
    public void IsWellFormedToken_ValidatesCorrectly(string? token, bool expected)
    {
        var actual = PersonalAccessTokenGenerator.IsWellFormedToken(token);
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void FixedTimeHashEquals_ComparesHashesSafely()
    {
        const string hashA = "a1b2c3d4e5f67890123456789012345678901234567890123456789012345678";
        const string hashB = "A1B2C3D4E5F67890123456789012345678901234567890123456789012345678";
        const string hashC = "b1b2c3d4e5f67890123456789012345678901234567890123456789012345678";

        Assert.IsTrue(PersonalAccessTokenGenerator.FixedTimeHashEquals(hashA, hashB));
        Assert.IsFalse(PersonalAccessTokenGenerator.FixedTimeHashEquals(hashA, hashC));
        Assert.IsFalse(PersonalAccessTokenGenerator.FixedTimeHashEquals(hashA, null!));
    }
}
