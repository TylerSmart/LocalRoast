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
        await AssignLlmWorkflowAsync(dialog, "refine", "gemma4:12b", profileName: "Thinking");

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
                    && [...section.querySelectorAll('mat-form-field, p')]
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
        await card.GetByTestId("edit-model-config-btn").ClickAsync();
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
        await card.GetByTestId("edit-model-config-btn").ClickAsync();
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
        await card.GetByTestId("edit-model-config-btn").ClickAsync();
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
        await connectionCard.GetByTestId("edit-connection-btn").ClickAsync();
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
}
