using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class TaggedStepDiagnosticsTests
{
    [TestMethod]
    public void GetIssues_CleanStepTokensPass()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    null,
                    null,
                    null,
                    null,
                    null,
                    DisplayName: "unsalted butter"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Melt ||{\"ingredient\": \"unsalted butter\", \"amount\": 2, \"unit\": \"tbsp\"}|| in pan."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.AreEqual(0, issues.Count);
    }

    [TestMethod]
    public void GetIssues_FlagsBrokenJsonTokens()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    null,
                    null,
                    null,
                    null,
                    null,
                    DisplayName: "unsalted butter"
                ),
            ],
            Steps: [new LlmExtractedStepItem(null, "Melt ||{broken json}|| in pan.")]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(issues.Any(i => i.Contains("unparseable inline token")));
    }

    [TestMethod]
    public void GetIssues_FlagsNonPositiveAmount()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new LlmExtractedIngredientItem(null, null, null, null, null, DisplayName: "sugar"),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Add ||{\"ingredient\": \"sugar\", \"amount\": -2, \"unit\": \"tbsp\"}||."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(issues.Any(i => i.Contains("non-positive amount")));
    }

    [TestMethod]
    public void GetIssues_FlagsInvalidTimerDuration()
    {
        var recipe = new LlmExtractedRecipePayload(
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Simmer for ||{\"type\": \"timer\", \"seconds\": 0}||."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(issues.Any(i => i.Contains("invalid duration")));
    }
}
