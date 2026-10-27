using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using App.Core.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;

namespace App.API.Authentication;

public sealed class DualBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<JwtOptions> jwtOptions,
    IOidcService oidcService,
    IAuthService authService,
    IMemoryCache cache
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly JwtSecurityTokenHandler TokenHandler = new();
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = null;
        string? authorizationHeader = Request.Headers[HeaderNames.Authorization];
        if (
            !string.IsNullOrWhiteSpace(authorizationHeader)
            && authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
        {
            token = authorizationHeader["Bearer ".Length..].Trim();
        }
        else if (
            Request.Query.TryGetValue("access_token", out var accessTokenFromQuery)
            && !string.IsNullOrWhiteSpace(accessTokenFromQuery)
        )
        {
            token = accessTokenFromQuery.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        if (
            token.StartsWith(
                PersonalAccessTokenGenerator.Prefix,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return AuthenticateResult.Fail(
                "Personal access tokens are only valid on the /api/mcp endpoint."
            );
        }

        // 1. Try local application JWT first
        if (TokenHandler.CanReadToken(token))
        {
            var jwt = TokenHandler.ReadJwtToken(token);
            if (string.Equals(jwt.Issuer, _jwtOptions.Issuer, StringComparison.OrdinalIgnoreCase))
            {
                var secretKey = string.IsNullOrWhiteSpace(_jwtOptions.Secret)
                    ? "App_Development_Super_Secret_Jwt_Key_2026!#*$"
                    : _jwtOptions.Secret;

                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = _jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = _jwtOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };

                try
                {
                    var principal = TokenHandler.ValidateToken(token, validationParameters, out _);
                    var ticket = new AuthenticationTicket(principal, Scheme.Name);
                    return AuthenticateResult.Success(ticket);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Local JWT token validation failed.");
                    return AuthenticateResult.Fail("Invalid local JWT token.");
                }
            }
        }

        // 2. Try External Provider token (Google opaque access token ya29..., external ID token, etc.)
        var cacheKey = $"ext_token_principal_{token}";
        if (cache.TryGetValue(cacheKey, out UserSummary? cachedUser) && cachedUser is not null)
        {
            var ticket = CreateExternalTicket(cachedUser);
            return AuthenticateResult.Success(ticket);
        }

        try
        {
            var externalPrincipal = await oidcService.ValidateIdTokenAsync(
                token,
                Context.RequestAborted
            );
            if (externalPrincipal is not null)
            {
                var userSummary = await authService.ProvisionOrGetExternalUserAsync(
                    externalPrincipal,
                    Context.RequestAborted
                );

                // Cache for 15 minutes to avoid querying the external provider on every request
                cache.Set(cacheKey, userSummary, TimeSpan.FromMinutes(15));

                var ticket = CreateExternalTicket(userSummary);
                return AuthenticateResult.Success(ticket);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "External token authentication failed.");
        }

        return AuthenticateResult.Fail("Invalid or expired token.");
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.Append(HeaderNames.WWWAuthenticate, "Bearer error=\"invalid_token\"");
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    private AuthenticationTicket CreateExternalTicket(UserSummary user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("sub", user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Username),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, Scheme.Name);
    }
}
