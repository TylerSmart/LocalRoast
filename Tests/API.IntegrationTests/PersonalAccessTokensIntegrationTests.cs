using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class PersonalAccessTokensIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task GetTokens_WhenUnauthenticated_ReturnsUnauthorized()
    {
        var response = await _client.GetAsync("/api/account/tokens");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task CreateToken_ValidRequest_ReturnsCreatedWithPlaintextAndPrefix()
    {
        await RegisterAndAuthorizeAsync("alice@example.com", "alice", "Password123!");

        var response = await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("Claude Desktop")
        );

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>();
        Assert.IsNotNull(created);
        Assert.IsNotNull(created.PlaintextToken);
        Assert.IsTrue(created.PlaintextToken.StartsWith("lr_pat_", StringComparison.Ordinal));
        Assert.AreEqual("Claude Desktop", created.Token.Name);
        Assert.IsTrue(created.Token.IsActive);
        Assert.IsTrue(created.Token.TokenPrefix.StartsWith("lr_pat_", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CreateToken_InvalidName_ReturnsBadRequest()
    {
        await RegisterAndAuthorizeAsync("bob@example.com", "bob", "Password123!");

        var response = await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("x") // too short
        );

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task GetTokens_ReturnsOnlyOwningUserTokens()
    {
        // User A creates token
        await RegisterAndAuthorizeAsync("userA@example.com", "userA", "Password123!");
        await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("User A Token")
        );

        // User B logs in
        await RegisterAndAuthorizeAsync("userB@example.com", "userB", "Password123!");
        await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("User B Token")
        );

        var response = await _client.GetAsync("/api/account/tokens");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var tokens = await response.Content.ReadFromJsonAsync<List<PersonalAccessTokenDto>>();
        Assert.IsNotNull(tokens);
        Assert.AreEqual(1, tokens.Count);
        Assert.AreEqual("User B Token", tokens[0].Name);
    }

    [TestMethod]
    public async Task RevokeToken_OtherUserToken_ReturnsForbidden()
    {
        await RegisterAndAuthorizeAsync("chef1@example.com", "chef1", "Password123!");
        var createResponse = await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("Chef1 Token")
        );
        var created =
            await createResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>();
        Assert.IsNotNull(created);

        // Chef 2 attempts to revoke Chef 1's token
        await RegisterAndAuthorizeAsync("chef2@example.com", "chef2", "Password123!");
        var revokeResponse = await _client.PostAsync(
            $"/api/account/tokens/{created.Token.Id}/revoke",
            null
        );

        Assert.AreEqual(HttpStatusCode.Forbidden, revokeResponse.StatusCode);
    }

    [TestMethod]
    public async Task DeleteToken_OtherUserToken_ReturnsForbidden()
    {
        await RegisterAndAuthorizeAsync("cook1@example.com", "cook1", "Password123!");
        var createResponse = await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("Cook1 Token")
        );
        var created =
            await createResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>();
        Assert.IsNotNull(created);

        // Cook 2 attempts to delete Cook 1's token
        await RegisterAndAuthorizeAsync("cook2@example.com", "cook2", "Password123!");
        var deleteResponse = await _client.DeleteAsync($"/api/account/tokens/{created.Token.Id}");

        Assert.AreEqual(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    [TestMethod]
    public async Task PersonalAccessToken_UsedOnRestEndpoints_ReturnsUnauthorized()
    {
        await RegisterAndAuthorizeAsync("patuser@example.com", "patuser", "Password123!");
        var createResponse = await _client.PostAsJsonAsync(
            "/api/account/tokens",
            new CreatePersonalAccessTokenRequest("My Desktop PAT")
        );
        var created =
            await createResponse.Content.ReadFromJsonAsync<CreatedPersonalAccessTokenDto>();
        Assert.IsNotNull(created);

        // Switch Authorization header to the PAT
        using var patClient = _factory.CreateClient();
        patClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            created.PlaintextToken
        );

        // Verify PAT is strictly rejected on standard REST endpoints
        var accountResponse = await patClient.GetAsync("/api/account");
        Assert.AreEqual(HttpStatusCode.Unauthorized, accountResponse.StatusCode);

        var recipesResponse = await patClient.GetAsync("/api/recipes");
        Assert.AreEqual(HttpStatusCode.Unauthorized, recipesResponse.StatusCode);

        var tokensResponse = await patClient.GetAsync("/api/account/tokens");
        Assert.AreEqual(HttpStatusCode.Unauthorized, tokensResponse.StatusCode);
    }

    private async Task<AuthResponse> RegisterAndAuthorizeAsync(
        string email,
        string username,
        string password
    )
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, username, password)
        );
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }
}
