using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class IngestionReviewTests
{
    [TestMethod]
    public void QualityChecks_AreSourceIndependentAndDoNotChangeAutomaticValidation()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new(null, 1, null, "whole", "tomato", "tomato"),
                new(null, 1, null, "tsp", "salt", "salt"),
            ],
            Steps:
            [
                new(null, "Add tomato, stir in salt, then simmer.", [0], [new(60, "Cook")]),
                new(null, "Season with salt.", [1], [new(30, "Rest tomatoes")]),
            ]
        );

        var warnings = RecipeExtractionDiagnostics.GetQualityWarnings(recipe);
        Assert.IsTrue(warnings.Any(warning => warning.Contains("vague label")));
        Assert.IsTrue(
            warnings.Any(warning =>
                warning.Contains("Step 1")
                && warning.Contains("salt")
                && warning.Contains("not linked")
            )
        );
        Assert.IsFalse(warnings.Any(warning => warning.Contains("Rest tomatoes")));
        Assert.IsEmpty(RecipeExtractionDiagnostics.GetReviewIssues(recipe));
    }

    [TestMethod]
    public void QualityChecks_AvoidAmbiguousFoodMentionsAndDuplicatePortions()
    {
        var recipe = new LlmExtractedRecipePayload(
            Ingredients:
            [
                new(null, 1, null, "tbsp", "olive oil", "olive oil"),
                new(null, 1, null, "tbsp", "sesame oil", "sesame oil"),
                new(null, 1, null, "tbsp", "butter", "butter"),
                new(null, 1, null, "tbsp", "butter", "butter"),
            ],
            Steps: [new(null, "Heat oil and melt butter.", [2])]
        );

        Assert.IsEmpty(RecipeExtractionDiagnostics.GetQualityWarnings(recipe));
    }

    [TestMethod]
    public void Normalize_ReportsUnknownIngredientIndicesBeforeOmittingThem()
    {
        var raw = new LlmExtractedRecipePayload(
            Title: "Rice",
            Ingredients: [new(null, 1, null, "cup", "rice", "rice")],
            Steps: [new(null, "Cook rice.", [0, 3])]
        );
        var draft = RecipeDraftNormalizer.Normalize(raw, [], [], null, null, null, null, null);

        CollectionAssert.AreEqual(new[] { 0 }, draft.Steps![0].IngredientIndices!.ToArray());
        Assert.IsTrue(
            draft.MeasurementWarnings!.Any(warning => warning.Contains("unknown ingredient index"))
        );
    }

    [TestMethod]
    public void RepairMapper_PreservesManualMetadataReferencesAndPortions()
    {
        var foodId = Guid.NewGuid();
        var original = CreateDraft(foodId);
        var payload = IngestionDraftRepairMapper.ToPayload(original);
        var proposed = payload with
        {
            Steps = [payload.Steps![0] with { Instruction = "Stir beans and sauce gently." }],
        };

        var result = IngestionDraftRepairMapper.Merge(original, proposed, [], null);

        Assert.AreEqual(foodId, result.Ingredients![0].MatchedFoodItemId);
        Assert.AreEqual(
            original.Ingredients![0].AlternativeMeasurement,
            result.Ingredients[0].AlternativeMeasurement
        );
        Assert.IsTrue(result.Ingredients[0].PreferAlternativeMeasurement);
        Assert.IsTrue(result.Ingredients[1].IsRecipeReference);
        Assert.AreEqual("Tomato Sauce", result.Ingredients[1].ReferencedRecipeTitle);
        Assert.AreEqual("/uploads/step.webp", result.Steps![0].ImagePath);
        CollectionAssert.AreEqual(
            original.Steps![0].IngredientAmounts!.ToArray(),
            result.Steps[0].IngredientAmounts!.ToArray()
        );
        CollectionAssert.AreEqual(
            original.SuggestedCollectionIds!.ToArray(),
            result.SuggestedCollectionIds!.ToArray()
        );
        Assert.IsEmpty(result.MeasurementWarnings!);
    }

    [TestMethod]
    public void RepairMapper_RejectsInvalidIndicesAndUnmappableStepImages()
    {
        var original = CreateDraft(Guid.NewGuid());
        var payload = IngestionDraftRepairMapper.ToPayload(original);
        var invalid = payload with
        {
            Steps = [payload.Steps![0] with { IngredientIndices = [42] }],
        };
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.Merge(original, invalid, [], null)
        );

        var split = payload with
        {
            Steps = [new(null, "Stir beans.", [0]), new(null, "Add sauce.", [1])],
        };
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.Merge(original, split, [], null)
        );
    }

    [TestMethod]
    public void RepairMapper_MatchesReorderedDuplicateIngredientsByTheirSourceAndAmounts()
    {
        var first = new IngestionDraftIngredientDto(
            Quantity: 1,
            Unit: MeasurementUnit.Tbsp,
            DisplayName: "butter",
            ProposedFoodItemName: "butter",
            OriginalRawText: "1 tbsp butter",
            AlternativeMeasurement: new RecipeMeasurement(14, MeasurementUnit.G),
            CreateNewFoodItem: false
        );
        var second = first with
        {
            Quantity = 2,
            OriginalRawText = "2 tbsp butter",
            AlternativeMeasurement = new RecipeMeasurement(28, MeasurementUnit.G),
            CreateNewFoodItem = true,
        };
        var original = new IngestionExtractedDraftDto(
            "Butter",
            Ingredients: [first, second],
            Steps: [new(Instruction: "Melt the butter.", IngredientIndices: [0, 1])]
        );
        var payload = IngestionDraftRepairMapper.ToPayload(original);
        var reordered = payload with
        {
            Ingredients = [payload.Ingredients![1], payload.Ingredients[0]],
        };

        var result = IngestionDraftRepairMapper.Merge(original, reordered, [], null);

        Assert.AreEqual(28m, result.Ingredients![0].AlternativeMeasurement!.Quantity);
        Assert.IsTrue(result.Ingredients[0].CreateNewFoodItem);
        Assert.AreEqual(14m, result.Ingredients[1].AlternativeMeasurement!.Quantity);
        Assert.IsFalse(result.Ingredients[1].CreateNewFoodItem);
    }

    [TestMethod]
    public void RepairMapper_RejectsAmbiguousDuplicateMetadataAndAmbiguousImageMappings()
    {
        var original = CreateDraft(Guid.NewGuid());
        var duplicate = original.Ingredients![0];
        original = original with
        {
            Ingredients = [duplicate, duplicate with { CreateNewFoodItem = false }],
            Steps =
            [
                new(Instruction: "Stir beans.", IngredientIndices: [0], ImagePath: "/step.webp"),
                new(Instruction: "Serve beans.", IngredientIndices: [1]),
            ],
        };
        var payload = IngestionDraftRepairMapper.ToPayload(original);
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.Merge(original, payload, [], null)
        );

        original = original with
        {
            Ingredients = [duplicate],
            Steps = [original.Steps![0], original.Steps[1] with { IngredientIndices = [0] }],
        };
        payload = IngestionDraftRepairMapper.ToPayload(original);
        var ambiguousImages = payload with
        {
            Steps = [new(null, "Warm the beans.", [0]), new(null, "Plate the beans.", [0])],
        };
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.Merge(original, ambiguousImages, [], null)
        );
        var reordered = payload with { Steps = [payload.Steps![1], payload.Steps[0]] };
        var result = IngestionDraftRepairMapper.Merge(original, reordered, [], null);
        Assert.IsNull(result.Steps![0].ImagePath);
        Assert.AreEqual("/step.webp", result.Steps[1].ImagePath);
    }

    [TestMethod]
    public void RepairMapper_WarnsWhenTheAiRemovesExplicitPortions()
    {
        var original = CreateDraft(Guid.NewGuid());
        var payload = IngestionDraftRepairMapper.ToPayload(original);
        var proposed = payload with { Steps = [payload.Steps![0] with { IngredientAmounts = [] }] };

        var result = IngestionDraftRepairMapper.Merge(original, proposed, [], null);

        Assert.IsTrue(result.MeasurementWarnings!.Any(warning => warning.Contains("portions")));
    }

    [TestMethod]
    public void RepairMapper_RejectsInvalidAnnotatedFieldsAndMalformedNestedRows()
    {
        var original = CreateDraft(Guid.NewGuid());
        var payload = IngestionDraftRepairMapper.ToPayload(original);
        var longTitle = payload with { Title = new string('x', 201) };
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.Merge(original, longTitle, [], null)
        );
        var invalidTimer = payload with
        {
            Steps = [payload.Steps![0] with { Timers = [new(0, "Warm")] }],
        };
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.Merge(original, invalidTimer, [], null)
        );
        var invalidDifficulty = payload with { Difficulty = "999" };
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.Merge(original, invalidDifficulty, [], null)
        );
        var malformed = payload with { Steps = [payload.Steps![0] with { Timers = [null!] }] };
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.Merge(original, malformed, [], null)
        );
        var malformedDraft = original with { Ingredients = [null!] };
        Assert.Throws<DomainValidationException>(() =>
            IngestionDraftRepairMapper.ToPayload(malformedDraft)
        );
    }

    private static IngestionExtractedDraftDto CreateDraft(Guid foodId) =>
        new(
            Title: "Beans and sauce",
            SuggestedCollectionIds: [Guid.NewGuid()],
            Ingredients:
            [
                new(
                    Quantity: 2,
                    Unit: MeasurementUnit.Cup,
                    MatchedFoodItemId: foodId,
                    MatchedFoodItemName: "Beans",
                    DisplayName: "Beans",
                    OriginalRawText: "2 cups beans",
                    AlternativeMeasurement: new RecipeMeasurement(300, MeasurementUnit.G),
                    PreferAlternativeMeasurement: true
                ),
                new(
                    Quantity: 1,
                    Unit: MeasurementUnit.Custom,
                    CustomUnitName: "batch",
                    DisplayName: "Tomato Sauce",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "Tomato Sauce",
                    CreateNewFoodItem: false
                ),
            ],
            Steps:
            [
                new(
                    Instruction: "Stir beans and sauce.",
                    IngredientIndices: [0, 1],
                    Timers: [new RecipeStepTimerInput(60, "Warm beans")],
                    ImagePath: "/uploads/step.webp",
                    IngredientAmounts:
                    [
                        new RecipeStepIngredientAmountInput(
                            0,
                            new RecipeMeasurement(1, MeasurementUnit.Cup)
                        ),
                    ]
                ),
            ]
        );
}
