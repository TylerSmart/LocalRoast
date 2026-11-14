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
                    2m,
                    null,
                    "tbsp",
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
                    2m,
                    null,
                    "tbsp",
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

    [TestMethod]
    public void GetQualityWarnings_DoesNotFlagIngredient_WhenPresentAsInlineToken()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new LlmExtractedIngredientItem(null, 2, null, "tbsp", null, DisplayName: "onion"),
                new LlmExtractedIngredientItem(
                    null,
                    1,
                    null,
                    "tsp",
                    null,
                    DisplayName: "kosher salt"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Place ||{\"ingredient\": \"onion\", \"amount\": 2, \"unit\": \"tbsp\"}|| and ||{\"ingredient\": \"kosher salt\", \"amount\": 1, \"unit\": \"tsp\"}|| on cutting board."
                ),
            ]
        );

        var warnings = RecipeExtractionDiagnostics.GetQualityWarnings(recipe);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void GetIssues_DoesNotFlagRepeatMentionOfFullRowAmount()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    null,
                    6,
                    null,
                    "whole",
                    null,
                    DisplayName: "large eggs"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Steam ||{\"ingredient\": \"large eggs\", \"amount\": 6, \"unit\": \"whole\"}|| in basket."
                ),
                new LlmExtractedStepItem(
                    null,
                    "Peel ||{\"ingredient\": \"large eggs\", \"amount\": 6, \"unit\": \"whole\"}|| under cold water."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.AreEqual(0, issues.Count);
    }

    [TestMethod]
    public void GetIssues_DuplicateIngredientAcrossSections_DoesNotCrossContaminateAmounts()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    SectionName: "BUFFALO SAUCE",
                    Quantity: 0.5m,
                    MaxQuantity: null,
                    Unit: "Cup",
                    CanonicalFoodName: "Hot Sauce",
                    DisplayName: "hot sauce"
                ),
                new LlmExtractedIngredientItem(
                    SectionName: "CAULIFLOWER",
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: "Tbsp",
                    CanonicalFoodName: "Hot Sauce",
                    DisplayName: "hot sauce"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    SectionName: "FOR THE BUFFALO SAUCE",
                    Instruction: "Whisk in ||{\"ingredient\": \"hot sauce\", \"amount\": 0.5, \"unit\": \"cup\"}||.",
                    IngredientIndices: [0]
                ),
                new LlmExtractedStepItem(
                    SectionName: "FOR THE CAULIFLOWER",
                    Instruction: "Whisk in ||{\"ingredient\": \"hot sauce\", \"amount\": 1, \"unit\": \"tbsp\"}||.",
                    IngredientIndices: [1]
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.AreEqual(0, issues.Count);
    }

    [TestMethod]
    public void GetIssues_FlagsOverTaggedIngredientInSingleStep()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    SectionName: null,
                    Quantity: 2m,
                    MaxQuantity: null,
                    Unit: "lb",
                    CanonicalFoodName: "eggplant",
                    DisplayName: "eggplant"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Turn ||{\"ingredient\": \"eggplant\"}|| until ||{\"ingredient\": \"eggplant\"}|| is soft and ||{\"ingredient\": \"eggplant\"}|| collapses."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(
            issues.Any(i => i.Contains("is tagged 3 times in the same step")),
            $"Expected over-tagging warning, got: {string.Join(", ", issues)}"
        );
    }

    [TestMethod]
    public void GetIssues_FlagsOverTaggedIngredientAcrossSteps()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    SectionName: null,
                    Quantity: 2m,
                    MaxQuantity: null,
                    Unit: "lb",
                    CanonicalFoodName: "eggplant",
                    DisplayName: "eggplant"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Prick ||{\"ingredient\": \"eggplant\", \"amount\": 2, \"unit\": \"lb\"}|| with fork."
                ),
                new LlmExtractedStepItem(
                    null,
                    "Turn ||{\"ingredient\": \"eggplant\"}|| occasionally."
                ),
                new LlmExtractedStepItem(null, "Steam ||{\"ingredient\": \"eggplant\"}|| in bowl."),
                new LlmExtractedStepItem(null, "Peel ||{\"ingredient\": \"eggplant\"}|| flesh."),
                new LlmExtractedStepItem(
                    null,
                    "Chop ||{\"ingredient\": \"eggplant\"}|| into cubes."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(
            issues.Any(i => i.Contains("tagged without amounts 4 times across steps")),
            $"Expected cross-step over-tagging warning, got: {string.Join(", ", issues)}"
        );
    }

    [TestMethod]
    public void GetIssues_FlagsUnbracketedJsonToken_WhenDelimitersMalformed()
    {
        var recipe = new LlmExtractedRecipePayload(
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Broil for |{\"type\": \"timer\", \"seconds\": 300}|."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(
            issues.Any(i =>
                i.Contains("unbracketed JSON token") && i.Contains("missing \"||\" delimiters")
            ),
            $"Expected unbracketed JSON issue, got: {string.Join(", ", issues)}"
        );
    }

    [TestMethod]
    public void GetIssues_FlagsBareJsonToken_WhenMalformedJsonAutoWrapped()
    {
        var recipe = new LlmExtractedRecipePayload(
            Steps: [new LlmExtractedStepItem(null, "Broil for {\"type\": \"timer\", broken}.")]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(
            issues.Any(i => i.Contains("unparseable inline token")),
            $"Expected unparseable token issue, got: {string.Join(", ", issues)}"
        );
    }

    [TestMethod]
    public void GetIssues_FlagsIngredientNamedAfterRecipeTitle()
    {
        var recipe = new LlmExtractedRecipePayload(
            Title: "Frico",
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: "lb",
                    CanonicalFoodName: "Frico",
                    DisplayName: "Frico",
                    OriginalRawText: "1 pound Montasio or aged Asiago cheese, grated fine"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Sprinkle ||{\"ingredient\": \"Frico\", \"amount\": 2, \"unit\": \"oz\"}|| over bottom of skillet."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(
            issues.Any(i => i.Contains("is named after the recipe title \"Frico\"")),
            $"Expected ingredient named after recipe title warning, got: {string.Join(", ", issues)}"
        );
    }

    [TestMethod]
    public void GetIssues_FlagsTruncatedStepEndingWithDanglingBrace()
    {
        var recipe = new LlmExtractedRecipePayload(
            Title: "Osso Buco",
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    null,
                    4m,
                    null,
                    "whole",
                    "veal shanks",
                    "veal shanks",
                    OriginalRawText: "4 veal shanks"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Sprinkle both sides of ||{\"ingredient\": \"veal shanks\", \"amount\": 4, \"unit\": \"whole\"}|| with {"
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(
            issues.Any(i => i.Contains("broken or unclosed inline token")),
            $"Expected broken token issue, got: {string.Join(", ", issues)}"
        );
        Assert.IsTrue(
            issues.Any(i => i.Contains("appears truncated at the end")),
            $"Expected truncated step issue, got: {string.Join(", ", issues)}"
        );
    }

    [TestMethod]
    public void GetIssues_FlagsUnmeasuredIngredientWithMeasuredStepAmount()
    {
        var recipe = new LlmExtractedRecipePayload(
            Title: "Patatas Bravas",
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    "POTATOES",
                    null,
                    null,
                    null,
                    "kosher salt",
                    "Kosher salt",
                    OriginalRawText: "Kosher salt"
                ),
            ],
            Steps:
            [
                new LlmExtractedStepItem(
                    "POTATOES",
                    "Add ||{\"ingredient\": \"Kosher salt\", \"amount\": 1.5, \"unit\": \"tsp\"}|| and boil."
                ),
            ]
        );

        var issues = TaggedStepDiagnostics.GetIssues(recipe);
        Assert.IsTrue(
            issues.Any(i =>
                i.Contains(
                    "has no quantity in the ingredient list, but steps use concrete measured amounts"
                )
            ),
            $"Expected unmeasured-with-step-amount issue, got: {string.Join(", ", issues)}"
        );
    }
}
