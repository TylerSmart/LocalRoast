using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class IngestionConfidenceScorerTests
{
    [TestMethod]
    public void Compute_DistinguishesPristineVsSparseVsRepairedRecipes_InsteadOfFlat96()
    {
        var butterId = Guid.NewGuid();
        var flourId = Guid.NewGuid();

        var pristine = new IngestionExtractedDraftDto(
            Title: "Golden Roux",
            Description: "A rich classic base for sauces.",
            BaseServings: 4m,
            YieldDisplay: "Serves 4",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 10,
            TotalTimeMinutes: 15,
            Ingredients:
            [
                new(
                    Quantity: 2m,
                    Unit: MeasurementUnit.Tbsp,
                    MatchedFoodItemId: butterId,
                    MatchedFoodItemName: "Butter",
                    IsNewFoodItem: false,
                    DisplayName: "butter"
                ),
                new(
                    Quantity: 2m,
                    Unit: MeasurementUnit.Tbsp,
                    MatchedFoodItemId: flourId,
                    MatchedFoodItemName: "All-Purpose Flour",
                    IsNewFoodItem: false,
                    DisplayName: "flour"
                ),
            ],
            Steps:
            [
                new(
                    Instruction: """Melt ||{"type":"ingredient","ingredient":"butter","amount":2,"unit":"tbsp","ingredientIndex":0}|| and whisk in ||{"type":"ingredient","ingredient":"flour","amount":2,"unit":"tbsp","ingredientIndex":1}||."""
                ),
            ]
        );

        var sparseNoTimeNewFoods = pristine with
        {
            Description = null,
            YieldDisplay = null,
            PrepTimeMinutes = null,
            CookTimeMinutes = null,
            TotalTimeMinutes = null,
            Ingredients =
            [
                new(
                    Quantity: 2m,
                    Unit: MeasurementUnit.Tbsp,
                    IsNewFoodItem: true,
                    ProposedFoodItemName: "Cultured Butter",
                    DisplayName: "cultured butter"
                ),
                new(
                    Quantity: 2m,
                    Unit: MeasurementUnit.Tbsp,
                    IsNewFoodItem: true,
                    ProposedFoodItemName: "Pastry Flour",
                    DisplayName: "pastry flour"
                ),
            ],
            PipelineRepairCount = 2,
        };

        var pristineScore = IngestionConfidenceScorer.Compute(pristine);
        var sparseScore = IngestionConfidenceScorer.Compute(sparseNoTimeNewFoods);

        Assert.AreEqual(0.99m, pristineScore);
        Assert.IsLessThan(pristineScore, sparseScore);
        Assert.IsGreaterThanOrEqualTo(0.85m, sparseScore);
        Assert.AreNotEqual(0.96m, pristineScore);
        Assert.AreNotEqual(0.96m, sparseScore);
    }

    [TestMethod]
    public void Compute_DropsBelowAutoPublishThresholdWhenValidationIssueOrWarningsExist()
    {
        var draft = new IngestionExtractedDraftDto(
            Title: "Broken Recipe",
            BaseServings: 4m,
            YieldDisplay: "Serves 4",
            PrepTimeMinutes: 10,
            CookTimeMinutes: 20,
            TotalTimeMinutes: 30,
            Ingredients: [new(Quantity: 1m, Unit: MeasurementUnit.Cup, DisplayName: "water")],
            Steps: [new(Instruction: "Boil water.")],
            ValidationIssues: ["Ingredient #1 references missing sub-recipe."]
        );

        var score = IngestionConfidenceScorer.Compute(draft);
        Assert.IsLessThan(0.85m, score);
    }

    [TestMethod]
    public void CleanPreparationNote_StripsRedundantOuterParentheses_WhilePreservingInternal()
    {
        Assert.AreEqual(
            "8-inch square",
            RecipeUnitAndSectionHelpers.CleanPreparationNote("(8-inch square)")
        );
        Assert.AreEqual(
            "8-inch square",
            RecipeUnitAndSectionHelpers.CleanPreparationNote("((8-inch square))")
        );
        Assert.AreEqual(
            "minced (about 1 tbsp)",
            RecipeUnitAndSectionHelpers.CleanPreparationNote("minced (about 1 tbsp)")
        );
        Assert.AreEqual(
            "(8-inch) square",
            RecipeUnitAndSectionHelpers.CleanPreparationNote("(8-inch) square")
        );
        Assert.IsNull(RecipeUnitAndSectionHelpers.CleanPreparationNote("((  ))"));
    }
}
