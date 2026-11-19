using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using App.Core.Authentication;
using App.Infrastructure.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class JwtTokenServiceTests
{
    private readonly JwtOptions _options = new()
    {
        Secret = "SuperSecretTestingKeyThatIsLongEnough1234567890!",
        Issuer = "AppTests",
        Audience = "AppTests",
        ExpirationMinutes = 60,
    };

    [TestMethod]
    public void GenerateToken_ProducesValidJwt()
    {
        var service = new JwtTokenService(Options.Create(_options));
        var user = new UserSummary(Guid.NewGuid(), "test@example.com", "testuser");

        var token = service.GenerateToken(user);

        Assert.IsFalse(string.IsNullOrWhiteSpace(token));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.IsNotNull(jwt.Payload.Expiration);

        var principal = service.ValidateToken(token);
        Assert.IsNotNull(principal);
        Assert.AreEqual(user.Id.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.AreEqual(user.Email, principal.FindFirst(ClaimTypes.Email)?.Value);
        Assert.AreEqual(user.Username, principal.FindFirst(ClaimTypes.Name)?.Value);
    }

    [TestMethod]
    public void GenerateToken_WithZeroExpirationMinutes_OmitsExpClaimAndValidates()
    {
        var persistentOptions = new JwtOptions
        {
            Secret = _options.Secret,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            ExpirationMinutes = 0,
        };
        var service = new JwtTokenService(Options.Create(persistentOptions));
        var user = new UserSummary(Guid.NewGuid(), "forever@example.com", "foreveruser");

        var token = service.GenerateToken(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.IsNull(jwt.Payload.Expiration);

        var principal = service.ValidateToken(token);
        Assert.IsNotNull(principal);
        Assert.AreEqual(user.Id.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.AreEqual(user.Email, principal.FindFirst(ClaimTypes.Email)?.Value);
    }

    [TestMethod]
    public void ValidateToken_WhenExpirationRequired_RejectsTokenWithoutExpClaim()
    {
        var persistentOptions = new JwtOptions
        {
            Secret = _options.Secret,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            ExpirationMinutes = 0,
        };
        var persistentService = new JwtTokenService(Options.Create(persistentOptions));
        var expiringService = new JwtTokenService(Options.Create(_options));
        var user = new UserSummary(Guid.NewGuid(), "noexp@example.com", "noexpuser");

        var nonExpiringToken = persistentService.GenerateToken(user);

        Assert.IsNull(expiringService.ValidateToken(nonExpiringToken));
    }

    [TestMethod]
    public void ValidateToken_WithExplicitlyExpiredToken_ReturnsNullEvenWhenDefaultIsPersistent()
    {
        var persistentOptions = new JwtOptions
        {
            Secret = _options.Secret,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            ExpirationMinutes = 0,
        };
        var service = new JwtTokenService(Options.Create(persistentOptions));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var expiredJwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())],
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddMinutes(-10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );
        var expiredToken = new JwtSecurityTokenHandler().WriteToken(expiredJwt);

        Assert.IsNull(service.ValidateToken(expiredToken));
    }

    [TestMethod]
    public void ValidateToken_WithTamperedToken_ReturnsNull()
    {
        var service = new JwtTokenService(Options.Create(_options));
        var user = new UserSummary(Guid.NewGuid(), "test@example.com", "testuser");

        var token = service.GenerateToken(user);
        var tamperedToken = token + "xyz";

        var principal = service.ValidateToken(tamperedToken);
        Assert.IsNull(principal);
    }
}
