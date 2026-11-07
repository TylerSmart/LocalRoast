using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class TaggedStepDiagnosticsTests
{
    private const string Instruction = "Melt 2 tbsp butter in 1 cup water.";

    [TestMethod]
    public void GetIssues_CleanTaggedStepsPass()
    {
        var recipe = Recipe(Step(Instruction, Butter(Instruction), Fixed(Instruction, "1 cup")));

        Assert.IsEmpty(TaggedStepDiagnostics.GetIssues(recipe));
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetReviewIssues(recipe));
    }

    [TestMethod]
    public void GetIssues_FlagsOverlappingSpans()
    {
        var butter = Butter(Instruction);
        var recipe = Recipe(
            Step(
                Instruction,
                butter,
                butter with
                {
                    Kind = InlineStepAmountKind.Fixed,
                    IngredientIndex = null,
                }
            )
        );

        Assert.IsTrue(
            TaggedStepDiagnostics.GetIssues(recipe).Any(issue => issue.Contains("overlaps"))
        );
    }

    [TestMethod]
    public void GetIssues_FlagsSpansOutsideTheStepText()
    {
        var recipe = Recipe(Step(Instruction, Butter(Instruction) with { Start = 40 }));

        Assert.IsTrue(
            TaggedStepDiagnostics
                .GetIssues(recipe)
                .Any(issue => issue.Contains("is outside the step text"))
        );
    }

    [TestMethod]
    public void GetIssues_FlagsKindAndIndexMismatches()
    {
        var invalidRow = Recipe(
            Step(Instruction, Butter(Instruction) with { IngredientIndex = 5 })
        );
        var untaggedRow = Recipe(
            Step(Instruction, Butter(Instruction) with { Kind = InlineStepAmountKind.Fixed })
        );

        Assert.IsTrue(
            TaggedStepDiagnostics
                .GetIssues(invalidRow)
                .Any(issue => issue.Contains("without a valid ingredient"))
        );
        Assert.IsTrue(
            TaggedStepDiagnostics
                .GetIssues(untaggedRow)
                .Any(issue => issue.Contains("references an ingredient but is not tagged"))
        );
    }

    [TestMethod]
    public void GetIssues_FlagsEditedTextThatIsNoLongerAnAmount()
    {
        const string edited = "Melt soft butter in 1 cup water.";
        var recipe = Recipe(Step(edited, Butter(Instruction)));

        Assert.IsTrue(
            TaggedStepDiagnostics
                .GetIssues(recipe)
                .Any(issue => issue.Contains("no longer a measured amount"))
        );
    }

    [TestMethod]
    public void GetIssues_FlagsStepAmountsThatExceedTheIngredientRow()
    {
        const string second = "Add 2 tbsp butter.";
        LlmExtractedStepIngredientAmountItem[] portion = [new(0, new(2, "tbsp"))];
        var recipe = Recipe(
            Step(Instruction, Butter(Instruction)) with
            {
                IngredientAmounts = portion,
            },
            Step(second, Butter(second)) with
            {
                IngredientAmounts = portion,
            }
        );

        Assert.IsTrue(
            TaggedStepDiagnostics
                .GetIssues(recipe)
                .Any(issue => issue.Contains("exceed the ingredient list"))
        );
    }

    [TestMethod]
    public void Normalize_MapsInlineAmountsOntoTheTrimmedInstruction()
    {
        const string verbatim = "  " + Instruction;
        var raw = Recipe(
            Step(
                verbatim,
                Butter(verbatim),
                Fixed(verbatim, "1 cup") with
                {
                    Kind = InlineStepAmountKind.Ingredient,
                    IngredientIndex = 3,
                }
            )
        );

        var draft = RecipeDraftNormalizer.Normalize(raw, [], [], null, null, null, null, null);

        var step = draft.Steps![0];
        Assert.AreEqual(Instruction, step.Instruction);
        Assert.HasCount(2, step.InlineAmounts!);
        var butter = step.InlineAmounts![0];
        Assert.AreEqual(Instruction.IndexOf("2 tbsp", StringComparison.Ordinal), butter.Start);
        Assert.AreEqual(6, butter.Length);
        Assert.AreEqual(InlineStepAmountKind.Ingredient, butter.Kind);
        Assert.AreEqual(0, butter.IngredientIndex);
        Assert.AreEqual(new RecipeMeasurement(2, MeasurementUnit.Tbsp), butter.Measurement);
        var water = step.InlineAmounts[1];
        Assert.AreEqual(InlineStepAmountKind.Fixed, water.Kind);
        Assert.IsNull(water.IngredientIndex);
        CollectionAssert.Contains(step.IngredientIndices!.ToArray(), 0);
    }

    [TestMethod]
    public void Normalize_DropsSpansThatDoNotFitTheStep()
    {
        var raw = Recipe(
            Step(
                Instruction,
                Butter(Instruction) with
                {
                    Start = 30,
                },
                Fixed(Instruction, "1 cup") with
                {
                    Measurement = new RecipeMeasurement(0, MeasurementUnit.Cup),
                }
            )
        );

        var draft = RecipeDraftNormalizer.Normalize(raw, [], [], null, null, null, null, null);

        Assert.IsEmpty(draft.Steps![0].InlineAmounts ?? []);
        Assert.IsTrue(draft.MeasurementWarnings!.Any(warning => warning.Contains("inline amount")));
    }

    [TestMethod]
    public void RepairMapper_RoundTripsInlineAmounts()
    {
        var draft = RecipeDraftNormalizer.Normalize(
            Recipe(Step(Instruction, Butter(Instruction), Fixed(Instruction, "1 cup"))),
            [],
            [],
            null,
            null,
            null,
            null,
            null
        );

        var payload = IngestionDraftRepairMapper.ToPayload(draft);
        var payloadAmounts = payload.Steps![0].InlineAmounts!;
        Assert.AreEqual("2 tbsp", payloadAmounts[0].Text);
        Assert.AreEqual("1 cup", payloadAmounts[1].Text);

        var merged = IngestionDraftRepairMapper.Merge(draft, payload, [], null);
        CollectionAssert.AreEqual(
            draft.Steps![0].InlineAmounts!.ToArray(),
            merged.Steps![0].InlineAmounts!.ToArray()
        );

        var padded = payload with
        {
            Steps =
            [
                payload.Steps[0] with
                {
                    Instruction = "  " + Instruction,
                    InlineAmounts = [.. payloadAmounts.Select(a => a with { Start = a.Start + 2 })],
                },
            ],
        };
        merged = IngestionDraftRepairMapper.Merge(draft, padded, [], null);
        CollectionAssert.AreEqual(
            draft.Steps[0].InlineAmounts!.ToArray(),
            merged.Steps![0].InlineAmounts!.ToArray()
        );
    }

    private static InlineStepAmount Butter(string instruction) =>
        new(
            instruction.IndexOf("2 tbsp", StringComparison.Ordinal),
            6,
            "2 tbsp",
            InlineStepAmountKind.Ingredient,
            0,
            new RecipeMeasurement(2, MeasurementUnit.Tbsp)
        );

    private static InlineStepAmount Fixed(string instruction, string text) =>
        new(
            instruction.IndexOf(text, StringComparison.Ordinal),
            text.Length,
            text,
            InlineStepAmountKind.Fixed,
            null,
            new RecipeMeasurement(1, MeasurementUnit.Cup)
        );

    private static LlmExtractedStepItem Step(
        string instruction,
        params InlineStepAmount[] amounts
    ) => new(null, instruction, [0], InlineAmounts: amounts);

    private static LlmExtractedRecipePayload Recipe(params LlmExtractedStepItem[] steps) =>
        new(
            Title: "Butter water",
            Ingredients:
            [
                new(null, 2, null, "tbsp", "butter", "butter", OriginalRawText: "2 tbsp butter"),
            ],
            Steps: steps
        );
}
