using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class InstructionAmountTaggerTests
{
    private const string SourdoughSource = """
        Title: Sourdough Starter

        Ingredients:
        - 4 1/2 cups (24 3/4 ounces) whole-wheat flour
        - 5 cups (25 ounces) all-purpose flour, plus extra for maintaining starter
        - Water, room temperature

        Instructions:
        1. Combine whole-wheat flour and all-purpose flour in large container. Using wooden spoon, mix 1 cup (5 ounces) flour mixture and 2/3 cup (5 1/3 ounces) room-temperature water in glass bowl until no dry flour remains (reserve remaining flour mixture). Cover with plastic wrap and let sit at room temperature until bubbly and fragrant, 48 to 72 hours.
        2. FEED STARTER: Measure out 1/4 cup (2 ounces) starter and transfer to clean bowl or jar; discard remaining starter. Stir 1/2 cup (2 1/2 ounces) flour mixture and 1/4 cup (2 ounces) water into starter until no dry flour remains. Cover with plastic wrap and let sit at room temperature for 24 hours.
        3. Repeat step 2 every 24 hours until starter is pleasantly aromatic and doubles in size 8 to 12 hours after being refreshed, about 10 to 14 days. (If baking, use starter once it has doubled in size during 8- to 12-hour window.)
        4. 4A. TO STORE AND MAINTAIN MATURE STARTER: Measure out 1/4 cup (2 ounces) starter and transfer to clean bowl. Let sit in 4-quart container at room temperature for 5 hours.
        5. 4B. TO PREPARE FOR BAKING: Eighteen to 24 hours before baking, stir 1 cup (5 ounces) all-purpose flour into starter. Transfer to refrigerator for at least 12 hours or up to 18 hours.
        """;

    [TestMethod]
    public void ParseMethod_StripsNumberingAndTurnsCapitalHeadingsIntoSections()
    {
        var paragraphs = InstructionAmountTagger.ParseMethod(SourdoughSource);

        Assert.HasCount(5, paragraphs);
        Assert.IsNull(paragraphs[0].SectionName);
        StringAssert.StartsWith(paragraphs[0].Text, "Combine whole-wheat flour");
        Assert.AreEqual("Feed starter", paragraphs[1].SectionName);
        StringAssert.StartsWith(paragraphs[1].Text, "Measure out 1/4 cup");
        Assert.AreEqual("Feed starter", paragraphs[2].SectionName);
        Assert.AreEqual("To store and maintain mature starter", paragraphs[3].SectionName);
        StringAssert.StartsWith(paragraphs[3].Text, "Measure out");
        Assert.AreEqual("To prepare for baking", paragraphs[4].SectionName);
    }

    [TestMethod]
    public void FindCandidates_LocatesAmountsWithEquivalentsAndDurations()
    {
        var candidates = InstructionAmountTagger.FindCandidates(
            InstructionAmountTagger.ParseMethod(SourdoughSource)
        );
        string TextOf(int paragraph, int start, int length) =>
            candidates.Paragraphs[paragraph - 1].Text.Substring(start, length);

        var first = candidates.Amounts.Where(a => a.Paragraph == 1).ToList();
        Assert.HasCount(2, first);
        Assert.AreEqual("1 cup (5 ounces)", TextOf(1, first[0].Start, first[0].Length));
        Assert.AreEqual(new RecipeMeasurement(1m, MeasurementUnit.Cup), first[0].Measurement);
        Assert.AreEqual(new RecipeMeasurement(5m, MeasurementUnit.Oz), first[0].Alternative);
        Assert.AreEqual("2/3 cup (5 1/3 ounces)", TextOf(1, first[1].Start, first[1].Length));
        // "4-quart container" is equipment, not an amount.
        Assert.HasCount(1, candidates.Amounts.Where(a => a.Paragraph == 4));

        var seconds = candidates
            .Durations.GroupBy(d => d.Paragraph)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Seconds).ToList());
        CollectionAssert.AreEqual(new[] { 172800 }, seconds[1]);
        CollectionAssert.AreEqual(new[] { 86400 }, seconds[2]);
        // Frequencies ("every 24 hours"), schedules ("before baking"), windows and limits are skipped.
        CollectionAssert.AreEqual(new[] { 864000 }, seconds[3]);
        CollectionAssert.AreEqual(new[] { 18000 }, seconds[4]);
        CollectionAssert.AreEqual(new[] { 43200 }, seconds[5]);
        var refrigerate = candidates.Durations.Single(d => d.Paragraph == 5);
        Assert.AreEqual("12 hours", TextOf(5, refrigerate.Start, refrigerate.Length));
    }

    [TestMethod]
    public void FindSpans_CombinesPlusAmountsIntoTheSmallerUnit()
    {
        var spans = SourceMeasurementParser.FindSpans(
            "Whisk in 1/4 cup plus 2 tablespoons sugar and 1 teaspoon salt."
        );

        Assert.HasCount(2, spans);
        Assert.AreEqual(new RecipeMeasurement(6m, MeasurementUnit.Tbsp), spans[0].Measurement);
        Assert.AreEqual(9, spans[0].Index);
        Assert.AreEqual("1/4 cup plus 2 tablespoons".Length, spans[0].Length);
    }

    [TestMethod]
    public void Assemble_KeepsTextVerbatimAndGivesTheRemainderToTheOneUntaggedStep()
    {
        var ingredients = new List<LlmExtractedIngredientItem>
        {
            Ingredient("unsalted butter", 4m, "tbsp"),
            Ingredient("chicken broth", 3m, "cup"),
        };
        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(
                1,
                null,
                "Melt 2 tablespoons butter. Add 2 cups broth; simmer until reduced to 1 cup, 10 to 12 minutes."
            ),
            new MethodParagraph(
                2,
                null,
                "Whisk in remaining butter and broth. Serve within 1 hour."
            ),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string>
            {
                ["A1"] = "0 unsalted butter",
                ["A2"] = "1 chicken broth",
                ["A3"] = InstructionAmountTagger.OtherLabel,
            },
            new Dictionary<string, string> { ["T1"] = "Simmer broth", ["T2"] = "" },
            new Dictionary<int, IReadOnlyList<string>>
            {
                [1] = ["0 unsalted butter", "1 chicken broth"],
                [2] = ["0 unsalted butter", "1 chicken broth"],
            }
        );

        var result = InstructionAmountTagger.Assemble(ingredients, candidates, tags);

        Assert.IsEmpty(result.Issues);
        var first = result.Steps[0];
        Assert.AreEqual(candidates.Paragraphs[0].Text, first.Instruction);
        Assert.HasCount(3, first.InlineAmounts!);
        Assert.AreEqual(InlineStepAmountKind.Fixed, first.InlineAmounts![2].Kind);
        Assert.AreEqual("1 cup", first.InlineAmounts[2].Text);
        Assert.AreEqual(600, first.Timers!.Single().DurationSeconds);
        var second = result.Steps[1];
        Assert.IsEmpty(second.Timers!);
        Assert.AreEqual(
            2m,
            second.IngredientAmounts!.Single(a => a.IngredientIndex == 0).Measurement.Quantity
        );
        Assert.AreEqual(
            1m,
            second.IngredientAmounts!.Single(a => a.IngredientIndex == 1).Measurement.Quantity
        );
        Assert.AreEqual(
            "cup",
            second.IngredientAmounts!.Single(a => a.IngredientIndex == 1).Measurement.Unit
        );
    }

    [TestMethod]
    public void Assemble_ConvertsWeightsThroughTheListedEquivalentAndFlagsOverages()
    {
        var flour = Ingredient("all-purpose flour", 1m, "cup") with
        {
            AlternativeMeasurement = new LlmExtractedMeasurementItem(5m, "oz"),
        };
        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Stir in 2 1/2 ounces flour, then 4 ounces flour."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string>
            {
                ["A1"] = "0 all-purpose flour",
                ["A2"] = "0 all-purpose flour",
            },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble([flour], candidates, tags);

        Assert.AreEqual(6.5m, result.Steps[0].IngredientAmounts!.Single().Measurement.Quantity);
        Assert.HasCount(1, result.Issues);
        StringAssert.Contains(result.Issues[0], "exceed the ingredient list (1 cup)");
    }

    [TestMethod]
    public void Assemble_MixtureAndUnclassifiedAmountsAreNotIngredientPortions()
    {
        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Mix 1 cup flour mixture with 1/4 cup water."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = InstructionAmountTagger.MixtureLabel },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble(
            [Ingredient("water", null, null)],
            candidates,
            tags
        );

        var inline = result.Steps[0].InlineAmounts!;
        Assert.AreEqual(InlineStepAmountKind.Mixture, inline[0].Kind);
        Assert.AreEqual(InlineStepAmountKind.Fixed, inline[1].Kind);
        Assert.IsEmpty(result.Steps[0].IngredientAmounts!);
        StringAssert.Contains(result.Issues.Single(), "\"1/4 cup\" was not classified");
    }

    [TestMethod]
    public void Assemble_LaterAmountsComeFromTheExtraRowOnceTheMainRowWasUsedUp()
    {
        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Combine flour in large container."),
            new MethodParagraph(2, null, "Stir 1 cup flour into starter."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 all-purpose flour" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>> { [1] = ["0 all-purpose flour"] }
        );

        var result = InstructionAmountTagger.Assemble(
            [
                Ingredient("all-purpose flour", 5m, "cup"),
                Ingredient("all-purpose flour", null, null),
            ],
            candidates,
            tags
        );

        Assert.IsEmpty(result.Issues);
        CollectionAssert.AreEqual(new[] { 0 }, result.Steps[0].IngredientIndices!.ToArray());
        Assert.IsEmpty(result.Steps[0].IngredientAmounts!);
        var second = result.Steps[1];
        Assert.AreEqual(1, second.IngredientAmounts!.Single().IngredientIndex);
        CollectionAssert.AreEqual(new[] { 1 }, second.IngredientIndices!.ToArray());
        Assert.AreEqual(1, second.InlineAmounts!.Single().IngredientIndex);
    }

    [TestMethod]
    public void Assemble_LinksAnUnlinkedMeasuredRowToTheFirstStepNamingIt()
    {
        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Stir in flour mixture."),
            new MethodParagraph(2, null, "Add shrimp shells, water and peppercorns; simmer."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble(
            [
                Ingredient("water", 7m, "cup"),
                Ingredient("black peppercorns", 15m, "whole"),
                Ingredient("all-purpose flour", 1m, "cup"),
            ],
            candidates,
            tags
        );

        Assert.IsEmpty(result.Steps[0].IngredientIndices!);
        CollectionAssert.AreEqual(new[] { 0, 1 }, result.Steps[1].IngredientIndices!.ToArray());
        Assert.HasCount(1, result.Issues);
    }

    [TestMethod]
    public void Assemble_MovesAnOverflowingAmountToTheMatchingRowOfTheSameFood()
    {
        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Mix 1 tablespoon flour with butter; grease pan."),
            new MethodParagraph(2, null, "Whisk 3 cups flour and salt."),
        ]);
        var tags = new MethodTags(
            new Dictionary<string, string>
            {
                ["A1"] = "0 all-purpose flour",
                ["A2"] = "0 all-purpose flour",
            },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>()
        );

        var result = InstructionAmountTagger.Assemble(
            [
                Ingredient("all-purpose flour", 3m, "cup"),
                Ingredient("all-purpose flour", 1m, "tbsp"),
            ],
            candidates,
            tags
        );

        Assert.IsEmpty(result.Issues);
        CollectionAssert.AreEqual(new[] { 1 }, result.Steps[0].IngredientIndices!.ToArray());
        Assert.AreEqual(1, result.Steps[0].InlineAmounts!.Single().IngredientIndex);
        Assert.AreEqual(3m, result.Steps[1].IngredientAmounts!.Single().Measurement.Quantity);
    }

    [TestMethod]
    public void RepairIngredientRows_FoldsOrAlternativesAndRestoresEquivalents()
    {
        const string nuts = "1 cup (4 ounces) pecans or walnuts, toasted and chopped";
        const string flour = "- 5 cups (25 ounces) all-purpose flour, plus extra for feeding";
        var rows = InstructionAmountTagger.RepairIngredientRows([
            Ingredient("pecan", 1m, "cup") with
            {
                OriginalRawText = nuts,
                PreparationNote = "chopped",
            },
            Ingredient("walnut", 1m, "cup") with
            {
                OriginalRawText = nuts,
            },
            Ingredient("all-purpose flour", 5m, "cup") with
            {
                OriginalRawText = flour,
            },
            Ingredient("all-purpose flour", null, null) with
            {
                OriginalRawText = flour,
            },
            Ingredient("salt", null, null) with
            {
                OriginalRawText = "Salt and pepper",
            },
            Ingredient("pepper", null, null) with
            {
                OriginalRawText = "Salt and pepper",
            },
            Ingredient("whole-wheat flour", 4.5m, "cup") with
            {
                MaxQuantity = 24.75m,
                OriginalRawText = "4 1/2 cups (24 3/4 ounces) whole-wheat flour",
            },
        ]);

        Assert.HasCount(6, rows);
        Assert.AreEqual("chopped; or walnut", rows[0].PreparationNote);
        Assert.AreEqual(new LlmExtractedMeasurementItem(25m, "oz"), rows[1].AlternativeMeasurement);
        Assert.IsNull(rows[2].AlternativeMeasurement);
        Assert.AreEqual("pepper", rows[4].DisplayName);
        Assert.IsNull(rows[5].MaxQuantity);
        Assert.AreEqual(24.75m, rows[5].AlternativeMeasurement!.Quantity);
    }

    [TestMethod]
    public void RepairIngredientRows_PastaAllaTrapanese_SanitizesFoodNamesAndPrepNotes()
    {
        var rawRows = new LlmExtractedIngredientItem[]
        {
            new(
                null,
                1m,
                null,
                "pound",
                "linguine or spaghetti",
                "linguine or spaghetti",
                OriginalRawText: "1 pound linguine or spaghetti"
            ),
            new(
                null,
                12m,
                null,
                "ounces",
                "cherry or grape tomatoes",
                "cherry or grape tomatoes",
                OriginalRawText: "12 ounces cherry or grape tomatoes"
            ),
        };

        var repaired = InstructionAmountTagger.RepairIngredientRows(rawRows);
        Assert.HasCount(2, repaired);
        Assert.AreEqual("linguine", repaired[0].DisplayName);
        Assert.AreEqual("linguine", repaired[0].CanonicalFoodName);
        Assert.AreEqual("or spaghetti", repaired[0].PreparationNote);

        Assert.AreEqual("cherry tomatoes", repaired[1].DisplayName);
        Assert.AreEqual("cherry tomatoes", repaired[1].CanonicalFoodName);
        Assert.AreEqual("or grape tomatoes", repaired[1].PreparationNote);

        var dummyRecipe = new LlmExtractedRecipePayload(
            Title: "Pasta alla Trapanese",
            ConfidenceScore: 0.95m,
            Ingredients: repaired,
            Steps: [new(null, "Cook pasta and tomatoes.", [0, 1])]
        );
        var issues = RecipeExtractionDiagnostics.GetIssues(dummyRecipe);
        Assert.IsFalse(
            issues.Any(i => i.Contains("out of food names")),
            "Should have zero food name alternative issues after repair."
        );
    }

    [TestMethod]
    public void RepairIngredientRows_AntipastoPastaSalad_SplitsPlusRowAndReconciles()
    {
        var saltRow = new LlmExtractedIngredientItem(
            null,
            0.5m,
            null,
            "teaspoon",
            "salt",
            "table salt",
            OriginalRawText: "1/2 teaspoon table salt, plus salt for cooking pasta"
        );
        var pastaRow = new LlmExtractedIngredientItem(
            null,
            1m,
            null,
            "pound",
            "campanelle",
            "pasta",
            OriginalRawText: "1 pound campanelle"
        );

        var repaired = InstructionAmountTagger.RepairIngredientRows([saltRow, pastaRow]);
        Assert.HasCount(3, repaired);
        Assert.AreEqual("table salt", repaired[0].DisplayName);
        Assert.AreEqual(0.5m, repaired[0].Quantity);
        Assert.AreEqual("teaspoon", repaired[0].Unit);
        Assert.IsNull(repaired[0].PreparationNote);

        Assert.AreEqual("table salt", repaired[1].DisplayName);
        Assert.IsNull(repaired[1].Quantity);
        Assert.IsNull(repaired[1].Unit);
        Assert.AreEqual("for cooking pasta", repaired[1].PreparationNote);

        var steps = new List<LlmExtractedStepItem>
        {
            new(
                null,
                "Whisk oil, vinegar, and 1/2 teaspoon salt in large bowl.",
                [0],
                IngredientAmounts: [new(0, new(0.5m, "tsp"))]
            ),
            new(
                null,
                "Bring 4 quarts water to boil in large pot. Add 1 tablespoon salt and pasta; cook until al dente.",
                [1, 2],
                IngredientAmounts: [new(1, new(1m, "tbsp"))] // Linked to unmeasured sibling row 1
            ),
        };

        var issues = InstructionAmountTagger.Reconcile(repaired, steps);
        Assert.IsEmpty(
            issues,
            $"Expected 0 reconciliation issues but got: {string.Join("; ", issues)}"
        );
    }

    [TestMethod]
    public void Reconcile_UltracreamyHummus_CansAndCloveConversions_ZeroErrors()
    {
        var chickpeas = new LlmExtractedIngredientItem(
            null,
            2m,
            null,
            "can",
            "chickpeas",
            "chickpeas",
            OriginalRawText: "2 (15-ounce) cans chickpeas, rinsed"
        );
        var garlic = new LlmExtractedIngredientItem(
            null,
            4m,
            null,
            "clove",
            "garlic",
            "garlic",
            OriginalRawText: "4 garlic cloves, peeled"
        );

        var ingredients = new[] { chickpeas, garlic };
        var steps = new List<LlmExtractedStepItem>
        {
            new(
                null,
                "Combine chickpeas, water, and baking soda in saucepan and simmer.",
                [0] // Bulk step using chickpeas without stated sub-portion amount
            ),
            new(
                null,
                "Set aside 2 tablespoons whole chickpeas for garnish.",
                [0],
                IngredientAmounts: [new(0, new(2m, "tbsp"))]
            ),
            new(
                null,
                "Measure out 1 tablespoon garlic and set aside; discard remaining garlic.",
                [1],
                IngredientAmounts: [new(1, new(1m, "tbsp"))] // 1 tbsp = 3 cloves <= 4 cloves
            ),
            new(null, "Process chickpeas with tahini and garlic.", [0, 1]),
        };

        var issues = InstructionAmountTagger.Reconcile(ingredients, steps);
        Assert.IsEmpty(
            issues,
            $"Expected 0 reconciliation issues for hummus but got: {string.Join("; ", issues)}"
        );
    }

    [TestMethod]
    public void Normalize_ParentheticalEquivalentAnywhere_ConfirmsAlternativeWithoutWarnings()
    {
        var raw = new LlmExtractedRecipePayload(
            Title: "Chicken Breasts",
            ConfidenceScore: 0.95m,
            Ingredients:
            [
                new(
                    null,
                    4m,
                    null,
                    "whole",
                    "boneless chicken breasts",
                    "chicken breast",
                    AlternativeMeasurement: new(1.5m, "lb"),
                    OriginalRawText: "4 boneless chicken breasts (about 1 1/2 pounds)"
                ),
            ],
            Steps:
            [
                new(
                    null,
                    "Season chicken with salt and pepper.",
                    [0],
                    OriginalRawText: "Season chicken with salt and pepper."
                ),
            ]
        );

        var draft = RecipeDraftNormalizer.Normalize(raw, [], [], null, null, null, null, null);
        Assert.IsEmpty(draft.MeasurementWarnings!);
        Assert.IsNotNull(draft.Ingredients![0].AlternativeMeasurement);
        Assert.AreEqual(1.5m, draft.Ingredients[0].AlternativeMeasurement!.Quantity);
        Assert.AreEqual(MeasurementUnit.Lb, draft.Ingredients[0].AlternativeMeasurement!.Unit);
        Assert.IsTrue(
            draft.ConfidenceScore >= 0.92m,
            $"Expected confidence >= 0.92 but got {draft.ConfidenceScore}"
        );
    }

    [TestMethod]
    public void Reconcile_DenverOmeletsWithRepeatStepAndCheeseVolume_HasZeroIssues()
    {
        var ingredients = new List<LlmExtractedIngredientItem>
        {
            new(
                null,
                1m,
                null,
                "whole",
                "Filling for Denver Omelets",
                "Filling for Denver Omelets",
                IsRecipeReference: true,
                ReferencedRecipeTitle: "Filling for Denver Omelets"
            ),
            new(null, 2m, null, "tbsp", "unsalted butter", "unsalted butter"),
            new(
                null,
                4m,
                null,
                "oz",
                "Monterey Jack cheese",
                "monterey jack cheese",
                PreparationNote: "shredded"
            ),
            new(null, 4m, null, "whole", "eggs", "eggs"),
        };

        var steps = new List<LlmExtractedStepItem>
        {
            new(null, "Beat eggs in bowl.", [3]),
            new(
                null,
                "Melt 1 tablespoon butter in skillet.",
                [1],
                IngredientAmounts: [new(1, new LlmExtractedMeasurementItem(1m, "tbsp"))]
            ),
            new(
                null,
                "Sprinkle 1/2 cup cheese and 1/2 cup filling over eggs.",
                [0, 2],
                IngredientAmounts:
                [
                    new(0, new LlmExtractedMeasurementItem(0.5m, "cup")),
                    new(2, new LlmExtractedMeasurementItem(0.5m, "cup")),
                ]
            ),
            new(null, "Repeat the instructions for the second omelet.", [0, 1, 2, 3]),
        };

        var issues = InstructionAmountTagger.Reconcile(ingredients, steps);
        Assert.IsEmpty(issues, string.Join("; ", issues));
    }

    [TestMethod]
    public void Reconcile_BoilingWaterInPot_DoesNotOverflowFillingWaterRow()
    {
        var ingredients = new List<LlmExtractedIngredientItem>
        {
            new(null, 2m, null, "tbsp", "water", "water", PreparationNote: "for filling"),
            new(null, 1m, null, "lb", "ground pork", "pork"),
        };

        var candidates = InstructionAmountTagger.FindCandidates([
            new MethodParagraph(1, null, "Mix pork and 2 tablespoons water for filling."),
            new MethodParagraph(
                2,
                null,
                "Bring 4 quarts water to boil in large pot; cook wontons."
            ),
        ]);

        var tags = new MethodTags(
            new Dictionary<string, string> { ["A1"] = "0 water", ["A2"] = "0 water" },
            new Dictionary<string, string>(),
            new Dictionary<int, IReadOnlyList<string>>
            {
                [1] = ["0 water", "1 ground pork"],
                [2] = ["0 water"],
            }
        );

        var result = InstructionAmountTagger.Assemble(ingredients, candidates, tags);
        Assert.IsEmpty(result.Issues, string.Join("; ", result.Issues));
    }

    private static LlmExtractedIngredientItem Ingredient(
        string name,
        decimal? quantity,
        string? unit
    ) => new(null, quantity, null, unit, name, name);
}
