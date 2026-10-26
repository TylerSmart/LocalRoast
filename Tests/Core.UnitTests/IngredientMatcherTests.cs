using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class IngredientMatcherTests
{
    private static RecipeSummaryDto CreateSummary(string title, int totalTime = 30)
    {
        return new RecipeSummaryDto(
            Id: Guid.NewGuid(),
            Title: title,
            Slug: title.ToLowerInvariant().Replace(" ", "-"),
            Description: null,
            Status: RecipeStatus.Published,
            BaseServings: 4m,
            YieldDisplay: "4 servings",
            PrepTimeMinutes: 10,
            CookTimeMinutes: totalTime - 10,
            TotalTimeMinutes: totalTime,
            Difficulty: RecipeDifficulty.Easy,
            SourceName: "Test Kitchen",
            ThumbnailImagePath: null,
            IngredientCount: 4,
            StepCount: 2,
            TimesCooked: 0,
            AverageRating: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ModifiedAt: DateTimeOffset.UtcNow
        );
    }

    [TestMethod]
    public void RankRecipes_EmptyInputs_ReturnsEmpty()
    {
        var result1 = IngredientMatcher.RankRecipes(
            [],
            [new MatchableOnHandItem(Guid.NewGuid(), "Lime", "lime", [])]
        );
        Assert.IsEmpty(result1);

        var candidate = new MatchableRecipeCandidate(
            CreateSummary("Test"),
            [new MatchableRecipeIngredient(Guid.NewGuid(), "Lime", false, false)]
        );
        var result2 = IngredientMatcher.RankRecipes([candidate], []);
        Assert.IsEmpty(result2);
    }

    [TestMethod]
    public void RankRecipes_NoMatchingIngredients_ExcludesRecipe()
    {
        var chickenId = Guid.NewGuid();
        var beefId = Guid.NewGuid();

        var candidate = new MatchableRecipeCandidate(
            CreateSummary("Beef Stew"),
            [new MatchableRecipeIngredient(beefId, "Beef Chuck", false, false)]
        );

        var onHand = new MatchableOnHandItem(chickenId, "Chicken Thighs", "chicken thighs", []);

        var results = IngredientMatcher.RankRecipes([candidate], [onHand]);
        Assert.IsEmpty(results);
    }

    [TestMethod]
    public void RankRecipes_RanksByMatchedCountDesc_ThenAdditionalNonStaplesAsc()
    {
        var chickenId = Guid.NewGuid();
        var limeId = Guid.NewGuid();
        var cilantroId = Guid.NewGuid();
        var avocadoId = Guid.NewGuid();
        var saltId = Guid.NewGuid();

        var onHandItems = new[]
        {
            new MatchableOnHandItem(chickenId, "Chicken Thighs", "chicken thighs", []),
            new MatchableOnHandItem(limeId, "Lime", "lime", ["fresh lime"]),
        };

        // Recipe 1: Matches 2 (Chicken, Lime), has Salt (pantry staple) -> 0 additional non-staples
        var recipe1 = new MatchableRecipeCandidate(
            CreateSummary("Lime Salt Chicken", totalTime: 40),
            [
                new MatchableRecipeIngredient(chickenId, "Chicken Thighs", false, false),
                new MatchableRecipeIngredient(limeId, "Lime", false, false),
                new MatchableRecipeIngredient(saltId, "Kosher Salt", false, true), // pantry staple
            ]
        );

        // Recipe 2: Matches 2 (Chicken, Lime), needs Cilantro and Avocado -> 2 additional non-staples
        var recipe2 = new MatchableRecipeCandidate(
            CreateSummary("Loaded Chicken Bowl", totalTime: 30),
            [
                new MatchableRecipeIngredient(chickenId, "Chicken Thighs", false, false),
                new MatchableRecipeIngredient(limeId, "Lime", false, false),
                new MatchableRecipeIngredient(cilantroId, "Cilantro", false, false),
                new MatchableRecipeIngredient(avocadoId, "Avocado", false, false),
            ]
        );

        // Recipe 3: Matches 1 (Chicken), needs Cilantro -> 1 additional non-staple
        var recipe3 = new MatchableRecipeCandidate(
            CreateSummary("Simple Cilantro Chicken", totalTime: 20),
            [
                new MatchableRecipeIngredient(chickenId, "Chicken Thighs", false, false),
                new MatchableRecipeIngredient(cilantroId, "Cilantro", false, false),
            ]
        );

        var results = IngredientMatcher.RankRecipes([recipe3, recipe2, recipe1], onHandItems);

        Assert.HasCount(3, results);
        // Recipe 1 should be first: 2 matches, 0 additional non-staples
        Assert.AreEqual("Lime Salt Chicken", results[0].Recipe.Title);
        Assert.AreEqual(2, results[0].MatchedCount);
        Assert.AreEqual(0, results[0].AdditionalNonStapleCount);
        Assert.AreEqual(1, results[0].PantryStaplesUsedCount);

        // Recipe 2 should be second: 2 matches, 2 additional non-staples
        Assert.AreEqual("Loaded Chicken Bowl", results[1].Recipe.Title);
        Assert.AreEqual(2, results[1].MatchedCount);
        Assert.AreEqual(2, results[1].AdditionalNonStapleCount);

        // Recipe 3 should be third: 1 match
        Assert.AreEqual("Simple Cilantro Chicken", results[2].Recipe.Title);
        Assert.AreEqual(1, results[2].MatchedCount);
        Assert.AreEqual(1, results[2].AdditionalNonStapleCount);
    }

    [TestMethod]
    public void RankRecipes_OptionalIngredients_DoNotPenalizeMissingCount()
    {
        var chickenId = Guid.NewGuid();
        var sesameId = Guid.NewGuid();

        var onHandItems = new[]
        {
            new MatchableOnHandItem(chickenId, "Chicken Thighs", "chicken thighs", []),
        };

        var candidate = new MatchableRecipeCandidate(
            CreateSummary("Roast Chicken"),
            [
                new MatchableRecipeIngredient(chickenId, "Chicken Thighs", false, false),
                new MatchableRecipeIngredient(sesameId, "Toasted Sesame Seeds", true, false), // optional!
            ]
        );

        var results = IngredientMatcher.RankRecipes([candidate], onHandItems);

        Assert.HasCount(1, results);
        Assert.AreEqual(1, results[0].MatchedCount);
        Assert.AreEqual(0, results[0].AdditionalNonStapleCount);
    }

    [TestMethod]
    public void RankRecipes_OptionalIngredient_MatchesOnHand_IncreasesMatchedCount()
    {
        var chickenId = Guid.NewGuid();
        var cilantroId = Guid.NewGuid();

        var onHandItems = new[]
        {
            new MatchableOnHandItem(chickenId, "Chicken Thighs", "chicken thighs", []),
            new MatchableOnHandItem(cilantroId, "Cilantro", "cilantro", []),
        };

        var candidate = new MatchableRecipeCandidate(
            CreateSummary("Roast Chicken"),
            [
                new MatchableRecipeIngredient(chickenId, "Chicken Thighs", false, false),
                new MatchableRecipeIngredient(cilantroId, "Cilantro for garnish", true, false), // optional but on-hand
            ]
        );

        var results = IngredientMatcher.RankRecipes([candidate], onHandItems);

        Assert.HasCount(1, results);
        Assert.AreEqual(2, results[0].MatchedCount);
    }

    [TestMethod]
    public void RankRecipes_FallbackMatchingByNameOrAlias_WhenFoodItemIdNull()
    {
        var onionId = Guid.NewGuid();

        var onHandItems = new[]
        {
            new MatchableOnHandItem(
                onionId,
                "Green Onion",
                "green onion",
                ["scallion", "spring onion"]
            ),
        };

        var candidate = new MatchableRecipeCandidate(
            CreateSummary("Scallion Pancakes"),
            [new MatchableRecipeIngredient(null, "Scallion", false, false)]
        );

        var results = IngredientMatcher.RankRecipes([candidate], onHandItems);

        Assert.HasCount(1, results);
        Assert.AreEqual(1, results[0].MatchedCount);
        Assert.AreEqual("Green Onion", results[0].MatchedIngredientNames[0]);
    }

    [TestMethod]
    public void RankRecipes_DeduplicatesMultipleIngredientOccurrences()
    {
        var garlicId = Guid.NewGuid();

        var onHandItems = new[]
        {
            new MatchableOnHandItem(garlicId, "Garlic", "garlic", ["garlic clove"]),
        };

        var candidate = new MatchableRecipeCandidate(
            CreateSummary("Double Garlic Chicken"),
            [
                new MatchableRecipeIngredient(garlicId, "Garlic (divided)", false, false),
                new MatchableRecipeIngredient(garlicId, "Garlic for sauce", false, false),
            ]
        );

        var results = IngredientMatcher.RankRecipes([candidate], onHandItems);

        Assert.HasCount(1, results);
        Assert.AreEqual(1, results[0].MatchedCount); // Garlic matched once, not twice
        Assert.AreEqual(0, results[0].AdditionalNonStapleCount);
    }
}
