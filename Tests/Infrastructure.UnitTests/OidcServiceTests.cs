using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using App.Core.Authentication;
using App.Infrastructure.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class OidcServiceTests
{
    [TestMethod]
    public void GetConfiguration_ReturnsConfiguredOptions()
    {
        var options = new OidcOptions
        {
            Enabled = true,
            ProviderName = "Google",
            Authority = "https://accounts.google.com",
            ClientId = "test-client-id",
            Scope = "openid email profile",
        };

        var service = new OidcService(
            Options.Create(options),
            new HttpClient(),
            NullLogger<OidcService>.Instance
        );

        var config = service.GetConfiguration();

        Assert.IsTrue(config.Enabled);
        Assert.AreEqual("Google", config.ProviderName);
        Assert.AreEqual("https://accounts.google.com", config.Authority);
        Assert.AreEqual("test-client-id", config.ClientId);
    }

    [TestMethod]
    public async Task ValidateIdTokenAsync_WhenDisabled_ReturnsNull()
    {
        var options = new OidcOptions { Enabled = false };
        var service = new OidcService(
            Options.Create(options),
            new HttpClient(),
            NullLogger<OidcService>.Instance
        );

        var result = await service.ValidateIdTokenAsync("any-token");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ValidateIdTokenAsync_WithReadableJwt_ExtractsClaims()
    {
        const string issuer = "https://issuer.example.com";
        const string clientId = "test-client-id";
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        configuration.SigningKeys.Add(signingKey);
        var configurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
            configuration
        );
        var options = new OidcOptions
        {
            Enabled = true,
            ProviderName = "Keycloak",
            Authority = issuer,
            ClientId = clientId,
        };

        var service = new OidcService(
            Options.Create(options),
            new HttpClient(),
            NullLogger<OidcService>.Instance,
            configurationManager
        );

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.WriteToken(
            new JwtSecurityToken(
                issuer: issuer,
                audience: clientId,
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, "external-12345"),
                    new Claim(JwtRegisteredClaimNames.Email, "extuser@example.com"),
                    new Claim("name", "External User"),
                ],
                notBefore: DateTime.UtcNow.AddMinutes(-10),
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
            )
        );

        var principal = await service.ValidateIdTokenAsync(jwt);

        Assert.IsNotNull(principal);
        Assert.AreEqual("external-12345", principal.SubjectId);
        Assert.AreEqual("extuser@example.com", principal.Email);
        Assert.AreEqual("External User", principal.Name);
        Assert.AreEqual("Keycloak", principal.Provider);
    }

    [TestMethod]
    public async Task ValidateIdTokenAsync_WithUntrustedJwt_ReturnsNull()
    {
        const string issuer = "https://issuer.example.com";
        const string clientId = "test-client-id";
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        using var untrustedRsa = RSA.Create(2048);
        var untrustedKey = new RsaSecurityKey(untrustedRsa) { KeyId = "other-key" };
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        configuration.SigningKeys.Add(signingKey);
        var configurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
            configuration
        );
        var options = new OidcOptions
        {
            Enabled = true,
            ProviderName = "Keycloak",
            Authority = issuer,
            ClientId = clientId,
        };
        var service = new OidcService(
            Options.Create(options),
            new HttpClient(),
            NullLogger<OidcService>.Instance,
            configurationManager
        );
        var tokenHandler = new JwtSecurityTokenHandler();

        var unsignedToken = tokenHandler.WriteToken(
            new JwtSecurityToken(
                issuer: issuer,
                audience: clientId,
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, "forged-subject"),
                    new Claim(JwtRegisteredClaimNames.Email, "victim@example.com"),
                ]
            )
        );
        var wrongIssuerToken = CreateSignedToken(
            tokenHandler,
            signingKey,
            "https://attacker.example.com",
            clientId
        );
        var wrongAudienceToken = CreateSignedToken(
            tokenHandler,
            signingKey,
            issuer,
            "another-client-id"
        );
        var wrongSignatureToken = CreateSignedToken(tokenHandler, untrustedKey, issuer, clientId);
        var expiredToken = CreateSignedToken(
            tokenHandler,
            signingKey,
            issuer,
            clientId,
            DateTime.UtcNow.AddMinutes(-5)
        );

        Assert.IsNull(await service.ValidateIdTokenAsync(unsignedToken));
        Assert.IsNull(await service.ValidateIdTokenAsync(wrongIssuerToken));
        Assert.IsNull(await service.ValidateIdTokenAsync(wrongAudienceToken));
        Assert.IsNull(await service.ValidateIdTokenAsync(wrongSignatureToken));
        Assert.IsNull(await service.ValidateIdTokenAsync(expiredToken));
    }

    [TestMethod]
    public async Task ValidateIdTokenAsync_WithOpaqueAccessToken_FetchesClaimsFromUserInfoEndpoint()
    {
        var options = new OidcOptions
        {
            Enabled = true,
            ProviderName = "Google",
            UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo",
        };

        var mockHandler = new TestHttpMessageHandler(
            (request, _) =>
            {
                Assert.AreEqual(
                    "https://openidconnect.googleapis.com/v1/userinfo",
                    request.RequestUri?.ToString()
                );
                Assert.AreEqual(
                    "Bearer ya29.test-token",
                    request.Headers.Authorization?.ToString()
                );

                var json = """
                    {
                        "sub": "google-user-123",
                        "email": "test@gmail.com",
                        "name": "Test User"
                    }
                    """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        json,
                        System.Text.Encoding.UTF8,
                        "application/json"
                    ),
                };
            }
        );

        var service = new OidcService(
            Options.Create(options),
            new HttpClient(mockHandler),
            NullLogger<OidcService>.Instance
        );

        var principal = await service.ValidateIdTokenAsync("ya29.test-token");

        Assert.IsNotNull(principal);
        Assert.AreEqual("google-user-123", principal.SubjectId);
        Assert.AreEqual("test@gmail.com", principal.Email);
        Assert.AreEqual("Test User", principal.Name);
        Assert.AreEqual("Google", principal.Provider);
    }

    private sealed class TestHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(handler(request, cancellationToken));
    }

    private static string CreateSignedToken(
        JwtSecurityTokenHandler tokenHandler,
        RsaSecurityKey signingKey,
        string issuer,
        string audience,
        DateTime? expires = null
    ) =>
        tokenHandler.WriteToken(
            new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, "external-12345"),
                    new Claim(JwtRegisteredClaimNames.Email, "extuser@example.com"),
                ],
                notBefore: DateTime.UtcNow.AddMinutes(-10),
                expires: expires ?? DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
            )
        );
}
