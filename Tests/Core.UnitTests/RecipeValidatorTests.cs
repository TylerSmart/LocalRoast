using App.Core.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Core.UnitTests;

[TestClass]
public sealed class RecipeValidatorTests
{
    private static ExtractedRecipeDto ValidSampleRecipe() =>
        new(
            Title: "Garlic Pasta",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "spaghetti",
                    DisplayName: "spaghetti",
                    Quantity: 1m,
                    Unit: "lb",
                    RawText: "1 lb spaghetti"
                ),
                new(
                    RefId: "ing_2",
                    CanonicalFoodItem: "olive oil",
                    DisplayName: "olive oil",
                    Quantity: 2m,
                    Unit: "tbsp",
                    RawText: "2 tbsp olive oil"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Boil {{ing_1_a}} for {{timer_1}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1")],
                    TimerTokens: [new("timer_1", 600, "Boil pasta")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Toss with {{ing_2_a}} at {{temp_1}}.",
                    IngredientTokens: [new("ing_2_a", "ing_2")],
                    TemperatureTokens: [new("temp_1", 350m, "F")]
                ),
            ]
        );

    [TestMethod]
    public void Validate_ValidRecipeWithMatchingTokens_HasNoErrorsOrWarnings()
    {
        var result = RecipeValidator.Validate(ValidSampleRecipe());

        Assert.IsTrue(result.IsValid);
        Assert.IsEmpty(result.Errors);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public void Validate_MissingTitleIngredientsAndSteps_ReportsCompletenessErrors()
    {
        var recipe = new ExtractedRecipeDto(Title: "  ", Ingredients: [], Steps: []);

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("Title", StringComparison.Ordinal)));
        Assert.IsTrue(
            result.Errors.Any(e =>
                e.Contains("Ingredients list is empty", StringComparison.Ordinal)
            )
        );
        Assert.IsTrue(
            result.Errors.Any(e => e.Contains("Steps list is empty", StringComparison.Ordinal))
        );
    }

    [TestMethod]
    public void Validate_PlaceholderMissingFromTokenArrays_ReturnsError()
    {
        var recipe = ValidSampleRecipe() with
        {
            Steps =
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Boil {{ing_1_a}} and {{ing_2_a}} for {{timer_1}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1")],
                    TimerTokens: [new("timer_1", 600, "Boil pasta")]
                ),
            ],
        };

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(
            result.Errors.Any(e =>
                e.Contains("{{ing_2_a}}", StringComparison.Ordinal)
                && e.Contains("missing from Step 1 token arrays", StringComparison.Ordinal)
            )
        );
    }

    [TestMethod]
    public void Validate_TokenArrayEntryMissingFromTemplateText_ReturnsError()
    {
        var recipe = ValidSampleRecipe() with
        {
            Steps =
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Boil {{ing_1_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1"), new("ing_2_a", "ing_2")],
                    TimerTokens: [new("timer_1", 600, "Boil pasta")]
                ),
            ],
        };

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("{{timer_1}}", StringComparison.Ordinal)));
        Assert.IsTrue(result.Errors.Any(e => e.Contains("{{ing_2_a}}", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Validate_UnknownIngredientRefIdAndLegacyInlineSyntax_ReturnsErrors()
    {
        var recipe = ValidSampleRecipe() with
        {
            Steps =
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Add ||{\"ingredient\":\"oil\"}|| and {{ing_missing}}.",
                    IngredientTokens: [new("ing_missing", "ing_999")]
                ),
            ],
        };

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(
            result.Errors.Any(e => e.Contains("not ||{...}|| syntax", StringComparison.Ordinal))
        );
        Assert.IsTrue(
            result.Errors.Any(e =>
                e.Contains("unknown IngredientRefId 'ing_999'", StringComparison.Ordinal)
            )
        );
    }

    [TestMethod]
    public void Validate_DividedQuantitySumExceedingMaster_ReturnsError()
    {
        var recipe = new ExtractedRecipeDto(
            Title: "Divided Sugar",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "sugar",
                    DisplayName: "sugar",
                    Quantity: 1m,
                    Unit: "cup",
                    RawText: "1 cup sugar, divided"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Mix {{ing_1_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 0.75m, "cup")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Top with {{ing_1_b}}.",
                    IngredientTokens: [new("ing_1_b", "ing_1", 0.75m, "cup")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(
            result.Errors.Any(e => e.Contains("Quantity sum mismatch", StringComparison.Ordinal))
        );
    }

    [TestMethod]
    public void Validate_DividedQuantityWithConvertibleUnitsWithinMaster_Passes()
    {
        var recipe = new ExtractedRecipeDto(
            Title: "Divided Butter",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "butter",
                    DisplayName: "butter",
                    Quantity: 1m,
                    Unit: "cup",
                    RawText: "1 cup butter, divided"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Melt {{ing_1_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 8m, "tbsp")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Finish with {{ing_1_b}}.",
                    IngredientTokens: [new("ing_1_b", "ing_1", 8m, "tbsp")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(recipe);

        Assert.IsTrue(result.IsValid);
        Assert.IsEmpty(result.Errors);
    }

    [TestMethod]
    public void Validate_UnreferencedRequiredVsBenignIngredients_EmitsAppropriateWarnings()
    {
        var recipe = new ExtractedRecipeDto(
            Title: "Herb Salad",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "lettuce",
                    DisplayName: "lettuce",
                    Quantity: 4m,
                    Unit: "cup"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "parsley",
                    DisplayName: "fresh parsley",
                    Quantity: 2m,
                    Unit: "tbsp",
                    RawText: "2 tbsp fresh parsley, for garnish"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "kosher salt",
                    DisplayName: "kosher salt",
                    Quantity: null,
                    RawText: "Kosher salt, to taste"
                ),
                new(
                    "ing_4",
                    CanonicalFoodItem: "walnuts",
                    DisplayName: "toasted walnuts",
                    Quantity: 0.5m,
                    Unit: "cup",
                    IsOptional: true,
                    RawText: "1/2 cup walnuts (optional)"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Plate cleanly without tokens.",
                    IngredientTokens: []
                ),
            ]
        );

        var result = RecipeValidator.Validate(recipe);

        Assert.IsTrue(result.IsValid);
        Assert.HasCount(1, result.Warnings);
        Assert.IsTrue(result.Warnings[0].Contains("lettuce", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Validate_InvalidTimerAndTemperatureTokens_ReportErrors()
    {
        var recipe = ValidSampleRecipe() with
        {
            Steps =
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Heat {{ing_1_a}} for {{timer_1}} at {{temp_1}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1")],
                    TimerTokens: [new("timer_1", 0, "")],
                    TemperatureTokens: [new("temp_1", 200m, "K")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Toss with {{ing_2_a}}.",
                    IngredientTokens: [new("ing_2_a", "ing_2")]
                ),
            ],
        };

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(
            result.Errors.Any(e => e.Contains("positive DurationSeconds", StringComparison.Ordinal))
        );
        Assert.IsTrue(
            result.Errors.Any(e => e.Contains("descriptive Label", StringComparison.Ordinal))
        );
        Assert.IsTrue(
            result.Errors.Any(e =>
                e.Contains("BaseUnit must be 'F' or 'C'", StringComparison.Ordinal)
            )
        );
    }

    [TestMethod]
    public void Validate_NonPositiveStepQtyAndQuantityMismatch_IncludeRetryGuidance()
    {
        var recipe = new ExtractedRecipeDto(
            Title: "Spiced Beef",
            Ingredients:
            [
                new(
                    "ing_1",
                    "bacon",
                    "bacon",
                    Quantity: 2m,
                    Unit: "slice",
                    RawText: "2 slices bacon"
                ),
                new(
                    "ing_2",
                    "ground beef",
                    "ground beef",
                    Quantity: 8m,
                    Unit: "oz",
                    RawText: "8 oz ground beef"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Cook {{ing_1_a}} and {{ing_2_a}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", StepQuantity: 0m, StepUnit: "slice"),
                        new("ing_2_a", "ing_2", StepQuantity: 8m, StepUnit: "oz"),
                    ]
                ),
                new(
                    2,
                    TemplateText: "Brown {{ing_2_b}}.",
                    IngredientTokens: [new("ing_2_b", "ing_2", StepQuantity: 8m, StepUnit: "oz")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.Contains(
            e =>
                e.Contains("Use null (not 0)", StringComparison.Ordinal)
                && e.Contains("ing_1_a", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e =>
                e.Contains("Quantity sum mismatch", StringComparison.Ordinal)
                && e.Contains("set stepQuantity and stepUnit to null", StringComparison.Ordinal),
            result.Errors
        );
    }

    [TestMethod]
    public void Validate_AltQuantityValidationAndRecipeReferenceWarningExemption_WorkAsExpected()
    {
        var validWithAlt = new ExtractedRecipeDto(
            Title: "Deviled Eggs",
            Ingredients:
            [
                new(
                    "ing_1",
                    "Easy-Peel Hard-Cooked Eggs",
                    "Easy-Peel Hard-Cooked Eggs",
                    Quantity: 1m,
                    Unit: "recipe",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "Easy-Peel Hard-Cooked Eggs",
                    RawText: "1 recipe Easy-Peel Hard-Cooked Eggs"
                ),
                new(
                    "ing_2",
                    "chickpea",
                    "chickpeas",
                    Quantity: 2m,
                    Unit: "can",
                    AltQuantity: 30m,
                    AltUnit: "oz",
                    RawText: "2 (15-oz) cans chickpeas"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Drain {{ing_2_a}} and halve eggs.",
                    IngredientTokens: [new("ing_2_a", "ing_2", StepQuantity: 30m, StepUnit: "oz")]
                ),
            ]
        );

        var passResult = RecipeValidator.Validate(validWithAlt);
        Assert.IsTrue(passResult.IsValid);
        Assert.IsEmpty(passResult.Warnings);

        var excessAlt = validWithAlt with
        {
            Steps =
            [
                new(
                    1,
                    TemplateText: "Drain {{ing_2_a}}.",
                    IngredientTokens: [new("ing_2_a", "ing_2", StepQuantity: 45m, StepUnit: "oz")]
                ),
            ],
        };

        var failResult = RecipeValidator.Validate(excessAlt);
        Assert.IsFalse(failResult.IsValid);
        Assert.Contains(
            e => e.Contains("exceeding alternate master quantity 30", StringComparison.Ordinal),
            failResult.Errors
        );
    }

    [TestMethod]
    public void Validate_OrphanedAltMeasurement_MicrowavePowerTemp_AndOmittedStepUnitSum_ReturnErrors()
    {
        var recipe = new ExtractedRecipeDto(
            Title: "Spinach & Bacon Dip",
            Ingredients:
            [
                new(
                    "ing_1",
                    "water",
                    "water",
                    Quantity: null,
                    Unit: null,
                    AltQuantity: 2m,
                    AltUnit: "cup",
                    RawText: "water (2 cups)"
                ),
                new(
                    "ing_2",
                    "bacon",
                    "bacon",
                    Quantity: 2m,
                    Unit: "slice",
                    RawText: "2 slices bacon"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Microwave for {{timer_1}} at {{temp_1}}.",
                    TimerTokens: [new("timer_1", 180, "Thaw spinach")],
                    TemperatureTokens: [new("temp_1", 40m, "F")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Cook {{ing_2_a}}, then transfer {{ing_2_b}} to plate.",
                    IngredientTokens:
                    [
                        new("ing_2_a", "ing_2", StepQuantity: 2m, StepUnit: "slice"),
                        new("ing_2_b", "ing_2", StepQuantity: 2m, StepUnit: null),
                    ]
                ),
            ]
        );

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.Contains(
            e =>
                e.Contains(
                    "altQuantity/altUnit without a measured primary",
                    StringComparison.Ordinal
                ),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("microwave/power instruction", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e =>
                e.Contains("Quantity sum mismatch for 'bacon'", StringComparison.Ordinal)
                && e.Contains("sum to 4", StringComparison.Ordinal),
            result.Errors
        );
    }

    [TestMethod]
    public void Validate_DuplicateTemplatePlaceholderWithinSameStep_ReturnsError()
    {
        var recipe = new ExtractedRecipeDto(
            Title: "Ultracreamy Hummus",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "garlic",
                    DisplayName: "garlic",
                    Quantity: 4m,
                    Unit: "clove",
                    RawText: "4 cloves garlic"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Process {{ing_1}} then scrape bowl and process {{ing_1}} again.",
                    IngredientTokens: [new("ing_1", "ing_1", StepQuantity: 4m, StepUnit: "clove")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(recipe);

        Assert.IsFalse(result.IsValid);
        Assert.Contains(
            e =>
                e.Contains("appears more than once in TemplateText", StringComparison.Ordinal)
                && e.Contains("{{ing_1}}", StringComparison.Ordinal),
            result.Errors
        );
    }

    [TestMethod]
    public void Validate_TimerMinutesAsSeconds_Outlier_AndPulses_AreRejectedWithSourceText()
    {
        const string source = """
            Gougères & Salsa
            Instructions:
            1. Blend tomatoes for 5 pulses until coarse.
            2. Simmer sauce for 15 seconds, then bake for 14 to 20 minutes.
            3. Cook tortilla for 22 to 28 minutes until golden.
            """;

        var invalid = new ExtractedRecipeDto(
            Title: "Gougères & Salsa",
            Ingredients:
            [
                new(
                    RefId: "ing_1",
                    CanonicalFoodItem: "tomato",
                    Quantity: 2m,
                    Unit: "whole",
                    RawText: "2 tomatoes"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Blend {{ing_1}} for {{timer_1}} pulses.",
                    IngredientTokens: [new("ing_1", "ing_1", 2m, "whole")],
                    TimerTokens: [new("timer_1", 10, "5 pulses")]
                ),
                new(
                    StepNumber: 2,
                    TemplateText: "Simmer {{timer_2}}, then bake {{timer_3}}.",
                    TimerTokens: [new("timer_2", 15, "Simmer"), new("timer_3", 14, "Bake")]
                ),
                new(
                    StepNumber: 3,
                    TemplateText: "Cook {{timer_4}} until golden.",
                    TimerTokens: [new("timer_4", 14400, "Cook tortilla")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(invalid, source);

        Assert.IsFalse(result.IsValid);
        Assert.Contains(
            e => e.Contains("food-processor pulses", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("precedes 'pulses'", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("durationSeconds=14", StringComparison.Ordinal) && e.Contains("840"),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("durationSeconds=14400", StringComparison.Ordinal),
            result.Errors
        );
        Assert.IsFalse(
            result.Errors.Any(e => e.Contains("timer_2", StringComparison.Ordinal)),
            "15s matches '15 seconds' in source and must remain valid."
        );
    }

    [TestMethod]
    public void NormalizeCompoundIngredients_AndValidate_HandleCompoundPlusMeasurement()
    {
        var splitCompound = new ExtractedRecipeDto(
            Title: "Spanish Tortilla",
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
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Cook in {{ing_1_a}}, brush with {{ing_1_b}}, sear with {{ing_1_c}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", StepQuantity: 3m, StepUnit: "tbsp"),
                        new("ing_1_b", "ing_1", StepQuantity: 1m, StepUnit: "tsp"),
                        new("ing_1_c", "ing_1", StepQuantity: 3m, StepUnit: "tbsp"),
                    ]
                ),
            ]
        );

        var unnormalizedValidation = RecipeValidator.Validate(splitCompound);
        Assert.IsFalse(unnormalizedValidation.IsValid);
        Assert.Contains(
            e => e.Contains("split compound 'plus' measurement", StringComparison.Ordinal),
            unnormalizedValidation.Errors
        );

        var normalized = RecipeValidator.NormalizeCompoundIngredients(splitCompound);
        Assert.AreEqual(6.3333m, normalized.Ingredients![0].Quantity);
        Assert.AreEqual("tbsp", normalized.Ingredients![0].Unit);
        Assert.IsNull(normalized.Ingredients![0].AltQuantity);
        Assert.IsNull(normalized.Ingredients![0].AltUnit);

        var normalizedValidation = RecipeValidator.Validate(normalized);
        Assert.IsTrue(normalizedValidation.IsValid, string.Join("; ", normalizedValidation.Errors));
    }

    [TestMethod]
    public void NormalizeCompoundIngredients_UpgradesParenCompoundAndStripsSpuriousMaxQuantity()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Congyoubing and Tortilla",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "all-purpose flour",
                    DisplayName: "all-purpose flour",
                    Quantity: 1.5m,
                    Unit: "cup",
                    AltQuantity: 7.5m,
                    AltUnit: "oz",
                    PreparationNotes: "plus 1 tablespoon",
                    RawText: "1 1/2 cups (7 1/2 ounces) plus 1 tablespoon all-purpose flour"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "cake flour",
                    DisplayName: "cake flour",
                    Quantity: 11.5m,
                    Unit: "oz",
                    AltQuantity: 2m,
                    AltUnit: "cup",
                    PreparationNotes: "plus 2 tablespoons, sifted",
                    RawText: "2 cups (11 1/2 ounces) plus 2 tablespoons cake flour"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "russet potato",
                    DisplayName: "russet potatoes",
                    Quantity: 1.5m,
                    MaxQuantity: 4m,
                    Unit: "lb",
                    RawText: "1 1/2 pounds Russet potatoes (3 to 4 medium)"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Mix {{ing_1_a}}, {{ing_2_a}}, and {{ing_3_a}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", StepQuantity: 1.5625m, StepUnit: "cup"),
                        new("ing_2_a", "ing_2", StepQuantity: 11.5m, StepUnit: "oz"),
                        new("ing_3_a", "ing_3", StepQuantity: 1.5m, StepUnit: "lb"),
                    ]
                ),
            ]
        );

        var normalized = RecipeValidator.NormalizeCompoundIngredients(extracted);

        Assert.AreEqual(1.5625m, normalized.Ingredients![0].Quantity);
        Assert.AreEqual("cup", normalized.Ingredients![0].Unit);
        Assert.IsNull(normalized.Ingredients![0].AltQuantity);
        Assert.IsNull(normalized.Ingredients![0].AltUnit);
        Assert.IsNull(normalized.Ingredients![0].PreparationNotes);

        Assert.AreEqual(11.5m, normalized.Ingredients![1].Quantity);
        Assert.AreEqual(2.125m, normalized.Ingredients![1].AltQuantity);
        Assert.AreEqual("cup", normalized.Ingredients![1].AltUnit);
        Assert.AreEqual("sifted", normalized.Ingredients![1].PreparationNotes);

        Assert.AreEqual(1.5m, normalized.Ingredients![2].Quantity);
        Assert.IsNull(normalized.Ingredients![2].MaxQuantity);

        var result = RecipeValidator.Validate(normalized);
        Assert.IsTrue(result.IsValid, string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void Validate_RejectsDescriptionPlaceholders_LeadingFractionMismatch_AndBadStepBindings()
    {
        var invalid = new ExtractedRecipeDto(
            Title: "Guacamole and Sesame Balls",
            Description: "Top after {{timer_1}} resting.",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "walnut",
                    DisplayName: "walnuts",
                    Quantity: 0.25m,
                    Unit: "cup",
                    RawText: "1/3 cup walnuts, toasted"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "avocado",
                    DisplayName: "avocados",
                    Quantity: 3m,
                    Unit: "whole",
                    RawText: "3 ripe avocados"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "sugar",
                    DisplayName: "sugar",
                    Quantity: 1m,
                    Unit: "cup",
                    RawText: "1 cup sugar"
                ),
                new(
                    "ing_4",
                    CanonicalFoodItem: "bacon",
                    DisplayName: "bacon",
                    Quantity: 2m,
                    Unit: "slice",
                    RawText: "2 slices bacon"
                ),
                new(
                    "ing_5",
                    CanonicalFoodItem: "olive oil",
                    DisplayName: "olive oil",
                    Quantity: 1m,
                    Unit: "tbsp",
                    RawText: "1 tablespoon olive oil"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Halve {{ing_2_a}}, boil {{ing_3_a}}, cook in {{ing_4_a}}, spray {{ing_5_a}}, slice {{ing_2_b}}, toss {{ing_1_a}}.",
                    IngredientTokens:
                    [
                        new("ing_2_a", "ing_2", StepQuantity: 0.3333m, StepUnit: "whole"),
                        new(
                            "ing_3_a",
                            "ing_3",
                            StepQuantity: 0.75m,
                            StepUnit: "cup",
                            FallbackDisplay: "3/4 cup water"
                        ),
                        new(
                            "ing_4_a",
                            "ing_4",
                            StepQuantity: 1m,
                            StepUnit: "tbsp",
                            FallbackDisplay: "bacon fat"
                        ),
                        new("ing_5_a", "ing_5", FallbackDisplay: "cooking spray"),
                        new("ing_2_b", "ing_2", StepQuantity: 0.5m, StepUnit: "inch"),
                        new("ing_1_a", "ing_1", StepQuantity: 0.25m, StepUnit: "cup"),
                    ]
                ),
            ]
        );

        var result = RecipeValidator.Validate(
            invalid,
            "Ingredients: 1/3 cup walnuts, 3 ripe avocados, 1 cup sugar, 2 slices bacon, 1 tbsp oil. Halve 1 avocado."
        );

        Assert.IsFalse(result.IsValid);
        Assert.Contains(
            e => e.Contains("Description must be plain text", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("leading measurement in rawText", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("suspicious whole-item fraction", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("binds water/ice to non-water", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("binds rendered fat/drippings", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("binds cooking spray to non-spray", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("physical length unit 'inch'", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("incompatible with master unit 'slice'", StringComparison.Ordinal),
            result.Errors
        );
    }

    [TestMethod]
    public void Validate_RejectsCombinedPrimaryAndAltOverConsumption_AndMismatchedTimers()
    {
        var invalid = new ExtractedRecipeDto(
            Title: "Hummus and Dough",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "chickpea",
                    DisplayName: "chickpeas",
                    Quantity: 2m,
                    Unit: "can",
                    AltQuantity: 30m,
                    AltUnit: "oz",
                    RawText: "2 (15-ounce) cans chickpeas"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "onion",
                    DisplayName: "onions",
                    Quantity: 2m,
                    Unit: "lb",
                    RawText: "2 pounds onions"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Drain {{ing_1_a}}, reserve {{ing_1_b}}, add {{ing_2_a}}, oil {{timer_1}}, mix {{timer_2}}.",
                    IngredientTokens:
                    [
                        new("ing_1_a", "ing_1", StepQuantity: 2m, StepUnit: "can"),
                        new("ing_1_b", "ing_1", StepQuantity: 30m, StepUnit: "oz"),
                        new("ing_2_a", "ing_2", StepQuantity: 2m, StepUnit: "tbsp"),
                    ],
                    TimerTokens:
                    [
                        new("timer_1", DurationSeconds: 1800, Label: "Measure oil depth"),
                        new("timer_2", DurationSeconds: 180, Label: "Knead dough"),
                    ]
                ),
            ]
        );

        var result = RecipeValidator.Validate(
            invalid,
            "2 (15-ounce) cans chickpeas, 2 pounds onions. Heat 1 1/2 inches oil, knead for 30 to 40 seconds."
        );

        Assert.IsFalse(result.IsValid);
        Assert.Contains(
            e => e.Contains("exceeding 100% of the ingredient", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e => e.Contains("incompatible with master unit 'lb'", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e =>
                e.Contains(
                    "physical measurement rather than a timed duration",
                    StringComparison.Ordinal
                ),
            result.Errors
        );
        Assert.Contains(
            e =>
                e.Contains(
                    "does not match any timed duration in the source",
                    StringComparison.Ordinal
                ),
            result.Errors
        );
    }

    [TestMethod]
    public void Validate_AcceptsWordAndCompoundSourceDurations()
    {
        var valid = new ExtractedRecipeDto(
            Title: "Slow Braise",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "beef",
                    Quantity: 2m,
                    Unit: "lb",
                    RawText: "2 lb beef"
                ),
            ],
            Steps:
            [
                new(
                    StepNumber: 1,
                    TemplateText: "Sear {{ing_1_a}} {{timer_1}}, rest {{timer_2}}, then braise {{timer_3}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", StepQuantity: 2m, StepUnit: "lb")],
                    TimerTokens:
                    [
                        new("timer_1", DurationSeconds: 90, Label: "Sear beef"),
                        new("timer_2", DurationSeconds: 1800, Label: "Rest meat"),
                        new("timer_3", DurationSeconds: 4500, Label: "Braise beef"),
                    ]
                ),
            ]
        );

        var result = RecipeValidator.Validate(
            valid,
            "Sear beef for 1 minute 30 seconds, let rest half an hour, and braise for 1 hour 15 minutes."
        );

        Assert.IsTrue(result.IsValid, string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void Validate_FalseSeeThisPageRawGroceryReference_ReturnsError_WhileRealRecipeReferenceStaysValid()
    {
        var falseRef = new ExtractedRecipeDto(
            Title: "Shrimp Cocktail",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "large shrimp",
                    DisplayName: "large shrimp",
                    Quantity: 1m,
                    Unit: "pound",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "large shrimp",
                    RawText: "1 pound large shrimp (26 to 30 per pound), peeled and deveined (see this page)"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Poach {{shrimp}}.",
                    IngredientTokens: [new("shrimp", "ing_1", 1m, "pound", "large shrimp")]
                ),
            ]
        );

        var falseResult = RecipeValidator.Validate(falseRef);
        Assert.IsFalse(falseResult.IsValid);
        Assert.IsTrue(
            falseResult.Errors.Any(e =>
                e.Contains("IsRecipeReference", StringComparison.OrdinalIgnoreCase)
            ),
            string.Join("; ", falseResult.Errors)
        );

        var trueRef = new ExtractedRecipeDto(
            Title: "Shrimp Cocktail Platter",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "Poached Shrimp",
                    DisplayName: "Poached Shrimp",
                    Quantity: 1m,
                    Unit: null,
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "Poached Shrimp",
                    RawText: "1 recipe Poached Shrimp (see this page)"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Serve {{shrimp}} cold.",
                    IngredientTokens: [new("shrimp", "ing_1", 1m, null, "Poached Shrimp")]
                ),
            ]
        );

        var trueResult = RecipeValidator.Validate(trueRef);
        Assert.IsTrue(trueResult.IsValid, string.Join("; ", trueResult.Errors));
    }

    [TestMethod]
    public void Validate_LeadingContainerPackSizeMisreadAsPrimaryUnit_ReturnsError()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Baked Brie en Croute",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "brie cheese",
                    DisplayName: "Brie cheese",
                    Quantity: 1m,
                    Unit: "ounce",
                    RawText: "1 (8-ounce) wheel Brie cheese"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Place {{brie}} on puff pastry.",
                    IngredientTokens: [new("brie", "ing_1", 1m, "ounce", "Brie cheese")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(extracted);
        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(
            result.Errors.Any(e => e.Contains("container", StringComparison.OrdinalIgnoreCase)),
            string.Join("; ", result.Errors)
        );
    }

    [TestMethod]
    public void Validate_InvalidCustomLengthOrTemperatureAltMeasurement_ReturnsErrors()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Alt Unit Validation Test",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "water",
                    DisplayName: "warm water",
                    Quantity: 1.5m,
                    Unit: "cup",
                    AltQuantity: 110m,
                    AltUnit: "degrees",
                    RawText: "1 1/2 cups warm water (110 degrees)"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "ginger",
                    DisplayName: "fresh ginger",
                    Quantity: 1m,
                    Unit: "whole",
                    AltQuantity: 1.5m,
                    AltUnit: "inch",
                    RawText: "1 (1 1/2-inch) piece fresh ginger"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "tortilla",
                    DisplayName: "flour tortilla",
                    Quantity: 4m,
                    Unit: "whole",
                    AltQuantity: 8m,
                    AltUnit: "custom",
                    RawText: "4 (8-inch) flour tortillas"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Combine {{water}}, {{ginger}}, and {{tortilla}}.",
                    IngredientTokens:
                    [
                        new("water", "ing_1", 1.5m, "cup", "warm water"),
                        new("ginger", "ing_2", 1m, "whole", "ginger"),
                        new("tortilla", "ing_3", 4m, "whole", "tortillas"),
                    ]
                ),
            ]
        );

        var result = RecipeValidator.Validate(extracted);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(3, result.Errors.Count, string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void NormalizeCompoundIngredients_OverestimatedPlusSumWithDuplicateAlt_NormalizesToExactTotal()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Classic Pizza Dough",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "bread flour",
                    DisplayName: "bread flour",
                    Quantity: 2.3333m,
                    Unit: "cup",
                    AltQuantity: 2.125m,
                    AltUnit: "cup",
                    RawText: "2 cups plus 2 tablespoons bread flour (11 1/2 ounces)"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "all-purpose flour",
                    DisplayName: "all-purpose flour",
                    Quantity: 2.3333m,
                    Unit: "cup",
                    AltQuantity: 11.5m,
                    AltUnit: "ounce",
                    RawText: "2 cups plus 2 tablespoons all-purpose flour (11 1/2 ounces)"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Mix {{bread_flour}} and {{ap_flour}}.",
                    IngredientTokens:
                    [
                        new("bread_flour", "ing_1", 2.125m, "cup", "bread flour"),
                        new("ap_flour", "ing_2", 2.125m, "cup", "all-purpose flour"),
                    ]
                ),
            ]
        );

        var normalized = RecipeValidator.NormalizeCompoundIngredients(extracted);
        Assert.AreEqual(2.125m, normalized.Ingredients![0].Quantity);
        Assert.AreEqual("cup", normalized.Ingredients![0].Unit);
        Assert.IsNull(normalized.Ingredients![0].AltQuantity);
        Assert.IsNull(normalized.Ingredients![0].AltUnit);

        Assert.AreEqual(2.125m, normalized.Ingredients![1].Quantity);
        Assert.AreEqual("cup", normalized.Ingredients![1].Unit);
        Assert.AreEqual(11.5m, normalized.Ingredients![1].AltQuantity);
        Assert.AreEqual("ounce", normalized.Ingredients![1].AltUnit);
    }

    [TestMethod]
    public void Validate_PulseTimerOnTitleDurationOnlyRecipe_ReturnsError()
    {
        var sourceText =
            "Title: One-Minute Salsa\nYield: About 2 cups\n\nInstructions:\n1. Pulse tomatoes, onion, and cilantro in food processor until coarsely chopped, 2/5 pulses.";

        var extracted = new ExtractedRecipeDto(
            Title: "One-Minute Salsa",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "tomato",
                    DisplayName: "tomatoes",
                    Quantity: 4m,
                    Unit: "whole"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Pulse {{tomatoes}} in food processor until coarsely chopped, {{pulse_time}}.",
                    IngredientTokens: [new("tomatoes", "ing_1", 4m, "whole", "tomatoes")],
                    TimerTokens: [new("pulse_time", 300, "pulse")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(extracted, sourceText);
        Assert.IsFalse(result.IsValid);
    }

    [TestMethod]
    public void Validate_TimerMismatchWithLocalSourceClause_ReturnsErrors()
    {
        var sourceText =
            "Instructions:\n1. Process flour and butter until no powdery bits remain, about 5 seconds.\n2. Let dough rise until nearly doubled in size, 1 to 1 1/2 hours.\n3. Bake tart for 30 minutes.";

        var extracted = new ExtractedRecipeDto(
            Title: "Clause Timer Check",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "flour",
                    DisplayName: "flour",
                    Quantity: 2m,
                    Unit: "cup"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Process {{flour}} until no powdery bits remain, {{process_time}}.",
                    IngredientTokens: [new("flour", "ing_1", 2m, "cup", "flour")],
                    TimerTokens: [new("process_time", 1800, "process")]
                ),
                new(
                    2,
                    TemplateText: "Cover tightly and let rise until doubled in size, {{rise_time}}.",
                    TimerTokens: [new("rise_time", 60, "let rise")]
                ),
                new(
                    3,
                    TemplateText: "Bake for {{bake_time}}.",
                    TimerTokens: [new("bake_time", 1800, "bake")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(extracted, sourceText);
        Assert.IsFalse(result.IsValid);
        Assert.HasCount(2, result.Errors, string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void Validate_LiteralCustomUnitOrContradictorySameDimensionAlt_ReturnsErrors()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Custom Unit & Alt Conversion Guard",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "Brie cheese",
                    DisplayName: "Brie cheese",
                    Quantity: 1m,
                    Unit: "custom",
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
                    TemplateText: "Place {{brie}} with {{water}}.",
                    IngredientTokens:
                    [
                        new("brie", "ing_1", 1m, "custom", "Brie cheese"),
                        new("water", "ing_2", 0.75m, "cup", "warm water"),
                    ]
                ),
            ]
        );

        var result = RecipeValidator.Validate(extracted);
        Assert.IsFalse(result.IsValid);
        Assert.HasCount(3, result.Errors, string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void Validate_FlagsRound4PlaceholderDistanceFacingPageOmittedStepAndBatchGlitches()
    {
        const string sourceText = """
            Albóndigas & Peppers
            Yield: SERVES 4
            Ingredients:
            - 1 pound peeled shrimp (facing page)
            - 1 cup plus 2 tablespoons all-purpose flour
            - 1 teaspoon yeast
            - 8 teaspoons sugar
            Instructions:
            1. Heat oven 4 inches below broiler for between 45 and 60 seconds, then fill 1 portion with sugar.
            2. Stir together flour and yeast.
            3. Shape mixture into 12 smooth meatballs and arrange on a parchment-lined baking sheet.
            4. Bake until golden.
            """;

        var extracted = new ExtractedRecipeDto(
            Title: "Albóndigas & Peppers",
            Servings: 8,
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "shrimp",
                    DisplayName: "peeled shrimp",
                    Quantity: 1m,
                    Unit: "lb",
                    IsRecipeReference: true,
                    ReferencedRecipeTitle: "shrimp",
                    RawText: "1 pound peeled shrimp (facing page)"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "all-purpose flour",
                    DisplayName: "all-purpose flour",
                    Quantity: 0.625m,
                    Unit: "cup",
                    RawText: "1 cup plus 2 tablespoons all-purpose flour"
                ),
                new(
                    "ing_3",
                    CanonicalFoodItem: "all-purpose flour",
                    DisplayName: "all-purpose flour",
                    Quantity: 0.625m,
                    Unit: "cup",
                    RawText: "1 cup plus 2 tablespoons all-purpose flour"
                ),
                new(
                    "ing_4",
                    CanonicalFoodItem: "yeast",
                    DisplayName: "yeast",
                    Quantity: 1m,
                    Unit: "tsp",
                    PreparationNotes: "warm, at {{temp_1}}",
                    RawText: "1 teaspoon yeast"
                ),
                new(
                    "ing_5",
                    CanonicalFoodItem: "sugar",
                    DisplayName: "sugar",
                    Quantity: 8m,
                    Unit: "tsp",
                    RawText: "8 teaspoons sugar"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Place tray {{temp_1}} inches below broiler for {{timer_1}}, then fill 1 portion with {{ing_5_a}}.",
                    IngredientTokens: [new("ing_5_a", "ing_5", 8m, "tsp", "sugar")],
                    TimerTokens: [new("timer_1", 45, "Broil peppers")],
                    TemperatureTokens: [new("temp_1", 4m, "F")]
                ),
                new(
                    2,
                    TemplateText: "Mix {{ing_2_a}}, {{ing_3_a}}, {{ing_4_a}}, and {{ing_1_a}}.",
                    IngredientTokens:
                    [
                        new("ing_2_a", "ing_2", 0.625m, "cup"),
                        new("ing_3_a", "ing_3", 0.625m, "cup"),
                        new("ing_4_a", "ing_4", 1m, "tsp"),
                        new("ing_1_a", "ing_1", 1m, "lb"),
                    ]
                ),
                new(3, TemplateText: "Bake until golden."),
            ]
        );

        var result = RecipeValidator.Validate(extracted, sourceText);
        Assert.IsFalse(result.IsValid);
        Assert.Contains(
            e => e.Contains("preparationNotes must be plain text", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(e => e.Contains("facing page", StringComparison.Ordinal), result.Errors);
        Assert.Contains(
            e => e.Contains("physical rack or length distance", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(e => e.Contains("1 portion", StringComparison.Ordinal), result.Errors);
        Assert.Contains(
            e => e.Contains("Source Instruction Step 3", StringComparison.Ordinal),
            result.Errors
        );
        Assert.Contains(
            e =>
                e.Contains(
                    "Multiple ingredient rows share compound rawText",
                    StringComparison.OrdinalIgnoreCase
                ),
            result.Errors
        );
        Assert.DoesNotContain(e => e.Contains("timer_1", StringComparison.Ordinal), result.Errors);
    }

    [TestMethod]
    public void RepairSafeStepTokenGlitches_CleansUnreferencedPhantomTokensAndTempPreposition()
    {
        var dto = new ExtractedRecipeDto(
            Title: "Buffalo Cauliflower",
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
                    1,
                    TemplateText: "Heat oven to at {{temp_1}}, mix {{ing_1_a}}.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 1m, "cup")],
                    TemperatureTokens: [new("temp_1", 400m, "F")]
                ),
                new(
                    2,
                    TemplateText: "Toss in the sauce.",
                    IngredientTokens: [new("ing_1_b", "ing_1", null, null, "flour")]
                ),
            ]
        );

        var repaired = RecipeValidator.RepairSafeStepTokenGlitches(dto);
        Assert.AreEqual(
            "Heat oven to {{temp_1}}, mix {{ing_1_a}}.",
            repaired.Steps![0].TemplateText
        );
        Assert.IsEmpty(repaired.Steps![1].IngredientTokens!);
    }

    [TestMethod]
    public void Validate_TemperatureNextToSkilletDimension_DoesNotFalsePositive()
    {
        var extracted = new ExtractedRecipeDto(
            Title: "Caramelized Onion, Pear, and Bacon Tart",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "bacon",
                    DisplayName: "bacon",
                    Quantity: 4m,
                    Unit: "slice",
                    RawText: "4 slices bacon"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Adjust oven rack to upper-middle position and heat oven to {{temp_1}}. Cook {{ing_1_a}} in 12-inch cast-iron skillet over medium heat until crispy.",
                    IngredientTokens: [new("ing_1_a", "ing_1", 4m, "slice")],
                    TemperatureTokens: [new("temp_1", 500m, "F")]
                ),
                new(
                    2,
                    TemplateText: "Heat oil in 12-inch skillet to {{temp_2}}.",
                    TemperatureTokens: [new("temp_2", 375m, "F")]
                ),
            ]
        );

        var result = RecipeValidator.Validate(extracted);
        Assert.IsTrue(result.IsValid, string.Join("; ", result.Errors));
    }

    [TestMethod]
    public void RepairSafeStepTokenGlitches_InlinesUnlistedNullFatStripsHyphenDegreeAndHealsBatchHalfDoubleCount()
    {
        var dto = new ExtractedRecipeDto(
            Title: "Crispy Cauliflower & Bacon",
            Ingredients:
            [
                new(
                    "ing_1",
                    CanonicalFoodItem: "cauliflower florets",
                    DisplayName: "cauliflower florets",
                    Quantity: 1m,
                    Unit: "lb",
                    RawText: "1 pound cauliflower florets"
                ),
                new(
                    "ing_2",
                    CanonicalFoodItem: "bacon",
                    DisplayName: "bacon",
                    Quantity: 2m,
                    Unit: "slice",
                    RawText: "2 slices bacon"
                ),
            ],
            Steps:
            [
                new(
                    1,
                    TemplateText: "Cook {{ing_2_a}} until crisp, reserve {{unlisted_fat}}. Heat oven to {{temp_1}}-degree oven and toss {{ing_1_a}}.",
                    IngredientTokens:
                    [
                        new("ing_2_a", "ing_2", 2m, "slice"),
                        new("unlisted_fat", "null", null, null, "bacon fat"),
                        new("ing_1_a", "ing_1", 1m, "lb"),
                    ],
                    TemperatureTokens: [new("temp_1", 400m, "F")]
                ),
                new(
                    2,
                    TemplateText: "Fry half of {{ing_1_b}}, then sprinkle {{ing_2_b}}.",
                    IngredientTokens:
                    [
                        new("ing_1_b", "ing_1", 0.5m, "lb"),
                        new("ing_2_b", "ing_2", null, null),
                    ]
                ),
            ]
        );

        var repaired = RecipeValidator.RepairSafeStepTokenGlitches(
            dto,
            repairBatchSplitDoubleCounts: true
        );

        Assert.AreEqual(
            "Cook {{ing_2_a}} until crisp, reserve bacon fat. Heat oven to {{temp_1}} oven and toss {{ing_1_a}}.",
            repaired.Steps![0].TemplateText
        );
        Assert.HasCount(2, repaired.Steps![0].IngredientTokens!);
        Assert.IsNull(repaired.Steps![1].IngredientTokens![0].StepQuantity);
        Assert.IsNull(repaired.Steps![1].IngredientTokens![0].StepUnit);

        var validated = RecipeValidator.Validate(repaired);
        Assert.IsTrue(validated.IsValid, string.Join("; ", validated.Errors));
    }
}
