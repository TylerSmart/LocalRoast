using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeExtractionDiagnosticsTests
{
    [TestMethod]
    public void GetReviewIssues_DoesNotConfusePurchasedStockWithAnIntermediateMixture()
    {
        const string line = "4 cups chicken stock";
        var stock = new LlmExtractedIngredientItem(
            null,
            2,
            null,
            "cup",
            "chicken stock",
            "chicken stock",
            OriginalRawText: line
        );
        var recipe = Recipe(
            [stock, stock],
            [new(null, "Add the first stock portion.", [0]), new(null, "Add remaining stock.", [1])]
        );
        Assert.IsEmpty(
            RecipeExtractionDiagnostics.GetReviewIssues(
                recipe,
                $"Ingredients:\n- {line}\nInstructions:\nAdd 2 cups stock, then the remaining stock."
            )
        );
    }

    [TestMethod]
    public void NormalizePreparationNote_DoesNotAssignWholeSourceWeightsToSplitRows()
    {
        var portion = Ingredient(2, "tbsp") with
        {
            PreparationNote = "for frying",
            OriginalRawText = "4 tablespoons oil (2 ounces)",
        };
        LlmExtractedIngredientItem[] portions =
        [
            portion,
            portion with
            {
                PreparationNote = "for finishing",
            },
        ];
        Assert.AreEqual(
            "for frying",
            RecipeDraftNormalizer.NormalizePreparationNote(portion, portions)
        );
        Assert.AreEqual(
            "for frying",
            RecipeDraftNormalizer.NormalizePreparationNote(portion, [portion])
        );
    }

    [TestMethod]
    [DataRow("onion, finely chopped")]
    [DataRow("butter, cut into pieces")]
    [DataRow("pecans or walnuts")]
    public void GetIssues_FlagsPreparationOrAlternativesInFoodNames(string name)
    {
        var recipe = Recipe(
            [Ingredient(4, "tbsp") with { DisplayName = name }],
            [new(null, "Prepare the food.", [0])]
        );
        Assert.IsTrue(
            RecipeExtractionDiagnostics.GetIssues(recipe).Any(i => i.Contains("out of food names"))
        );
    }

    [TestMethod]
    public void GetIssues_AllowsPreparedProductNamesAndVarieties()
    {
        var recipe = Recipe(
            [Ingredient(4, "tbsp") with { DisplayName = "canned diced tomatoes" }],
            [new(null, "Heat the tomatoes.", [0])]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe));
    }

    [TestMethod]
    public void GetIssues_FlagsExpandedPreparationNotesBeforeAutoPublication()
    {
        var recipe = Recipe(
            [
                Ingredient(4, "tbsp") with
                {
                    PreparationNote = new string('x', 201),
                    OriginalRawText = "4 tbsp butter (2 ounces)",
                },
            ],
            [new(null, "Melt butter.", [0])]
        );
        Assert.IsTrue(
            RecipeExtractionDiagnostics.GetIssues(recipe).Any(i => i.Contains("200-character"))
        );
    }

    [TestMethod]
    public void GetIssues_AcceptsExplicitStepAmountsAndRecipeReferences()
    {
        var recipe = Recipe(
            [
                Ingredient(4, "tbsp"),
                new(
                    null,
                    1,
                    null,
                    "whole",
                    "",
                    "All-Butter Dough",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "All-Butter Dough"
                ),
            ],
            [
                new(null, "Heat the oil.", [0], IngredientAmounts: [new(0, new(2, "tbsp"))]),
                new(
                    null,
                    "Add the remaining oil and dough.",
                    [0, 1],
                    IngredientAmounts: [new(0, new(2, "tbsp"))]
                ),
            ]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe, Source));
        var invalid = recipe with
        {
            Steps = [new(null, "Add oil.", [0], IngredientAmounts: [new(2, new(2, "tbsp"))])],
        };
        Assert.IsTrue(
            RecipeExtractionDiagnostics.GetIssues(invalid).Any(i => i.Contains("out of range"))
        );
    }

    [TestMethod]
    public void GetIssues_DetectsInvalidIngredientLinksBeforeNormalization()
    {
        var issues = RecipeExtractionDiagnostics.GetIssues(
            Recipe([Ingredient(4, "tbsp")], [new(null, "Heat oil.", [1])])
        );
        Assert.IsTrue(issues.Any(i => i.Contains("out of range")));
    }

    [TestMethod]
    public void GetIssues_DetectsLostProvenanceAndAlternativeShoppingDuplicates()
    {
        const string source = "Ingredients:\n- 1 shallot or 1 garlic clove\nInstructions:\nCook.";
        var recipe = Recipe(
            [
                new(
                    null,
                    1,
                    null,
                    "whole",
                    "shallot",
                    "shallot",
                    OriginalRawText: "1 shallot or 1 garlic clove"
                ),
                new(
                    null,
                    1,
                    null,
                    "clove",
                    "garlic",
                    "garlic",
                    OriginalRawText: "1 shallot or 1 garlic clove"
                ),
            ],
            [new(null, "Cook aromatics.", [0, 1])]
        );
        Assert.IsTrue(
            RecipeExtractionDiagnostics
                .GetIssues(recipe, source)
                .Any(i => i.Contains("alternatives"))
        );
        Assert.Contains(
            "Source ingredient line 1: \"4 tablespoons oil\" is missing or its originalRawText is not verbatim; copy this exact text into originalRawText.",
            RecipeExtractionDiagnostics.GetIssues(recipe, Source)
        );
    }

    [TestMethod]
    public void GetIssues_QuotesMissingSourceLinesInFullAndUnnamedIngredientsBySourceText()
    {
        var line =
            "2 cups chicken broth, preferably homemade, warmed in a small saucepan over low heat until steaming and kept warm while you work";
        var source = $"Ingredients:\n- {line}\n- 4 tablespoons oil\nInstructions:\nCook.";
        var rewritten = Recipe(
            [new(null, 2, null, "cup", " ", " ", OriginalRawText: "2 cups broth")],
            [new(null, "Warm the broth.", [0])]
        );
        var issues = RecipeExtractionDiagnostics.GetIssues(rewritten, source);
        Assert.Contains(
            $"Source ingredient line 1: \"{line}\" is missing or its originalRawText is not verbatim; copy this exact text into originalRawText.",
            issues
        );
        Assert.Contains("Ingredient 0 \"2 cups broth\": missing a meaningful food name.", issues);

        var dropped = Recipe([Ingredient(4, "tbsp")], [new(null, "Heat the oil.", [0])]);
        Assert.Contains(
            $"Source ingredient line 1: \"{line}\" is missing; add an ingredient row with this exact originalRawText.",
            RecipeExtractionDiagnostics.GetIssues(dropped, source)
        );
    }

    [TestMethod]
    public void GetSourceIngredientLines_IgnoresHeadnotesAndSectionLabels()
    {
        var lines = RecipeExtractionDiagnostics.GetSourceIngredientLines(
            "Description:\n- Not an ingredient\nIngredients:\n[For the sauce]\n- 4 tablespoons oil\nInstructions:\n- Not an ingredient"
        );
        CollectionAssert.AreEqual(new[] { "4 tablespoons oil" }, lines.ToArray());
    }

    [TestMethod]
    public void GetQualityWarnings_SkipsMentionedWarning_WhenIngredientLinkedInEarlierStep()
    {
        var mushrooms = new LlmExtractedIngredientItem(
            null,
            8,
            null,
            "oz",
            "cremini mushrooms",
            "cremini mushrooms",
            OriginalRawText: "8 ounces cremini mushrooms, sliced"
        );
        var pasta = new LlmExtractedIngredientItem(
            null,
            1,
            null,
            "lb",
            "penne",
            "penne",
            OriginalRawText: "1 pound penne"
        );
        var recipe = Recipe(
            [mushrooms, pasta],
            [
                new(null, "Cook mushrooms in skillet until browned.", [0]),
                new(null, "Add pasta and basil to mushrooms and toss.", [1]),
            ]
        );
        var warnings = RecipeExtractionDiagnostics.GetQualityWarnings(recipe);
        Assert.IsFalse(
            warnings.Any(w => w.Contains("mushrooms") && w.Contains("mentioned but not linked")),
            "Should not warn about mushrooms mentioned in step 2 because it was linked in step 1."
        );
    }

    [TestMethod]
    public void GetIssues_FlagsDroppedPrimarySteps_WhileIgnoringAlternativeSubSteps()
    {
        var source = """
            Ingredients:
            - 4 tablespoons oil
            Instructions:
            1. Heat oil in skillet.
            2. 2A. FOR A CHARCOAL GRILL: Light coals.
            3. 2B. FOR A GAS GRILL: Heat burners.
            4. Cook food until browned.
            5. Rest and serve.
            """;

        // 4 primary steps (1, 2A, 4, 5) - 2B is secondary alternative
        Assert.AreEqual(4, RecipeExtractionDiagnostics.CountPrimarySourceSteps(source));

        var truncated = Recipe([Ingredient(4, "tbsp")], [new(null, "Heat oil in skillet.", [0])]);
        var issues = RecipeExtractionDiagnostics.GetIssues(truncated, source);
        Assert.IsTrue(
            issues.Any(i =>
                i.Contains(
                    "Extracted 1 step(s), but the source Instructions section has 4 primary steps"
                )
            ),
            $"Expected step count mismatch issue, got: {string.Join(", ", issues)}"
        );
        Assert.IsTrue(RecipeExtractionDiagnostics.IsHardStructuralIssue(issues[^1]));
        Assert.IsFalse(
            RecipeExtractionDiagnostics.IsHardStructuralIssue("Oil: not used in any step.")
        );
    }

    private const string Source = "Ingredients:\n- 4 tablespoons oil\nInstructions:\nCook.";

    private static LlmExtractedIngredientItem Ingredient(decimal quantity, string unit) =>
        new(null, quantity, null, unit, "oil", "oil", OriginalRawText: "4 tablespoons oil");

    private static LlmExtractedRecipePayload Recipe(
        LlmExtractedIngredientItem[] ingredients,
        LlmExtractedStepItem[] steps
    ) => new(Title: "Sauce", ConfidenceScore: 0.99m, Ingredients: ingredients, Steps: steps);
}
