using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeDraftNormalizerTests
{
    private readonly List<CatalogFoodLookupItem> _catalogFoods =
    [
        new(
            Guid.NewGuid(),
            "Garlic",
            "garlic",
            Guid.NewGuid(),
            false,
            ["garlic clove", "garlic cloves"]
        ),
        new(
            Guid.NewGuid(),
            "Yellow Onion",
            "yellow onion",
            Guid.NewGuid(),
            false,
            ["onion", "sweet onion"]
        ),
        new(Guid.NewGuid(), "Kosher Salt", "kosher salt", Guid.NewGuid(), true, ["salt"]),
    ];

    private readonly List<CatalogAisleLookupItem> _aisles =
    [
        new(Guid.NewGuid(), "Produce", "produce"),
        new(Guid.NewGuid(), "Spices & Baking", "spices & baking"),
        new(Guid.NewGuid(), "Other", "other"),
    ];

    private readonly List<CatalogTagLookupItem> _tags =
    [
        new(Guid.NewGuid(), "Main", "main", "course-main"),
        new(Guid.NewGuid(), "One-Pot", "one-pot", "equipment-one-pot"),
    ];

    [TestMethod]
    public void NormalizeUnit_StandardUnits_MapsCorrectly()
    {
        Assert.AreEqual(MeasurementUnit.Tbsp, RecipeDraftNormalizer.NormalizeUnit("tbsp").Unit);
        Assert.AreEqual(MeasurementUnit.Cup, RecipeDraftNormalizer.NormalizeUnit("cups").Unit);
        Assert.AreEqual(MeasurementUnit.G, RecipeDraftNormalizer.NormalizeUnit("grams").Unit);
        Assert.AreEqual(MeasurementUnit.Oz, RecipeDraftNormalizer.NormalizeUnit("ounce").Unit);
        Assert.AreEqual(MeasurementUnit.Tsp, RecipeDraftNormalizer.NormalizeUnit("teaspoon").Unit);
        Assert.AreEqual(MeasurementUnit.Pinch, RecipeDraftNormalizer.NormalizeUnit("pinch").Unit);
        Assert.AreEqual(MeasurementUnit.Clove, RecipeDraftNormalizer.NormalizeUnit("cloves").Unit);
    }

    [TestMethod]
    public void NormalizeUnit_CustomUnit_SetsCustomAndName()
    {
        var (unit, customName) = RecipeDraftNormalizer.NormalizeUnit("stalks");
        Assert.AreEqual(MeasurementUnit.Custom, unit);
        Assert.AreEqual("stalks", customName);
    }

    [TestMethod]
    public void Normalize_ExistingFoodItemMatched_BindsExistingIdAndNotNew()
    {
        var raw = new LlmExtractedRecipePayload(
            Title: "Simple Garlic Rice",
            Description: "Garlicky side dish.",
            BaseServings: 4,
            YieldDisplay: "4 servings",
            PrepTimeMinutes: 5,
            CookTimeMinutes: 20,
            TotalTimeMinutes: 25,
            Difficulty: "Easy",
            SourceName: "Home Kitchen",
            SourceAuthor: "Chef",
            ConfidenceScore: 0.95m,
            SuggestedTagNames: ["Main", "One-Pot"],
            Ingredients:
            [
                new(
                    SectionName: null,
                    Quantity: 4,
                    MaxQuantity: null,
                    Unit: "cloves",
                    CanonicalFoodName: "garlic cloves",
                    DisplayName: "garlic cloves, minced",
                    PreparationNote: "minced",
                    IsOptional: false,
                    OriginalRawText: "4 cloves garlic, minced",
                    SuggestedAisleName: "Produce"
                ),
            ],
            Steps:
            [
                new(
                    SectionName: null,
                    Instruction: "Saute garlic for 2 minutes then simmer for 15 minutes.",
                    IngredientIndices: [0],
                    Timers: null
                ),
            ]
        );

        var draft = RecipeDraftNormalizer.Normalize(
            raw,
            _catalogFoods,
            _aisles,
            _tags,
            sourceUrl: "https://example.com/garlic-rice",
            fallbackSourceName: null,
            fallbackSourceAuthor: null,
            coverImagePath: "/api/uploads/recipes/cover.webp",
            thumbnailImagePath: "/api/uploads/recipes/thumb.webp"
        );

        Assert.AreEqual("Simple Garlic Rice", draft.Title);
        Assert.AreEqual(1, draft.Ingredients?.Count);

        var ing = draft.Ingredients![0];
        Assert.IsFalse(ing.IsNewFoodItem);
        Assert.IsFalse(ing.CreateNewFoodItem);
        Assert.AreEqual(_catalogFoods[0].Id, ing.MatchedFoodItemId);
        Assert.AreEqual("Garlic", ing.MatchedFoodItemName);

        Assert.AreEqual(1, draft.Steps?.Count);
        var step = draft.Steps![0];
        Assert.AreEqual(2, step.Timers?.Count); // 2 minutes, 15 minutes auto-detected

        Assert.AreEqual(2, draft.SuggestedTagIds?.Count);
    }

    [TestMethod]
    public void Normalize_UnmatchedFoodItem_FlagsNewWithSuggestedAisle()
    {
        var raw = new LlmExtractedRecipePayload(
            Title: "Miso Glazed Tofu",
            Description: null,
            BaseServings: 2,
            YieldDisplay: null,
            PrepTimeMinutes: 10,
            CookTimeMinutes: 15,
            TotalTimeMinutes: null,
            Difficulty: "Medium",
            SourceName: null,
            SourceAuthor: null,
            ConfidenceScore: 0.88m,
            SuggestedTagNames: null,
            Ingredients:
            [
                new(
                    SectionName: null,
                    Quantity: 2,
                    MaxQuantity: null,
                    Unit: "tbsp",
                    CanonicalFoodName: "white miso paste",
                    DisplayName: "white miso paste",
                    PreparationNote: null,
                    IsOptional: false,
                    OriginalRawText: "2 tbsp white miso paste",
                    SuggestedAisleName: "International"
                ),
            ],
            Steps:
            [
                new(
                    SectionName: null,
                    Instruction: "Whisk miso and bake for 20 minutes.",
                    IngredientIndices: [0],
                    Timers: null
                ),
            ]
        );

        var draft = RecipeDraftNormalizer.Normalize(
            raw,
            _catalogFoods,
            _aisles,
            _tags,
            sourceUrl: null,
            fallbackSourceName: null,
            fallbackSourceAuthor: null,
            coverImagePath: null,
            thumbnailImagePath: null
        );

        Assert.AreEqual(1, draft.Ingredients?.Count);
        var ing = draft.Ingredients![0];
        Assert.IsTrue(ing.IsNewFoodItem);
        Assert.IsTrue(ing.CreateNewFoodItem);
        Assert.IsNull(ing.MatchedFoodItemId);
        Assert.AreEqual("White Miso Paste", ing.ProposedFoodItemName);
        Assert.AreEqual(_aisles[2].Id, ing.SuggestedStoreAisleId); // fallback to "Other"
    }
}
