using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using App.Core.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace App.Infrastructure.Authentication;

public sealed class JwtTokenService(IOptions<JwtOptions> options) : IJwtTokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public string GenerateToken(UserSummary user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(_options.Secret))
        {
            throw new InvalidOperationException("JWT Secret is not configured.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        DateTime? expires =
            _options.ExpirationMinutes > 0
                ? DateTime.UtcNow.AddMinutes(_options.ExpirationMinutes)
                : null;

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: credentials
        );

        return _tokenHandler.WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.Secret))
        {
            throw new InvalidOperationException("JWT Secret is not configured.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            RequireExpirationTime = _options.ExpirationMinutes > 0,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        try
        {
            var principal = _tokenHandler.ValidateToken(token, parameters, out var securityToken);
            if (
                securityToken is not JwtSecurityToken jwt
                || !jwt.Header.Alg.Equals(
                    SecurityAlgorithms.HmacSha256,
                    StringComparison.InvariantCultureIgnoreCase
                )
            )
            {
                return null;
            }

            return principal;
        }
        catch
        {
            return null;
        }
    }
}
