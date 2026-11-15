using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace App.Client.E2ETests;

[TestClass]
public sealed class LlmProfilesAndRefinementE2ETests : E2ETestBase
{
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
        await AddLlmConnectionInDialogAsync(dialog, mockServer.BaseUrl);

        // A profile applied to a custom model keeps the model name but copies its tuning.
        await AddLlmModelInDialogAsync(
            dialog,
            "my-custom-e4b",
            tune: async editor =>
            {
                await ApplyProfileInModelEditorAsync(editor, "Gemma 4 12B with thinking");
                await Expect(editor.GetByTestId("model-config-model"))
                    .ToHaveValueAsync("my-custom-e4b");
                await Expect(editor.GetByTestId("model-config-temperature")).ToHaveValueAsync("1");
                await Expect(editor.GetByTestId("model-config-top-k")).ToHaveValueAsync("64");
                await Expect(editor.GetByTestId("model-config-timeout")).ToHaveValueAsync("900");
            }
        );

        // A profile applied to a blank model fills in the profile's own model tag.
        await AddModelFromProfileAsync(dialog, "Gemma 4 26B A4B", "gemma4:26b-a4b", "1200");

        await AssignLlmWorkflowAsync(dialog, "text", "my-custom-e4b");
        await AssignLlmWorkflowAsync(dialog, "refine", "gemma4:26b-a4b");
        var refineDetail = dialog.GetByTestId("workflow-refine-detail");
        await Expect(refineDetail).ToContainTextAsync($"gemma4:26b-a4b on {E2ELlmConnectionName}");
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
            .ToContainTextAsync(LlmConfigName("my-custom-e4b"));
        await Expect(dialog.GetByTestId("workflow-refine-select"))
            .ToContainTextAsync(LlmConfigName("gemma4:26b-a4b"));
        await OpenLlmSettingsTabAsync(dialog, "Models");
        var refineCard = dialog.Locator(
            ".model-config-card",
            new() { HasText = LlmConfigName("gemma4:26b-a4b") }
        );
        await Expect(refineCard).ToContainTextAsync("Temp: 1");
        await Expect(refineCard).ToContainTextAsync("Refinement");
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

        await AddModelFromProfileAsync(
            dialog,
            "Gemini Flash Lite",
            "gemini-2.5-flash-lite",
            connectionName: connectionName
        );
        await AssignLlmWorkflowAsync(dialog, "text", "gemini-2.5-flash-lite", connectionName);
        await Expect(dialog.GetByTestId("auto-resume-toggle")).ToBeVisibleAsync();
        await SaveLlmWorkflowsAsync(dialog);

        // Reopen dialog and verify persisted settings
        await Page.Locator(".llm-settings-btn").ClickAsync();
        await Expect(dialog.GetByTestId("workflow-text-select"))
            .ToContainTextAsync(LlmConfigName("gemini-2.5-flash-lite", connectionName));
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

    private async Task ApplyProfileInModelEditorAsync(ILocator editor, string profileName)
    {
        await editor.GetByTestId("llm-profile-select").ClickAsync();
        await Page.Locator("mat-option", new() { HasText = profileName }).ClickAsync();
        await editor.GetByTestId("llm-apply-profile").ClickAsync();
        await Expect(editor.Locator(".profile-message"))
            .ToContainTextAsync($"Applied {profileName}");
    }

    /// <summary>Adds a model configuration by applying a profile to a blank model field.</summary>
    private async Task AddModelFromProfileAsync(
        ILocator dialog,
        string profileName,
        string expectedModel,
        string? expectedTimeout = null,
        string connectionName = E2ELlmConnectionName
    )
    {
        await OpenLlmSettingsTabAsync(dialog, "Models");
        await dialog.GetByTestId("add-model-config-btn").ClickAsync();
        var editor = Page.GetByTestId("model-config-editor-form");
        var connectionSelect = editor.GetByTestId("model-config-connection");
        if (!(await connectionSelect.InnerTextAsync()).Contains(connectionName))
        {
            await connectionSelect.ClickAsync();
            await Page.Locator("mat-option", new() { HasText = connectionName }).ClickAsync();
        }
        await Expect(editor.GetByTestId("model-config-model")).ToHaveValueAsync("");
        await ApplyProfileInModelEditorAsync(editor, profileName);
        await Expect(editor.GetByTestId("model-config-model")).ToHaveValueAsync(expectedModel);
        if (expectedTimeout is not null)
        {
            await Expect(editor.GetByTestId("model-config-timeout"))
                .ToHaveValueAsync(expectedTimeout);
        }
        await Page.RunAndWaitForResponseAsync(
            () => editor.GetByTestId("save-model-config-btn").ClickAsync(),
            resp =>
                resp.Url.EndsWith("/api/llm-settings/model-configurations")
                && resp.Request.Method == "POST"
                && resp.Ok
        );
        await Expect(editor).Not.ToBeVisibleAsync();
        await Expect(
                dialog.Locator(
                    ".model-config-card",
                    new() { HasText = LlmConfigName(expectedModel, connectionName) }
                )
            )
            .ToBeVisibleAsync();
    }

    private static Task SetConcurrencySliderAsync(ILocator editor, string value) =>
        editor
            .GetByTestId("max-concurrent-slider")
            .EvaluateAsync(
                "(el, value) => { el.value = value; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); }",
                value
            );
}
