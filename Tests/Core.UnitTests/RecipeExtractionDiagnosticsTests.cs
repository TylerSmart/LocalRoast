using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeExtractionDiagnosticsTests
{
    [TestMethod]
    public void GetReviewIssues_PreparedMixturePortionsRequireReviewEvenAfterAmountsWereErased()
    {
        var source = Source + "\nAdd 1 cup prepared stock to the pan.";
        var recipe = Recipe([Ingredient(4, "tbsp")], [new(null, "Heat oil and add stock.", [0])]);
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe, source));
        Assert.IsTrue(
            RecipeExtractionDiagnostics
                .GetReviewIssues(recipe, source)
                .Any(issue => issue.Contains("prepared stock"))
        );
    }

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
    public void GetIssues_SeparatedEggComponentsAreNotSummedAsWholeEggs()
    {
        const string source =
            "Ingredients:\n- 2 large eggs, separated\nInstructions:\nSeparate the eggs.";
        var whites = new LlmExtractedIngredientItem(
            null,
            2,
            null,
            "whole",
            "egg white",
            "egg whites",
            OriginalRawText: "2 large eggs, separated"
        );
        var yolks = whites with { CanonicalFoodName = "egg yolk", DisplayName = "egg yolks" };
        var recipe = Recipe(
            [whites, yolks],
            [new(null, "Beat whites.", [0]), new(null, "Mix yolks.", [1])]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe, source));
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
    public void GetIssues_UnmeasuredOptionalExtraDoesNotChangeKnownSourceTotal()
    {
        var recipe = Recipe(
            [
                Ingredient(4, "tbsp"),
                Ingredient(4, "tbsp") with
                {
                    Quantity = null,
                    Unit = null,
                    IsOptional = true,
                },
            ],
            [new(null, "Heat oil.", [0]), new(null, "Grease the dish if needed.", [1])]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe, Source));
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
    public void GetIssues_DetectsOmittedTimedPhaseButAcceptsNormalizerFallback()
    {
        var source =
            Source
            + "\nInstructions:\nCook first side for 2 to 3 minutes. Cook second side for 2 to 3 minutes.";
        var incomplete = Recipe(
            [Ingredient(4, "tbsp")],
            [new(null, "Cook the food.", [0], [new(120, "Cook first side")])]
        );
        Assert.IsTrue(
            RecipeExtractionDiagnostics
                .GetIssues(incomplete, source)
                .Any(i => i.Contains("missing a 120-second timer"))
        );
        var complete = Recipe(
            [Ingredient(4, "tbsp")],
            [
                new(
                    null,
                    "Cook first side for 2 to 3 minutes. Cook second side for 2 to 3 minutes.",
                    [0]
                ),
            ]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(complete, source));
    }

    [TestMethod]
    public void GetIssues_IdenticalLinesInDifferentSectionsAreNotDoubleCounted()
    {
        var source =
            "Ingredients:\n[First]\n- 4 tablespoons oil\n[Second]\n- 4 tablespoons oil\nInstructions:\nCook.";
        var recipe = Recipe(
            [Ingredient(4, "tbsp"), Ingredient(4, "tbsp")],
            [new(null, "Cook first sauce.", [0]), new(null, "Cook second sauce.", [1])]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe, source));
    }

    [TestMethod]
    public void GetIssues_FlagsDividedAdditionReusingWholeAmount()
    {
        var recipe = Recipe(
            [Ingredient(4, "tbsp")],
            [new(null, "Heat half the oil.", [0]), new(null, "Add remaining oil.", [0])]
        );
        Assert.IsTrue(
            RecipeExtractionDiagnostics
                .GetIssues(recipe, Source)
                .Any(i => i.Contains("divided addition"))
        );
        var split = Recipe(
            [Ingredient(2, "tbsp"), Ingredient(2, "tbsp")],
            [new(null, "Heat the frying oil.", [0]), new(null, "Add remaining oil.", [1])]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(split, Source));
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
    public void GetIssues_DetectsWrongTotalsWithoutGuessingCorrections()
    {
        var recipe = Recipe([Ingredient(2, "tbsp")], [new(null, "Heat the oil.", [0])]);
        var issues = RecipeExtractionDiagnostics.GetIssues(recipe, Source);
        Assert.IsTrue(issues.Any(i => i.Contains("source total")));
        Assert.AreEqual(2, recipe.Ingredients![0].Quantity);
    }

    [TestMethod]
    public void GetIssues_AcceptsConservedPortionsAndEquivalentUnits()
    {
        var recipe = Recipe(
            [Ingredient(2, "tbsp"), Ingredient(6, "tsp")],
            [new(null, "Heat the frying oil.", [0]), new(null, "Add the finishing oil.", [1])]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe, Source));
    }

    [TestMethod]
    public void GetIssues_AcceptsSourceProvidedWeightWithoutGuessingDensity()
    {
        var recipe = Recipe(
            [
                new(
                    null,
                    25,
                    null,
                    "oz",
                    "flour",
                    "flour",
                    OriginalRawText: "5 cups (25 ounces) flour"
                ),
            ],
            [new(null, "Mix the flour.", [0])]
        );
        Assert.IsEmpty(
            RecipeExtractionDiagnostics.GetIssues(
                recipe,
                "Ingredients:\n- 5 cups (25 ounces) flour\nInstructions:\nMix the flour."
            )
        );
        Assert.IsTrue(
            RecipeExtractionDiagnostics
                .GetIssues(
                    recipe with
                    {
                        Ingredients = [recipe.Ingredients![0] with { Quantity = 24 }],
                    },
                    "Ingredients:\n- 5 cups (25 ounces) flour\nInstructions:\nMix the flour."
                )
                .Any(i => i.Contains("source total"))
        );
    }

    [TestMethod]
    public void GetIssues_DetectsInvalidAndMissingIngredientLinksBeforeNormalization()
    {
        var issues = RecipeExtractionDiagnostics.GetIssues(
            Recipe([Ingredient(4, "tbsp")], [new(null, "Heat oil.", [1])])
        );
        Assert.IsTrue(issues.Any(i => i.Contains("out of range")));
        Assert.IsTrue(issues.Any(i => i.Contains("not linked")));
    }

    [TestMethod]
    public void GetIssues_PreservesProcessMeasurementsButFlagsFixedIngredientAmounts()
    {
        var recipe = Recipe(
            [Ingredient(4, "tbsp")],
            [
                new(
                    null,
                    "Heat oil in a 12-inch pan at 350 degrees. Simmer for 5 minutes until reduced to 1/3 cup.",
                    [0]
                ),
            ]
        );
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetIssues(recipe, Source));
        var invalid = recipe with { Steps = [new(null, "Add 2 tablespoons oil.", [0])] };
        Assert.IsTrue(
            RecipeExtractionDiagnostics
                .GetIssues(invalid, Source)
                .Any(i => i.Contains("fixed ingredient amounts"))
        );
    }

    [TestMethod]
    public void GetIssues_MixedFractionRangeAndOptionalExtraConserveTotal()
    {
        const string line = "1 1/2-2 tablespoons oil";
        var recipe = Recipe(
            [
                Ingredient(1.5m, "tbsp") with
                {
                    OriginalRawText = line,
                },
                Ingredient(0.5m, "tbsp") with
                {
                    OriginalRawText = line,
                    IsOptional = true,
                },
            ],
            [new(null, "Heat the oil.", [0]), new(null, "Add finishing oil if needed.", [1])]
        );
        Assert.IsEmpty(
            RecipeExtractionDiagnostics.GetIssues(
                recipe,
                $"Ingredients:\n- {line}\nInstructions:\nCook."
            )
        );
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
        Assert.IsTrue(
            RecipeExtractionDiagnostics
                .GetIssues(recipe, Source)
                .Any(i => i.Contains("not verbatim"))
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

    private const string Source = "Ingredients:\n- 4 tablespoons oil\nInstructions:\nCook.";

    private static LlmExtractedIngredientItem Ingredient(decimal quantity, string unit) =>
        new(null, quantity, null, unit, "oil", "oil", OriginalRawText: "4 tablespoons oil");

    private static LlmExtractedRecipePayload Recipe(
        LlmExtractedIngredientItem[] ingredients,
        LlmExtractedStepItem[] steps
    ) => new(Title: "Sauce", ConfidenceScore: 0.99m, Ingredients: ingredients, Steps: steps);
}
