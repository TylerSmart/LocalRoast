using App.Core.Recipes;
using App.Core.Shopping;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class ShoppingConsolidatorTests
{
    [TestMethod]
    public void GetDimension_CategorizesCorrectly()
    {
        Assert.AreEqual(
            MeasurementDimension.Unspecified,
            ShoppingConsolidator.GetDimension(MeasurementUnit.Cup, null)
        );
        Assert.AreEqual(
            MeasurementDimension.Unspecified,
            ShoppingConsolidator.GetDimension(MeasurementUnit.Cup, 0m)
        );

        Assert.AreEqual(
            MeasurementDimension.Volume,
            ShoppingConsolidator.GetDimension(MeasurementUnit.Tbsp, 2m)
        );
        Assert.AreEqual(
            MeasurementDimension.Volume,
            ShoppingConsolidator.GetDimension(MeasurementUnit.Ml, 250m)
        );

        Assert.AreEqual(
            MeasurementDimension.Weight,
            ShoppingConsolidator.GetDimension(MeasurementUnit.Oz, 8m)
        );
        Assert.AreEqual(
            MeasurementDimension.Weight,
            ShoppingConsolidator.GetDimension(MeasurementUnit.G, 500m)
        );

        Assert.AreEqual(
            MeasurementDimension.Discrete,
            ShoppingConsolidator.GetDimension(MeasurementUnit.Clove, 3m)
        );
        Assert.AreEqual(
            MeasurementDimension.Discrete,
            ShoppingConsolidator.GetDimension(MeasurementUnit.Whole, 2m)
        );
        Assert.AreEqual(MeasurementDimension.Discrete, ShoppingConsolidator.GetDimension(null, 1m));
    }

    [TestMethod]
    public void BuildConsolidationKey_DifferentiatesDimensionsAndDiscreteUnits()
    {
        var foodItemId = Guid.NewGuid();

        var keyVol = ShoppingConsolidator.BuildConsolidationKey(
            foodItemId,
            "Yellow Onion",
            MeasurementDimension.Volume,
            MeasurementUnit.Cup,
            null
        );

        var keyDiscWhole = ShoppingConsolidator.BuildConsolidationKey(
            foodItemId,
            "Yellow Onion",
            MeasurementDimension.Discrete,
            MeasurementUnit.Whole,
            null
        );

        var keyDiscClove = ShoppingConsolidator.BuildConsolidationKey(
            foodItemId,
            "Yellow Onion",
            MeasurementDimension.Discrete,
            MeasurementUnit.Clove,
            null
        );

        Assert.AreNotEqual(keyVol, keyDiscWhole);
        Assert.AreNotEqual(keyDiscWhole, keyDiscClove);
        StringAssert.StartsWith(keyVol, $"fi:{foodItemId:N}:vol");
        StringAssert.StartsWith(keyDiscWhole, $"fi:{foodItemId:N}:disc:Whole");
    }

    [TestMethod]
    public void Consolidate_Volumes_SumsCompatibleUsCustomaryUnitsCleanly()
    {
        // 2 tbsp + 1/4 cup Olive Oil = 6 tsp + 12 tsp = 18 tsp = 0.375 cup = 3/8 cup
        var foodItemId = Guid.NewGuid();
        var inputs = new List<ConsolidatableIngredientInput>
        {
            new(
                foodItemId,
                "Extra-Virgin Olive Oil",
                true,
                2m,
                null,
                MeasurementUnit.Tbsp,
                null,
                "for marinade",
                ShoppingListSourceKind.MealPlanEntry,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Tue: Chicken Tacos",
                new DateOnly(2026, 11, 3)
            ),
            new(
                foodItemId,
                "Extra-Virgin Olive Oil",
                true,
                0.25m,
                null,
                MeasurementUnit.Cup,
                null,
                null,
                ShoppingListSourceKind.MealPlanEntry,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Thu: Enchiladas",
                new DateOnly(2026, 11, 5)
            ),
        };

        var buckets = ShoppingConsolidator.Consolidate(inputs);

        Assert.AreEqual(1, buckets.Count);
        var b = buckets[0];
        Assert.AreEqual(foodItemId, b.FoodItemId);
        Assert.AreEqual(MeasurementDimension.Volume, b.Dimension);
        Assert.AreEqual(MeasurementUnit.Cup, b.Unit);
        Assert.AreEqual(0.375m, b.Quantity);
        Assert.IsNull(b.MaxQuantity);
        Assert.IsTrue(b.IsPantryStaple);
        Assert.AreEqual("for marinade", b.MergedNotes);
        Assert.AreEqual(2, b.Sources.Count);
    }

    [TestMethod]
    public void Consolidate_Weights_SumsOzAndLbIntoCleanLb()
    {
        // 8 oz + 1 lb = 0.5 lb + 1 lb = 1.5 lb
        var foodItemId = Guid.NewGuid();
        var inputs = new List<ConsolidatableIngredientInput>
        {
            new(
                foodItemId,
                "Chicken Thighs",
                false,
                8m,
                null,
                MeasurementUnit.Oz,
                null,
                null,
                ShoppingListSourceKind.MealPlanEntry,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Tue: Tacos",
                null
            ),
            new(
                foodItemId,
                "Chicken Thighs",
                false,
                1m,
                null,
                MeasurementUnit.Lb,
                null,
                null,
                ShoppingListSourceKind.MealPlanEntry,
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Sat: Roast",
                null
            ),
        };

        var buckets = ShoppingConsolidator.Consolidate(inputs);

        Assert.AreEqual(1, buckets.Count);
        var b = buckets[0];
        Assert.AreEqual(MeasurementDimension.Weight, b.Dimension);
        Assert.AreEqual(MeasurementUnit.Lb, b.Unit);
        Assert.AreEqual(1.5m, b.Quantity);
    }

    [TestMethod]
    public void Consolidate_IncompatibleDimensions_SeparatesIntoDistinctBuckets()
    {
        // 2 Whole Yellow Onion in Recipe A + 1 cup Yellow Onion diced in Recipe B
        var foodItemId = Guid.NewGuid();
        var inputs = new List<ConsolidatableIngredientInput>
        {
            new(
                foodItemId,
                "Yellow Onion",
                false,
                2m,
                null,
                MeasurementUnit.Whole,
                null,
                null,
                ShoppingListSourceKind.SingleRecipe,
                null,
                Guid.NewGuid(),
                "Recipe A",
                null
            ),
            new(
                foodItemId,
                "Yellow Onion",
                false,
                1m,
                null,
                MeasurementUnit.Cup,
                null,
                "diced",
                ShoppingListSourceKind.SingleRecipe,
                null,
                Guid.NewGuid(),
                "Recipe B",
                null
            ),
        };

        var buckets = ShoppingConsolidator.Consolidate(inputs);

        Assert.AreEqual(2, buckets.Count);
        Assert.IsTrue(
            buckets.Any(b =>
                b.Dimension == MeasurementDimension.Discrete
                && b.Quantity == 2m
                && b.Unit == MeasurementUnit.Whole
            )
        );
        Assert.IsTrue(
            buckets.Any(b =>
                b.Dimension == MeasurementDimension.Volume
                && b.Quantity == 1m
                && b.Unit == MeasurementUnit.Cup
            )
        );
    }

    [TestMethod]
    public void Consolidate_DeduplicatesMultipleContributionsFromSameSource()
    {
        // 1 tbsp olive oil in marinade + 2 tbsp olive oil in sear inside Tuesday Chicken Tacos
        var foodItemId = Guid.NewGuid();
        var mealPlanEntryId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();

        var inputs = new List<ConsolidatableIngredientInput>
        {
            new(
                foodItemId,
                "Olive Oil",
                true,
                1m,
                null,
                MeasurementUnit.Tbsp,
                null,
                "for marinade",
                ShoppingListSourceKind.MealPlanEntry,
                mealPlanEntryId,
                recipeId,
                "Tue: Chicken Tacos",
                new DateOnly(2026, 11, 3)
            ),
            new(
                foodItemId,
                "Olive Oil",
                true,
                2m,
                null,
                MeasurementUnit.Tbsp,
                null,
                "for sear",
                ShoppingListSourceKind.MealPlanEntry,
                mealPlanEntryId,
                recipeId,
                "Tue: Chicken Tacos",
                new DateOnly(2026, 11, 3)
            ),
        };

        var buckets = ShoppingConsolidator.Consolidate(inputs);

        Assert.AreEqual(1, buckets.Count);
        var b = buckets[0];
        Assert.AreEqual(1, b.Sources.Count);
        Assert.AreEqual("Tue: Chicken Tacos", b.Sources[0].SourceLabel);
    }
}
