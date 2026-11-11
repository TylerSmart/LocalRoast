using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.Recipes;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task GetCollectionsAndTags_StartEmpty_AndSupportCategoryFiltering()
    {
        await RegisterAndAuthenticateAsync(_client, "tax1@example.com", "tax1");

        // Collections start empty
        var collRes = await _client.GetAsync("/api/collections");
        Assert.AreEqual(HttpStatusCode.OK, collRes.StatusCode);
        var collections = await collRes.Content.ReadFromJsonAsync<IReadOnlyList<CollectionDto>>();
        Assert.IsNotNull(collections);
        Assert.AreEqual(0, collections.Count);

        // Tags start empty
        var initialTagRes = await _client.GetAsync("/api/tags");
        Assert.AreEqual(HttpStatusCode.OK, initialTagRes.StatusCode);
        var initialTags = await initialTagRes.Content.ReadFromJsonAsync<IReadOnlyList<TagDto>>();
        Assert.IsNotNull(initialTags);
        Assert.AreEqual(0, initialTags.Count);

        // Create tags in different categories
        await _client.PostAsJsonAsync(
            "/api/tags",
            new UpsertTagRequest("Vegetarian", TagCategory.Dietary, 10)
        );
        await _client.PostAsJsonAsync(
            "/api/tags",
            new UpsertTagRequest("Sheet Pan", TagCategory.Equipment, 20)
        );

        var tagRes = await _client.GetAsync("/api/tags");
        Assert.AreEqual(HttpStatusCode.OK, tagRes.StatusCode);
        var tags = await tagRes.Content.ReadFromJsonAsync<IReadOnlyList<TagDto>>();
        Assert.IsNotNull(tags);
        Assert.AreEqual(2, tags.Count);
        Assert.IsTrue(tags.Any(t => t.Name == "Vegetarian" && t.Category == TagCategory.Dietary));
        Assert.IsTrue(tags.Any(t => t.Name == "Sheet Pan" && t.Category == TagCategory.Equipment));

        // Filter tags by category
        var dietaryRes = await _client.GetAsync("/api/tags?category=Dietary");
        Assert.AreEqual(HttpStatusCode.OK, dietaryRes.StatusCode);
        var dietaryTags = await dietaryRes.Content.ReadFromJsonAsync<IReadOnlyList<TagDto>>();
        Assert.IsNotNull(dietaryTags);
        Assert.AreEqual(1, dietaryTags.Count);
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

    [TestMethod]
    public async Task NewTagCategories_ProteinOccasionMealPrep_SupportCrudAndFiltering()
    {
        await RegisterAndAuthenticateAsync(_client, "tax_newcats@example.com", "tax_newcats");

        // 1. Create tags in new categories
        var proteinRes = await _client.PostAsJsonAsync(
            "/api/tags",
            new UpsertTagRequest("Grass-Fed Beef", TagCategory.Protein, 15)
        );
        Assert.AreEqual(HttpStatusCode.Created, proteinRes.StatusCode);
        var proteinTag = await proteinRes.Content.ReadFromJsonAsync<TagDto>();
        Assert.IsNotNull(proteinTag);
        Assert.AreEqual(TagCategory.Protein, proteinTag.Category);
        Assert.AreEqual("protein-grass-fed-beef", proteinTag.Slug);

        var occasionRes = await _client.PostAsJsonAsync(
            "/api/tags",
            new UpsertTagRequest("Date Night Special", TagCategory.Occasion, 25)
        );
        Assert.AreEqual(HttpStatusCode.Created, occasionRes.StatusCode);
        var occasionTag = await occasionRes.Content.ReadFromJsonAsync<TagDto>();
        Assert.IsNotNull(occasionTag);
        Assert.AreEqual(TagCategory.Occasion, occasionTag.Category);
        Assert.AreEqual("occasion-date-night-special", occasionTag.Slug);

        var mealPrepRes = await _client.PostAsJsonAsync(
            "/api/tags",
            new UpsertTagRequest("Freezer Batch", TagCategory.MealPrep, 35)
        );
        Assert.AreEqual(HttpStatusCode.Created, mealPrepRes.StatusCode);
        var mealPrepTag = await mealPrepRes.Content.ReadFromJsonAsync<TagDto>();
        Assert.IsNotNull(mealPrepTag);
        Assert.AreEqual(TagCategory.MealPrep, mealPrepTag.Category);
        Assert.AreEqual("mealprep-freezer-batch", mealPrepTag.Slug);

        // 2. Filter by category
        var getProtein = await _client.GetAsync("/api/tags?category=Protein");
        Assert.AreEqual(HttpStatusCode.OK, getProtein.StatusCode);
        var proteinTags = await getProtein.Content.ReadFromJsonAsync<IReadOnlyList<TagDto>>();
        Assert.IsNotNull(proteinTags);
        Assert.IsTrue(proteinTags.Any(t => t.Name == "Grass-Fed Beef"));

        var getOccasion = await _client.GetAsync("/api/tags?category=Occasion");
        Assert.AreEqual(HttpStatusCode.OK, getOccasion.StatusCode);
        var occasionTags = await getOccasion.Content.ReadFromJsonAsync<IReadOnlyList<TagDto>>();
        Assert.IsNotNull(occasionTags);
        Assert.IsTrue(occasionTags.Any(t => t.Name == "Date Night Special"));

        var getMealPrep = await _client.GetAsync("/api/tags?category=MealPrep");
        Assert.AreEqual(HttpStatusCode.OK, getMealPrep.StatusCode);
        var mealPrepTags = await getMealPrep.Content.ReadFromJsonAsync<IReadOnlyList<TagDto>>();
        Assert.IsNotNull(mealPrepTags);
        Assert.IsTrue(mealPrepTags.Any(t => t.Name == "Freezer Batch"));
    }

    [TestMethod]
    public async Task TagSeeder_SeedsAllBuiltInTagsIdempotently()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<App.Data.AppDbContext>();

        await App.Data.Services.TagSeeder.EnsureBuiltInTagsSeededAsync(db);

        var countAfterFirstSeed = db.Tags.Count(t => t.IsSystemSeeded);
        Assert.AreEqual(BuiltInTags.All.Count, countAfterFirstSeed);

        // Verify categories are populated
        Assert.IsTrue(
            db.Tags.Any(t => t.Category == TagCategory.Protein && t.Name == "Chicken Breast")
        );
        Assert.IsTrue(
            db.Tags.Any(t => t.Category == TagCategory.Occasion && t.Name == "Comfort Food")
        );
        Assert.IsTrue(
            db.Tags.Any(t => t.Category == TagCategory.MealPrep && t.Name == "Freezer-Friendly")
        );
        Assert.IsTrue(db.Tags.Any(t => t.Category == TagCategory.Course && t.Name == "Dinner"));
        Assert.IsTrue(
            db.Tags.Any(t => t.Category == TagCategory.Equipment && t.Name == "Cast Iron")
        );
        Assert.IsTrue(
            db.Tags.Any(t => t.Category == TagCategory.Dietary && t.Name == "Gluten-Free")
        );
        Assert.IsTrue(db.Tags.Any(t => t.Category == TagCategory.Dietary && t.Name == "Alcohol"));
        Assert.IsTrue(db.Tags.Any(t => t.Category == TagCategory.Cuisine && t.Name == "Italian"));

        // Idempotency: second run does not duplicate tags
        await App.Data.Services.TagSeeder.EnsureBuiltInTagsSeededAsync(db);
        var countAfterSecondSeed = db.Tags.Count(t => t.IsSystemSeeded);
        Assert.AreEqual(countAfterFirstSeed, countAfterSecondSeed);
    }
}
