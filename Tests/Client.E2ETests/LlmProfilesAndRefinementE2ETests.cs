using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class LlmProfilesAndRefinementE2ETests : E2ETestBase
{
    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_WorkflowModelAndProfileSelectionsPersistOnMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.SetViewportSizeAsync(360, 740);
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await AddLlmModelInDialogAsync(dialog, "gemma4:12b");
        await AddLlmProfileInDialogAsync(
            dialog,
            "gemma4:12b",
            "Thinking",
            tune: async editor =>
            {
                await editor.Locator("[data-testid='profile-form-thinking'] button").ClickAsync();
                await editor.GetByTestId("profile-form-timeout").FillAsync("900");
            }
        );

        // Assign the exact same saved model to Text (Defaults) and Refinement (Thinking) using the grouped profile selects
        await AssignLlmWorkflowAsync(dialog, "text", "gemma4:12b", profileName: "Defaults");

        // Before assigning a refinement profile, both refinement toggles are disabled and off
        var refineIngestionToggle = dialog.GetByTestId("refine-during-ingestion-toggle");
        var refineIdleToggle = dialog.GetByTestId("refine-during-idle-toggle");
        await Expect(refineIngestionToggle.Locator("button[role='switch']")).ToBeDisabledAsync();
        await Expect(refineIdleToggle.Locator("button[role='switch']")).ToBeDisabledAsync();
        await Expect(refineIngestionToggle)
            .Not.ToHaveClassAsync(new Regex("mat-mdc-slide-toggle-checked"));
        await Expect(refineIdleToggle)
            .Not.ToHaveClassAsync(new Regex("mat-mdc-slide-toggle-checked"));

        await AssignLlmWorkflowAsync(dialog, "refine", "gemma4:12b", profileName: "Thinking");

        await Expect(refineIngestionToggle.Locator("button[role='switch']")).ToBeEnabledAsync();
        await Expect(refineIdleToggle.Locator("button[role='switch']")).ToBeEnabledAsync();
        await refineIngestionToggle.Locator("button[role='switch']").ClickAsync();
        await refineIdleToggle.Locator("button[role='switch']").ClickAsync();
        await Expect(refineIngestionToggle)
            .ToHaveClassAsync(new Regex("mat-mdc-slide-toggle-checked"));
        await Expect(refineIdleToggle).ToHaveClassAsync(new Regex("mat-mdc-slide-toggle-checked"));

        var refineDetail = dialog.GetByTestId("workflow-refine-detail");
        await Expect(refineDetail).ToContainTextAsync($"gemma4:12b on {E2ELlmConnectionName}");
        await Expect(refineDetail).ToContainTextAsync("Thinking");

        var refineAssignment = dialog.Locator(
            ".workflow-assignment:has([data-testid='workflow-refine-detail'])"
        );
        await refineAssignment.ScrollIntoViewIfNeededAsync();
        Assert.IsTrue(
            await refineAssignment.EvaluateAsync<bool>(
                """
                section => {
                  const bounds = section.getBoundingClientRect();
                  return section.scrollWidth <= section.clientWidth
                    && [...section.querySelectorAll('mat-form-field, p, .refinement-toggles, mat-slide-toggle')]
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
        await SaveLlmWorkflowsAsync(dialog);

        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog.GetByTestId("workflow-text-select"))
            .ToContainTextAsync(LlmConfigName("gemma4:12b"));
        await Expect(dialog.GetByTestId("workflow-text-select")).ToContainTextAsync("Defaults");
        await Expect(dialog.GetByTestId("workflow-refine-select"))
            .ToContainTextAsync(LlmConfigName("gemma4:12b"));
        await Expect(dialog.GetByTestId("workflow-refine-select")).ToContainTextAsync("Thinking");
        await Expect(dialog.GetByTestId("refine-during-ingestion-toggle"))
            .ToHaveClassAsync(new Regex("mat-mdc-slide-toggle-checked"));
        await Expect(dialog.GetByTestId("refine-during-idle-toggle"))
            .ToHaveClassAsync(new Regex("mat-mdc-slide-toggle-checked"));

        await OpenLlmSettingsTabAsync(dialog, "Models");
        var modelCard = dialog.Locator(
            ".model-config-card",
            new() { HasText = LlmConfigName("gemma4:12b") }
        );
        await Expect(modelCard).ToContainTextAsync("2 profiles");
        await Expect(modelCard).ToContainTextAsync("Text");
        await Expect(modelCard).ToContainTextAsync("Refinement");

        await OpenLlmSettingsTabAsync(dialog, "Profiles");
        await Expect(GetLlmProfileCard(dialog, "gemma4:12b", "Defaults"))
            .ToContainTextAsync("Text");
        await Expect(GetLlmProfileCard(dialog, "gemma4:12b", "Thinking"))
            .ToContainTextAsync("Refinement");
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await ResetLlmSettingsViaApiAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_PerModelRateLimitsPersistClearAndBlockDuplicatesOnMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.SetViewportSizeAsync(360, 740);
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await AddLlmModelInDialogAsync(
            dialog,
            "gemini-2.5-flash",
            tune: async editor =>
            {
                await editor.GetByTestId("model-config-max-concurrent").FillAsync("2");
                await editor.GetByTestId("model-config-rpm").FillAsync("10");
                await editor.GetByTestId("model-config-rpd").FillAsync("250");
                await editor.GetByTestId("model-config-reset-timezone").ClickAsync();
                await Page.Locator("mat-option", new() { HasText = "Pacific Time" }).ClickAsync();
                await editor.GetByTestId("model-config-reset-time").FillAsync("00:00");
                Assert.IsTrue(
                    await editor.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"),
                    "Per-model rate limit controls must fit the model editor at 360px."
                );
            }
        );

        var card = dialog.Locator(".model-config-card", new() { HasText = "gemini-2.5-flash" });
        await Expect(card).ToContainTextAsync("Concurrent: 2");
        await Expect(card).ToContainTextAsync("RPM: 10");
        await Expect(card).ToContainTextAsync("RPD: 250");
        await Expect(card).ToContainTextAsync("Reset: 00:00 (America/Los_Angeles)");

        // Attempting to add the same model on the same connection is blocked upfront
        await dialog.GetByTestId("add-model-config-btn").ClickAsync();
        var dupEditor = Page.GetByTestId("model-config-editor-form");
        await dupEditor.GetByTestId("model-config-model").FillAsync("GEMINI-2.5-FLASH");
        await Page.Keyboard.PressAsync("Tab");
        await Expect(dupEditor.GetByTestId("duplicate-model-warning")).ToBeVisibleAsync();
        await Expect(dupEditor.GetByTestId("save-model-config-btn")).ToBeDisabledAsync();
        await dupEditor.GetByTestId("cancel-model-config-btn").ClickAsync();
        await Expect(dupEditor).Not.ToBeVisibleAsync();

        // Reload and verify persistence, then clear per-model rate limits
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
        await Page.ReloadAsync();
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await OpenLlmSettingsTabAsync(dialog, "Models");
        await card.GetByTestId("model-config-actions-menu-btn").ClickAsync();
        await Page.GetByTestId("edit-model-config-btn").ClickAsync();
        var editEditor = Page.GetByTestId("model-config-editor-form");
        await Expect(editEditor.GetByTestId("model-config-max-concurrent")).ToHaveValueAsync("2");
        await Expect(editEditor.GetByTestId("model-config-rpm")).ToHaveValueAsync("10");
        await Expect(editEditor.GetByTestId("model-config-rpd")).ToHaveValueAsync("250");
        await Expect(editEditor.GetByTestId("model-config-reset-time")).ToHaveValueAsync("00:00");

        await editEditor.GetByTestId("model-config-max-concurrent").FillAsync("");
        await editEditor.GetByTestId("model-config-rpm").FillAsync("");
        await editEditor.GetByTestId("model-config-rpd").FillAsync("");
        await editEditor.GetByTestId("model-config-reset-timezone").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = "Inherit from connection" })
            .ClickAsync();
        await editEditor.GetByTestId("model-config-reset-time").FillAsync("");
        await Page.RunAndWaitForResponseAsync(
            () => editEditor.GetByTestId("save-model-config-btn").ClickAsync(),
            resp =>
                resp.Url.Contains("/api/llm-settings/model-configurations/")
                && resp.Request.Method == "PUT"
                && resp.Ok
        );
        await Expect(editEditor).Not.ToBeVisibleAsync();
        await Expect(card).Not.ToContainTextAsync("RPM:");
        await Expect(card).Not.ToContainTextAsync("RPD:");
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_GpuLayersPersistAndClearOnMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.SetViewportSizeAsync(360, 740);
        await Page.Locator(".llm-settings-btn").ClickAsync();
        var dialog = Page.Locator("app-llm-settings-dialog");
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await AddLlmModelInDialogAsync(
            dialog,
            "gemma4:26b-a4b",
            tune: async editor =>
            {
                var input = editor.GetByTestId("model-config-gpu-layers");
                await Expect(input).ToHaveValueAsync("");
                await input.FillAsync("-1");
                await Expect(editor.GetByTestId("save-model-config-btn")).ToBeDisabledAsync();
                await input.FillAsync("1.5");
                await Expect(editor.GetByTestId("save-model-config-btn")).ToBeDisabledAsync();
                await input.FillAsync("24");
                await Expect(editor.GetByTestId("save-model-config-btn")).ToBeEnabledAsync();
                Assert.IsTrue(
                    await editor.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"),
                    "GPU layer controls and hints must fit the model editor at 360px."
                );
            }
        );
        var card = dialog.Locator(".model-config-card", new() { HasText = "gemma4:26b-a4b" });
        await Expect(card).ToContainTextAsync("GPU layers: 24");
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
        await Page.ReloadAsync();
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await OpenLlmSettingsTabAsync(dialog, "Models");
        await card.GetByTestId("model-config-actions-menu-btn").ClickAsync();
        await Page.GetByTestId("edit-model-config-btn").ClickAsync();
        var modelEditor = Page.GetByTestId("model-config-editor-form");
        await Expect(modelEditor.GetByTestId("model-config-gpu-layers")).ToHaveValueAsync("24");
        await modelEditor.GetByTestId("model-config-gpu-layers").FillAsync("");
        await Page.RunAndWaitForResponseAsync(
            () => modelEditor.GetByTestId("save-model-config-btn").ClickAsync(),
            resp =>
                resp.Url.Contains("/api/llm-settings/model-configurations/")
                && resp.Request.Method == "PUT"
                && resp.Ok
        );
        await Expect(modelEditor).Not.ToBeVisibleAsync();
        await Expect(card).Not.ToContainTextAsync("GPU layers:");
        await card.GetByTestId("model-config-actions-menu-btn").ClickAsync();
        await Page.GetByTestId("edit-model-config-btn").ClickAsync();
        await Expect(modelEditor.GetByTestId("model-config-gpu-layers")).ToHaveValueAsync("");
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            )
        );
        await modelEditor.GetByTestId("cancel-model-config-btn").ClickAsync();
        await Expect(modelEditor).Not.ToBeVisibleAsync();
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
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
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await OpenLlmSettingsTabAsync(dialog, "Models");
        await dialog.GetByTestId("add-model-config-btn").ClickAsync();
        var editor = Page.GetByTestId("model-config-editor-form");
        await Expect(editor).ToContainTextAsync("2 model(s) found on this connection");

        var options = Page.Locator("mat-option");
        var model = editor.GetByTestId("model-config-model");
        await model.ClickAsync();
        await Expect(options).ToHaveCountAsync(2);

        await model.PressSequentiallyAsync("VL 7");
        await Expect(options).ToHaveCountAsync(1);
        await Expect(options.First).ToHaveTextAsync("qwen2.5vl:7b");
        await options.First.ClickAsync();
        await Expect(model).ToHaveValueAsync("qwen2.5vl:7b");

        await model.FillAsync("gemini");
        await Expect(options).ToHaveCountAsync(1);
        await Expect(options.First).ToHaveTextAsync("No models match");
        await Expect(options.First).ToHaveAttributeAsync("aria-disabled", "true");
        await Page.Keyboard.PressAsync("Tab");

        await editor.GetByTestId("cancel-model-config-btn").ClickAsync();
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_GeminiProfilesAndRateLimitSafeguardsTogglePersist()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        const string connectionName = "Google AI Studio";
        await AddLlmConnectionInDialogAsync(
            dialog,
            mockServer.BaseUrl,
            connectionName,
            async editor =>
            {
                await editor.GetByTestId("connection-form-provider").ClickAsync();
                await Page.Locator("mat-option", new() { HasText = "OpenAI-Compatible" })
                    .ClickAsync();
                await Expect(editor.GetByTestId("connection-form-base-url"))
                    .ToHaveValueAsync(mockServer.BaseUrl);
                await editor.GetByTestId("connection-form-api-key").FillAsync("test-gemini-key");

                var safeguardsToggle = editor.Locator(
                    "mat-slide-toggle[formcontrolname='enableRateLimitSafeguards']"
                );
                await Expect(safeguardsToggle).ToBeVisibleAsync();
                await Expect(safeguardsToggle)
                    .ToHaveClassAsync(new Regex("mat-mdc-slide-toggle-checked"));

                await SetConcurrencySliderAsync(editor, "3");
                await editor.GetByTestId("rpm-input").FillAsync("15");
                await editor.GetByTestId("rpd-input").FillAsync("1500");
                await editor.GetByTestId("quota-reset-timezone-select").ClickAsync();
                await Page.Locator("mat-option", new() { HasText = "Pacific Time" }).ClickAsync();
                await editor.GetByTestId("quota-reset-time-input").FillAsync("00:00");
            }
        );
        var connectionCard = dialog.Locator(".connection-card", new() { HasText = connectionName });
        await Expect(connectionCard).ToContainTextAsync("Concurrent: 3");
        await Expect(connectionCard).ToContainTextAsync("RPM: 15");
        await Expect(connectionCard).ToContainTextAsync("RPD: 1500");
        await Expect(connectionCard).ToContainTextAsync("Key: ");

        await AddLlmModelInDialogAsync(
            dialog,
            "gemini-2.5-flash-lite",
            connectionName,
            async editor =>
            {
                await editor.GetByTestId("model-config-rpm").FillAsync("10");
                await editor.GetByTestId("model-config-rpd").FillAsync("1000");
            }
        );
        await AddLlmProfileInDialogAsync(
            dialog,
            "gemini-2.5-flash-lite",
            "Fast Lite",
            connectionName,
            async editor =>
            {
                // OpenAI-compatible cloud models hide the Ollama-only context window section
                await Expect(editor.GetByTestId("profile-context-section")).ToHaveCountAsync(0);
                await editor.GetByTestId("profile-form-timeout").FillAsync("180");
            }
        );
        await AssignLlmWorkflowAsync(
            dialog,
            "text",
            "gemini-2.5-flash-lite",
            connectionName,
            profileName: "Fast Lite"
        );
        await Expect(dialog.GetByTestId("auto-resume-toggle")).ToBeVisibleAsync();
        await SaveLlmWorkflowsAsync(dialog);

        // Reopen dialog and verify persisted settings
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog.GetByTestId("workflow-text-select"))
            .ToContainTextAsync(LlmConfigName("gemini-2.5-flash-lite", connectionName));
        await Expect(dialog.GetByTestId("workflow-text-select")).ToContainTextAsync("Fast Lite");
        await OpenLlmSettingsTabAsync(dialog, "Connections");
        await connectionCard.GetByTestId("connection-actions-menu-btn").ClickAsync();
        await Page.GetByTestId("edit-connection-btn").ClickAsync();
        var editor = Page.GetByTestId("connection-editor-form");
        await Expect(editor.Locator(".slider-field-value")).ToHaveTextAsync("3");
        await Expect(editor.GetByTestId("rpm-input")).ToHaveValueAsync("15");
        await Expect(editor.GetByTestId("rpd-input")).ToHaveValueAsync("1500");
        await Expect(editor.GetByTestId("quota-reset-timezone-select"))
            .ToContainTextAsync("Pacific Time");
        await Expect(editor.GetByTestId("quota-reset-time-input")).ToHaveValueAsync("00:00");
        await Expect(editor.GetByTestId("connection-form-api-key")).ToHaveValueAsync("");

        await editor.GetByTestId("cancel-connection-btn").ClickAsync();
        await Expect(editor).Not.ToBeVisibleAsync();
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
    }

    private static Task SetConcurrencySliderAsync(ILocator editor, string value) =>
        editor
            .GetByTestId("max-concurrent-slider")
            .EvaluateAsync(
                "(el, value) => { el.value = value; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); }",
                value
            );

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_EmbeddingWorkflowPersistsAndFitsOnMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        mockServer.ExtraModels.Add("embeddinggemma");
        await RegisterAndLoginAsync();
        await Page.SetViewportSizeAsync(360, 800);
        await Page.GotoAsync($"{ServerUrl}/ingest");

        await Page.Locator(".llm-settings-btn").ClickAsync();
        var dialog = Page.Locator("app-llm-settings-dialog");
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await AddLlmModelInDialogAsync(dialog, "qwen2.5:14b");
        await AddLlmModelInDialogAsync(dialog, "embeddinggemma");

        await AssignLlmWorkflowAsync(dialog, "text", "qwen2.5:14b");
        await AssignLlmWorkflowAsync(dialog, "embedding", "embeddinggemma");

        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "LLM Settings embedding workflow assignment must fit a 360px mobile viewport."
        );

        await SaveLlmWorkflowsAsync(dialog);

        await Page.Locator(".llm-settings-btn").ClickAsync();
        await OpenLlmSettingsTabAsync(dialog, "Workflows");
        await Expect(dialog.GetByTestId("workflow-embedding-select"))
            .ToContainTextAsync(LlmConfigName("embeddinggemma"));
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RefineDuringIngestion_CleansFoodCatalogBeforePublishAndShowsDualModelAttribution()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var recipeTitle = $"Ingest Refined Tofu {suffix}";
        var pollutedBrand = $"BrandXIngest{suffix}";

        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        mockServer.ExtraModels.Add("gemma4:26b-a4b");
        mockServer.ResponsePayload = $$"""
            {
              "title": "{{recipeTitle}}",
              "description": "Golden crisp tofu tossed in fragrant sesame sauce.",
              "baseServings": 4,
              "yieldDisplay": "4 bowls",
              "prepTimeMinutes": 15,
              "cookTimeMinutes": 10,
              "totalTimeMinutes": 25,
              "difficulty": "Easy",
              "sourceName": "Food Blog",
              "sourceAuthor": "Chef Alex",
              "ingredients": [
                {
                  "sortOrder": 0,
                  "displayName": "Garlic",
                  "quantity": 2,
                  "unit": "Clove",
                  "matchedFoodName": "Garlic",
                  "isPantryStaple": true,
                  "originalRawText": "2 cloves garlic, minced"
                },
                {
                  "sortOrder": 1,
                  "displayName": "{{pollutedBrand}} Sesame Oil",
                  "quantity": 2,
                  "unit": "Tbsp",
                  "isPantryStaple": false,
                  "originalRawText": "2 tbsp {{pollutedBrand}} Sesame Oil"
                }
              ],
              "steps": [
                {
                  "stepNumber": 1,
                  "instruction": "Heat oil and sauté minced garlic for 2 minutes.",
                  "ingredientIndices": [0, 1],
                  "timers": [
                    {
                      "durationSeconds": 120,
                      "label": "Sauté garlic"
                    }
                  ]
                }
              ],
              "suggestedTagNames": ["Quick & Easy"],
              "confidenceScore": 0.94
            }
            """;
        mockServer.Stage6RefinementPayload = """
            {
              "title": null,
              "description": null,
              "servings": null,
              "yield": null,
              "timing": null,
              "tags": null,
              "ingredientPatches": [
                {
                  "index": 1,
                  "displayName": "toasted sesame oil",
                  "quantity": null,
                  "maxQuantity": null,
                  "unit": null,
                  "prepNote": null,
                  "isOptional": null,
                  "canonicalFoodName": "Sesame Oil",
                  "replacementPatches": null
                }
              ],
              "stepPatches": [],
              "refinementNotes": ["Cleaned branded sesame oil catalog name before publishing."]
            }
            """;

        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();
        var dialog = Page.Locator("app-llm-settings-dialog");

        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await AddLlmModelInDialogAsync(dialog, "qwen2.5:14b");
        await AddLlmModelInDialogAsync(dialog, "gemma4:26b-a4b");

        await AssignLlmWorkflowAsync(dialog, "text", "qwen2.5:14b");
        await AssignLlmWorkflowAsync(dialog, "refine", "gemma4:26b-a4b");
        await dialog
            .GetByTestId("refine-during-ingestion-toggle")
            .Locator("button[role='switch']")
            .ClickAsync();
        var autoPublishToggle = dialog.Locator(
            "mat-slide-toggle[formcontrolname='autoPublishHighConfidenceText'] button[role='switch']"
        );
        if (await autoPublishToggle.GetAttributeAsync("aria-checked") != "true")
        {
            await autoPublishToggle.ClickAsync();
        }
        await SaveLlmWorkflowsAsync(dialog);

        await Page.Locator("textarea[formcontrolname='urlsText']")
            .FillAsync($"{mockServer.BaseUrl}/blog/crispy-sesame-tofu");
        await Page.Locator("button[aria-label='Queue recipe URLs']").ClickAsync();

        await Page.Locator("button.filter-pill", new() { HasText = "Published" }).ClickAsync();
        var jobCard = Page.Locator(".job-card", new() { HasText = recipeTitle }).First;
        await WaitForLocatorWithRefreshAsync(jobCard);

        await Expect(jobCard.GetByTestId("job-extracted-model-chip"))
            .ToContainTextAsync("qwen2.5:14b");
        await Expect(jobCard.GetByTestId("job-refined-model-chip"))
            .ToContainTextAsync("Refined: gemma4:26b-a4b");

        // Open published recipe via job overflow menu and verify both model pills + mobile fit
        await jobCard.Locator(".job-actions-menu-btn").ClickAsync();
        await Page.Locator("a.open-recipe-btn").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/.*"));
        await Expect(Page.GetByTestId("recipe-extracted-model-pill"))
            .ToContainTextAsync("qwen2.5:14b");
        await Expect(Page.GetByTestId("recipe-refined-model-pill"))
            .ToContainTextAsync("gemma4:26b-a4b");

        await Page.SetViewportSizeAsync(360, 740);
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "Recipe detail with both Ingested and Refined model pills must fit a 360px viewport."
        );

        // Follow refined model pill into Ingestion Review and verify both badges
        await Page.GetByTestId("recipe-refined-model-pill").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/ingest/review/.*"));
        await Expect(Page.GetByTestId("review-extracted-model-badge"))
            .ToContainTextAsync("Ingested: qwen2.5:14b");
        await Expect(Page.GetByTestId("review-refined-model-badge"))
            .ToContainTextAsync("Refined: gemma4:26b-a4b");

        // Verify Food Catalog has clean "Sesame Oil" and NEVER created the polluted brand item
        await Page.GotoAsync($"{ServerUrl}/catalog");
        var searchInput = Page.Locator("input[placeholder='e.g. olive oil, garlic']");
        await searchInput.FillAsync("Sesame Oil");
        await Expect(Page.Locator(".catalog-page")).ToContainTextAsync("Sesame Oil");

        await searchInput.FillAsync(pollutedBrand);
        await Expect(Page.Locator(".catalog-page .empty-state")).ToBeVisibleAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RefineDuringIdle_BackfillsExistingPublishedJobAndDisplaysRefinedModel()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var initialTitle = $"Idle Unrefined Tofu {suffix}";
        var refinedTitle = $"Idle Refined Tofu {suffix}";
        var pollutedBrand = $"BrandXIdle{suffix}";

        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        mockServer.ExtraModels.Add("gemma4:26b-a4b");
        mockServer.ResponsePayload = $$"""
            {
              "title": "{{initialTitle}}",
              "description": "Golden crisp tofu tossed in fragrant sesame sauce.",
              "baseServings": 4,
              "yieldDisplay": "4 bowls",
              "prepTimeMinutes": 15,
              "cookTimeMinutes": 10,
              "totalTimeMinutes": 25,
              "difficulty": "Easy",
              "sourceName": "Food Blog",
              "sourceAuthor": "Chef Alex",
              "ingredients": [
                {
                  "sortOrder": 0,
                  "displayName": "Garlic",
                  "quantity": 2,
                  "unit": "Clove",
                  "matchedFoodName": "Garlic",
                  "isPantryStaple": true,
                  "originalRawText": "2 cloves garlic, minced"
                },
                {
                  "sortOrder": 1,
                  "displayName": "{{pollutedBrand}} Sesame Oil",
                  "quantity": 2,
                  "unit": "Tbsp",
                  "isPantryStaple": false,
                  "originalRawText": "2 tbsp {{pollutedBrand}} Sesame Oil"
                }
              ],
              "steps": [
                {
                  "stepNumber": 1,
                  "instruction": "Heat oil and sauté minced garlic for 2 minutes.",
                  "ingredientIndices": [0, 1],
                  "timers": [
                    {
                      "durationSeconds": 120,
                      "label": "Sauté garlic"
                    }
                  ]
                }
              ],
              "suggestedTagNames": ["Quick & Easy"],
              "confidenceScore": 0.94
            }
            """;
        mockServer.Stage6RefinementPayload = $$"""
            {
              "title": "{{refinedTitle}}",
              "description": null,
              "servings": null,
              "yield": null,
              "timing": null,
              "tags": null,
              "ingredientPatches": [
                {
                  "index": 1,
                  "displayName": "toasted sesame oil",
                  "quantity": null,
                  "maxQuantity": null,
                  "unit": null,
                  "prepNote": null,
                  "isOptional": null,
                  "canonicalFoodName": "Sesame Oil",
                  "replacementPatches": null
                }
              ],
              "stepPatches": [],
              "refinementNotes": ["Idle refinement cleaned title and sesame oil catalog name."]
            }
            """;

        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();
        var dialog = Page.Locator("app-llm-settings-dialog");

        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await AddLlmModelInDialogAsync(dialog, "qwen2.5:14b");
        await AddLlmModelInDialogAsync(dialog, "gemma4:26b-a4b");

        // Assign Refinement profile but leave BOTH refinement toggles OFF initially
        await AssignLlmWorkflowAsync(dialog, "text", "qwen2.5:14b");
        await AssignLlmWorkflowAsync(dialog, "refine", "gemma4:26b-a4b");
        var autoPublishToggle = dialog.Locator(
            "mat-slide-toggle[formcontrolname='autoPublishHighConfidenceText'] button[role='switch']"
        );
        if (await autoPublishToggle.GetAttributeAsync("aria-checked") != "true")
        {
            await autoPublishToggle.ClickAsync();
        }
        await SaveLlmWorkflowsAsync(dialog);

        await Page.Locator("textarea[formcontrolname='urlsText']")
            .FillAsync($"{mockServer.BaseUrl}/blog/crispy-sesame-tofu");
        await Page.Locator("button[aria-label='Queue recipe URLs']").ClickAsync();

        await Page.Locator("button.filter-pill", new() { HasText = "Published" }).ClickAsync();
        var jobCard = Page.Locator(".job-card", new() { HasText = initialTitle }).First;
        await WaitForLocatorWithRefreshAsync(jobCard);

        // Initially ingested only — no refined badge yet
        await Expect(jobCard.GetByTestId("job-extracted-model-chip"))
            .ToContainTextAsync("qwen2.5:14b");
        await Expect(jobCard.GetByTestId("job-refined-model-chip")).ToHaveCountAsync(0);

        // Enable Refine during idle and verify background worker refines the published job automatically
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await dialog
            .GetByTestId("refine-during-idle-toggle")
            .Locator("button[role='switch']")
            .ClickAsync();
        await SaveLlmWorkflowsAsync(dialog);

        var refinedCard = Page.Locator(".job-card", new() { HasText = refinedTitle }).First;
        var refinedChip = refinedCard.GetByTestId("job-refined-model-chip");
        await WaitForLocatorWithRefreshAsync(refinedChip);
        await Expect(refinedChip).ToContainTextAsync("Refined: gemma4:26b-a4b");

        // Open the live recipe via job overflow menu and verify title update + dual model pills
        await refinedCard.Locator(".job-actions-menu-btn").ClickAsync();
        await Page.Locator("a.open-recipe-btn").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/recipes/.*"));
        await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(refinedTitle);
        await Expect(Page.GetByTestId("recipe-extracted-model-pill"))
            .ToContainTextAsync("qwen2.5:14b");
        await Expect(Page.GetByTestId("recipe-refined-model-pill"))
            .ToContainTextAsync("gemma4:26b-a4b");

        // Verify orphaned polluted food item was cleaned up from Food Catalog
        await Page.GotoAsync($"{ServerUrl}/catalog");
        var searchInput = Page.Locator("input[placeholder='e.g. olive oil, garlic']");
        await searchInput.FillAsync(pollutedBrand);
        await Expect(Page.Locator(".catalog-page .empty-state")).ToBeVisibleAsync();

        await searchInput.FillAsync("Sesame Oil");
        await Expect(Page.Locator(".catalog-page")).ToContainTextAsync("Sesame Oil");

        await ResetLlmSettingsViaApiAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task LlmSettingsDialog_DeleteConnectionAndAssignedModel_CascadesDependenciesAndDeselectsWorkflowsOnMobile()
    {
        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.SetViewportSizeAsync(360, 740);
        await Page.Locator(".llm-settings-btn").ClickAsync();

        var dialog = Page.Locator("app-llm-settings-dialog");
        const string googleConnection = "Google AI Studio";
        const string ollamaConnection = "Local Ollama";

        // 1. Add two connections: Google AI Studio (3 models + 1 custom profile) and Local Ollama (1 model)
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl, googleConnection);
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl, ollamaConnection);

        await AddLlmModelInDialogAsync(dialog, "models/gemini-3.8-flash", googleConnection);
        await AddLlmModelInDialogAsync(dialog, "models/gemini-4-argon", googleConnection);
        await AddLlmModelInDialogAsync(dialog, "models/gemini-4-hydrogen", googleConnection);
        await AddLlmModelInDialogAsync(dialog, "qwen2.5vl:7b", ollamaConnection);

        await AddLlmProfileInDialogAsync(
            dialog,
            "models/gemini-4-argon",
            "Deep Reasoning",
            googleConnection
        );

        // 2. Assign Google AI Studio models to Text and Refinement, and Local Ollama to Vision
        await AssignLlmWorkflowAsync(
            dialog,
            "text",
            "models/gemini-3.8-flash",
            googleConnection,
            profileName: "Defaults"
        );
        await AssignLlmWorkflowAsync(
            dialog,
            "refine",
            "models/gemini-4-argon",
            googleConnection,
            profileName: "Deep Reasoning"
        );
        await AssignLlmWorkflowAsync(
            dialog,
            "vision",
            "qwen2.5vl:7b",
            ollamaConnection,
            profileName: "Defaults"
        );
        await dialog
            .GetByTestId("refine-during-ingestion-toggle")
            .Locator("button[role='switch']")
            .ClickAsync();
        await SaveLlmWorkflowsAsync(dialog);

        // 3. Reopen dialog and delete Google AI Studio from the Connections tab
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await OpenLlmSettingsTabAsync(dialog, "Connections");

        var googleCard = dialog.Locator(".connection-card", new() { HasText = googleConnection });
        await Expect(googleCard).ToBeVisibleAsync();

        // Cancel confirmation first -> nothing deleted
        await googleCard.GetByTestId("connection-actions-menu-btn").ClickAsync();
        await Page.GetByTestId("delete-connection-btn").ClickAsync();
        var confirmDialog = Page.Locator("app-confirm-dialog");
        await Expect(confirmDialog).ToBeVisibleAsync();
        await Expect(confirmDialog).ToContainTextAsync("3 saved models and their profiles");
        await Expect(confirmDialog).ToContainTextAsync("Text and Refinement workflows");
        Assert.IsTrue(
            await Page.EvaluateAsync<bool>(
                "document.documentElement.scrollWidth <= window.innerWidth"
            ),
            "Connection delete confirmation dialog must fit a 360px viewport."
        );
        await confirmDialog
            .GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true })
            .ClickAsync();
        await Expect(confirmDialog).Not.ToBeVisibleAsync();
        await Expect(googleCard).ToBeVisibleAsync();

        // Confirm delete -> cascades models & profiles and deselects Text & Refinement workflows
        await googleCard.GetByTestId("connection-actions-menu-btn").ClickAsync();
        await Page.GetByTestId("delete-connection-btn").ClickAsync();
        await Expect(confirmDialog).ToBeVisibleAsync();
        await Page.RunAndWaitForResponseAsync(
            () =>
                confirmDialog
                    .GetByRole(AriaRole.Button, new() { Name = "Delete Connection", Exact = true })
                    .ClickAsync(),
            resp =>
                resp.Url.Contains("/api/llm-settings/connections/")
                && resp.Request.Method == "DELETE"
                && resp.Ok
        );
        await Expect(confirmDialog).Not.ToBeVisibleAsync();
        await Expect(googleCard).Not.ToBeVisibleAsync();

        // 4. Models and Profiles tabs only retain Local Ollama's model/profile
        await OpenLlmSettingsTabAsync(dialog, "Models");
        await Expect(dialog.Locator(".model-config-card")).ToHaveCountAsync(1);
        await Expect(dialog.Locator(".model-config-card")).ToContainTextAsync("qwen2.5vl:7b");

        await OpenLlmSettingsTabAsync(dialog, "Profiles");
        await Expect(dialog.GetByTestId("profile-cards-list").Locator(".profile-card"))
            .ToHaveCountAsync(1);
        await Expect(dialog.Locator(".profile-model-group-title"))
            .ToContainTextAsync("qwen2.5vl:7b");

        // 5. Workflows tab shows Text & Refinement deselected (and NOT auto-switched to Local Ollama) while Vision stays
        await OpenLlmSettingsTabAsync(dialog, "Workflows");
        await Expect(dialog.GetByTestId("workflow-text-select"))
            .ToContainTextAsync("Not assigned (text extraction off)");
        await Expect(dialog.GetByTestId("workflow-refine-select"))
            .ToContainTextAsync("Not assigned (refinement off)");
        await Expect(
                dialog
                    .GetByTestId("refine-during-ingestion-toggle")
                    .Locator("button[role='switch']")
            )
            .ToBeDisabledAsync();
        await Expect(dialog.GetByTestId("workflow-vision-select"))
            .ToContainTextAsync(LlmConfigName("qwen2.5vl:7b", ollamaConnection));

        // 6. Close WITHOUT clicking Save Settings, reopen, and verify server persisted workflow adjustments
        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog.GetByTestId("workflow-text-select"))
            .ToContainTextAsync("Not assigned (text extraction off)");
        await Expect(dialog.GetByTestId("workflow-refine-select"))
            .ToContainTextAsync("Not assigned (refinement off)");
        await Expect(dialog.GetByTestId("workflow-vision-select"))
            .ToContainTextAsync(LlmConfigName("qwen2.5vl:7b", ollamaConnection));

        // 7. Deleting the remaining assigned model directly confirms and deselects Vision
        await OpenLlmSettingsTabAsync(dialog, "Models");
        var visionCard = dialog.Locator(".model-config-card", new() { HasText = "qwen2.5vl:7b" });
        await visionCard.GetByTestId("model-config-actions-menu-btn").ClickAsync();
        await Page.GetByTestId("delete-model-config-btn").ClickAsync();
        await Expect(confirmDialog).ToBeVisibleAsync();
        await Expect(confirmDialog).ToContainTextAsync("deselect it from the Vision workflow");
        await Page.RunAndWaitForResponseAsync(
            () =>
                confirmDialog
                    .GetByRole(AriaRole.Button, new() { Name = "Delete Model", Exact = true })
                    .ClickAsync(),
            resp =>
                resp.Url.Contains("/api/llm-settings/model-configurations/")
                && resp.Request.Method == "DELETE"
                && resp.Ok
        );
        await Expect(visionCard).Not.ToBeVisibleAsync();

        await OpenLlmSettingsTabAsync(dialog, "Workflows");
        await Expect(dialog.GetByTestId("workflow-vision-select"))
            .ToContainTextAsync("Not assigned (photo and PDF scans off)");

        await dialog
            .GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true })
            .ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await ResetLlmSettingsViaApiAsync();
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task RecipeDetail_RefineNow_RefinesManualNonIngestedRecipeWhileQueuePausedOnMobile()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var initialTitle = $"Manual Unrefined Potatoes {suffix}";
        var refinedTitle = $"Manual Refined Potatoes {suffix}";
        var pollutedFoodName = $"PollutedCubePotato{suffix}";

        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        mockServer.ExtraModels.Add("gemma4:26b-a4b");
        mockServer.Stage6RefinementPayload = $$"""
            {
              "title": "{{refinedTitle}}",
              "description": "Crispy oven-roasted potatoes.",
              "servings": null,
              "yield": null,
              "timing": null,
              "tags": null,
              "ingredientPatches": [
                {
                  "index": 0,
                  "displayName": "yukon gold potatoes",
                  "quantity": null,
                  "maxQuantity": null,
                  "unit": null,
                  "prepNote": "cubed",
                  "isOptional": null,
                  "canonicalFoodName": "Yukon Gold Potato",
                  "category": "Produce",
                  "isPantryStaple": false,
                  "replacementPatches": null
                }
              ],
              "stepPatches": [],
              "refinementNotes": ["Cleaned manual recipe title and catalog item."]
            }
            """;

        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");

        // Configure Refinement model ONLY, leave both automatic refinement toggles OFF, and PAUSE ingestion
        await Page.Locator(".llm-settings-btn").ClickAsync();
        var dialog = Page.Locator("app-llm-settings-dialog");
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await AddLlmModelInDialogAsync(dialog, "gemma4:26b-a4b");
        await AssignLlmWorkflowAsync(dialog, "refine", "gemma4:26b-a4b");
        await SaveLlmWorkflowsAsync(dialog);

        var pauseBtn = Page.Locator(".toggle-ingestion-btn");
        if ((await pauseBtn.InnerTextAsync()).Contains("Pause Ingestion"))
        {
            await pauseBtn.ClickAsync();
            await Expect(pauseBtn).ToContainTextAsync("Resume Ingestion");
        }

        try
        {
            // Create a manually authored (non-ingested) recipe with a polluted FoodItem
            var badFoodId = await CreateFoodItemViaApiAsync(pollutedFoodName);
            var recipeId = await CreateRecipeWithIngredientViaApiAsync(
                initialTitle,
                pollutedFoodName,
                badFoodId
            );

            await Page.SetViewportSizeAsync(360, 740);
            await Page.GotoAsync($"{ServerUrl}/recipes/{recipeId}");
            await Expect(Page.Locator(".recipe-title")).ToContainTextAsync(initialTitle);
            await Expect(Page.GetByTestId("recipe-extracted-model-pill")).ToHaveCountAsync(0);
            await Expect(Page.GetByTestId("recipe-refined-model-pill")).ToHaveCountAsync(0);

            // Open Actions menu and trigger immediate refinement despite paused queue & no ingestion job
            await Page.Locator("button.recipe-actions-menu-btn").ClickAsync();
            var refineMenuItem = Page.GetByTestId("refine-recipe-menu-item");
            await Expect(refineMenuItem).ToBeVisibleAsync();
            await Expect(refineMenuItem).ToContainTextAsync("Refine Recipe");

            await Page.RunAndWaitForResponseAsync(
                () => refineMenuItem.ClickAsync(),
                resp => resp.Url.Contains("/refine-now") && resp.Status == 202
            );

            // Page auto-polls and updates with refined title + Refined Model pill (and still no Ingested Model pill)
            await Expect(Page.Locator(".recipe-title"))
                .ToContainTextAsync(refinedTitle, new() { Timeout = 25000 });
            await Expect(Page.GetByTestId("recipe-refined-model-pill"))
                .ToContainTextAsync("gemma4:26b-a4b");
            await Expect(Page.GetByTestId("recipe-extracted-model-pill")).ToHaveCountAsync(0);

            Assert.IsTrue(
                await Page.EvaluateAsync<bool>(
                    "document.documentElement.scrollWidth <= window.innerWidth"
                ),
                "Refined manual recipe detail must fit a 360px mobile viewport."
            );

            // Actions menu now offers Re-run Refinement
            await Page.Locator("button.recipe-actions-menu-btn").ClickAsync();
            await Expect(Page.GetByTestId("refine-recipe-menu-item"))
                .ToContainTextAsync("Re-run Refinement");
            await Page.Keyboard.PressAsync("Escape");

            // Orphaned polluted FoodItem was removed and clean catalog item exists
            await Page.GotoAsync($"{ServerUrl}/catalog");
            var searchInput = Page.Locator("input[placeholder='e.g. olive oil, garlic']");
            await searchInput.FillAsync(pollutedFoodName);
            await Expect(Page.Locator(".catalog-page .empty-state")).ToBeVisibleAsync();
            await searchInput.FillAsync("Yukon Gold Potato");
            await Expect(Page.Locator(".catalog-page")).ToContainTextAsync("Yukon Gold Potato");

            // Verify queue remained paused the whole time
            await Page.GotoAsync($"{ServerUrl}/ingest");
            await Expect(Page.Locator(".toggle-ingestion-btn"))
                .ToContainTextAsync("Resume Ingestion");
        }
        finally
        {
            await ResetLlmSettingsViaApiAsync();
        }
    }

    [TestMethod]
    [Timeout(DefaultTestTimeoutMs)]
    public async Task IngestionHub_RefineNowWhilePaused_RefinesOnlyChosenDraftAndFitsOnMobile()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var chosenInitialTitle = $"Chosen Draft Tofu {suffix}";
        var refinedTitle = $"Refined Chosen Tofu {suffix}";
        var heldInitialTitle = $"Held Draft Tofu {suffix}";

        await using var mockServer = await MockLanLlmAndBlogServer.StartAsync();
        mockServer.ExtraModels.Add("gemma4:26b-a4b");
        mockServer.Stage6RefinementPayload = $$"""
            {
              "title": "{{refinedTitle}}",
              "description": null,
              "servings": null,
              "yield": null,
              "timing": null,
              "tags": null,
              "ingredientPatches": [],
              "stepPatches": [],
              "refinementNotes": ["Polished chosen draft while paused."]
            }
            """;

        await RegisterAndLoginAsync();
        await Page.GotoAsync($"{ServerUrl}/ingest");
        await Page.Locator(".llm-settings-btn").ClickAsync();
        var dialog = Page.Locator("app-llm-settings-dialog");

        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);
        await AddLlmModelInDialogAsync(dialog, "qwen2.5:14b");
        await AddLlmModelInDialogAsync(dialog, "gemma4:26b-a4b");
        await AssignLlmWorkflowAsync(dialog, "text", "qwen2.5:14b");
        await AssignLlmWorkflowAsync(dialog, "refine", "gemma4:26b-a4b");
        // Keep AutoPublish OFF and both automatic refinement toggles OFF
        var autoPublishToggle = dialog.Locator(
            "mat-slide-toggle[formcontrolname='autoPublishHighConfidenceText'] button[role='switch']"
        );
        if (await autoPublishToggle.GetAttributeAsync("aria-checked") == "true")
        {
            await autoPublishToggle.ClickAsync();
        }
        await SaveLlmWorkflowsAsync(dialog);

        try
        {
            async Task SubmitRawDraftAsync(string title)
            {
                mockServer.ResponsePayload = $$"""
                    {
                      "title": "{{title}}",
                      "description": "Simple draft recipe.",
                      "baseServings": 4,
                      "yieldDisplay": "4 servings",
                      "prepTimeMinutes": 10,
                      "cookTimeMinutes": 15,
                      "totalTimeMinutes": 25,
                      "difficulty": "Easy",
                      "ingredients": [
                        {
                          "sortOrder": 0,
                          "displayName": "Garlic",
                          "quantity": 2,
                          "unit": "Clove",
                          "originalRawText": "2 cloves garlic"
                        }
                      ],
                      "steps": [
                        {
                          "stepNumber": 1,
                          "instruction": "Saute garlic for 2 minutes.",
                          "ingredientIndices": [0],
                          "timers": [{ "durationSeconds": 120, "label": "Saute" }]
                        }
                      ],
                      "suggestedTagNames": ["Quick & Easy"],
                      "confidenceScore": 0.90
                    }
                    """;

                await Page.Locator("button.tab-pill", new() { HasText = "Paste Raw Text" })
                    .ClickAsync();
                await Page.Locator("input[formcontrolname='titleHint']").FillAsync(title);
                await Page.Locator("textarea[formcontrolname='rawText']")
                    .FillAsync($"{title}\n2 cloves garlic\nSaute garlic for 2 minutes.");
                await Page.Locator("button[aria-label='Queue raw text recipe']").ClickAsync();

                var readyCard = Page.Locator(".job-card", new() { HasText = title }).First;
                await WaitForLocatorWithRefreshAsync(readyCard.Locator("a.review-action-btn"));
            }

            await SubmitRawDraftAsync(heldInitialTitle);
            await SubmitRawDraftAsync(chosenInitialTitle);

            // Pause ingestion queue
            var pauseBtn = Page.Locator(".toggle-ingestion-btn");
            await pauseBtn.ClickAsync();
            await Expect(pauseBtn).ToContainTextAsync("Resume Ingestion");

            await Page.SetViewportSizeAsync(360, 740);
            await Page.Locator("button.filter-pill", new() { HasText = "Review Inbox" })
                .ClickAsync();

            var chosenCard = Page.Locator(
                ".job-card",
                new() { HasText = chosenInitialTitle }
            ).First;
            var heldCard = Page.Locator(".job-card", new() { HasText = heldInitialTitle }).First;

            Assert.IsTrue(
                await Page.EvaluateAsync<bool>(
                    "document.documentElement.scrollWidth <= window.innerWidth"
                ),
                "Ingestion Hub cards with overflow menu must fit a 360px mobile viewport."
            );

            await chosenCard.Locator(".job-actions-menu-btn").ClickAsync();
            var refineNowBtn = Page.GetByTestId("refine-now-btn");
            await Expect(refineNowBtn).ToBeVisibleAsync();
            await Expect(refineNowBtn).ToContainTextAsync("Refine now");

            await Page.RunAndWaitForResponseAsync(
                () => refineNowBtn.ClickAsync(),
                resp => resp.Url.Contains("/refine-now") && resp.Status == 202
            );

            var refinedCard = Page.Locator(".job-card", new() { HasText = refinedTitle }).First;
            await WaitForLocatorWithRefreshAsync(refinedCard.GetByTestId("job-refined-model-chip"));
            await Expect(refinedCard.GetByTestId("job-refined-model-chip"))
                .ToContainTextAsync("Refined: gemma4:26b-a4b");

            // Held card stays unrefined and queue stays paused
            await Expect(heldCard.GetByTestId("job-refined-model-chip")).ToHaveCountAsync(0);
            await Expect(pauseBtn).ToContainTextAsync("Resume Ingestion");
        }
        finally
        {
            await ResetLlmSettingsViaApiAsync();
        }
    }
}
