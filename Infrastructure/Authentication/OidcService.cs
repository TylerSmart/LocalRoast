using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using App.Core.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace App.Infrastructure.Authentication;

public sealed class OidcService(
    IOptions<OidcOptions> options,
    HttpClient httpClient,
    ILogger<OidcService> logger,
    IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = null
) : IOidcService
{
    private static readonly ConcurrentDictionary<
        string,
        IConfigurationManager<OpenIdConnectConfiguration>
    > ConfigurationManagers = new(StringComparer.OrdinalIgnoreCase);
    private readonly OidcOptions _options = options.Value;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();
    private readonly IConfigurationManager<OpenIdConnectConfiguration>? _configurationManager =
        configurationManager ?? GetConfigurationManager(options.Value.Authority);

    public OidcConfigurationDto GetConfiguration()
    {
        var authEndpoint = _options.AuthorizationEndpoint;
        if (
            string.IsNullOrWhiteSpace(authEndpoint)
            && !string.IsNullOrWhiteSpace(_options.Authority)
        )
        {
            authEndpoint = $"{_options.Authority.TrimEnd('/')}/authorize";
        }

        return new OidcConfigurationDto(
            Enabled: _options.Enabled,
            ProviderName: _options.ProviderName,
            Authority: _options.Authority,
            ClientId: _options.ClientId,
            Scope: _options.Scope,
            AuthorizationEndpoint: authEndpoint
        );
    }

    public async Task<ExternalUserPrincipal?> ValidateIdTokenAsync(
        string idToken,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(idToken) || !_options.Enabled)
        {
            return null;
        }

        try
        {
            if (_tokenHandler.CanReadToken(idToken))
            {
                try
                {
                    var principal = await ValidateJwtAsync(idToken, cancellationToken);
                    if (principal is not null)
                    {
                        return principal;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "OIDC JWT validation failed.");
                }
            }

            // UserInfo lets the provider validate opaque access tokens and JWT access tokens.
            var userInfoEndpoint = _options.UserInfoEndpoint;
            if (
                string.IsNullOrWhiteSpace(userInfoEndpoint)
                && !string.IsNullOrWhiteSpace(_options.Authority)
            )
            {
                userInfoEndpoint = $"{_options.Authority.TrimEnd('/')}/userinfo";
            }

            if (!string.IsNullOrWhiteSpace(userInfoEndpoint))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

                var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var userInfo = await response.Content.ReadFromJsonAsync<JsonElement>(
                        cancellationToken: cancellationToken
                    );

                    var sub = userInfo.TryGetProperty("sub", out var subProp)
                        ? subProp.GetString()
                        : null;
                    var email = userInfo.TryGetProperty("email", out var emailProp)
                        ? emailProp.GetString()
                        : null;
                    var name = userInfo.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString()
                        : null;

                    if (!string.IsNullOrWhiteSpace(sub) && !string.IsNullOrWhiteSpace(email))
                    {
                        return new ExternalUserPrincipal(sub, email, name, _options.ProviderName);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "OIDC UserInfo endpoint returned {StatusCode} when validating token.",
                        response.StatusCode
                    );
                }
            }

            logger.LogWarning(
                "Provided token could not be validated via JWT or UserInfo endpoint."
            );
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to validate external ID or access token.");
            return null;
        }
    }

    public async Task<ExternalUserPrincipal?> ExchangeCodeAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(code) || !_options.Enabled)
        {
            return null;
        }

        var tokenEndpoint = _options.TokenEndpoint;
        if (
            string.IsNullOrWhiteSpace(tokenEndpoint)
            && !string.IsNullOrWhiteSpace(_options.Authority)
        )
        {
            tokenEndpoint = $"{_options.Authority.TrimEnd('/')}/token";
        }

        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            logger.LogWarning("No token endpoint configured for OIDC code exchange.");
            return null;
        }

        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = _options.ClientId,
            };

            if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
            {
                parameters["client_secret"] = _options.ClientSecret;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(parameters),
            };

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "OIDC token exchange failed: {StatusCode} {Error}",
                    response.StatusCode,
                    error
                );
                return null;
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<JsonElement>(
                cancellationToken: cancellationToken
            );
            if (tokenResponse.TryGetProperty("id_token", out var idTokenProp))
            {
                var idToken = idTokenProp.GetString();
                if (!string.IsNullOrWhiteSpace(idToken))
                {
                    return await ValidateIdTokenAsync(idToken, cancellationToken);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during OIDC code exchange.");
            return null;
        }
    }

    private async Task<ExternalUserPrincipal?> ValidateJwtAsync(
        string token,
        CancellationToken cancellationToken
    )
    {
        if (_configurationManager is null || string.IsNullOrWhiteSpace(_options.ClientId))
        {
            return null;
        }

        var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.Issuer) || configuration.SigningKeys.Count == 0)
        {
            logger.LogWarning("OIDC discovery returned no issuer or signing keys.");
            return null;
        }

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuration.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            RequireSignedTokens = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        var principal = _tokenHandler.ValidateToken(token, validationParameters, out _);

        var subject = principal
            .Claims.FirstOrDefault(c =>
                c.Type == JwtRegisteredClaimNames.Sub || c.Type == ClaimTypes.NameIdentifier
            )
            ?.Value;
        var email = principal
            .Claims.FirstOrDefault(c =>
                c.Type == JwtRegisteredClaimNames.Email || c.Type == ClaimTypes.Email
            )
            ?.Value;
        var name = principal
            .Claims.FirstOrDefault(c =>
                c.Type == "name" || c.Type == ClaimTypes.Name || c.Type == "preferred_username"
            )
            ?.Value;

        return !string.IsNullOrWhiteSpace(subject) && !string.IsNullOrWhiteSpace(email)
            ? new ExternalUserPrincipal(subject, email, name, _options.ProviderName)
            : null;
    }

    private static IConfigurationManager<OpenIdConnectConfiguration>? GetConfigurationManager(
        string authority
    )
    {
        if (
            !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
            || authorityUri.Scheme != Uri.UriSchemeHttps
        )
        {
            return null;
        }

        var normalizedAuthority = authority.TrimEnd('/');
        return ConfigurationManagers.GetOrAdd(
            normalizedAuthority,
            static normalizedAuthority => new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{normalizedAuthority}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true }
            )
        );
    }
}
