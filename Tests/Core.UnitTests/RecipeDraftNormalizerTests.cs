using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeDraftNormalizerTests
{
    private readonly List<CatalogFoodLookupItem> _catalogFoods =
    [
        new(Guid.NewGuid(), "Garlic", "garlic", false, ["garlic clove", "garlic cloves"]),
        new(Guid.NewGuid(), "Yellow Onion", "yellow onion", false, ["onion", "sweet onion"]),
        new(Guid.NewGuid(), "Kosher Salt", "kosher salt", true, ["salt"]),
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
                    OriginalRawText: "4 cloves garlic, minced"
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
    public void Normalize_UnmatchedFoodItem_FlagsNew()
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
                    OriginalRawText: "2 tbsp white miso paste"
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
    }

    [TestMethod]
    public void BuildNormalizedWebText_CompleteJsonLd_OmitsCleanedBodyTextClutter()
    {
        var payload = new ScrapedWebRecipePayload(
            CanonicalUrl: "https://instantpot.com/blogs/recipes/coconut-chicken-curry",
            PageTitle: "Coconut Chicken Curry",
            SiteName: "Instant Pot",
            Author: "Instant Pot Kitchen",
            Description: "Creamy weeknight coconut curry.",
            PrepTimeIso: "PT10M",
            PrepTimeMinutes: 10,
            CookTimeIso: "PT15M",
            CookTimeMinutes: 15,
            TotalTimeIso: "PT25M",
            TotalTimeMinutes: 25,
            RecipeYieldText: "4 servings",
            PrimaryImageUrl: null,
            RawIngredientLines: ["1.5 lbs chicken thighs", "1 can coconut milk"],
            RawInstructionLines: ["Saute chicken.", "Pressure cook on High for 8 minutes."],
            CleanedBodyText: "Shop Accessories Cart Newsletter Mega Menu Footer Clutter...",
            HadRecipeJsonLd: true
        );

        var normalized = IngestionProcessor.BuildNormalizedWebText(payload);

        StringAssert.Contains(normalized, "Title: Coconut Chicken Curry");
        StringAssert.Contains(normalized, "- 1.5 lbs chicken thighs");
        StringAssert.Contains(normalized, "1. Saute chicken.");
        Assert.IsFalse(
            normalized.Contains("Cleaned Page Text:", StringComparison.Ordinal),
            "Complete JSON-LD recipes should omit raw DOM body clutter."
        );
        Assert.IsFalse(normalized.Contains("Newsletter Mega Menu", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildNormalizedWebText_MissingOrIncompleteJsonLd_IncludesCleanedBodyText()
    {
        var noJsonLdPayload = new ScrapedWebRecipePayload(
            CanonicalUrl: "https://example.com/blog-recipe",
            PageTitle: "Simple Soup",
            SiteName: "Example Kitchen",
            Author: null,
            Description: null,
            PrepTimeIso: null,
            PrepTimeMinutes: null,
            CookTimeIso: null,
            CookTimeMinutes: null,
            TotalTimeIso: null,
            TotalTimeMinutes: null,
            RecipeYieldText: null,
            PrimaryImageUrl: null,
            RawIngredientLines: [],
            RawInstructionLines: [],
            CleanedBodyText: "Simmer 4 cups broth with diced carrots for 20 minutes.",
            HadRecipeJsonLd: false
        );

        var normalized = IngestionProcessor.BuildNormalizedWebText(noJsonLdPayload);

        StringAssert.Contains(normalized, "Cleaned Page Text:");
        StringAssert.Contains(normalized, "Simmer 4 cups broth with diced carrots for 20 minutes.");

        var incompleteJsonLdPayload = noJsonLdPayload with
        {
            HadRecipeJsonLd = true,
            RawIngredientLines = ["4 cups broth"],
            RawInstructionLines = [],
        };

        var normalizedIncomplete = IngestionProcessor.BuildNormalizedWebText(
            incompleteJsonLdPayload
        );
        StringAssert.Contains(normalizedIncomplete, "Cleaned Page Text:");
    }

    [TestMethod]
    public void Normalize_EmptyAppCatalogs_ProposesCleanTagsFoodItemsAndPreservesCollections()
    {
        var collectionId = Guid.NewGuid();
        var raw = new LlmExtractedRecipePayload(
            Title: "Skillet Chicken Parmesan",
            Description: "Crisp weeknight chicken parmesan.",
            BaseServings: 4,
            YieldDisplay: "Serves 4",
            PrepTimeMinutes: 20,
            CookTimeMinutes: 25,
            TotalTimeMinutes: 45,
            Difficulty: "Medium",
            SourceName: null,
            SourceAuthor: null,
            ConfidenceScore: 0.93m,
            SuggestedTagNames: ["dinner", "italian", "one-pot", "Italian"],
            Ingredients:
            [
                new(
                    SectionName: null,
                    Quantity: 1.5m,
                    MaxQuantity: null,
                    Unit: "lb",
                    CanonicalFoodName: "boneless skinless chicken breast",
                    DisplayName: "boneless, skinless chicken breasts",
                    PreparationNote: "pounded to 1/2-inch thickness",
                    IsOptional: false,
                    OriginalRawText: "1 1/2 pounds boneless, skinless chicken breasts",
                    SuggestedIsPantryStaple: false
                ),
                new(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: "tsp",
                    CanonicalFoodName: "table salt",
                    DisplayName: "table salt",
                    PreparationNote: null,
                    IsOptional: false,
                    OriginalRawText: "1 teaspoon table salt",
                    SuggestedIsPantryStaple: true
                ),
            ],
            Steps:
            [
                new(
                    SectionName: null,
                    Instruction: "Bake until cheese melts, about 10 minutes.",
                    IngredientIndices: [0, 1],
                    Timers: null
                ),
            ]
        );

        var draft = RecipeDraftNormalizer.Normalize(
            raw: raw,
            catalogFoods: [],
            tags: [],
            sourceUrl: null,
            fallbackSourceName: "America's Test Kitchen",
            fallbackSourceAuthor: "America's Test Kitchen",
            coverImagePath: "/api/uploads/recipes/chicken-parm.webp",
            thumbnailImagePath: "/api/uploads/recipes/chicken-parm-thumb.webp",
            fallbackSourceBookTitle: "The Complete America's Test Kitchen TV Show Cookbook",
            fallbackSourceBookPage: 214,
            fallbackCollectionIds: [collectionId]
        );

        Assert.AreEqual(0, draft.SuggestedTagIds?.Count);
        CollectionAssert.AreEqual(
            new[] { "Dinner", "Italian", "One-Pot" },
            draft.ProposedTagNames?.ToArray()
        );
        CollectionAssert.AreEqual(new[] { collectionId }, draft.SuggestedCollectionIds?.ToArray());
        Assert.AreEqual("America's Test Kitchen", draft.SourceName);
        Assert.AreEqual(214, draft.SourceBookPage);
        Assert.IsTrue(draft.Ingredients![1].SuggestedIsPantryStaple);
    }

    [TestMethod]
    public void Normalize_MixedExistingAndNewTagsAndBidirectionalPluralFoods_MatchesCleanly()
    {
        var catalogFoods = new List<CatalogFoodLookupItem>
        {
            new(Guid.NewGuid(), "Eggs", "eggs", false, []),
        };
        var existingDessertTagId = Guid.NewGuid();
        var tags = new List<CatalogTagLookupItem>
        {
            new(existingDessertTagId, "Dessert", "dessert", "course-dessert"),
        };

        var raw = new LlmExtractedRecipePayload(
            Title: "French Silk Pie",
            Description: null,
            BaseServings: 8,
            YieldDisplay: "1 pie",
            PrepTimeMinutes: 30,
            CookTimeMinutes: 15,
            TotalTimeMinutes: 45,
            Difficulty: "Project",
            SourceName: null,
            SourceAuthor: null,
            ConfidenceScore: 0.91m,
            SuggestedTagNames: ["Desserts", "French", "Make-Ahead"],
            Ingredients:
            [
                new(
                    SectionName: null,
                    Quantity: 4m,
                    MaxQuantity: null,
                    Unit: "whole",
                    CanonicalFoodName: "egg",
                    DisplayName: "large eggs",
                    PreparationNote: null,
                    IsOptional: false,
                    OriginalRawText: "4 large eggs"
                ),
            ],
            Steps:
            [
                new(
                    SectionName: null,
                    Instruction: "Whip until silky.",
                    IngredientIndices: [0],
                    Timers: null
                ),
            ]
        );

        var draft = RecipeDraftNormalizer.Normalize(
            raw,
            catalogFoods,
            tags,
            sourceUrl: null,
            fallbackSourceName: null,
            fallbackSourceAuthor: null,
            coverImagePath: null,
            thumbnailImagePath: null
        );

        Assert.AreEqual(catalogFoods[0].Id, draft.Ingredients![0].MatchedFoodItemId);
        CollectionAssert.AreEqual(new[] { existingDessertTagId }, draft.SuggestedTagIds?.ToArray());
        CollectionAssert.AreEqual(
            new[] { "French", "Make-Ahead" },
            draft.ProposedTagNames?.ToArray()
        );
    }

    [TestMethod]
    public void InferTagCategory_MapsCommonCategoriesCorrectly()
    {
        Assert.AreEqual(TagCategory.Course, RecipeDraftNormalizer.InferTagCategory("Dinner"));
        Assert.AreEqual(TagCategory.Cuisine, RecipeDraftNormalizer.InferTagCategory("Italian"));
        Assert.AreEqual(TagCategory.Dietary, RecipeDraftNormalizer.InferTagCategory("Gluten-Free"));
        Assert.AreEqual(TagCategory.Equipment, RecipeDraftNormalizer.InferTagCategory("Sheet Pan"));
        Assert.AreEqual(TagCategory.Custom, RecipeDraftNormalizer.InferTagCategory("Weeknight"));
    }

    [TestMethod]
    public void NormalizeQuantities_ZeroOrNegativeQuantity_NormalizesToNull()
    {
        var (qZero, maxQZero) = RecipeDraftNormalizer.NormalizeQuantities(0m, null);
        Assert.IsNull(qZero);
        Assert.IsNull(maxQZero);

        var (qNeg, maxQNeg) = RecipeDraftNormalizer.NormalizeQuantities(-1.5m, 0m);
        Assert.IsNull(qNeg);
        Assert.IsNull(maxQNeg);
    }

    [TestMethod]
    public void NormalizeQuantities_MaxQuantitySmallerThanQuantity_SwapsValues()
    {
        var (qty, maxQty) = RecipeDraftNormalizer.NormalizeQuantities(2m, 1m);
        Assert.AreEqual(1m, qty);
        Assert.AreEqual(2m, maxQty);
    }

    [TestMethod]
    public void NormalizeQuantities_MaxQuantityOnly_PromotesToBaseQuantity()
    {
        var (qty, maxQty) = RecipeDraftNormalizer.NormalizeQuantities(null, 2.5m);
        Assert.AreEqual(2.5m, qty);
        Assert.IsNull(maxQty);
    }

    [TestMethod]
    public void NormalizeQuantities_EqualQuantityAndMaxQuantity_ClearsRedundantMax()
    {
        var (qty, maxQty) = RecipeDraftNormalizer.NormalizeQuantities(2m, 2m);
        Assert.AreEqual(2m, qty);
        Assert.IsNull(maxQty);
    }

    [TestMethod]
    public void Normalize_ZeroQuantityIngredient_ProducesNullQuantityInDraft()
    {
        var raw = new LlmExtractedRecipePayload(
            Title: "Garlic Bread",
            Description: null,
            BaseServings: 4,
            YieldDisplay: null,
            PrepTimeMinutes: null,
            CookTimeMinutes: null,
            TotalTimeMinutes: null,
            Difficulty: null,
            SourceName: null,
            SourceAuthor: null,
            ConfidenceScore: 0.9m,
            SuggestedTagNames: null,
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    SectionName: null,
                    Quantity: 0m, // 0 quantity from LLM
                    MaxQuantity: 0m,
                    Unit: null,
                    CanonicalFoodName: "garlic powder",
                    DisplayName: "garlic powder to taste",
                    PreparationNote: null,
                    IsOptional: false,
                    OriginalRawText: "garlic powder to taste"
                ),
            ],
            Steps: [new LlmExtractedStepItem(null, "Toast bread with butter and garlic powder.")]
        );

        var draft = RecipeDraftNormalizer.Normalize(
            raw,
            catalogFoods: [],
            tags: [],
            sourceUrl: null,
            fallbackSourceName: null,
            fallbackSourceAuthor: null,
            coverImagePath: null,
            thumbnailImagePath: null
        );

        Assert.AreEqual(1, draft.Ingredients?.Count);
        Assert.IsNull(draft.Ingredients![0].Quantity, "0 quantity should be normalized to null.");
        Assert.IsNull(
            draft.Ingredients![0].MaxQuantity,
            "0 max quantity should be normalized to null."
        );
    }

    [TestMethod]
    public void Normalize_PlaceholderFoodName_DoesNotProposeNewFoodItemOrMatchPlaceholderCatalogItem()
    {
        var catalogWithNullItem = new List<CatalogFoodLookupItem>
        {
            new(Guid.NewGuid(), "Null", "null", false, []),
            new(Guid.NewGuid(), "Kosher Salt", "kosher salt", true, ["salt"]),
        };

        var raw = new LlmExtractedRecipePayload(
            Title: "Seasoned Water",
            Description: null,
            BaseServings: 1,
            YieldDisplay: null,
            PrepTimeMinutes: null,
            CookTimeMinutes: null,
            TotalTimeMinutes: null,
            Difficulty: null,
            SourceName: null,
            SourceAuthor: null,
            ConfidenceScore: 0.9m,
            SuggestedTagNames: null,
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: "pinch",
                    CanonicalFoodName: "null",
                    DisplayName: "salt and pepper to taste",
                    PreparationNote: null,
                    IsOptional: false,
                    OriginalRawText: "salt and pepper to taste"
                ),
                new LlmExtractedIngredientItem(
                    SectionName: null,
                    Quantity: null,
                    MaxQuantity: null,
                    Unit: null,
                    CanonicalFoodName: "none",
                    DisplayName: "optional garnish",
                    PreparationNote: null,
                    IsOptional: true,
                    OriginalRawText: "optional garnish"
                ),
            ],
            Steps: [new LlmExtractedStepItem(null, "Stir ingredients.")]
        );

        var draft = RecipeDraftNormalizer.Normalize(
            raw,
            catalogFoods: catalogWithNullItem,
            tags: [],
            sourceUrl: null,
            fallbackSourceName: null,
            fallbackSourceAuthor: null,
            coverImagePath: null,
            thumbnailImagePath: null
        );

        Assert.AreEqual(2, draft.Ingredients?.Count);

        var ing1 = draft.Ingredients![0];
        Assert.IsNull(
            ing1.MatchedFoodItemId,
            "Placeholder canonical name should not match catalog item 'Null'."
        );
        Assert.IsFalse(
            ing1.CreateNewFoodItem,
            "Should not create a new food item from placeholder name."
        );
        Assert.AreNotEqual(
            "Null",
            ing1.ProposedFoodItemName,
            "Proposed food name should never be 'Null'."
        );

        var ing2 = draft.Ingredients![1];
        Assert.IsNull(ing2.MatchedFoodItemId);
        Assert.IsFalse(ing2.CreateNewFoodItem);
        Assert.AreNotEqual("None", ing2.ProposedFoodItemName);
        Assert.IsNull(
            draft.Ingredients![0].MaxQuantity,
            "0 max quantity should be normalized to null."
        );
    }

    [TestMethod]
    [DataRow(
        "WHY THIS RECIPE WORKS\r\n\r\nTraditional chili con carne often relies on chili powder, which can taste stale and muddy. We wanted a deeply flavored, robust chili.",
        "Traditional chili con carne often relies on chili powder, which can taste stale and muddy. We wanted a deeply flavored, robust chili."
    )]
    [DataRow(
        "Why This Recipe Works: We toasted ancho and chipotle chiles and ground them ourselves.",
        "We toasted ancho and chipotle chiles and ground them ourselves."
    )]
    [DataRow(
        "Why This Recipe Works - Over low heat, onions take nearly an hour to caramelize.",
        "Over low heat, onions take nearly an hour to caramelize."
    )]
    [DataRow("WHY THIS RECIPE WORKS", null)]
    [DataRow(
        "A classic French onion soup topped with toasted baguette and melted Gruyere.",
        "A classic French onion soup topped with toasted baguette and melted Gruyere."
    )]
    [DataRow(null, null)]
    public void NormalizeDescription_StripsAtkPrefixAndPreservesVerbatimNarrative(
        string? input,
        string? expected
    )
    {
        var result = RecipeDraftNormalizer.NormalizeDescription(input);
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void TryExtractAtkHeadnote_ExtractsVerbatimHeadnoteBetweenHeaderAndTerminator()
    {
        var rawText = """
            SLOW-COOKER BEEF CHILI
            WHY THIS RECIPE WORKS
            Traditional chili con carne often relies on chili powder, which can taste stale and muddy. We wanted a deeply flavored, robust chili that highlighted the real flavor of whole dried chiles. We toasted ancho and chipotle chiles and ground them ourselves with cumin, coriander, and oregano. For the beef, chuck roast cut into bite-sized pieces provided far better texture than ground beef.

            SERVES 6 TO 8
            TOTAL TIME: 7 to 8 hours on low or 5 to 6 hours on high
            BEFORE YOU BEGIN
            Look for dried ancho and chipotle chiles in the supermarket.

            INGREDIENTS
            3 dried ancho chiles
            """;

        var headnote = RecipeDraftNormalizer.TryExtractAtkHeadnote(rawText);

        Assert.IsNotNull(headnote);
        Assert.IsTrue(headnote.StartsWith("Traditional chili con carne"));
        Assert.IsTrue(headnote.EndsWith("far better texture than ground beef."));
        Assert.IsFalse(headnote.Contains("WHY THIS RECIPE WORKS"));
        Assert.IsFalse(headnote.Contains("SERVES 6 TO 8"));
    }

    [TestMethod]
    public void Normalize_WithRawSourceContainingAtkHeadnote_PrefersVerbatimHeadnoteOverInferiorLlmSummary()
    {
        var rawSource = """
            CLASSIC BEEF CHILI
            WHY THIS RECIPE WORKS
            Traditional chili con carne often relies on chili powder, which can taste stale and muddy. We wanted a deeply flavored, robust chili that highlighted the real flavor of whole dried chiles. We toasted ancho and chipotle chiles and ground them ourselves with cumin, coriander, and oregano.

            SERVES 6 TO 8
            INGREDIENTS
            1 lb chuck roast
            """;

        var rawPayload = new LlmExtractedRecipePayload(
            Title: "Classic Beef Chili",
            Description: "A hearty beef chili.",
            BaseServings: 6,
            YieldDisplay: "6 to 8 servings",
            PrepTimeMinutes: null,
            CookTimeMinutes: null,
            TotalTimeMinutes: null,
            Difficulty: null,
            SourceName: "America's Test Kitchen",
            SourceAuthor: null,
            ConfidenceScore: 0.95m,
            SuggestedTagNames: null,
            Ingredients:
            [
                new LlmExtractedIngredientItem(
                    SectionName: null,
                    Quantity: 1m,
                    MaxQuantity: null,
                    Unit: "lb",
                    CanonicalFoodName: "beef chuck roast",
                    DisplayName: "chuck roast",
                    PreparationNote: null,
                    IsOptional: false,
                    OriginalRawText: "1 lb chuck roast"
                ),
            ],
            Steps: [new LlmExtractedStepItem(null, "Cook chili in pot.")]
        );

        var draft = RecipeDraftNormalizer.Normalize(
            raw: rawPayload,
            catalogFoods: [],
            tags: [],
            sourceUrl: null,
            fallbackSourceName: null,
            fallbackSourceAuthor: null,
            coverImagePath: null,
            thumbnailImagePath: null,
            rawSourceText: rawSource
        );

        Assert.IsNotNull(draft.Description);
        Assert.IsTrue(
            draft.Description.StartsWith("Traditional chili con carne"),
            "Should use the verbatim ATK headnote from the source text rather than the inferior LLM summary."
        );
        Assert.IsFalse(
            draft.Description.Contains("WHY THIS RECIPE WORKS"),
            "Boilerplate header should be stripped."
        );
    }
}
