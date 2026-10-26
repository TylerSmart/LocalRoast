using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class TaxonomyIntegrationTests
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

    [TestMethod]
    public async Task GetCollectionsAndTags_ReturnsSeededItems()
    {
        await RegisterAndAuthenticateAsync(_client, "tax1@example.com", "tax1");

        // Seeded collections
        var collRes = await _client.GetAsync("/api/collections");
        Assert.AreEqual(HttpStatusCode.OK, collRes.StatusCode);
        var collections = await collRes.Content.ReadFromJsonAsync<IReadOnlyList<CollectionDto>>();
        Assert.IsNotNull(collections);
        Assert.IsTrue(collections.Count >= 4);
        Assert.IsTrue(collections.Any(c => c.Name == "Weeknight Staples"));

        // Seeded tags
        var tagRes = await _client.GetAsync("/api/tags");
        Assert.AreEqual(HttpStatusCode.OK, tagRes.StatusCode);
        var tags = await tagRes.Content.ReadFromJsonAsync<IReadOnlyList<TagDto>>();
        Assert.IsNotNull(tags);
        Assert.IsTrue(tags.Count >= 20);
        Assert.IsTrue(tags.Any(t => t.Name == "Vegetarian" && t.Category == TagCategory.Dietary));
        Assert.IsTrue(tags.Any(t => t.Name == "Sheet Pan" && t.Category == TagCategory.Equipment));

        // Filter tags by category
        var dietaryRes = await _client.GetAsync("/api/tags?category=Dietary");
        Assert.AreEqual(HttpStatusCode.OK, dietaryRes.StatusCode);
        var dietaryTags = await dietaryRes.Content.ReadFromJsonAsync<IReadOnlyList<TagDto>>();
        Assert.IsNotNull(dietaryTags);
        Assert.IsTrue(dietaryTags.All(t => t.Category == TagCategory.Dietary));
    }

    [TestMethod]
    public async Task Collection_CreateUpdateDelete_Succeeds()
    {
        await RegisterAndAuthenticateAsync(_client, "tax2@example.com", "tax2");

        // 1. Create
        var createReq = new UpsertCollectionRequest(
            "Holiday Baking",
            "Festive cookies and pies",
            50
        );
        var createRes = await _client.PostAsJsonAsync("/api/collections", createReq);
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<CollectionDto>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Holiday Baking", created.Name);
        Assert.AreEqual("holiday-baking", created.Slug);

        // Duplicate conflict
        var dupRes = await _client.PostAsJsonAsync("/api/collections", createReq);
        Assert.AreEqual(HttpStatusCode.Conflict, dupRes.StatusCode);

        // Empty validation
        var emptyRes = await _client.PostAsJsonAsync(
            "/api/collections",
            new UpsertCollectionRequest("")
        );
        Assert.AreEqual(HttpStatusCode.BadRequest, emptyRes.StatusCode);

        // 2. Update
        var updateReq = new UpsertCollectionRequest(
            "Holiday Baking & Sweets",
            "Festive cookies, pies and cakes",
            55
        );
        var updateRes = await _client.PutAsJsonAsync($"/api/collections/{created.Id}", updateReq);
        Assert.AreEqual(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<CollectionDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Holiday Baking & Sweets", updated.Name);

        // 3. Delete
        var deleteRes = await _client.DeleteAsync($"/api/collections/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Verify gone
        var getRes = await _client.GetAsync("/api/collections");
        var list = await getRes.Content.ReadFromJsonAsync<IReadOnlyList<CollectionDto>>();
        Assert.IsNotNull(list);
        Assert.IsFalse(list.Any(c => c.Id == created.Id));
    }

    [TestMethod]
    public async Task Tag_CreateUpdateDelete_Succeeds()
    {
        await RegisterAndAuthenticateAsync(_client, "tax3@example.com", "tax3");

        // 1. Create tag
        var createReq = new UpsertTagRequest("Keto", TagCategory.Dietary, 25);
        var createRes = await _client.PostAsJsonAsync("/api/tags", createReq);
        Assert.AreEqual(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<TagDto>();
        Assert.IsNotNull(created);
        Assert.AreEqual("Keto", created.Name);
        Assert.AreEqual("dietary-keto", created.Slug);

        // Duplicate conflict in same category
        var dupRes = await _client.PostAsJsonAsync("/api/tags", createReq);
        Assert.AreEqual(HttpStatusCode.Conflict, dupRes.StatusCode);

        // Same name in different category is allowed
        var customReq = new UpsertTagRequest("Keto", TagCategory.Custom, 10);
        var customRes = await _client.PostAsJsonAsync("/api/tags", customReq);
        Assert.AreEqual(HttpStatusCode.Created, customRes.StatusCode);

        // 2. Update
        var updateReq = new UpsertTagRequest("Strict Keto", TagCategory.Dietary, 26);
        var updateRes = await _client.PutAsJsonAsync($"/api/tags/{created.Id}", updateReq);
        Assert.AreEqual(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<TagDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual("Strict Keto", updated.Name);

        // 3. Delete
        var deleteRes = await _client.DeleteAsync($"/api/tags/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteRes.StatusCode);
    }
}
