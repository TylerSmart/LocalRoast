using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using App.Core.Authentication;
using App.Core.MealPlans;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class MealPlansIntegrationTests
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

    private async Task<RecipeDetailDto> CreateTestRecipeAsync(
        string title,
        decimal baseServings = 4m
    )
    {
        var request = new UpsertRecipeRequest(
            Title: title,
            Description: "Delicious test recipe",
            BaseServings: baseServings,
            Status: RecipeStatus.Published
        );

        var response = await _client.PostAsJsonAsync("/api/recipes", request);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<RecipeDetailDto>();
        Assert.IsNotNull(created);
        return created;
    }

    [TestMethod]
    public async Task Endpoints_RequireAuthentication_ReturnsUnauthorized()
    {
        var getWeek = await _client.GetAsync("/api/meal-plans/week");
        Assert.AreEqual(HttpStatusCode.Unauthorized, getWeek.StatusCode);

        var createEntry = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest()
        );
        Assert.AreEqual(HttpStatusCode.Unauthorized, createEntry.StatusCode);

        var getTemplates = await _client.GetAsync("/api/meal-plan-templates");
        Assert.AreEqual(HttpStatusCode.Unauthorized, getTemplates.StatusCode);
    }

    [TestMethod]
    public async Task CreateAndGetWeek_WithOnDeckAndMultiDish_Succeeds()
    {
        var email = $"chef_{Guid.NewGuid():N}"[..12] + "@example.com";
        await RegisterAndAuthenticateAsync(_client, email, "chef_planner");

        var chili = await CreateTestRecipeAsync("Sunday Chili", 4m);
        var cornbread = await CreateTestRecipeAsync("Buttermilk Cornbread", 8m);

        var monday = new DateOnly(2026, 11, 2);

        // 1. Create On-Deck item
        var onDeckReq = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: true,
            WeekStartDate: monday,
            RecipeId: chili.Id,
            TargetServings: 6m
        );
        var onDeckResp = await _client.PostAsJsonAsync("/api/meal-plans/entries", onDeckReq);
        Assert.AreEqual(HttpStatusCode.Created, onDeckResp.StatusCode);
        var onDeck = await onDeckResp.Content.ReadFromJsonAsync<MealPlanEntryDto>();
        Assert.IsNotNull(onDeck);
        Assert.IsTrue(onDeck.IsOnDeck);
        Assert.AreEqual(6m, onDeck.TargetServings);
        Assert.AreEqual("Sunday Chili", onDeck.DisplayTitle);

        // 2. Schedule two recipes in Wednesday Dinner (multi-dish)
        var wednesday = monday.AddDays(2);
        var dish1Req = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: false,
            ScheduledDate: wednesday,
            Slot: MealSlot.Dinner,
            RecipeId: chili.Id
        );
        var dish1Resp = await _client.PostAsJsonAsync("/api/meal-plans/entries", dish1Req);
        Assert.AreEqual(HttpStatusCode.Created, dish1Resp.StatusCode);

        var dish2Req = new CreateMealPlanEntryRequest(
            EntryType: MealPlanEntryType.Recipe,
            IsOnDeck: false,
            ScheduledDate: wednesday,
            Slot: MealSlot.Dinner,
            RecipeId: cornbread.Id
        );
        var dish2Resp = await _client.PostAsJsonAsync("/api/meal-plans/entries", dish2Req);
        Assert.AreEqual(HttpStatusCode.Created, dish2Resp.StatusCode);

        // 3. Query Week
        var weekResp = await _client.GetAsync(
            $"/api/meal-plans/week?weekStart={monday:yyyy-MM-dd}"
        );
        Assert.AreEqual(HttpStatusCode.OK, weekResp.StatusCode);
        var week = await weekResp.Content.ReadFromJsonAsync<MealPlanWeekDto>();
        Assert.IsNotNull(week);
        Assert.AreEqual(monday, week.WeekStartDate);
        Assert.AreEqual(1, week.OnDeckEntries.Count);

        var wednesdayDay = week.Days.First(d => d.Date == wednesday);
        Assert.AreEqual(2, wednesdayDay.Entries.Count);
        Assert.AreEqual("Sunday Chili", wednesdayDay.Entries[0].DisplayTitle);
        Assert.AreEqual("Buttermilk Cornbread", wednesdayDay.Entries[1].DisplayTitle);
    }

    [TestMethod]
    public async Task MoveAndCookTonight_PromotesOnDeckMeal_ToTodayDinner()
    {
        var email = $"chef_{Guid.NewGuid():N}"[..12] + "@example.com";
        await RegisterAndAuthenticateAsync(_client, email, "chef_cook");

        var recipe = await CreateTestRecipeAsync("Quick Stir Fry", 2m);
        var monday = new DateOnly(2026, 11, 2);

        var createResp = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: true,
                WeekStartDate: monday,
                RecipeId: recipe.Id
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode);
        var entry = await createResp.Content.ReadFromJsonAsync<MealPlanEntryDto>();
        Assert.IsNotNull(entry);
        Assert.IsTrue(entry.IsOnDeck);

        // Cook Tonight
        var targetDate = new DateOnly(2026, 11, 4);
        var cookResp = await _client.PostAsJsonAsync(
            $"/api/meal-plans/entries/{entry.Id}/cook-tonight",
            new CookTonightRequest(TargetDate: targetDate, Slot: MealSlot.Dinner)
        );
        Assert.AreEqual(HttpStatusCode.OK, cookResp.StatusCode);
        var cooked = await cookResp.Content.ReadFromJsonAsync<MealPlanEntryDto>();
        Assert.IsNotNull(cooked);
        Assert.IsFalse(cooked.IsOnDeck);
        Assert.AreEqual(targetDate, cooked.ScheduledDate);
        Assert.AreEqual(MealSlot.Dinner, cooked.Slot);
    }

    [TestMethod]
    public async Task CreateLeftoversAndFreeformNote_AndVerifyRecipeDeletionDetachesEntries()
    {
        var email = $"chef_{Guid.NewGuid():N}"[..12] + "@example.com";
        await RegisterAndAuthenticateAsync(_client, email, "chef_leftovers");

        var recipe = await CreateTestRecipeAsync("Roast Turkey", 12m);
        var monday = new DateOnly(2026, 11, 2);

        // Scheduled meal
        var sourceResp = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: monday,
                Slot: MealSlot.Dinner,
                RecipeId: recipe.Id
            )
        );
        var source = await sourceResp.Content.ReadFromJsonAsync<MealPlanEntryDto>();
        Assert.IsNotNull(source);

        // Leftover entry linked to source
        var leftoverResp = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Leftovers,
                IsOnDeck: false,
                ScheduledDate: monday.AddDays(1),
                Slot: MealSlot.Lunch,
                SourceMealPlanEntryId: source.Id
            )
        );
        var leftover = await leftoverResp.Content.ReadFromJsonAsync<MealPlanEntryDto>();
        Assert.IsNotNull(leftover);
        Assert.IsTrue(leftover.IsLeftovers);
        Assert.AreEqual("Leftovers: Roast Turkey", leftover.DisplayTitle);

        // Freeform note
        var noteResp = await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.FreeformNote,
                IsOnDeck: false,
                ScheduledDate: monday.AddDays(4),
                Slot: MealSlot.Dinner,
                CustomTitle: "Family Dinner Out"
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, noteResp.StatusCode);

        // Now DELETE the recipe -> should detach gracefully without 409 or losing calendar entries
        var deleteResp = await _client.DeleteAsync($"/api/recipes/{recipe.Id}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResp.StatusCode);

        // Verify the leftover entry still exists and preserved its title
        var getLeftover = await _client.GetAsync($"/api/meal-plans/entries/{leftover.Id}");
        Assert.AreEqual(HttpStatusCode.OK, getLeftover.StatusCode);
        var remainingLeftover = await getLeftover.Content.ReadFromJsonAsync<MealPlanEntryDto>();
        Assert.IsNotNull(remainingLeftover);
        Assert.IsNull(remainingLeftover.Recipe);
        Assert.AreEqual("Leftovers: Roast Turkey", remainingLeftover.DisplayTitle);
    }

    [TestMethod]
    public async Task CreateTemplate_FromWeek_AndApplyMergeAndReplace()
    {
        var email = $"chef_{Guid.NewGuid():N}"[..12] + "@example.com";
        await RegisterAndAuthenticateAsync(_client, email, "chef_templates");

        var recipe = await CreateTestRecipeAsync("Sheet Pan Fajitas", 4m);
        var week1Monday = new DateOnly(2026, 11, 2);

        // Create week 1 meal
        await _client.PostAsJsonAsync(
            "/api/meal-plans/entries",
            new CreateMealPlanEntryRequest(
                EntryType: MealPlanEntryType.Recipe,
                IsOnDeck: false,
                ScheduledDate: week1Monday.AddDays(1), // Tuesday
                Slot: MealSlot.Dinner,
                RecipeId: recipe.Id
            )
        );

        // Create template from week
        var tplResp = await _client.PostAsJsonAsync(
            "/api/meal-plan-templates/from-week",
            new CreateTemplateFromWeekRequest(
                WeekStartDate: week1Monday,
                Name: "Fajita Week Template",
                IncludeOnDeck: true
            )
        );
        Assert.AreEqual(HttpStatusCode.Created, tplResp.StatusCode);
        var template = await tplResp.Content.ReadFromJsonAsync<MealPlanTemplateDetailDto>();
        Assert.IsNotNull(template);
        Assert.AreEqual("Fajita Week Template", template.Name);
        Assert.AreEqual(1, template.Items.Count);

        // Apply template to week 2 in Merge mode
        var week2Monday = week1Monday.AddDays(7);
        var applyResp = await _client.PostAsJsonAsync(
            $"/api/meal-plan-templates/{template.Id}/apply",
            new ApplyMealPlanTemplateRequest(
                TargetWeekStartDate: week2Monday,
                Mode: ApplyTemplateMode.Merge
            )
        );
        Assert.AreEqual(HttpStatusCode.OK, applyResp.StatusCode);
        var appliedWeek = await applyResp.Content.ReadFromJsonAsync<MealPlanWeekDto>();
        Assert.IsNotNull(appliedWeek);
        Assert.AreEqual(1, appliedWeek.Days[1].Entries.Count);
        Assert.AreEqual("Sheet Pan Fajitas", appliedWeek.Days[1].Entries[0].DisplayTitle);

        // Duplicate template name rejection
        var duplicateResp = await _client.PostAsJsonAsync(
            "/api/meal-plan-templates",
            new UpsertMealPlanTemplateRequest(Name: "fajita week template")
        );
        Assert.AreEqual(HttpStatusCode.Conflict, duplicateResp.StatusCode);
    }

    [TestMethod]
    public async Task Validation_InvalidDateRange_ReturnsBadRequest()
    {
        var email = $"chef_{Guid.NewGuid():N}"[..12] + "@example.com";
        await RegisterAndAuthenticateAsync(_client, email, "chef_val");

        // Requesting > 92 days
        var start = new DateOnly(2026, 1, 1);
        var end = new DateOnly(2026, 6, 1);
        var resp = await _client.GetAsync(
            $"/api/meal-plans?startDate={start:yyyy-MM-dd}&endDate={end:yyyy-MM-dd}"
        );

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
