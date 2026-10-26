using System.Text.RegularExpressions;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class ShareTokenGeneratorTests
{
    [TestMethod]
    public void GenerateToken_ReturnsUrlSafe32CharacterToken()
    {
        var token = ShareTokenGenerator.GenerateToken();

        Assert.IsNotNull(token);
        Assert.AreEqual(32, token.Length);
        Assert.IsTrue(ShareTokenGenerator.IsWellFormedToken(token));
        StringAssert.DoesNotMatch(token, new Regex(@"[+/=]"));
    }

    [TestMethod]
    public void GenerateToken_GeneratesDistinctTokens()
    {
        var tokens = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var token = ShareTokenGenerator.GenerateToken();
            Assert.IsTrue(tokens.Add(token), $"Duplicate token generated: {token}");
        }
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow("abc", false)] // too short
    [DataRow("123456789012345", false)] // 15 chars (< 16)
    [DataRow("1234567890123456", true)] // 16 chars (min)
    [DataRow("aB3-_123456789012345678901234567", true)] // 32 chars
    [DataRow("token_with+plus", false)]
    [DataRow("token/with/slash", false)]
    [DataRow("token=with=equals", false)]
    [DataRow("token with spaces", false)]
    [DataRow("token?query=1", false)]
    public void IsWellFormedToken_ValidatesCorrectly(string? token, bool expected)
    {
        var actual = ShareTokenGenerator.IsWellFormedToken(token);
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void IsWellFormedToken_ExceedingMaxLength_ReturnsFalse()
    {
        var longToken = new string('a', 65);
        Assert.IsFalse(ShareTokenGenerator.IsWellFormedToken(longToken));

        var maxToken = new string('a', 64);
        Assert.IsTrue(ShareTokenGenerator.IsWellFormedToken(maxToken));
    }
}
