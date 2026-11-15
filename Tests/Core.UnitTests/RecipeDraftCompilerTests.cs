using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeDraftCompilerTests
{
    [TestMethod]
    public void Compile_TransformsPlaceholdersIntoCanonicalInlineTokensWithMetadata()
    {
        var flourId = Guid.NewGuid();
        var dinnerTagId = Guid.NewGuid();

        var foods = new List<CatalogFoodLookupItem>
        {
            new(
                flourId,
                "All-Purpose Flour",
                "all purpose flour",
                true,
                ["flour"],
                FoodCategory.Baking
            ),
        };
        var tags = new List<CatalogTagLookupItem>
        {
            new(dinnerTagId, "Weeknight Dinner", "weeknight dinner", "weeknight-dinner"),
        };

        var extracted = new ExtractedRecipeDto(
            Title: "Simple Flatbread",
            Description: "Quick pan flatbread.",
            Servings: 4m,
            Difficulty: "easy",
            ProposedTags: ["weeknight dinner", "quick bread"],
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    SectionName: "Main Ingredients",
                    CanonicalFoodItem: "all-purpose flour",
                    DisplayName: "All-purpose flour",
                    Quantity: 2m,
                    Unit: "cups",
                    AltQuantity: 240m,
                    AltUnit: "g",
                    RawText: "2 cups (240 g) all-purpose flour"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 2,
                    TemplateText: "Bake at {{temp_1}} for {{timer_1}}.",
                    TimerTokens: [new("timer_1", 300, "Bake flatbread")],
                    TemperatureTokens: [new("temp_1", 425m, "F")]
                ),
                new(
                    StepNumber: 1,
                    TemplateText: "Whisk {{ing_1_a}} in a bowl.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 2m, "cups")]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        );

        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, foods, tags);

        var draft = RecipeDraftCompiler.Compile(
            extracted,
            validation,
            matches,
            sourceUrl: "https://example.com/flatbread",
            extractedModel: "gemma4:12b"
        );

        Assert.AreEqual("Simple Flatbread", draft.Title);
        Assert.AreEqual(RecipeDifficulty.Easy, draft.Difficulty);
        Assert.AreEqual("https://example.com/flatbread", draft.SourceUrl);
        Assert.AreEqual("gemma4:12b", draft.ExtractedModel);
        Assert.HasCount(1, draft.Ingredients!);
        Assert.IsNull(draft.Ingredients![0].SectionName); // Generic "Main Ingredients" normalized to null
        Assert.AreEqual(flourId, draft.Ingredients[0].MatchedFoodItemId);
        Assert.AreEqual(240m, draft.Ingredients[0].AlternativeMeasurement?.Quantity);
        Assert.AreEqual(MeasurementUnit.G, draft.Ingredients[0].AlternativeMeasurement?.Unit);

        Assert.IsTrue(draft.SuggestedTagIds!.Contains(dinnerTagId));
        Assert.AreEqual("Quick Bread", draft.ProposedTagNames!.Single());

        // Verify StepNumber ordering and compiled ||{...}|| tokens
        Assert.HasCount(2, draft.Steps!);
        StringAssert.Contains(
            draft.Steps![0].Instruction,
            "||{\"ingredient\":\"All-purpose flour\",\"amount\":2,\"unit\":\"cup\",\"type\":\"Ingredient\"}||"
        );
        Assert.AreEqual(0, draft.Steps[0].IngredientIndices!.Single());
        Assert.AreEqual(2m, draft.Steps[0].IngredientAmounts!.Single().Measurement.Quantity);

        StringAssert.Contains(
            draft.Steps[1].Instruction,
            "||{\"fahrenheit\":425,\"type\":\"Temperature\"}||"
        );
        StringAssert.Contains(
            draft.Steps[1].Instruction,
            "||{\"seconds\":300,\"label\":\"Bake flatbread\",\"type\":\"Timer\"}||"
        );
        Assert.AreEqual(300, draft.Steps[1].Timers!.Single().DurationSeconds);
        Assert.AreEqual("Bake flatbread", draft.Steps[1].Timers!.Single().Label);

        Assert.IsEmpty(draft.ValidationIssues!);
        Assert.IsEmpty(draft.QualityWarnings!);
        Assert.AreEqual(0.96m, draft.ConfidenceScore);
    }

    [TestMethod]
    public void Compile_ImplausibleRecipeAndValidationErrors_LowersConfidenceAndCapturesDiagnostics()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Blog Post",
            Ingredients:
            [
                new("ing_1", CanonicalFoodItem: "apples", DisplayName: "apples", Quantity: 2m),
            ],
            Steps: [new(StepNumber: 1, TemplateText: "Read about {{missing_tok}}.")],
            QualityAudit: new RecipeQualityAuditDto(
                IsPlausibleRecipe: false,
                Warnings: ["Instructions are missing quantities."]
            )
        );

        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, [], []);
        var draft = RecipeDraftCompiler.Compile(extracted, validation, matches);

        Assert.IsNotEmpty(draft.ValidationIssues!);
        Assert.IsTrue(
            draft.QualityWarnings!.Any(w =>
                w.Contains("not be a complete recipe", StringComparison.OrdinalIgnoreCase)
            )
        );
        Assert.IsTrue(
            draft.QualityWarnings.Any(w =>
                w.Contains("Instructions are missing quantities", StringComparison.Ordinal)
            )
        );
        Assert.IsLessThan(0.80m, draft.ConfidenceScore);
    }

    [TestMethod]
    public void Compile_FixesOtherCategoryStaplesPrecisionDurationTagsAndCleanTokens()
    {
        var boilTagId = Guid.NewGuid();
        var quickTagId = Guid.NewGuid();
        var tags = new List<CatalogTagLookupItem>
        {
            new(boilTagId, "Boil & Simmer", "boil & simmer", "boil-and-simmer"),
            new(quickTagId, "Quick Prep", "quick prep", "quick-prep"),
        };

        var extracted = new ExtractedRecipeDto(
            Title: "Slow Steamed Beans",
            TotalTimeMinutes: 55,
            ProposedTags: ["Boiled", "Quick Prep", "30-Minute Meal", "SpicesAndSeasonings"],
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "vegetable oil",
                    DisplayName: "vegetable oil",
                    Category: "Other",
                    Quantity: 0.3333333333333333m,
                    Unit: "cup",
                    RawText: "1/3 cup vegetable oil"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "baking soda",
                    DisplayName: "baking soda",
                    Category: "Baking",
                    Quantity: 0.5m,
                    Unit: "tsp",
                    RawText: "1/2 tsp baking soda"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "cold water",
                    DisplayName: "cold water",
                    Category: "Other",
                    Quantity: 1m,
                    Unit: "inch",
                    RawText: "1 inch cold water"
                ),
                new(
                    "ing_4",
                    CanonicalFoodItem: "large egg",
                    DisplayName: "large egg",
                    Category: "DairyAndEggs",
                    Quantity: 2m,
                    Unit: "whole",
                    RawText: "2 large eggs"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Bring {{ing_3_a}} water to a boil with {{ing_1_a}} for {{timer_1}} to {{timer_2}} seconds.",
                    IngredientTokens:
                    [
                        new("ing_3_a", "ing_3", 1m, "inch"),
                        new("ing_1_a", "ing_1", 0.3333333333333333m, "cup"),
                    ],
                    TimerTokens:
                    [
                        new("timer_1", 60, "Simmer beans"),
                        new("timer_2", 120, "Finish simmer"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        );

        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, [], tags);
        var draft = RecipeDraftCompiler.Compile(extracted, validation, matches);

        var oil = draft.Ingredients![0];
        Assert.AreEqual(FoodCategory.CondimentsAndSauces, oil.SuggestedCategory);
        Assert.IsTrue(oil.SuggestedIsPantryStaple);
        Assert.AreEqual(0.3333m, oil.Quantity);

        var soda = draft.Ingredients[1];
        Assert.IsTrue(soda.SuggestedIsPantryStaple);

        var water = draft.Ingredients[2];
        Assert.AreEqual("water", water.ProposedFoodItemName);
        Assert.IsTrue(water.SuggestedIsPantryStaple);

        var egg = draft.Ingredients[3];
        Assert.AreEqual("egg", egg.ProposedFoodItemName);
        Assert.IsFalse(egg.SuggestedIsPantryStaple);

        Assert.Contains(boilTagId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(quickTagId, draft.SuggestedTagIds!);
        Assert.IsEmpty(draft.ProposedTagNames!);

        var step = draft.Steps!.Single();
        StringAssert.Contains(step.Instruction, "\"amount\":0.3333");
        StringAssert.Contains(step.Instruction, "\"seconds\":60");
        Assert.IsFalse(step.Instruction.Contains("\"seconds\":120", StringComparison.Ordinal));
        Assert.IsFalse(step.Instruction.Contains("} water to a boil", StringComparison.Ordinal));
        Assert.IsFalse(step.Instruction.Contains("} seconds", StringComparison.Ordinal));
        Assert.AreEqual(
            1m,
            step.IngredientAmounts!.First(a => a.IngredientIndex == 2).Measurement.Quantity
        );
        Assert.AreEqual(
            0.3333m,
            step.IngredientAmounts!.First(a => a.IngredientIndex == 0).Measurement.Quantity
        );
    }

    [TestMethod]
    public void Compile_PromotesOrphanedAltMeasurement_NormalizesZeroTimes_AndFixesMisclassifiedCategory()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Guacamole & Eggs",
            PrepTimeMinutes: null,
            CookTimeMinutes: 0,
            TotalTimeMinutes: 0,
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "water",
                    DisplayName: "water",
                    Category: "Other",
                    Quantity: null,
                    Unit: null,
                    AltQuantity: 2m,
                    AltUnit: "cup",
                    RawText: "2 cups cold water"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "egg",
                    DisplayName: "eggs",
                    Category: "DairyAndEggs",
                    Quantity: 6m,
                    Unit: null,
                    AltQuantity: 10.5m,
                    AltUnit: "oz",
                    RawText: "6 large eggs (10.5 oz)"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "lemon",
                    DisplayName: "lemon juice",
                    Category: "CondimentsAndSauces",
                    Quantity: 2m,
                    Unit: "tsp",
                    RawText: "2 teaspoons lemon juice"
                ),
                new(
                    "ing_4",
                    CanonicalFoodItem: "spinach",
                    DisplayName: "frozen chopped spinach",
                    Category: "Frozen",
                    Quantity: 10m,
                    Unit: "oz",
                    RawText: "10 oz frozen chopped spinach"
                ),
                new(
                    "ing_5",
                    CanonicalFoodItem: "2 garlic cloves",
                    DisplayName: "garlic",
                    Category: "Produce",
                    Quantity: 2m,
                    Unit: "clove",
                    RawText: "2 garlic cloves"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Use {{ing_1_a}}, {{ing_2_a}}, {{ing_3_a}}, {{ing_4_a}}, and {{ing_5_a}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", 2m, "cup"),
                        new("ing_2_a", "ing_2", 6m, "whole"),
                        new("ing_3_a", "ing_3", 2m, "tsp"),
                        new("ing_4_a", "ing_4", 10m, "oz"),
                        new("ing_5_a", "ing_5", 2m, "clove"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        );

        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, [], []);
        var draft = RecipeDraftCompiler.Compile(extracted, validation, matches);

        Assert.IsNull(draft.PrepTimeMinutes);
        Assert.IsNull(draft.CookTimeMinutes);
        Assert.IsNull(draft.TotalTimeMinutes);

        var promotedWater = draft.Ingredients![0];
        Assert.AreEqual(2m, promotedWater.Quantity);
        Assert.AreEqual(MeasurementUnit.Cup, promotedWater.Unit);
        Assert.IsNull(promotedWater.AlternativeMeasurement);

        var inferredWholeEggs = draft.Ingredients[1];
        Assert.AreEqual(6m, inferredWholeEggs.Quantity);
        Assert.AreEqual(MeasurementUnit.Whole, inferredWholeEggs.Unit);
        Assert.IsNotNull(inferredWholeEggs.AlternativeMeasurement);

        var lemon = draft.Ingredients[2];
        Assert.AreEqual(FoodCategory.Produce, lemon.SuggestedCategory);

        var frozenSpinach = draft.Ingredients[3];
        Assert.AreEqual(FoodCategory.Frozen, frozenSpinach.SuggestedCategory);

        Assert.AreEqual("garlic", FoodNameSanitizer.NormalizeCanonicalFoodName("garlic cloves"));
    }

    [TestMethod]
    public void Compile_CollapsedRangeTimers_StrippedQualifierPrefix_AndCompoundPlusMeasurement()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Zaalouk & Olive Oil",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "olive oil",
                    DisplayName: "extra-virgin olive oil",
                    Quantity: 6m,
                    Unit: "tbsp",
                    AltQuantity: 1m,
                    AltUnit: "tsp",
                    RawText: "6 tablespoons plus 1 teaspoon extra-virgin olive oil, divided"
                ),
                new(
                    RefId: "ing_2",
                    CanonicalFoodItem: "parsley",
                    DisplayName: "fresh parsley",
                    Quantity: 0.25m,
                    Unit: "cup",
                    RawText: "1/4 cup chopped fresh parsley, divided"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Heat {{ing_1_a}} and simmer {{timer_1}} to {{timer_2}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 3m, "tbsp")],
                    TimerTokens: [new("timer_1", 1200, "Simmer"), new("timer_2", 1500, "Simmer")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Top with remaining {{ing_2_b}}.",
                    IngredientTokens:
                    [
                        new("ing_2_b", "ing_2", FallbackDisplay: "remaining fresh parsley"),
                    ]
                ),
            ],
            QualityAudit: new RecipeQualityAuditDto(IsPlausibleRecipe: true, Warnings: [])
        );

        var normalized = RecipeValidator.NormalizeCompoundIngredients(extracted);
        var validation = RecipeValidator.Validate(normalized);
        var matches = EntityCandidateMatcher.Match(normalized, [], []);
        var draft = RecipeDraftCompiler.Compile(normalized, validation, matches);

        Assert.AreEqual(6.3333m, draft.Ingredients![0].Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, draft.Ingredients![0].Unit);
        Assert.IsNull(draft.Ingredients![0].AlternativeMeasurement);

        var step1 = draft.Steps![0];
        Assert.IsNotNull(step1.Timers);
        Assert.HasCount(1, step1.Timers);
        Assert.AreEqual(1200, step1.Timers[0].DurationSeconds);

        var step2 = draft.Steps![1];
        Assert.AreEqual(
            "Top with remaining fresh parsley.",
            StepInstructionParser.ToPlainText(step2.Instruction)
        );
    }

    [TestMethod]
    public void Compile_ResolvesAndStripsPlaceholdersInDescription_AndBackfillsTimesFromActiveCookTimers()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Gougères",
            Description: "Reheat at {{temp_1}} for {{timer_1}} before serving; cool {{unknown_tok}}.",
            PrepTimeMinutes: 15,
            CookTimeMinutes: null,
            TotalTimeMinutes: null,
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "flour",
                    DisplayName: "flour",
                    Quantity: 1m,
                    Unit: "cup",
                    RawText: "1 cup flour"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Chill {{ing_1_a}} for {{timer_2}} then bake at {{temp_1}} for {{timer_1}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", StepQuantity: 1m, StepUnit: "cup")],
                    TimerTokens:
                    [
                        new("timer_1", DurationSeconds: 1200, Label: "Bake gougères"),
                        new("timer_2", DurationSeconds: 7200, Label: "Chill dough"),
                    ],
                    TemperatureTokens: [new("temp_1", Value: 425m, BaseUnit: "F")]
                ),
            ]
        );

        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, [], []);
        var draft = RecipeDraftCompiler.Compile(extracted, validation, matches);

        Assert.IsNotNull(draft.Description);
        Assert.DoesNotContain("{{", draft.Description, StringComparison.Ordinal);
        StringAssert.Contains(draft.Description, "425°F");
        StringAssert.Contains(draft.Description, "20 minutes");
        Assert.AreEqual(20, draft.CookTimeMinutes);
        Assert.AreEqual(35, draft.TotalTimeMinutes);
    }

    [TestMethod]
    public void Compile_NonEntreeCourseTag_RemovesConflictingMainCourseAndDinnerTags()
    {
        var appetizerTagId = Guid.NewGuid();
        var mainCourseTagId = Guid.NewGuid();
        var glutenFreeTagId = Guid.NewGuid();

        var tags = new List<CatalogTagLookupItem>
        {
            new(appetizerTagId, "Appetizer", "appetizer", "appetizer"),
            new(mainCourseTagId, "Main Course", "main course", "main-course"),
            new(glutenFreeTagId, "Gluten-Free", "gluten-free", "gluten-free"),
        };

        var extracted = new ExtractedRecipeDto(
            Title: "Bacon and Chive Deviled Eggs",
            PrepTimeMinutes: 20,
            CookTimeMinutes: 10,
            TotalTimeMinutes: 30,
            ProposedTags: ["Appetizer", "Main Course", "Dinner", "Gluten-Free"],
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "egg",
                    DisplayName: "eggs",
                    Quantity: 6m,
                    Unit: "whole"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Prep {{eggs}}.",
                    IngredientTokens: [new("eggs", "ing_1", 6m, "whole", "eggs")]
                ),
            ]
        );

        var validation = RecipeValidator.Validate(extracted);
        var matches = EntityCandidateMatcher.Match(extracted, [], tags);
        var draft = RecipeDraftCompiler.Compile(extracted, validation, matches);

        Assert.IsNotNull(draft.SuggestedTagIds);
        Assert.Contains(appetizerTagId, draft.SuggestedTagIds);
        Assert.Contains(glutenFreeTagId, draft.SuggestedTagIds);
        Assert.DoesNotContain(mainCourseTagId, draft.SuggestedTagIds);
        Assert.IsFalse(
            draft.ProposedTagNames?.Contains("Dinner", StringComparer.OrdinalIgnoreCase) ?? false
        );
    }

    [TestMethod]
    public void Compile_NoCookRecipeWithPrepTimeAndLowWaterTemp_SetsCookTimeToZero()
    {
        var doughRecipe = new ExtractedRecipeDto(
            Title: "Classic Pizza Dough",
            PrepTimeMinutes: 25,
            CookTimeMinutes: null,
            TotalTimeMinutes: null,
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "water",
                    DisplayName: "warm water",
                    Quantity: 1.5m,
                    Unit: "cup"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Combine flour with {{warm_water}} at {{water_temp}} and let rise for {{rise_time}}.",
                    IngredientTokens: [new("warm_water", "ing_1", 1.5m, "cup", "warm water")],
                    TimerTokens: [new("rise_time", 3600, "rise")],
                    TemperatureTokens: [new("water_temp", 110m, "F")]
                ),
            ]
        );

        var draft = RecipeDraftCompiler.Compile(
            doughRecipe,
            RecipeValidator.Validate(doughRecipe),
            EntityCandidateMatcher.Match(doughRecipe, [], [])
        );

        Assert.AreEqual(25, draft.PrepTimeMinutes);
        Assert.AreEqual(0, draft.CookTimeMinutes);
        Assert.AreEqual(25, draft.TotalTimeMinutes);

        var bakedWithMissingCookTime = new ExtractedRecipeDto(
            Title: "Roast Chicken",
            PrepTimeMinutes: 15,
            CookTimeMinutes: null,
            TotalTimeMinutes: null,
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "chicken",
                    DisplayName: "chicken",
                    Quantity: 4m,
                    Unit: "pound"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Roast {{chicken}} at {{oven_temp}} until cooked.",
                    IngredientTokens: [new("chicken", "ing_1", 4m, "pound", "chicken")],
                    TemperatureTokens: [new("oven_temp", 425m, "F")]
                ),
            ]
        );

        var ovenDraft = RecipeDraftCompiler.Compile(
            bakedWithMissingCookTime,
            RecipeValidator.Validate(bakedWithMissingCookTime),
            EntityCandidateMatcher.Match(bakedWithMissingCookTime, [], [])
        );

        Assert.AreEqual(15, ovenDraft.PrepTimeMinutes);
        Assert.IsNull(ovenDraft.CookTimeMinutes);
        Assert.AreEqual(15, ovenDraft.TotalTimeMinutes);
    }

    [TestMethod]
    public void Compile_StripsDisallowedCustomLengthAndTemperatureAltUnits()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Alt Unit Cleanup",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "water",
                    DisplayName: "warm water",
                    Quantity: 1.5m,
                    Unit: "cup",
                    AltQuantity: 110m,
                    AltUnit: "degrees"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "tortilla",
                    DisplayName: "tortillas",
                    Quantity: 4m,
                    Unit: "whole",
                    AltQuantity: 8m,
                    AltUnit: "inch"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "garlic",
                    DisplayName: "garlic",
                    Quantity: 1m,
                    Unit: "clove",
                    AltQuantity: 1m,
                    AltUnit: "custom"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Combine {{w}}, {{t}}, {{g}}.",
                    IngredientTokens:
                    [
                        new("w", "ing_1", 1.5m, "cup", "warm water"),
                        new("t", "ing_2", 4m, "whole", "tortillas"),
                        new("g", "ing_3", 1m, "clove", "garlic"),
                    ]
                ),
            ]
        );

        var draft = RecipeDraftCompiler.Compile(
            extracted,
            RecipeValidator.Validate(extracted),
            EntityCandidateMatcher.Match(extracted, [], [])
        );

        Assert.IsNotNull(draft.Ingredients);
        Assert.IsNull(draft.Ingredients[0].AlternativeMeasurement);
        Assert.IsNull(draft.Ingredients[1].AlternativeMeasurement);
        Assert.IsNull(draft.Ingredients[2].AlternativeMeasurement);
    }

    [TestMethod]
    public void Compile_SingleOccurrenceUnquantifiedStepToken_BackfillsMasterAmount_UnlessDividedOrPartial()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Zaalouk Step Backfill",
            Ingredients:
            [
                new(
                    "ing_oil",
                    CanonicalFoodItem: "olive oil",
                    DisplayName: "olive oil",
                    Quantity: 2m,
                    Unit: "tablespoon",
                    RawText: "2 tablespoons extra-virgin olive oil"
                ),
                new(
                    "ing_cilantro",
                    CanonicalFoodItem: "cilantro",
                    DisplayName: "fresh cilantro",
                    Quantity: 1m,
                    Unit: "bunch",
                    RawText: "1 bunch fresh cilantro, chopped"
                ),
                new(
                    "ing_salt",
                    CanonicalFoodItem: "kosher salt",
                    DisplayName: "kosher salt",
                    Quantity: 1m,
                    Unit: "teaspoon",
                    RawText: "1 teaspoon kosher salt"
                ),
                new(
                    "ing_pepper",
                    CanonicalFoodItem: "black pepper",
                    DisplayName: "black pepper",
                    Quantity: 0.5m,
                    Unit: "teaspoon",
                    RawText: "1/2 teaspoon black pepper, divided"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Heat {{oil}} with {{salt}} and {{pepper}}.",
                    IngredientTokens:
                    [
                        new("oil", "ing_oil", null, null, "olive oil"),
                        new("salt", "ing_salt", null, null, "salt"),
                        new("pepper", "ing_pepper", null, null, "black pepper"),
                    ]
                ),
                new(
                    2,
                    TemplateText: "Season with extra {{salt}} and garnish with a handful of {{cilantro}}.",
                    IngredientTokens:
                    [
                        new("salt", "ing_salt", null, null, "salt"),
                        new("cilantro", "ing_cilantro", null, null, "chopped cilantro"),
                    ]
                ),
            ]
        );

        var draft = RecipeDraftCompiler.Compile(
            extracted,
            RecipeValidator.Validate(extracted),
            EntityCandidateMatcher.Match(extracted, [], [])
        );

        var step1Amounts = draft.Steps![0].IngredientAmounts;
        Assert.IsNotNull(step1Amounts);
        Assert.HasCount(1, step1Amounts);
        Assert.AreEqual(0, step1Amounts[0].IngredientIndex);
        Assert.AreEqual(2m, step1Amounts[0].Measurement.Quantity);
        Assert.AreEqual(MeasurementUnit.Tbsp, step1Amounts[0].Measurement.Unit);
        StringAssert.Contains(draft.Steps[0].Instruction, "\"amount\":2,\"unit\":\"tbsp\"");

        Assert.IsNull(draft.Steps[1].IngredientAmounts);
    }

    [TestMethod]
    public void Compile_LiteralCustomUnitAndContradictorySameDimensionAlt_AreStripped()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Literal Custom & Contradictory Alt",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "Brie cheese",
                    DisplayName: "Brie cheese",
                    Quantity: 1m,
                    Unit: "custom",
                    AltQuantity: 8m,
                    AltUnit: "ounce",
                    RawText: "1 (8-ounce) wheel Brie cheese"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "warm water",
                    DisplayName: "warm water",
                    Quantity: 0.75m,
                    Unit: "cup",
                    AltQuantity: 11m,
                    AltUnit: "fl oz",
                    RawText: "3/4 cup warm water (110 degrees)"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Use {{brie}} and {{water}}.",
                    IngredientTokens:
                    [
                        new("brie", "ing_1", 1m, "custom", "Brie cheese"),
                        new("water", "ing_2", 0.75m, "cup", "warm water"),
                    ]
                ),
            ]
        );

        var draft = RecipeDraftCompiler.Compile(
            extracted,
            RecipeValidator.Validate(extracted),
            EntityCandidateMatcher.Match(extracted, [], [])
        );

        Assert.IsNotNull(draft.Ingredients);
        Assert.AreEqual(MeasurementUnit.Whole, draft.Ingredients[0].Unit);
        Assert.IsNull(draft.Ingredients[0].CustomUnitName);
        Assert.AreEqual(8m, draft.Ingredients[0].AlternativeMeasurement?.Quantity);
        Assert.DoesNotContain(
            "custom",
            draft.Steps![0].Instruction,
            StringComparison.OrdinalIgnoreCase
        );

        Assert.IsNull(draft.Ingredients[1].AlternativeMeasurement);
    }

    [TestMethod]
    public void Compile_AppliesRound4YieldFallbackPrepNotesPepperSpiceAndTagGuardrails()
    {
        var deepFryId = Guid.NewGuid();
        var panSearId = Guid.NewGuid();
        var stirFryId = Guid.NewGuid();
        var onePanId = Guid.NewGuid();
        var veganId = Guid.NewGuid();
        var vegId = Guid.NewGuid();
        var dairyFreeId = Guid.NewGuid();
        var sideDishId = Guid.NewGuid();
        var dinnerId = Guid.NewGuid();
        var noCookId = Guid.NewGuid();

        var tags = new List<CatalogTagLookupItem>
        {
            new(deepFryId, "Deep-Fry", "deep-fry", "deep-fry"),
            new(panSearId, "Pan-Sear", "pan-sear", "pan-sear"),
            new(stirFryId, "Stir-Fry", "stir-fry", "stir-fry"),
            new(onePanId, "One-Pan", "one-pan", "one-pan"),
            new(veganId, "Vegan", "vegan", "vegan"),
            new(vegId, "Vegetarian", "vegetarian", "vegetarian"),
            new(dairyFreeId, "Dairy-Free", "dairy-free", "dairy-free"),
            new(sideDishId, "Side Dish", "side dish", "side-dish"),
            new(dinnerId, "Dinner", "dinner", "dinner"),
            new(noCookId, "No-Cook", "no-cook", "no-cook"),
        };

        const string sourceText =
            "Crisp Walnuts\nYield: SERVES 4 SEASON 26 (Base Servings: 4.0)\nIngredients:\n1/4 tsp pepper\n1 cup butter\nInstructions:\n1. Mix.";

        var extracted = new ExtractedRecipeDto(
            Title: "Spiced Walnut Topping",
            YieldDisplay: null,
            CookTimeMinutes: 0,
            ProposedTags:
            [
                "Deep-Fry",
                "Pan-Sear",
                "Stir-Fry",
                "One-Pan",
                "Vegan",
                "Dairy-Free",
                "Vegetarian",
                "Side Dish",
                "Dinner",
                "No-Cook",
            ],
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "pepper",
                    DisplayName: "pepper",
                    Quantity: 0.25m,
                    Unit: "tsp",
                    RawText: "1/4 tsp pepper"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "unsalted butter",
                    DisplayName: "unsalted butter",
                    Quantity: 2m,
                    Unit: "tbsp",
                    PreparationNotes: "warm, at {{temp_1}}",
                    RawText: "2 tbsp unsalted butter"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Toast walnuts in a skillet until fragrant, then transfer to a baking sheet at {{temp_1}} with {{ing_1_a}} and {{ing_2_a}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", 0.25m, "tsp"),
                        new("ing_2_a", "ing_2", 2m, "tbsp"),
                    ],
                    TemperatureTokens: [new("temp_1", 350m, "F")]
                ),
            ]
        );

        var matches = EntityCandidateMatcher.Match(extracted, [], tags);
        var draft = RecipeDraftCompiler.Compile(
            extracted,
            RecipeValidator.Validate(extracted),
            matches,
            sourceText: sourceText
        );

        Assert.AreEqual("Serves 4", draft.YieldDisplay);
        Assert.AreEqual("black pepper", draft.Ingredients![0].ProposedFoodItemName);
        Assert.AreEqual("warm, at 350°F", draft.Ingredients![1].PreparationNote);
        Assert.Contains(deepFryId, draft.SuggestedTagIds!);
        Assert.Contains(sideDishId, draft.SuggestedTagIds!);
        Assert.Contains(vegId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(panSearId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(stirFryId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(onePanId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(veganId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(dairyFreeId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(dinnerId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(noCookId, draft.SuggestedTagIds!);
    }

    [TestMethod]
    public void Compile_StripsUngroundedMeatTagsTaxonomyHeadersAndHyphenDegreeSuffix()
    {
        var frenchId = Guid.NewGuid();
        var beefId = Guid.NewGuid();
        var porkId = Guid.NewGuid();
        var baconId = Guid.NewGuid();
        var appetizerId = Guid.NewGuid();
        IReadOnlyList<CatalogTagLookupItem> tags =
        [
            new(frenchId, "French", "french", "french"),
            new(beefId, "Beef", "beef", "beef"),
            new(porkId, "Pork", "pork", "pork"),
            new(baconId, "Bacon", "bacon", "bacon"),
            new(appetizerId, "Appetizer", "appetizer", "appetizer"),
        ];

        var extracted = new ExtractedRecipeDto(
            Title: "French Onion and Bacon Tart",
            Description: "Rich tart.",
            Servings: 6m,
            YieldDisplay: "Serves 6",
            CookTimeMinutes: 45,
            ProposedTags: ["French", "Beef", "Appetizer", "Bacon", "Protein & Base"],
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "bacon",
                    DisplayName: "bacon",
                    Category: "MeatAndSeafood",
                    Quantity: 4m,
                    Unit: "oz",
                    RawText: "4 ounces bacon"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "onion",
                    DisplayName: "onions",
                    Category: "Produce",
                    Quantity: 1.5m,
                    Unit: "lb",
                    RawText: "1 1/2 pounds onions"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Cook {{ing_1_a}} and {{ing_2_a}}, then bake in a {{temp_1}}-degree oven.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", 4m, "oz"),
                        new("ing_2_a", "ing_2", 1.5m, "lb"),
                    ],
                    TemperatureTokens: [new("temp_1", 375m, "F")]
                ),
            ]
        );

        var matches = EntityCandidateMatcher.Match(extracted, [], tags);
        var draft = RecipeDraftCompiler.Compile(
            extracted,
            RecipeValidator.Validate(extracted),
            matches
        );

        Assert.Contains(frenchId, draft.SuggestedTagIds!);
        Assert.Contains(appetizerId, draft.SuggestedTagIds!);
        Assert.Contains(baconId, draft.SuggestedTagIds!);
        Assert.DoesNotContain(beefId, draft.SuggestedTagIds!);
        Assert.DoesNotContain("Protein & Base", draft.ProposedTagNames!);
        Assert.AreEqual(
            "Cook ||{\"ingredient\":\"bacon\",\"amount\":4,\"unit\":\"oz\",\"type\":\"Ingredient\"}|| and ||{\"ingredient\":\"onions\",\"amount\":1.5,\"unit\":\"lb\",\"type\":\"Ingredient\"}||, then bake in a ||{\"fahrenheit\":375,\"type\":\"Temperature\"}|| oven.",
            draft.Steps![0].Instruction
        );
    }
}
