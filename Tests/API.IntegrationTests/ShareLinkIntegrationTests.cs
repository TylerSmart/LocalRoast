using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class ShareLinkIntegrationTests
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

    private static async Task<AuthResponse> RegisterAndAuthenticateAsync(
        HttpClient client,
        string email,
        string username
    )
    {
        var register = new RegisterRequest(email, username, "Password123!");
        var response = await client.PostAsJsonAsync("/api/auth/register", register);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.IsNotNull(auth);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            auth.Token
        );
        return auth;
    }

    private async Task<RecipeDetailDto> CreateRecipeAsync(
        string title,
        RecipeStatus status = RecipeStatus.Published,
        IReadOnlyList<RecipeIngredientInput>? ingredients = null,
        IReadOnlyList<RecipeStepInput>? steps = null
    )
    {
        var request = new UpsertRecipeRequest(
            Title: title,
            Description: "Delicious test recipe",
            Status: status,
            BaseServings: 4m,
            Ingredients: ingredients
                ??
                [
                    new RecipeIngredientInput(
                        DisplayName: "Salt",
                        Quantity: 1m,
                        Unit: MeasurementUnit.Tsp
                    ),
                ],
            Steps: steps ?? [new RecipeStepInput(Instruction: "Mix everything well.")]
        );

        var res = await _client.PostAsJsonAsync("/api/recipes", request);
        Assert.AreEqual(HttpStatusCode.Created, res.StatusCode);

        var recipe = await res.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(recipe);
        return recipe;
    }

    [TestMethod]
    public async Task CreateShareLink_ValidPublishedRecipe_Returns201CreatedAndPersists()
    {
        await RegisterAndAuthenticateAsync(_client, "chef1@example.com", "chef1");
        var recipe = await CreateRecipeAsync("Smoked Brisket");

        var createReq = new CreateShareLinkRequest(
            Label: "Family Gathering",
            IncludeCookLogs: true,
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(7)
        );

        var res = await _client.PostAsJsonAsync($"/api/recipes/{recipe.Id}/shares", createReq);
        Assert.AreEqual(HttpStatusCode.Created, res.StatusCode);

        var share = await res.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(share);
        Assert.AreEqual(recipe.Id, share.RecipeId);
        Assert.AreEqual("Family Gathering", share.Label);
        Assert.IsTrue(share.IncludeCookLogs);
        Assert.IsTrue(share.IsActive);
        Assert.AreEqual(0, share.ViewCount);
        Assert.IsNotNull(share.ExpiresAt);
        Assert.IsNull(share.RevokedAt);
        Assert.IsTrue(ShareTokenGenerator.IsWellFormedToken(share.Token));
    }

    [TestMethod]
    public async Task GetShareLinks_ReturnsAllLinksForRecipe()
    {
        await RegisterAndAuthenticateAsync(_client, "chef2@example.com", "chef2");
        var recipe = await CreateRecipeAsync("Fish Tacos");

        await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
            new CreateShareLinkRequest("Link 1")
        );
        await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
            new CreateShareLinkRequest("Link 2")
        );

        var listRes = await _client.GetAsync($"/api/recipes/{recipe.Id}/shares");
        Assert.AreEqual(HttpStatusCode.OK, listRes.StatusCode);

        var links = await listRes.Content.ReadFromJsonAsync<IReadOnlyList<ShareLinkDto>>();
        Assert.IsNotNull(links);
        Assert.HasCount(2, links);
    }

    [TestMethod]
    public async Task RevokeShareLink_SetsRevokedAtAndIsActiveFalse()
    {
        await RegisterAndAuthenticateAsync(_client, "chef3@example.com", "chef3");
        var recipe = await CreateRecipeAsync("Homemade Pasta");

        var createRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
            new CreateShareLinkRequest("Revoke Test")
        );
        var link = await createRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(link);

        var revokeRes = await _client.PostAsync(
            $"/api/recipes/{recipe.Id}/shares/{link.Id}/revoke",
            null
        );
        Assert.AreEqual(HttpStatusCode.OK, revokeRes.StatusCode);

        var updated = await revokeRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(updated);
        Assert.IsFalse(updated.IsActive);
        Assert.IsNotNull(updated.RevokedAt);
    }

    [TestMethod]
    public async Task DeleteShareLink_RemovesShareLink()
    {
        await RegisterAndAuthenticateAsync(_client, "chef4@example.com", "chef4");
        var recipe = await CreateRecipeAsync("Berry Crumble");

        var createRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
            new CreateShareLinkRequest("Delete Test")
        );
        var link = await createRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(link);

        var deleteRes = await _client.DeleteAsync($"/api/recipes/{recipe.Id}/shares/{link.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteRes.StatusCode);

        var listRes = await _client.GetAsync($"/api/recipes/{recipe.Id}/shares");
        var links = await listRes.Content.ReadFromJsonAsync<IReadOnlyList<ShareLinkDto>>();
        Assert.IsNotNull(links);
        Assert.IsEmpty(links);
    }

    [TestMethod]
    public async Task UnauthenticatedRequests_ToRecipeShares_Return401()
    {
        var dummyId = Guid.NewGuid();
        var res = await _client.GetAsync($"/api/recipes/{dummyId}/shares");
        Assert.AreEqual(HttpStatusCode.Unauthorized, res.StatusCode);

        var postRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{dummyId}/shares",
            new CreateShareLinkRequest()
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, postRes.StatusCode);
    }

    [TestMethod]
    public async Task CreateShareLink_DraftOrArchivedRecipe_Returns400BadRequest()
    {
        await RegisterAndAuthenticateAsync(_client, "chef5@example.com", "chef5");
        var draftRecipe = await CreateRecipeAsync("Draft Recipe", RecipeStatus.DraftReview);
        var archivedRecipe = await CreateRecipeAsync("Archived Recipe", RecipeStatus.Archived);

        var draftRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{draftRecipe.Id}/shares",
            new CreateShareLinkRequest()
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, draftRes.StatusCode);

        var archRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{archivedRecipe.Id}/shares",
            new CreateShareLinkRequest()
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, archRes.StatusCode);
    }

    [TestMethod]
    public async Task CreateShareLink_PastExpiration_Returns400BadRequest()
    {
        await RegisterAndAuthenticateAsync(_client, "chef6@example.com", "chef6");
        var recipe = await CreateRecipeAsync("Past Expire Recipe");

        var pastReq = new CreateShareLinkRequest(ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var res = await _client.PostAsJsonAsync($"/api/recipes/{recipe.Id}/shares", pastReq);
        Assert.AreEqual(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [TestMethod]
    public async Task CreateShareLink_NonExistentRecipe_Returns404NotFound()
    {
        await RegisterAndAuthenticateAsync(_client, "chef7@example.com", "chef7");
        var nonExistentId = Guid.NewGuid();

        var res = await _client.PostAsJsonAsync(
            $"/api/recipes/{nonExistentId}/shares",
            new CreateShareLinkRequest()
        );
        Assert.AreEqual(HttpStatusCode.NotFound, res.StatusCode);
    }

    [TestMethod]
    public async Task GetPublicSharedRecipe_ValidToken_Returns200WithNoStoreHeadersAndScrubbedData()
    {
        await RegisterAndAuthenticateAsync(_client, "chef8@example.com", "chef8");
        var recipe = await CreateRecipeAsync(
            "Neapolitan Pizza",
            RecipeStatus.Published,
            ingredients:
            [
                new RecipeIngredientInput(
                    SectionName: "Dough",
                    DisplayName: "Flour",
                    Quantity: 500m,
                    Unit: MeasurementUnit.G
                ),
                new RecipeIngredientInput(
                    SectionName: "Dough",
                    DisplayName: "Water",
                    Quantity: 325m,
                    Unit: MeasurementUnit.Ml
                ),
                new RecipeIngredientInput(
                    SectionName: "Sauce",
                    DisplayName: "San Marzano Tomatoes",
                    Quantity: 1m,
                    Unit: MeasurementUnit.Can
                ),
            ],
            steps:
            [
                new RecipeStepInput(
                    SectionName: "Dough Prep",
                    Instruction: "Mix flour and water and autolyse.",
                    IngredientIndices: [0, 1],
                    Timers: [new RecipeStepTimerInput(DurationSeconds: 1800, Label: "Autolyse")]
                ),
            ]
        );

        var shareRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
            new CreateShareLinkRequest("Pizza Night")
        );
        var link = await shareRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(link);

        // Anonymous client: clear authorization header
        using var anonymousClient = _factory.CreateClient();

        var publicRes = await anonymousClient.GetAsync($"/api/public/shares/{link.Token}");
        Assert.AreEqual(HttpStatusCode.OK, publicRes.StatusCode);

        // Verify headers
        Assert.IsTrue(publicRes.Headers.CacheControl?.NoStore ?? false);
        Assert.IsTrue(publicRes.Headers.TryGetValues("X-Robots-Tag", out var robots));
        StringAssert.Contains(string.Join(", ", robots), "noindex");

        // Verify content
        var sharedRecipe = await publicRes.Content.ReadFromJsonAsync<PublicSharedRecipeDto>();
        Assert.IsNotNull(sharedRecipe);
        Assert.AreEqual("Neapolitan Pizza", sharedRecipe.Title);
        Assert.HasCount(3, sharedRecipe.Ingredients);
        Assert.AreEqual("Dough", sharedRecipe.Ingredients[0].SectionName);
        Assert.HasCount(1, sharedRecipe.Steps);
        Assert.AreEqual("Autolyse", sharedRecipe.Steps[0].Timers[0].Label);

        // Verify raw JSON does not leak audit / identity information
        var rawJson = await publicRes.Content.ReadAsStringAsync();
        StringAssert.DoesNotMatch(
            rawJson,
            new System.Text.RegularExpressions.Regex(
                "createdBy",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            )
        );
        StringAssert.DoesNotMatch(
            rawJson,
            new System.Text.RegularExpressions.Regex(
                "chef8@example.com",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            )
        );
        StringAssert.DoesNotMatch(
            rawJson,
            new System.Text.RegularExpressions.Regex(
                "Collections",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            )
        );

        // Call again to verify view counter increments
        var secondRes = await anonymousClient.GetAsync($"/api/public/shares/{link.Token}");
        Assert.AreEqual(HttpStatusCode.OK, secondRes.StatusCode);

        // Verify view counter in authorized list
        var listRes = await _client.GetAsync($"/api/recipes/{recipe.Id}/shares");
        var links = await listRes.Content.ReadFromJsonAsync<IReadOnlyList<ShareLinkDto>>();
        Assert.IsNotNull(links);
        var refreshedLink = links.First(l => l.Id == link.Id);
        Assert.AreEqual(2, refreshedLink.ViewCount);
        Assert.IsNotNull(refreshedLink.LastAccessedAt);
    }

    [TestMethod]
    public async Task GetPublicSharedRecipe_InvalidExpiredOrRevokedToken_Returns404NotFound()
    {
        await RegisterAndAuthenticateAsync(_client, "chef9@example.com", "chef9");
        var recipe = await CreateRecipeAsync("Chocolate Mousse");

        var shareRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{recipe.Id}/shares",
            new CreateShareLinkRequest("Mousse Share")
        );
        var link = await shareRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(link);

        using var anonymousClient = _factory.CreateClient();

        // 1. Unknown token
        var unknownRes = await anonymousClient.GetAsync(
            "/api/public/shares/unknown_token_that_does_not_exist_at_all"
        );
        Assert.AreEqual(HttpStatusCode.NotFound, unknownRes.StatusCode);

        // 2. Malformed token
        var malformedRes = await anonymousClient.GetAsync("/api/public/shares/too$short!");
        Assert.AreEqual(HttpStatusCode.NotFound, malformedRes.StatusCode);

        // 3. Wrong case token
        var wrongCase =
            link.Token.ToLowerInvariant() == link.Token
                ? link.Token.ToUpperInvariant()
                : link.Token.ToLowerInvariant();
        var caseRes = await anonymousClient.GetAsync($"/api/public/shares/{wrongCase}");
        Assert.AreEqual(HttpStatusCode.NotFound, caseRes.StatusCode);

        // 4. Revoked token
        await _client.PostAsync($"/api/recipes/{recipe.Id}/shares/{link.Id}/revoke", null);
        var revokedRes = await anonymousClient.GetAsync($"/api/public/shares/{link.Token}");
        Assert.AreEqual(HttpStatusCode.NotFound, revokedRes.StatusCode);
    }

    [TestMethod]
    public async Task GetPublicSharedRecipe_WithSubRecipes_IncludesPublishedAndScrubsNonPublished()
    {
        await RegisterAndAuthenticateAsync(_client, "chef10@example.com", "chef10");

        var publishedSub = await CreateRecipeAsync("Pickled Red Onions", RecipeStatus.Published);
        var draftSub = await CreateRecipeAsync("Secret Chipotle Crema", RecipeStatus.DraftReview);

        var parentRecipe = await CreateRecipeAsync(
            "Braised Carnitas Tacos",
            RecipeStatus.Published,
            ingredients:
            [
                new RecipeIngredientInput(
                    DisplayName: "Pork Shoulder",
                    Quantity: 2m,
                    Unit: MeasurementUnit.Lb
                ),
                new RecipeIngredientInput(
                    DisplayName: "Pickled Onions",
                    Quantity: 1m,
                    LinkedRecipeId: publishedSub.Id
                ),
                new RecipeIngredientInput(
                    DisplayName: "Secret Crema",
                    Quantity: 1m,
                    LinkedRecipeId: draftSub.Id
                ),
            ]
        );

        var shareRes = await _client.PostAsJsonAsync(
            $"/api/recipes/{parentRecipe.Id}/shares",
            new CreateShareLinkRequest()
        );
        var link = await shareRes.Content.ReadFromJsonAsync<ShareLinkDto>();
        Assert.IsNotNull(link);

        using var anonymousClient = _factory.CreateClient();
        var publicRes = await anonymousClient.GetAsync($"/api/public/shares/{link.Token}");
        Assert.AreEqual(HttpStatusCode.OK, publicRes.StatusCode);

        var shared = await publicRes.Content.ReadFromJsonAsync<PublicSharedRecipeDto>();
        Assert.IsNotNull(shared);

        // Only the published sub-recipe is included in LinkedSubRecipes list
        Assert.HasCount(1, shared.LinkedSubRecipes);
        Assert.AreEqual(publishedSub.Id, shared.LinkedSubRecipes[0].Id);
        Assert.AreEqual("Pickled Red Onions", shared.LinkedSubRecipes[0].Title);

        // The published sub ingredient retains link
        var publishedIng = shared.Ingredients.First(i => i.DisplayName == "Pickled Onions");
        Assert.AreEqual(publishedSub.Id, publishedIng.LinkedSubRecipeId);
        Assert.AreEqual("Pickled Red Onions", publishedIng.LinkedSubRecipeTitle);

        // The draft sub ingredient has its sub link scrubbed
        var draftIng = shared.Ingredients.First(i => i.DisplayName == "Secret Crema");
        Assert.IsNull(draftIng.LinkedSubRecipeId);
        Assert.IsNull(draftIng.LinkedSubRecipeTitle);
    }
}
