using System.Globalization;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests.Ingestion;

[TestClass]
public sealed class Phase132ReconciliationTests
{
    private static LlmExtractedIngredientItem Ingredient(
        string name,
        decimal? quantity,
        string? unit,
        string? note = null
    ) =>
        new(
            SectionName: null,
            Quantity: quantity,
            MaxQuantity: null,
            Unit: unit,
            DisplayName: name,
            CanonicalFoodName: name,
            PreparationNote: note,
            OriginalRawText: $"{quantity} {unit} {name}".Trim()
        );

    // =========================================================================
    // Category 1: Source-Line Synthesis, Compound Measurements & "A or B" Deduplication
    // =========================================================================

    [TestMethod]
    public void Category1_TrySumCompoundLeadingMeasurement_SumsUnitsToLargest()
    {
        var m1 = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
            "1 teaspoon plus 2 tablespoons minced fresh sage"
        );
        Assert.IsNotNull(m1);
        Assert.AreEqual(MeasurementUnit.Tbsp, m1.Unit);
        // 1 tsp = 0.3333 tbsp; 2 + 0.3333 = 2.3333 tbsp
        Assert.AreEqual(2.3333m, m1.Quantity);

        var m2 = SourceMeasurementParser.TrySumCompoundLeadingMeasurement(
            "3 tablespoons plus 1/8 teaspoon sugar"
        );
        Assert.IsNotNull(m2);
        Assert.AreEqual(MeasurementUnit.Tbsp, m2.Unit);
        Assert.AreEqual(3.0417m, m2.Quantity);
    }

    [TestMethod]
    public void Category1_TryParseSourceIngredientLine_ExtractsBulletedItem()
    {
        var item = SourceMeasurementParser.TryParseSourceIngredientLine(
            "- 2 cups chicken broth",
            "Sauce"
        );
        Assert.IsNotNull(item);
        Assert.AreEqual(2m, item.Quantity);
        Assert.AreEqual("cup", item.Unit);
        Assert.AreEqual("chicken broth", item.CanonicalFoodName);
        Assert.AreEqual("Sauce", item.SectionName);
    }

    [TestMethod]
    public void Category1_RepairIngredientRows_SynthesizesMissingBulletedSourceLines()
    {
        const string source = """
            Ingredients:
            - 4 veal shanks
            - 2 cups chicken broth
            - 1 cup dry white wine

            Instructions:
            1. Brown shanks in pot.
            """;

        var existing = new List<LlmExtractedIngredientItem>
        {
            Ingredient("veal shanks", 4m, "whole") with
            {
                OriginalRawText = "- 4 veal shanks",
            },
            Ingredient("white wine", 1m, "cup") with
            {
                OriginalRawText = "- 1 cup dry white wine",
            },
        };

        var repaired = InstructionAmountTagger.RepairIngredientRows(existing, source);
        Assert.HasCount(3, repaired);
        Assert.AreEqual("chicken broth", repaired[1].CanonicalFoodName);
        Assert.AreEqual(2m, repaired[1].Quantity);
    }

    [TestMethod]
    public void Category1_RestoreSourceMeasurement_ClearsHallucinatedMaxQuantityOnFractions()
    {
        // "1/3 cup (2 1/3 ounces) sugar" where LLM mistakenly parsed 2.3333 as MaxQuantity
        var row = Ingredient("sugar", 0.3333m, "cup") with
        {
            MaxQuantity = 2.3333m,
            OriginalRawText = "1/3 cup (2 1/3 ounces) sugar",
        };

        var repaired = InstructionAmountTagger.RepairIngredientRows([row]);
        Assert.HasCount(1, repaired);
        Assert.IsNull(repaired[0].MaxQuantity);
        Assert.AreEqual(
            2.3333m,
            Math.Round(repaired[0].AlternativeMeasurement!.Quantity!.Value, 4)
        );
    }

    [TestMethod]
    public void Category1_RepairIngredientRows_DeduplicatesSplitOrAlternativesWithCommonHeadNoun()
    {
        // "1 cup shredded sharp cheddar cheese or Gruyere cheese"
        const string sourceLine = "1 cup shredded sharp cheddar cheese or Gruyere cheese";
        var rows = new List<LlmExtractedIngredientItem>
        {
            Ingredient("cheddar cheese", 1m, "cup") with
            {
                OriginalRawText = sourceLine,
                DisplayName = "sharp cheddar cheese",
                CanonicalFoodName = "cheddar cheese",
            },
            Ingredient("gruyere cheese", 1m, "cup") with
            {
                OriginalRawText = sourceLine,
                DisplayName = "Gruyere cheese",
                CanonicalFoodName = "Gruyere cheese",
            },
        };

        var repaired = InstructionAmountTagger.RepairIngredientRows(rows);
        Assert.HasCount(1, repaired);
        Assert.AreEqual("sharp cheddar cheese", repaired[0].DisplayName);
        StringAssert.Contains(repaired[0].PreparationNote, "Gruyere");
    }

    // =========================================================================
    // Category 2: AlternativeMeasurement-First Unit Conversion & Produce Conversions
    // =========================================================================

    [TestMethod]
    public void Category2_AlternativeMeasurementFirst_UsesListedEquivalentForGratedCheese()
    {
        // 1 oz Parmesan (1/2 cup) in Cheese Soufflé: step says 1/2 cup
        var ing = Ingredient("Parmesan cheese", 1m, "oz") with
        {
            OriginalRawText = "1 ounce Parmesan cheese, grated (about 1/2 cup)",
            AlternativeMeasurement = new LlmExtractedMeasurementItem(0.5m, "cup"),
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Dust prepared dish with 1/2 cup Parmesan."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 Parmesan cheese" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([ing], candidates, tags);
        Assert.IsEmpty(result.Issues);
    }

    [TestMethod]
    public void Category2_ScallionsProduceConversion_ConvertsVolumeToCount()
    {
        // 6 scallions: Step uses 1/4 cup scallion greens (2 scallions), rest in Step 2
        var ing = Ingredient("scallions", 6m, "whole") with
        {
            OriginalRawText = "6 scallions, white parts minced, green parts sliced thin",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Stir in white parts and 1/4 cup scallion greens."),
            new MethodParagraph(2, null, "Sprinkle remaining scallions on top."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 scallions" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>> { [2] = ["0 scallions"] }
        );

        var result = InstructionAmountTagger.Assemble([ing], candidates, tags);
        Assert.IsEmpty(result.Issues);
    }

    [TestMethod]
    public void Category2_Reconcile_ExemptsSubPortionLeafyGreensWithRemainingStep()
    {
        // 12 oz mustard greens: Step 1 uses 1/3 cup, Step 2 transfers remaining greens
        var ing = Ingredient("mustard greens", 12m, "oz") with
        {
            OriginalRawText = "12 ounces mustard greens, chopped",
        };

        var steps = new List<LlmExtractedStepItem>
        {
            new(
                null,
                "Transfer 1/3 cup greens to blender.",
                [0],
                [],
                [
                    new LlmExtractedStepIngredientAmountItem(
                        0,
                        new LlmExtractedMeasurementItem(0.3333m, "cup")
                    ),
                ],
                "Transfer 1/3 cup greens to blender."
            ),
            new(
                null,
                "Transfer remaining mustard greens to skillet.",
                [0],
                [],
                [],
                "Transfer remaining mustard greens to skillet."
            ),
        };

        var issues = InstructionAmountTagger.Reconcile([ing], steps);
        Assert.IsEmpty(issues);
    }

    // =========================================================================
    // Category 3: Per-Item/Per-Batch Multipliers & Rate/Conditional Phrases
    // =========================================================================

    [TestMethod]
    public void Category3_Assemble_ResolvesMultiStepBatchMultiplier()
    {
        // Moussaka: 1/2 cup oil; "Divide eggplant evenly between 2 prepared sheets. Toss each batch with 1/4 cup oil."
        var oil = Ingredient("olive oil", 0.5m, "cup") with
        {
            OriginalRawText = "1/2 cup olive oil",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(
                1,
                null,
                "Divide eggplant evenly between 2 prepared sheets. Toss each batch with 1/4 cup oil."
            ),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 olive oil" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([oil], candidates, tags);
        Assert.IsEmpty(result.Issues);
        Assert.AreEqual(0.5m, result.Steps[0].IngredientAmounts![0].Measurement.Quantity);
    }

    [TestMethod]
    public void Category3_Assemble_ResolvesSingleStepPerItemConsumption()
    {
        // Baked Sole Fillets: 2 teaspoons Dijon mustard; "Spread 1/2 teaspoon mustard on each fillet."
        var mustard = Ingredient("Dijon mustard", 2m, "tsp") with
        {
            OriginalRawText = "2 teaspoons Dijon mustard",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Spread 1/2 teaspoon mustard on each fillet."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 Dijon mustard" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([mustard], candidates, tags);
        Assert.IsEmpty(result.Issues);
        Assert.AreEqual(2m, result.Steps[0].IngredientAmounts![0].Measurement.Quantity);
    }

    [TestMethod]
    public void Category3_Assemble_ReclassifiesRatePhrasesToFixed()
    {
        // Aligot: 1/2 cup milk; "stir in 2 tablespoons milk at a time (up to 1/2 cup)"
        var milk = Ingredient("whole milk", 0.5m, "cup") with
        {
            OriginalRawText = "1/2 cup whole milk",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(
                1,
                null,
                "Stir in 2 tablespoons milk at a time (up to 1/2 cup) until smooth."
            ),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 whole milk", ["A2"] = "0 whole milk" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([milk], candidates, tags);
        Assert.IsEmpty(result.Issues);
        Assert.AreEqual(0.5m, result.Steps[0].IngredientAmounts![0].Measurement.Quantity);
    }

    [TestMethod]
    public void Category3_Assemble_ReclassifiesConditionalTopUpOverflowToFixed()
    {
        // Roast Turkey for a Crowd: 4 cups turkey broth; unconditional 4 cups + conditional "add up to 1 cup more if dry"
        var broth = Ingredient("turkey broth", 4m, "cup") with
        {
            OriginalRawText = "4 cups turkey broth",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Pour 4 cups broth into roasting pan."),
            new MethodParagraph(
                2,
                null,
                "Add up to 1 cup more broth if the liquid in pan has totally evaporated."
            ),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 turkey broth", ["A2"] = "0 turkey broth" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([broth], candidates, tags);
        Assert.IsEmpty(result.Issues);
    }

    // =========================================================================
    // Category 4: Multi-Row Spillover, Rendered Pan Fat & Unstated Remainder Linking
    // =========================================================================

    [TestMethod]
    public void Category4_Assemble_ReassignsSingleStepSumToSiblingRow()
    {
        // Beef Burgundy: Row 0 is stew water (4 cups), Row 1 is garnish water (3/4 cup).
        // Step 2 has two spans (1/2 cup + 1/4 cup = 3/4 cup) that belong to Row 1.
        var water1 = Ingredient("water", 4m, "cup") with
        {
            OriginalRawText = "4 cups water",
        };
        var water2 = Ingredient("water", 0.75m, "cup") with
        {
            OriginalRawText = "3/4 cup water",
            SectionName = "Garnish",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Add 4 cups water to pot."),
            new MethodParagraph(
                2,
                null,
                "In small saucepan, add 1/2 cup water. Simmer, then add remaining 1/4 cup water."
            ),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string>
            {
                ["A1"] = "0 water",
                ["A2"] = "0 water",
                ["A3"] = "0 water",
            },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([water1, water2], candidates, tags);
        Assert.IsEmpty(result.Issues);
        Assert.AreEqual(4m, result.Steps[0].IngredientAmounts![0].Measurement.Quantity);
        Assert.AreEqual(0.75m, result.Steps[1].IngredientAmounts![0].Measurement.Quantity);
        Assert.AreEqual(1, result.Steps[1].IngredientAmounts![0].IngredientIndex);
    }

    [TestMethod]
    public void Category4_Reconcile_SuppressesFalseErrorsAcrossSameFoodGroup()
    {
        // Beef Stir-Fry: 3 tbsp + 1 tsp vegetable oil.
        // Steps use 1 tbsp, 2 tbsp, and 1 tsp (total 3.333 tbsp = exactly 3 tbsp + 1 tsp).
        var oil1 = Ingredient("vegetable oil", 3m, "tbsp");
        var oil2 = Ingredient("vegetable oil", 1m, "tsp");

        var steps = new List<LlmExtractedStepItem>
        {
            new(
                null,
                "Heat 1 tablespoon oil.",
                [0],
                [],
                [
                    new LlmExtractedStepIngredientAmountItem(
                        0,
                        new LlmExtractedMeasurementItem(1m, "tbsp")
                    ),
                ],
                "Heat 1 tablespoon oil."
            ),
            new(
                null,
                "Heat 2 tablespoons oil.",
                [0],
                [],
                [
                    new LlmExtractedStepIngredientAmountItem(
                        0,
                        new LlmExtractedMeasurementItem(2m, "tbsp")
                    ),
                ],
                "Heat 2 tablespoons oil."
            ),
            new(
                null,
                "Add 1 teaspoon oil.",
                [1],
                [],
                [
                    new LlmExtractedStepIngredientAmountItem(
                        1,
                        new LlmExtractedMeasurementItem(1m, "tsp")
                    ),
                ],
                "Add 1 teaspoon oil."
            ),
        };

        var issues = InstructionAmountTagger.Reconcile([oil1, oil2], steps);
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public void Category4_Assemble_ReclassifiesRenderedPanFat()
    {
        // Braised Chicken Thighs: 2 tbsp oil; Step 2 says "Pour off all but 1 tablespoon fat."
        var oil = Ingredient("olive oil", 2m, "tbsp") with
        {
            OriginalRawText = "2 tablespoons olive oil",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Heat 2 tablespoons oil and brown chicken."),
            new MethodParagraph(2, null, "Pour off all but 1 tablespoon fat from skillet."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 olive oil", ["A2"] = "0 olive oil" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([oil], candidates, tags);
        Assert.IsEmpty(result.Issues);
    }

    [TestMethod]
    public void Category4_Reconcile_RecognizesBroadenedBatchRepetition()
    {
        // French Omelets: 2 tbsp butter; Step 1 uses 1 tbsp, Step 2 says "repeating instructions for second omelet"
        var butter = Ingredient("unsalted butter", 2m, "tbsp");

        var steps = new List<LlmExtractedStepItem>
        {
            new(
                null,
                "Melt 1 tablespoon butter in skillet.",
                [0],
                [],
                [
                    new LlmExtractedStepIngredientAmountItem(
                        0,
                        new LlmExtractedMeasurementItem(1m, "tbsp")
                    ),
                ],
                "Melt 1 tablespoon butter in skillet."
            ),
            new(
                null,
                "Repeating instructions for second omelet.",
                [0],
                [],
                [],
                "Repeating instructions for second omelet."
            ),
        };

        var issues = InstructionAmountTagger.Reconcile([butter], steps);
        Assert.IsEmpty(issues);
    }

    [TestMethod]
    public void Category4_Assemble_LinksUnstatedRemainderStepForAddTheOil()
    {
        // Steak Diane: 2 tbsp oil; Step 1 uses 1 tbsp; Step 3 says "Off heat, add the oil and shallot."
        var oil = Ingredient("vegetable oil", 2m, "tbsp") with
        {
            OriginalRawText = "2 tablespoons vegetable oil",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Heat 1 tablespoon oil and sear steaks."),
            new MethodParagraph(2, null, "Transfer steaks to plate."),
            new MethodParagraph(3, null, "Off the heat, add the oil and shallot to the skillet."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 vegetable oil" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([oil], candidates, tags);
        Assert.IsEmpty(result.Issues);
        Assert.AreEqual(1m, result.Steps[2].IngredientAmounts![0].Measurement.Quantity);
    }

    // =========================================================================
    // Category 5: Quality Warning Deduplication, "to taste" Auto-Linking & Divided Typo Auto-Heal
    // =========================================================================

    [TestMethod]
    public void Category5_GetQualityWarnings_DeduplicatesUnlinkedIngredientAcrossSteps()
    {
        // Mechouia: salt mentioned in Steps 1, 2, and 4
        var recipe = new LlmExtractedRecipePayload(
            Title: "Mechouia",
            Ingredients: [Ingredient("salt", null, null) with { CanonicalFoodName = "salt" }],
            Steps:
            [
                new LlmExtractedStepItem(
                    null,
                    "Sprinkle vegetables with salt.",
                    [],
                    [],
                    [],
                    "Sprinkle vegetables with salt."
                ),
                new LlmExtractedStepItem(
                    null,
                    "Toss vegetables with salt.",
                    [],
                    [],
                    [],
                    "Toss vegetables with salt."
                ),
                new LlmExtractedStepItem(
                    null,
                    "Season with salt to taste.",
                    [],
                    [],
                    [],
                    "Season with salt to taste."
                ),
            ]
        );

        var warnings = RecipeExtractionDiagnostics.GetQualityWarnings(recipe);
        // Should report salt at most once, not 3 times
        Assert.AreEqual(
            1,
            warnings.Count(w => w.Contains("salt", StringComparison.OrdinalIgnoreCase))
        );
    }

    [TestMethod]
    public void Category5_Assemble_AutoLinksUnmeasuredToTasteSeasoning()
    {
        var salt = Ingredient("salt", null, null) with
        {
            OriginalRawText = "Salt and pepper",
            CanonicalFoodName = "salt",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Season with salt and pepper to taste."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([salt], candidates, tags);
        CollectionAssert.Contains(result.Steps[0].IngredientIndices!.ToArray(), 0);
    }

    [TestMethod]
    public void Category5_Assemble_AutoHealsExplicitRemainingDividedTypo()
    {
        // Osso Buco: "3/8 teaspoon pepper, divided" vs 1/2 tsp + remaining 1/8 tsp = 5/8 tsp (0.625)
        var pepper = Ingredient("black pepper", 0.375m, "tsp") with
        {
            OriginalRawText = "3/8 teaspoon black pepper, divided",
            PreparationNote = "divided",
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Season shanks with 1/2 teaspoon pepper."),
            new MethodParagraph(2, null, "Stir in remaining 1/8 teaspoon pepper."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 black pepper", ["A2"] = "0 black pepper" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([pepper], candidates, tags);
        Assert.IsEmpty(result.Issues);
        Assert.IsNotNull(result.Ingredients);
        Assert.AreEqual(0.625m, result.Ingredients[0].Quantity);
    }
}
