using System.Text.Json;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class LlmProfilesAndRefinementE2ETests : E2ETestBase
{
    private const string RefineModel = "qwen2.5vl:7b";

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_ProfilesApplyRecommendationsAndRefineSettingsPersistOnMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.SetViewportSizeAsync(360, 740);
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        var textModel = dialog.Locator("input[formcontrolname='textModel']");
        await textModel.FillAsync("my-custom-e4b");
        await Page.Keyboard.PressAsync("Tab");

        await dialog.Locator("[data-testid='llm-profile-select']").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Gemma 4 12B with thinking" })
            .ClickAsync();
        await dialog.Locator("[data-testid='llm-apply-profile']").ClickAsync();
        await Expect(dialog.Locator(".profile-message").First)
            .ToContainTextAsync("Applied Gemma 4 12B with thinking");
        await Expect(textModel).ToHaveValueAsync("my-custom-e4b");
        await Expect(dialog.Locator("input[formcontrolname='temperature']")).ToHaveValueAsync("1");
        await Expect(dialog.Locator("input[formcontrolname='topK']")).ToHaveValueAsync("64");
        await Expect(dialog.Locator("input[formcontrolname='requestTimeoutSeconds']"))
            .ToHaveValueAsync("900");
        await Expect(dialog.Locator("[data-testid='llm-extraction-strategy']"))
            .ToContainTextAsync("Single request");

        var refineSection = dialog.Locator(".refine-section");
        await refineSection.ScrollIntoViewIfNeededAsync();
        await refineSection.Locator("[data-testid='llm-refine-profile-select']").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Gemma 4 26B A4B" }).ClickAsync();
        await refineSection.Locator("[data-testid='llm-apply-refine-profile']").ClickAsync();
        var refineModelInput = refineSection.Locator("[data-testid='llm-refine-model']");
        await Expect(refineModelInput).ToHaveValueAsync("gemma4:26b-a4b");
        await Expect(refineSection.Locator("input[formcontrolname='refineRequestTimeoutSeconds']"))
            .ToHaveValueAsync("1200");

        Assert.IsTrue(
            await refineSection.EvaluateAsync<bool>(
                """
                section => {
                  const bounds = section.getBoundingClientRect();
                  return section.scrollWidth <= section.clientWidth
                    && [...section.querySelectorAll('input, button')]
                      .map(el => el.getBoundingClientRect())
                      .every(r => r.left >= bounds.left - 1 && r.right <= bounds.right + 1);
                }
                """
            ),
            "Refine settings must fit inside the dialog at 360px."
        );
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "LLM settings must fit a 360px viewport."
        );

        await dialog.Locator("button", new() { HasText = "Save Settings" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(textModel).ToHaveValueAsync("my-custom-e4b");
        await Expect(dialog.Locator("[data-testid='llm-extraction-strategy']"))
            .ToContainTextAsync("Single request");
        await Expect(refineModelInput).ToHaveValueAsync("gemma4:26b-a4b");
        await Expect(dialog.Locator("input[formcontrolname='refineTemperature']"))
            .ToHaveValueAsync("1");
        await dialog.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_ModelInputsFilterDiscoveredModelsAsYouType()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.SetViewportSizeAsync(360, 740);
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await dialog.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await dialog
            .Locator("button[aria-label='Test LLM connection and refresh models']")
            .ClickAsync();
        await Expect(dialog.Locator(".connection-status-banner.status-success")).ToBeVisibleAsync();

        var options = Page.Locator("mat-option");
        var textModel = dialog.Locator("input[formcontrolname='textModel']");
        await textModel.FillAsync("");
        await textModel.ClickAsync();
        await Expect(options).ToHaveCountAsync(2);

        await textModel.PressSequentiallyAsync("VL 7");
        await Expect(options).ToHaveCountAsync(1);
        await Expect(options.First).ToHaveTextAsync("qwen2.5vl:7b");
        await options.First.ClickAsync();
        await Expect(textModel).ToHaveValueAsync("qwen2.5vl:7b");

        var refineModel = dialog.Locator("[data-testid='llm-refine-model']");
        await refineModel.ScrollIntoViewIfNeededAsync();
        await refineModel.FillAsync("gemini");
        await Expect(options).ToHaveCountAsync(1);
        await Expect(options.First).ToHaveTextAsync("No discovered models match");
        await Expect(options.First).ToHaveAttributeAsync("aria-disabled", "true");
        await Page.Keyboard.PressAsync("Tab");

        await dialog.Locator("button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task ReviewRefinement_RefineModelProposalComparesAndAppliesOnMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        mockServer.ResponsePayload = """
            {
              "title": "Refine Journey", "baseServings": 4,
              "ingredients": [
                {"displayName":"butter","canonicalFoodName":"butter","quantity":1,"unit":"Tbsp","originalRawText":"1 tbsp butter"},
                {"displayName":"salt","canonicalFoodName":"salt","quantity":1,"unit":"Tsp","originalRawText":"1 tsp salt"}
              ],
              "steps": [
                {"instruction":"Melt butter and add salt.","ingredientIndices":[0],
                 "timers":[{"durationSeconds":60,"label":"Cook"}]}
              ]
            }
            """;
        mockServer.ResponsesByModel[RefineModel] = """
            {
              "title":"Refined Journey","baseServings":4,
              "ingredients":[
                {"displayName":"butter","canonicalFoodName":"butter","quantity":1,"unit":"Tbsp","originalRawText":"1 tbsp butter"},
                {"displayName":"salt","canonicalFoodName":"salt","quantity":1,"unit":"Tsp","originalRawText":"1 tsp salt"}
              ],
              "steps":[
                {"instruction":"Melt butter, then add salt.","ingredientIndices":[0,1],
                 "timers":[{"durationSeconds":60,"label":"Melt butter"}]}
              ]
            }
            """;

        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();
        var settings = Page.Locator("app-llm-settings-dialog");
        await settings.Locator("input[formcontrolname='baseUrl']").FillAsync(mockServer.BaseUrl);
        await settings.Locator("input[formcontrolname='textModel']").FillAsync("qwen2.5:14b");
        await settings.Locator("[data-testid='llm-refine-model']").FillAsync(RefineModel);
        await Page.Keyboard.PressAsync("Tab");
        await settings.Locator("button", new() { HasText = "Save Settings" }).ClickAsync();
        await Expect(settings).Not.ToBeVisibleAsync();

        await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" }).ClickAsync();
        await Page.Locator("textarea[formcontrolname='rawText']")
            .FillAsync(
                "Refine Journey\nIngredients:\n- 1 tbsp butter\n- 1 tsp salt\nInstructions:\nMelt butter and add salt."
            );
        var queuedResponse = await Page.RunAndWaitForResponseAsync(
            () => Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync(),
            response =>
                response.Url.EndsWith("/api/ingestion/jobs/text", StringComparison.Ordinal)
                && response.Request.Method == "POST"
        );
        Assert.IsTrue(queuedResponse.Ok);
        using var queuedJob = JsonDocument.Parse(await queuedResponse.TextAsync());
        var jobId = queuedJob.RootElement.GetProperty("id").GetGuid();
        try
        {
            var reviewButton = Page.Locator($"a.review-action-btn[href$='{jobId}']");
            await WaitForLocatorWithRefreshAsync(reviewButton);
            await reviewButton.ClickAsync();
            await Page.SetViewportSizeAsync(360, 800);

            var panel = Page.Locator("[data-testid='refinement-panel']");
            var compare = panel.Locator("[data-testid='compare-refinement']");
            for (var attempt = 0; attempt < 20 && !await compare.IsVisibleAsync(); attempt++)
            {
                await Page.WaitForTimeoutAsync(1500);
                if (!await compare.IsVisibleAsync())
                {
                    await Page.ReloadAsync();
                    await Page.Locator("input[formcontrolname='title']").WaitForAsync();
                }
            }
            await Expect(panel).ToContainTextAsync("A refined draft is ready");
            await Expect(panel).ToContainTextAsync(RefineModel);
            CollectionAssert.Contains(mockServer.RequestedModels, RefineModel);

            var title = Page.Locator("input[formcontrolname='title']");
            await Expect(title).ToHaveValueAsync("Refine Journey");
            await compare.ClickAsync();
            var preview = Page.Locator("app-ingestion-repair-dialog");
            await Expect(preview).ToBeVisibleAsync();
            await Expect(preview.Locator("h2")).ToHaveTextAsync("Review refined draft");
            await Expect(preview).ToContainTextAsync("Refined Journey");
            await Expect(preview).ToContainTextAsync("Melt butter: 60s");
            Assert.IsTrue(
                await preview.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"),
                "The refinement comparison must fit a 360px viewport."
            );
            Assert.IsTrue(
                await Page.EvaluateAsync<bool>(
                    "document.documentElement.scrollWidth <= window.innerWidth"
                ),
                "The review page must fit a 360px viewport."
            );
            await preview.Locator("button", new() { HasText = "Apply to editor" }).ClickAsync();
            await Expect(title).ToHaveValueAsync("Refined Journey");
            await Expect(
                    Page.Locator("mat-snack-bar-container")
                        .Filter(new() { HasText = "Refined draft applied to the editor." })
                )
                .ToBeVisibleAsync();

            await panel.Locator("[data-testid='dismiss-refinement']").ClickAsync();
            await Expect(panel).Not.ToBeVisibleAsync();
            await Expect(Page.Locator("[data-testid='request-refinement']"))
                .ToContainTextAsync($"Refine with {RefineModel}");
        }
        finally
        {
            var status = await Page.EvaluateAsync<int>(
                """
                async jobId => {
                    const tokenKey = Object.keys(localStorage).find(key => key.endsWith('-token'));
                    const token = tokenKey ? localStorage.getItem(tokenKey) : null;
                    const response = await fetch(`/api/ingestion/jobs/${jobId}`, {
                        method: 'DELETE',
                        headers: { Authorization: `Bearer ${token}` }
                    });
                    return response.status;
                }
                """,
                jobId.ToString()
            );
            Assert.AreEqual(204, status, "The refinement journey must remove its own job.");
        }
    }
}
