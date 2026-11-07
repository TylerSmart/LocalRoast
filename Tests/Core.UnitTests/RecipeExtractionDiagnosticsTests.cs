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
                .Any(i =>
                    i.Contains(
                        "missing a 120-second timer (source has 2, extracted 1: \"Cook first side for 2 to 3 minutes.\"; \"Cook second side for 2 to 3 minutes.\")"
                    )
                )
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
        Assert.Contains(
            "Ingredient 0 \"oil\": measured ingredient is not linked to any step; step 1 mentions it, so add 0 to the ingredientIndices of each step that uses it.",
            issues
        );
    }

    [TestMethod]
    public void GetIssues_NamesTheStepsThatMentionAnUnlinkedIngredient()
    {
        LlmExtractedIngredientItem Item(string name) =>
            new(null, 1, null, "whole", name, name, OriginalRawText: $"1 {name}");
        var issues = RecipeExtractionDiagnostics.GetIssues(
            Recipe(
                [Item("unsalted butter"), Item("fennel bulb"), Item("bay leaves"), Item("saffron")],
                [
                    new(null, "Melt 2 tablespoons butter in a pot.", []),
                    new(null, "Add the fennel and bay leaves; cook until soft.", []),
                    new(null, "Stir in the remaining butters.", []),
                ]
            )
        );

        CollectionAssert.AreEqual(
            new[]
            {
                "Ingredient 0 \"unsalted butter\": measured ingredient is not linked to any step; steps 1 and 3 mention it, so add 0 to the ingredientIndices of each step that uses it.",
                "Ingredient 1 \"fennel bulb\": measured ingredient is not linked to any step; step 2 mentions it, so add 1 to the ingredientIndices of each step that uses it.",
                "Ingredient 2 \"bay leaves\": measured ingredient is not linked to any step; step 2 mentions it, so add 2 to the ingredientIndices of each step that uses it.",
                "Ingredient 3 \"saffron\": measured ingredient is not linked to any step.",
            },
            issues.Where(i => i.Contains("not linked")).ToArray()
        );
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
        var invalid = recipe with
        {
            Steps = [new(null, "Add 2 tablespoons oil, then 2 tablespoons more.", [0])],
        };
        Assert.Contains(
            "Step 1: contains fixed ingredient amounts (\"2 tablespoons\"); split measured portions and link them before removing those amounts from prose.",
            RecipeExtractionDiagnostics.GetIssues(invalid, Source)
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
    public void GetIssues_TruncatesLongTimedSentenceQuotes()
    {
        const string sentence =
            "Meanwhile, in the preheated oven, bake the cake on the middle rack, rotating the pan halfway through so it browns evenly on every side, for 45 minutes.";
        var source = $"{Source}\nInstructions:\n{sentence}";
        var issue = RecipeExtractionDiagnostics
            .GetIssues(Recipe([Ingredient(4, "tbsp")], [new(null, "Bake.", [0])]), source)
            .Single(i => i.StartsWith("Source timed phases:"));
        StringAssert.Contains(
            issue,
            $"(source has 1, extracted 0: \"{sentence[..117].TrimEnd()}...\")"
        );
    }

    [TestMethod]
    public void GetIssues_QuotesOnlyTheTimedSentenceAfterAParenthetical()
    {
        var source =
            $"{Source}\nInstructions:\n(Chill the dough if it softens.) Bake for 45 minutes.";
        var issue = RecipeExtractionDiagnostics
            .GetIssues(Recipe([Ingredient(4, "tbsp")], [new(null, "Bake.", [0])]), source)
            .Single(i => i.StartsWith("Source timed phases:"));
        StringAssert.Contains(issue, "(source has 1, extracted 0: \"Bake for 45 minutes.\")");
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
