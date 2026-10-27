using System.Security.Claims;
using System.Text.Encodings.Web;
using App.Core.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace App.API.Authentication;

public sealed class PersonalAccessTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPersonalAccessTokenService personalAccessTokenService
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
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

        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        if (!token.StartsWith(PersonalAccessTokenGenerator.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail(
                "Personal access token (lr_pat_...) required for /api/mcp."
            );
        }

        var validated = await personalAccessTokenService.ValidateAndTouchTokenAsync(
            token,
            Context.RequestAborted
        );

        if (validated is null)
        {
            return AuthenticateResult.Fail("Invalid, revoked, or expired personal access token.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, validated.UserId.ToString()),
            new Claim("sub", validated.UserId.ToString()),
            new Claim(ClaimTypes.Email, validated.Email),
            new Claim(ClaimTypes.Name, validated.Username),
            new Claim("token_type", "pat"),
            new Claim("pat_id", validated.TokenId.ToString()),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.Append(
            HeaderNames.WWWAuthenticate,
            "Bearer realm=\"LocalRoast MCP\", error=\"invalid_token\""
        );
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
