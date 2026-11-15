using App.Core.FoodCatalog;
using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class EntityCandidateMatcherTests
{
    [TestMethod]
    public void Match_ResolvesExactNameAliasAndPluralFormsDeterministically()
    {
        var garlicId = Guid.NewGuid();
        var saltId = Guid.NewGuid();
        var veganTagId = Guid.NewGuid();

        var foods = new List<CatalogFoodLookupItem>
        {
            new(garlicId, "Garlic", "garlic", false, ["garlic clove"], FoodCategory.Produce),
            new(
                saltId,
                "Kosher Salt",
                "kosher salt",
                true,
                ["salt"],
                FoodCategory.SpicesAndSeasonings
            ),
        };
        var tags = new List<CatalogTagLookupItem> { new(veganTagId, "Vegan", "vegan", "vegan") };

        var recipe = new ExtractedRecipeDto(
            Title: "Roasted Garlic",
            ProposedTags: ["Vegan", "Brand New Tag"],
            Ingredients:
            [
                new("ing_1", CanonicalFoodItem: "garlics", RawText: "4 cloves garlic"),
                new("ing_2", CanonicalFoodItem: "salt", RawText: "1 tsp salt"),
                new("ing_3", CanonicalFoodItem: "dragonfruit", RawText: "1 cup dragonfruit"),
            ]
        );

        var result = EntityCandidateMatcher.Match(recipe, foods, tags);

        Assert.AreEqual(garlicId, result.IngredientMatches["ing_1"].MatchedFoodItemId);
        Assert.IsFalse(result.IngredientMatches["ing_1"].IsNewFoodItem);

        Assert.AreEqual(saltId, result.IngredientMatches["ing_2"].MatchedFoodItemId);
        Assert.IsTrue(result.IngredientMatches["ing_2"].IsPantryStaple);

        Assert.IsNull(result.IngredientMatches["ing_3"].MatchedFoodItemId);
        Assert.IsTrue(result.IngredientMatches["ing_3"].IsNewFoodItem);

        Assert.IsEmpty(result.AmbiguousIngredients);
        Assert.AreEqual(veganTagId, result.TagMatches["Vegan"].MatchedTagId);
        Assert.IsNull(result.TagMatches["Brand New Tag"].MatchedTagId);
        Assert.IsEmpty(result.AmbiguousTags);
    }

    [TestMethod]
    public void Match_SurfacesTopFuzzyCandidatesForAmbiguousEntriesAndAppliesDisambiguation()
    {
        var unsaltedId = Guid.NewGuid();
        var saltedId = Guid.NewGuid();
        var italianTagId = Guid.NewGuid();

        var foods = new List<CatalogFoodLookupItem>
        {
            new(
                unsaltedId,
                "Unsalted Butter",
                "unsalted butter",
                false,
                [],
                FoodCategory.DairyAndEggs
            ),
            new(saltedId, "Salted Butter", "salted butter", false, [], FoodCategory.DairyAndEggs),
        };
        var tags = new List<CatalogTagLookupItem>
        {
            new(italianTagId, "Italian Dinner", "italian dinner", "italian-dinner"),
        };

        var recipe = new ExtractedRecipeDto(
            Title: "Butter Pasta",
            ProposedTags: ["Italian"],
            Ingredients:
            [
                new("ing_1", CanonicalFoodItem: "butter", RawText: "4 tbsp butter"),
                new(
                    "ing_2",
                    CanonicalFoodItem: "Homemade Pasta",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "Homemade Pasta",
                    RawText: "1 recipe Homemade Pasta"
                ),
            ]
        );

        var initial = EntityCandidateMatcher.Match(recipe, foods, tags);

        // Recipe reference should not match a catalog food or trigger ambiguity
        Assert.IsFalse(initial.IngredientMatches["ing_2"].IsNewFoodItem);
        Assert.IsNull(initial.IngredientMatches["ing_2"].MatchedFoodItemId);

        // "butter" fuzzy-matches both Unsalted Butter and Salted Butter in [0.55, 0.92)
        Assert.HasCount(1, initial.AmbiguousIngredients);
        Assert.AreEqual("ing_1", initial.AmbiguousIngredients[0].RefId);
        Assert.IsGreaterThanOrEqualTo(2, initial.AmbiguousIngredients[0].Candidates.Count);
        Assert.IsTrue(initial.IngredientMatches["ing_1"].IsNewFoodItem);

        Assert.HasCount(1, initial.AmbiguousTags);
        Assert.AreEqual("Italian", initial.AmbiguousTags[0].ProposedTag);

        // Apply disambiguation decisions from Stage 3 micro-call
        var resolved = EntityCandidateMatcher.ApplyDisambiguation(
            initial,
            new LlmEntityDisambiguationResult(
                IngredientDecisions: [new("ing_1", unsaltedId)],
                TagDecisions: [new("Italian", italianTagId)]
            ),
            foods,
            tags
        );

        Assert.AreEqual(unsaltedId, resolved.IngredientMatches["ing_1"].MatchedFoodItemId);
        Assert.AreEqual("Unsalted Butter", resolved.IngredientMatches["ing_1"].MatchedFoodItemName);
        Assert.IsFalse(resolved.IngredientMatches["ing_1"].IsNewFoodItem);
        Assert.AreEqual(italianTagId, resolved.TagMatches["Italian"].MatchedTagId);
    }

    [TestMethod]
    public void ApplyDisambiguation_IgnoresHallucinatedCandidateIdsNotPresentedToLlm()
    {
        var unsaltedId = Guid.NewGuid();
        var unrelatedCatalogId = Guid.NewGuid();

        var foods = new List<CatalogFoodLookupItem>
        {
            new(
                unsaltedId,
                "Unsalted Butter",
                "unsalted butter",
                false,
                [],
                FoodCategory.DairyAndEggs
            ),
            new(
                unrelatedCatalogId,
                "Saffron Threads",
                "saffron threads",
                false,
                [],
                FoodCategory.SpicesAndSeasonings
            ),
        };

        var recipe = new ExtractedRecipeDto(
            Title: "Toast",
            Ingredients: [new("ing_1", CanonicalFoodItem: "butter", RawText: "1 tbsp butter")]
        );

        var initial = EntityCandidateMatcher.Match(recipe, foods, []);
        Assert.HasCount(1, initial.AmbiguousIngredients);

        var resolved = EntityCandidateMatcher.ApplyDisambiguation(
            initial,
            new LlmEntityDisambiguationResult(
                IngredientDecisions: [new("ing_1", unrelatedCatalogId)]
            ),
            foods,
            []
        );

        Assert.IsNull(resolved.IngredientMatches["ing_1"].MatchedFoodItemId);
        Assert.IsTrue(resolved.IngredientMatches["ing_1"].IsNewFoodItem);
    }

    [TestMethod]
    public void Match_ResolvesTagAliasesModifiersAndFiltersDisallowedTags()
    {
        var waterId = Guid.NewGuid();
        var eggId = Guid.NewGuid();
        var boilTagId = Guid.NewGuid();
        var eggsTagId = Guid.NewGuid();

        var foods = new List<CatalogFoodLookupItem>
        {
            new(waterId, "Water", "water", true, [], FoodCategory.Other),
            new(eggId, "Egg", "egg", false, [], FoodCategory.DairyAndEggs),
        };
        var tags = new List<CatalogTagLookupItem>
        {
            new(boilTagId, "Boil & Simmer", "boil & simmer", "boil-and-simmer"),
            new(eggsTagId, "Eggs", "eggs", "eggs"),
        };

        var recipe = new ExtractedRecipeDto(
            Title: "Hard-Cooked Eggs",
            ProposedTags: ["Boiled", "Egg", "Produce", "Kosher Salt"],
            Ingredients:
            [
                new("ing_1", CanonicalFoodItem: "cold water", RawText: "2 cups cold water"),
                new("ing_2", CanonicalFoodItem: "large egg", RawText: "6 large eggs"),
            ]
        );

        var result = EntityCandidateMatcher.Match(recipe, foods, tags);

        Assert.AreEqual(waterId, result.IngredientMatches["ing_1"].MatchedFoodItemId);
        Assert.AreEqual(eggId, result.IngredientMatches["ing_2"].MatchedFoodItemId);
        Assert.AreEqual(boilTagId, result.TagMatches["Boiled"].MatchedTagId);
        Assert.AreEqual(eggsTagId, result.TagMatches["Egg"].MatchedTagId);
        Assert.IsFalse(result.TagMatches.ContainsKey("Produce"));
        Assert.IsFalse(result.TagMatches.ContainsKey("Kosher Salt"));
    }

    [TestMethod]
    public void Match_WhenVeganMatchedWithoutVegetarian_AutomaticallyIncludesVegetarianTag()
    {
        var veganTagId = Guid.NewGuid();
        var vegetarianTagId = Guid.NewGuid();
        var tags = new List<CatalogTagLookupItem>
        {
            new(veganTagId, "Vegan", "vegan", "vegan"),
            new(vegetarianTagId, "Vegetarian", "vegetarian", "vegetarian"),
        };

        var recipe = new ExtractedRecipeDto(
            Title: "Ultracreamy Hummus",
            ProposedTags: ["Vegan", "Appetizer"],
            Ingredients: [new("ing_1", CanonicalFoodItem: "chickpea", RawText: "2 cans chickpeas")]
        );

        var result = EntityCandidateMatcher.Match(recipe, [], tags);

        Assert.IsTrue(result.TagMatches.ContainsKey("Vegan"));
        Assert.IsTrue(result.TagMatches.ContainsKey("Vegetarian"));
        Assert.AreEqual(vegetarianTagId, result.TagMatches["Vegetarian"].MatchedTagId);
    }

    [TestMethod]
    public void Match_ResolvesBareSpicePepperToBlackPepper_AndAutoAttachesDairyFreeAndNoCook()
    {
        var blackPepperId = Guid.NewGuid();
        var bellPepperId = Guid.NewGuid();
        var veganId = Guid.NewGuid();
        var vegId = Guid.NewGuid();
        var dairyFreeId = Guid.NewGuid();
        var noCookId = Guid.NewGuid();

        var foods = new List<CatalogFoodLookupItem>
        {
            new(
                blackPepperId,
                "Black Pepper",
                "black pepper",
                true,
                [],
                FoodCategory.SpicesAndSeasonings
            ),
            new(bellPepperId, "Bell Pepper", "bell pepper", false, [], FoodCategory.Produce),
        };
        var tags = new List<CatalogTagLookupItem>
        {
            new(veganId, "Vegan", "vegan", "vegan"),
            new(vegId, "Vegetarian", "vegetarian", "vegetarian"),
            new(dairyFreeId, "Dairy-Free", "dairy-free", "dairy-free"),
            new(noCookId, "No-Cook", "no-cook", "no-cook"),
        };

        var recipe = new ExtractedRecipeDto(
            Title: "Herbed Avocado Dip",
            CookTimeMinutes: 0,
            ProposedTags: ["Vegan", "Dip"],
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "pepper",
                    DisplayName: "pepper",
                    Quantity: 0.25m,
                    Unit: "tsp",
                    RawText: "1/4 teaspoon pepper"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "bell pepper",
                    DisplayName: "red bell pepper",
                    Quantity: 1m,
                    Unit: "whole",
                    RawText: "1 red bell pepper, seeded and diced"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Toss {{ing_2_a}} with {{ing_1_a}}.",
                    IngredientTokens:
                    [
                        new("ing_2_a", "ing_2", 1m, "whole"),
                        new("ing_1_a", "ing_1", 0.25m, "tsp"),
                    ]
                ),
            ]
        );

        var result = EntityCandidateMatcher.Match(recipe, foods, tags);

        Assert.AreEqual(blackPepperId, result.IngredientMatches["ing_1"].MatchedFoodItemId);
        Assert.AreEqual(bellPepperId, result.IngredientMatches["ing_2"].MatchedFoodItemId);
        Assert.AreEqual(vegId, result.TagMatches["Vegetarian"].MatchedTagId);
        Assert.AreEqual(dairyFreeId, result.TagMatches["Dairy-Free"].MatchedTagId);
        Assert.AreEqual(noCookId, result.TagMatches["No-Cook"].MatchedTagId);
    }
}
