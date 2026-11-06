using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class RecipeDiscoveryIntegrationTests
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
    public async Task Recipe_CreateWithCollectionsAndTags_FilterByCollectionAndTags()
    {
        await RegisterAndAuthenticateAsync(_client, "disc1@example.com", "disc1");

        // Create test collections
        var weeknightRes = await _client.PostAsJsonAsync(
            "/api/collections",
            new UpsertCollectionRequest("Weeknight Staples", "Quick dinners", 10)
        );
        Assert.AreEqual(HttpStatusCode.Created, weeknightRes.StatusCode);
        var weeknightColl = await weeknightRes.Content.ReadFromJsonAsync<CollectionDto>();
        Assert.IsNotNull(weeknightColl);

        var projectsRes = await _client.PostAsJsonAsync(
            "/api/collections",
            new UpsertCollectionRequest("Weekend Projects", "Slow braises", 20)
        );
        Assert.AreEqual(HttpStatusCode.Created, projectsRes.StatusCode);
        var projectsColl = await projectsRes.Content.ReadFromJsonAsync<CollectionDto>();
        Assert.IsNotNull(projectsColl);

        var vegTagRes = await _client.PostAsJsonAsync(
            "/api/tags",
            new UpsertTagRequest("Vegetarian", TagCategory.Dietary, 10)
        );
        Assert.AreEqual(HttpStatusCode.Created, vegTagRes.StatusCode);
        var vegTag = await vegTagRes.Content.ReadFromJsonAsync<TagDto>();
        Assert.IsNotNull(vegTag);

        var sheetPanTagRes = await _client.PostAsJsonAsync(
            "/api/tags",
            new UpsertTagRequest("Sheet Pan", TagCategory.Equipment, 20)
        );
        Assert.AreEqual(HttpStatusCode.Created, sheetPanTagRes.StatusCode);
        var sheetPanTag = await sheetPanTagRes.Content.ReadFromJsonAsync<TagDto>();
        Assert.IsNotNull(sheetPanTag);

        // Create Recipe 1: In Weeknight Staples, has Vegetarian AND Sheet Pan
        var recipe1Req = new UpsertRecipeRequest(
            Title: "Sheet Pan Tofu & Veggies",
            CollectionIds: [weeknightColl.Id],
            TagIds: [vegTag.Id, sheetPanTag.Id]
        );
        var r1Res = await _client.PostAsJsonAsync("/api/recipes", recipe1Req);
        Assert.AreEqual(HttpStatusCode.Created, r1Res.StatusCode);
        var r1 = await r1Res.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(r1);
        Assert.AreEqual(1, r1.Collections.Count);
        Assert.AreEqual("Weeknight Staples", r1.Collections[0].Name);
        Assert.AreEqual(2, r1.Tags.Count);

        // Create Recipe 2: In Weekend Projects, has Sheet Pan only
        var recipe2Req = new UpsertRecipeRequest(
            Title: "Slow Roast Pork Belly",
            CollectionIds: [projectsColl.Id],
            TagIds: [sheetPanTag.Id]
        );
        var r2Res = await _client.PostAsJsonAsync("/api/recipes", recipe2Req);
        Assert.AreEqual(HttpStatusCode.Created, r2Res.StatusCode);
        var r2 = await r2Res.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(r2);

        // Filter by CollectionId = Weeknight Staples
        var filterCollRes = await _client.GetAsync($"/api/recipes?collectionId={weeknightColl.Id}");
        var collFiltered = await filterCollRes.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(collFiltered);
        Assert.IsTrue(collFiltered.Any(r => r.Id == r1.Id));
        Assert.IsFalse(collFiltered.Any(r => r.Id == r2.Id));

        // Filter by TagIds = Sheet Pan
        var filterTag1Res = await _client.GetAsync($"/api/recipes?tagIds={sheetPanTag.Id}");
        var tag1Filtered = await filterTag1Res.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(tag1Filtered);
        Assert.IsTrue(tag1Filtered.Any(r => r.Id == r1.Id));
        Assert.IsTrue(tag1Filtered.Any(r => r.Id == r2.Id));

        // Filter by TagIds = Sheet Pan AND Vegetarian (AND logic!)
        var filterBothRes = await _client.GetAsync(
            $"/api/recipes?tagIds={sheetPanTag.Id}&tagIds={vegTag.Id}"
        );
        var bothFiltered = await filterBothRes.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(bothFiltered);
        Assert.IsTrue(bothFiltered.Any(r => r.Id == r1.Id));
        Assert.IsFalse(bothFiltered.Any(r => r.Id == r2.Id));

        // Search text matching tag name "Vegetarian"
        var searchTagRes = await _client.GetAsync("/api/recipes?search=vegetarian");
        var searchResults = await searchTagRes.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(searchResults);
        Assert.IsTrue(searchResults.Any(r => r.Id == r1.Id));

        // Search text matching collection name "Weekend Projects"
        var searchCollRes = await _client.GetAsync("/api/recipes?search=weekend");
        var searchCollResults = await searchCollRes.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(searchCollResults);
        Assert.IsTrue(searchCollResults.Any(r => r.Id == r2.Id));
    }

    [TestMethod]
    public async Task Recipe_Search_ToleratesDiacriticsPunctuationAndOneMisspelledTitleWord()
    {
        await RegisterAndAuthenticateAsync(_client, "search1@example.com", "search1");

        const string title = "Sweet and Saucy Grilled Salmon with Lime-Jalapeño Glaze";
        var createResponse = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(Title: title)
        );
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        var secondCreateResponse = await _client.PostAsJsonAsync(
            "/api/recipes",
            new UpsertRecipeRequest(Title: $"{title} with Honey")
        );
        Assert.AreEqual(HttpStatusCode.Created, secondCreateResponse.StatusCode);

        var typoSearch = Uri.EscapeDataString(
            "Sweet and Saucy Grilled Salmon with Lime-Jalapeao Glaze"
        );
        var typoResponse = await _client.GetAsync($"/api/recipes?search={typoSearch}");
        Assert.AreEqual(HttpStatusCode.OK, typoResponse.StatusCode);
        var typoMatches = await typoResponse.Content.ReadFromJsonAsync<
            IReadOnlyList<RecipeSummaryDto>
        >();
        Assert.IsNotNull(typoMatches);
        Assert.AreEqual(2, typoMatches.Count);
        Assert.IsTrue(typoMatches.Any(recipe => recipe.Title == title));

        var accentResponse = await _client.GetAsync(
            $"/api/recipes?search={Uri.EscapeDataString("Jalapeno Glaze")}&page=1&pageSize=1"
        );
        Assert.AreEqual(HttpStatusCode.OK, accentResponse.StatusCode);
        var accentMatches = await accentResponse.Content.ReadFromJsonAsync<RecipePagedResultDto>();
        Assert.IsNotNull(accentMatches);
        Assert.AreEqual(2, accentMatches.TotalCount);
        Assert.AreEqual(1, accentMatches.Items.Count);
        Assert.IsTrue(accentMatches.HasMore);
        Assert.IsTrue(accentMatches.IsFuzzySearch);

        var secondPageResponse = await _client.GetAsync(
            $"/api/recipes?search={Uri.EscapeDataString("Jalapeno Glaze")}&page=2&pageSize=1&includeTotalCount=false&fuzzySearch=true"
        );
        var secondPage = await secondPageResponse.Content.ReadFromJsonAsync<RecipePagedResultDto>();
        Assert.IsNotNull(secondPage);
        Assert.IsNull(secondPage.TotalCount);
        Assert.IsFalse(secondPage.HasMore);
        Assert.IsTrue(secondPage.IsFuzzySearch);
        Assert.AreNotEqual(accentMatches.Items[0].Id, secondPage.Items[0].Id);
    }

    [TestMethod]
    public async Task Recipe_Pagination_SkipsRepeatedCountsAndKeepsStablePages()
    {
        await RegisterAndAuthenticateAsync(_client, "search2@example.com", "search2");

        for (var index = 1; index <= 3; index++)
        {
            var createResponse = await _client.PostAsJsonAsync(
                "/api/recipes",
                new UpsertRecipeRequest(Title: $"Searchable Recipe {index}")
            );
            Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        }

        var pageOneResponse = await _client.GetAsync(
            "/api/recipes?search=Searchable&page=1&pageSize=1"
        );
        var pageOne = await pageOneResponse.Content.ReadFromJsonAsync<RecipePagedResultDto>();
        Assert.IsNotNull(pageOne);
        Assert.AreEqual(3, pageOne.TotalCount);
        Assert.IsTrue(pageOne.HasMore);

        var pageTwoResponse = await _client.GetAsync(
            "/api/recipes?search=Searchable&page=2&pageSize=1&includeTotalCount=false"
        );
        var pageTwo = await pageTwoResponse.Content.ReadFromJsonAsync<RecipePagedResultDto>();
        Assert.IsNotNull(pageTwo);
        Assert.IsNull(pageTwo.TotalCount);
        Assert.IsTrue(pageTwo.HasMore);

        var pageThreeResponse = await _client.GetAsync(
            "/api/recipes?search=Searchable&page=3&pageSize=1&includeTotalCount=false"
        );
        var pageThree = await pageThreeResponse.Content.ReadFromJsonAsync<RecipePagedResultDto>();
        Assert.IsNotNull(pageThree);
        Assert.IsNull(pageThree.TotalCount);
        Assert.IsFalse(pageThree.HasMore);
        Assert.AreEqual(
            3,
            new[] { pageOne.Items[0].Id, pageTwo.Items[0].Id, pageThree.Items[0].Id }
                .Distinct()
                .Count()
        );
    }

    [TestMethod]
    public async Task Recipe_UpdateOrganization_ModifiesCollectionsAndTags()
    {
        await RegisterAndAuthenticateAsync(_client, "disc2@example.com", "disc2");

        var holidayRes = await _client.PostAsJsonAsync(
            "/api/collections",
            new UpsertCollectionRequest("Holiday & Hosting", "Crowd-pleasers", 30)
        );
        Assert.AreEqual(HttpStatusCode.Created, holidayRes.StatusCode);
        var holidayColl = await holidayRes.Content.ReadFromJsonAsync<CollectionDto>();
        Assert.IsNotNull(holidayColl);

        var createReq = new UpsertRecipeRequest(Title: "Holiday Ham");
        var createRes = await _client.PostAsJsonAsync("/api/recipes", createReq);
        var created = await createRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);
        Assert.AreEqual(0, created.Collections.Count);

        // Update organization via PUT /api/recipes/{id}/organization
        var orgReq = new UpdateRecipeOrganizationRequest(
            CollectionIds: [holidayColl.Id],
            TagIds: []
        );
        var orgRes = await _client.PutAsJsonAsync(
            $"/api/recipes/{created.Id}/organization",
            orgReq
        );
        Assert.AreEqual(HttpStatusCode.OK, orgRes.StatusCode);
        var updated = await orgRes.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(updated);
        Assert.AreEqual(1, updated.Collections.Count);
        Assert.AreEqual("Holiday & Hosting", updated.Collections[0].Name);
    }

    [TestMethod]
    public async Task Recipe_MatchIngredients_RanksAndIgnoresPantryStaples()
    {
        await RegisterAndAuthenticateAsync(_client, "disc3@example.com", "disc3");

        async Task<FoodItemDto> CreateFoodAsync(string name, bool isPantryStaple = false)
        {
            var res = await _client.PostAsJsonAsync(
                "/api/food-items",
                new CreateFoodItemRequest(name, IsPantryStaple: isPantryStaple)
            );
            Assert.AreEqual(HttpStatusCode.Created, res.StatusCode);
            var dto = await res.Content.ReadFromJsonAsync<FoodItemDto>();
            Assert.IsNotNull(dto);
            return dto;
        }

        var chicken = await CreateFoodAsync("Chicken Thighs");
        var lime = await CreateFoodAsync("Lime");
        var cilantro = await CreateFoodAsync("Cilantro");
        var onion = await CreateFoodAsync("Yellow Onion");
        var salt = await CreateFoodAsync("Kosher Salt", isPantryStaple: true);

        // Recipe A: Chicken + Lime + Kosher Salt -> 2 on-hand matched, 0 non-staples needed
        var recipeAReq = new UpsertRecipeRequest(
            Title: "Lime Chicken Quick",
            TotalTimeMinutes: 25,
            Ingredients:
            [
                new RecipeIngredientInput(FoodItemId: chicken.Id, DisplayName: "Chicken Thighs"),
                new RecipeIngredientInput(FoodItemId: lime.Id, DisplayName: "Fresh Lime"),
                new RecipeIngredientInput(FoodItemId: salt.Id, DisplayName: "Kosher Salt"),
            ]
        );
        var rARes = await _client.PostAsJsonAsync("/api/recipes", recipeAReq);
        Assert.AreEqual(HttpStatusCode.Created, rARes.StatusCode);

        // Recipe B: Chicken + Lime + Cilantro + Yellow Onion -> 2 on-hand matched, 2 non-staples needed
        var recipeBReq = new UpsertRecipeRequest(
            Title: "Cilantro Lime Chicken Skillet",
            TotalTimeMinutes: 35,
            Ingredients:
            [
                new RecipeIngredientInput(FoodItemId: chicken.Id, DisplayName: "Chicken Thighs"),
                new RecipeIngredientInput(FoodItemId: lime.Id, DisplayName: "Fresh Lime"),
                new RecipeIngredientInput(FoodItemId: cilantro.Id, DisplayName: "Cilantro"),
                new RecipeIngredientInput(FoodItemId: onion.Id, DisplayName: "Yellow Onion"),
            ]
        );
        var rBRes = await _client.PostAsJsonAsync("/api/recipes", recipeBReq);
        Assert.AreEqual(HttpStatusCode.Created, rBRes.StatusCode);

        // Call match-ingredients with Chicken and Lime
        var matchReq = new IngredientMatchRequest([chicken.Id, lime.Id]);
        var matchRes = await _client.PostAsJsonAsync("/api/recipes/match-ingredients", matchReq);
        Assert.AreEqual(HttpStatusCode.OK, matchRes.StatusCode);

        var matches = await matchRes.Content.ReadFromJsonAsync<
            IReadOnlyList<IngredientMatchResultDto>
        >();
        Assert.IsNotNull(matches);
        Assert.IsTrue(matches.Count >= 2);

        var matchA = matches.First(m => m.Recipe.Title == "Lime Chicken Quick");
        var matchB = matches.First(m => m.Recipe.Title == "Cilantro Lime Chicken Skillet");

        // Verify Recipe A is ranked higher than Recipe B
        var indexA = matches.ToList().IndexOf(matchA);
        var indexB = matches.ToList().IndexOf(matchB);
        Assert.IsTrue(indexA < indexB, "Recipe A should rank ahead of Recipe B");

        Assert.AreEqual(2, matchA.MatchedCount);
        Assert.AreEqual(0, matchA.AdditionalNonStapleCount);
        Assert.AreEqual(1, matchA.PantryStaplesUsedCount);

        Assert.AreEqual(2, matchB.MatchedCount);
        Assert.AreEqual(2, matchB.AdditionalNonStapleCount);
    }
}
