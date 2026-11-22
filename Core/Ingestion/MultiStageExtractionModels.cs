using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public sealed record LlmStageChatMessage(string Role, string Content);

public sealed record LlmStructuredStageResult<T>(
    T? Parsed,
    string RawContent,
    string Model,
    long ElapsedMs,
    string? Error = null,
    string? DoneReason = null
);

// Stage 1 DTOs
public sealed record Stage1RecipeDto(
    string Title,
    string? Description,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Servings,
    string? Yield,
    IReadOnlyList<Stage1SectionDto> Sections,
    Stage1TimingDto? Timing,
    IReadOnlyList<string> Tags
);

public sealed record Stage1SectionDto(
    string? Name,
    IReadOnlyList<Stage1IngredientDto> Ingredients,
    IReadOnlyList<string> Instructions
);

public sealed record Stage1IngredientDto(
    string Name,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Quantity,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? MaxQuantity,
    string? Unit,
    string? PrepNote,
    bool IsOptional,
    IReadOnlyList<Stage1ReplacementDto>? Replacements,
    IReadOnlyList<Stage1AlternateMeasurementDto>? AlternateMeasurements
);

public sealed record Stage1ReplacementDto(
    string Name,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Quantity,
    string? Unit,
    string? PrepNote,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? MaxQuantity = null
);

public sealed record Stage1AlternateMeasurementDto(
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Quantity,
    string? Unit
);

public sealed record Stage1TimingDto(
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? Prep,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? Cook,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? Total
);

// Stage 2 DTO
public sealed record Stage2ShortenedDescriptionDto(string Description);

// Stage 3 DTOs
public sealed record Stage3SearchTermsDto(IReadOnlyList<string> SearchTerms);

public sealed record Stage3MatchDecisionDto(
    string MatchType,
    string? Id,
    string? Name,
    string? Category
);

public sealed record Stage3BatchMatchEntryDto(
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int IngredientIndex,
    string MatchType,
    string? Id,
    string? Name,
    string? Category
);

public sealed record Stage3BatchMatchResponseDto(IReadOnlyList<Stage3BatchMatchEntryDto> Matches);

// Stage 4 DTOs
public sealed record Stage4BatchStepDto(
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int StepNumber,
    JsonArray Tokens
);

public sealed record Stage4BatchResponseDto(IReadOnlyList<Stage4BatchStepDto> Steps);

// Stage 5 DTOs
public sealed record Stage5IngredientPatchDto(
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int Index,
    string? Name,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Quantity,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? MaxQuantity,
    string? Unit,
    string? PrepNote,
    bool? IsOptional,
    IReadOnlyList<Stage1AlternateMeasurementDto>? AlternateMeasurements = null
);

public sealed record Stage5StepPatchDto(
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int StepNumber,
    JsonArray Tokens
);

public sealed record Stage5TargetedPolishResponseDto(
    string? Title,
    string? Description,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Servings,
    string? Yield,
    Stage1TimingDto? Timing,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<Stage5IngredientPatchDto> IngredientPatches,
    IReadOnlyList<Stage5StepPatchDto> StepPatches,
    IReadOnlyList<string> PolishNotes
);

// Stage 6 Refinement DTOs
public sealed record Stage6ReplacementPatchDto(
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int ReplacementIndex,
    string? DisplayName = null,
    string? CanonicalFoodName = null,
    string? Category = null,
    bool? IsPantryStaple = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? Quantity = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? MaxQuantity = null,
    string? Unit = null,
    string? PrepNote = null
);

public sealed record Stage6IngredientPatchDto(
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int Index,
    string? DisplayName = null,
    string? CanonicalFoodName = null,
    string? Category = null,
    bool? IsPantryStaple = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? Quantity = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? MaxQuantity = null,
    string? Unit = null,
    string? PrepNote = null,
    bool? IsOptional = null,
    bool? IsRecipeReference = null,
    string? ReferencedRecipeTitle = null,
    IReadOnlyList<Stage1AlternateMeasurementDto>? AlternateMeasurements = null,
    IReadOnlyList<Stage6ReplacementPatchDto>? ReplacementPatches = null
);

public sealed record Stage6RefineRecipeResponseDto(
    string? Title,
    string? Description,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Servings,
    string? Yield,
    Stage1TimingDto? Timing,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<Stage6IngredientPatchDto> IngredientPatches,
    IReadOnlyList<Stage5StepPatchDto> StepPatches,
    IReadOnlyList<string> RefinementNotes
);

// Single-pass external extraction DTOs (Antigravity workflow bulk import)
public sealed record ExternalExtractedReplacementDto(
    string Name,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? Quantity = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? MaxQuantity = null,
    string? Unit = null,
    string? PrepNote = null,
    string? CanonicalFoodName = null,
    string? Category = null,
    bool? IsPantryStaple = null
);

public sealed record ExternalExtractedIngredientDto(
    string Name,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? Quantity = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? MaxQuantity = null,
    string? Unit = null,
    string? PrepNote = null,
    bool IsOptional = false,
    string? CanonicalFoodName = null,
    string? Category = null,
    bool? IsPantryStaple = null,
    bool IsRecipeReference = false,
    string? ReferencedRecipeTitle = null,
    IReadOnlyList<ExternalExtractedReplacementDto>? Replacements = null,
    IReadOnlyList<Stage1AlternateMeasurementDto>? AlternateMeasurements = null
);

public sealed record ExternalExtractedStepDto(string Instruction, JsonArray Tokens);

public sealed record ExternalExtractedSectionDto(
    string? Name,
    IReadOnlyList<ExternalExtractedIngredientDto> Ingredients,
    IReadOnlyList<ExternalExtractedStepDto> Steps
);

public sealed record ExternalRecipeExtractionDto(
    string Title,
    string? Description,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Servings,
    string? Yield,
    IReadOnlyList<ExternalExtractedSectionDto> Sections,
    Stage1TimingDto? Timing,
    IReadOnlyList<string>? Tags
);

// Parsed intermediate representation of Stage 4 token JSON nodes before compilation into ||{...}|| strings
public abstract record Stage4ParsedToken(string Type);

public sealed record Stage4TextToken(string Text) : Stage4ParsedToken("text");

public sealed record Stage4IngredientToken(
    int? IngredientIndex,
    string Name,
    decimal? Quantity,
    decimal? MaxQuantity,
    string? Unit,
    bool Fixed = false
) : Stage4ParsedToken("ingredient");

public sealed record Stage4MixtureToken(
    string Name,
    decimal Quantity,
    decimal? MaxQuantity,
    string? Unit,
    bool Fixed = false
) : Stage4ParsedToken("mixture");

public sealed record Stage4TimerToken(decimal Amount, decimal? MaxAmount, string Unit, string Label)
    : Stage4ParsedToken("timer");

public sealed record Stage4TemperatureToken(decimal Amount, decimal? MaxAmount, string Unit)
    : Stage4ParsedToken("temperature");

public sealed record Stage4DimensionToken(
    decimal Amount,
    decimal? MaxAmount,
    string Unit,
    bool Hyphenated
) : Stage4ParsedToken("dimension");

public sealed record Stage4FixedToken(
    string Name,
    decimal Amount,
    decimal? MaxAmount,
    string? Unit,
    bool Hyphenated
) : Stage4ParsedToken("fixed");

public sealed record ValidatedStage4StepResult(
    int StepNumber,
    string? SectionName,
    string OriginalInstruction,
    JsonArray SanitizedTokensJson,
    IReadOnlyList<Stage4ParsedToken> ParsedTokens,
    string CompiledInstruction,
    IReadOnlyList<RecipeStepTimerInput> Timers,
    IReadOnlyList<string> ValidationWarnings
);

public static class MultiStageSchemas
{
    private static readonly JsonSerializerOptions SchemaOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonNode ToNode(object schema) =>
        JsonSerializer.SerializeToNode(schema, SchemaOptions)!;

    private static object TimingSchema() =>
        new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                prep = new { type = new[] { "integer", "null" } },
                cook = new { type = new[] { "integer", "null" } },
                total = new { type = new[] { "integer", "null" } },
            },
            required = new[] { "prep", "cook", "total" },
        };

    private static object AlternateMeasurementsArraySchema() =>
        new
        {
            type = "array",
            items = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    quantity = new { type = "number" },
                    unit = new { type = new[] { "string", "null" } },
                },
                required = new[] { "quantity", "unit" },
            },
        };

    private static object IngredientObjectSchema(bool includeIndexAndSection)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        if (includeIndexAndSection)
        {
            properties["index"] = new { type = "integer" };
            properties["section"] = new { type = new[] { "string", "null" } };
            required.Add("index");
            required.Add("section");
        }

        properties["name"] = new { type = "string" };
        properties["quantity"] = new { type = new[] { "number", "null" } };
        properties["maxQuantity"] = new { type = new[] { "number", "null" } };
        properties["unit"] = new { type = new[] { "string", "null" } };
        properties["prepNote"] = new { type = new[] { "string", "null" } };
        properties["isOptional"] = new { type = "boolean" };
        properties["replacements"] = new
        {
            type = "array",
            items = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    name = new { type = "string" },
                    quantity = new { type = new[] { "number", "null" } },
                    unit = new { type = new[] { "string", "null" } },
                    prepNote = new { type = new[] { "string", "null" } },
                },
                required = new[] { "name", "quantity", "unit", "prepNote" },
            },
        };
        properties["alternateMeasurements"] = AlternateMeasurementsArraySchema();

        required.AddRange([
            "name",
            "quantity",
            "maxQuantity",
            "unit",
            "prepNote",
            "isOptional",
            "replacements",
            "alternateMeasurements",
        ]);

        return new
        {
            type = "object",
            additionalProperties = false,
            properties,
            required = required.ToArray(),
        };
    }

    private static object StepTokenArraySchema() =>
        new
        {
            type = "array",
            items = new
            {
                anyOf = new object[]
                {
                    new { type = "string" },
                    new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "ingredient" } },
                            ingredientIndex = new { type = new[] { "integer", "null" } },
                            name = new { type = "string" },
                            quantity = new { type = "number" },
                            maxQuantity = new { type = new[] { "number", "null" } },
                            unit = new { type = new[] { "string", "null" } },
                            @fixed = new { type = new[] { "boolean", "null" } },
                        },
                        required = new[]
                        {
                            "type",
                            "ingredientIndex",
                            "name",
                            "quantity",
                            "maxQuantity",
                            "unit",
                        },
                    },
                    new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "mixture" } },
                            name = new { type = "string" },
                            quantity = new { type = "number" },
                            maxQuantity = new { type = new[] { "number", "null" } },
                            unit = new { type = new[] { "string", "null" } },
                            @fixed = new { type = new[] { "boolean", "null" } },
                        },
                        required = new[] { "type", "name", "quantity", "maxQuantity", "unit" },
                    },
                    new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "timer" } },
                            amount = new { type = "number" },
                            maxAmount = new { type = new[] { "number", "null" } },
                            unit = new
                            {
                                type = "string",
                                @enum = new[] { "second", "minute", "hour", "day" },
                            },
                            label = new { type = "string" },
                        },
                        required = new[] { "type", "amount", "maxAmount", "unit", "label" },
                    },
                    new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "temperature" } },
                            amount = new { type = "number" },
                            maxAmount = new { type = new[] { "number", "null" } },
                            unit = new
                            {
                                type = "string",
                                @enum = new[] { "fahrenheit", "celsius" },
                            },
                        },
                        required = new[] { "type", "amount", "maxAmount", "unit" },
                    },
                    new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "dimension" } },
                            amount = new { type = "number" },
                            maxAmount = new { type = new[] { "number", "null" } },
                            unit = new
                            {
                                type = "string",
                                @enum = new[]
                                {
                                    "inch",
                                    "foot",
                                    "yard",
                                    "millimeter",
                                    "centimeter",
                                    "meter",
                                },
                            },
                            hyphenated = new { type = "boolean" },
                        },
                        required = new[] { "type", "amount", "maxAmount", "unit", "hyphenated" },
                    },
                    new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "fixed" } },
                            name = new { type = new[] { "string", "null" } },
                            amount = new { type = "number" },
                            maxAmount = new { type = new[] { "number", "null" } },
                            unit = new { type = new[] { "string", "null" } },
                            hyphenated = new { type = "boolean" },
                        },
                        required = new[]
                        {
                            "type",
                            "name",
                            "amount",
                            "maxAmount",
                            "unit",
                            "hyphenated",
                        },
                    },
                },
            },
        };

    public static JsonNode Stage1ExtractRecipeSchema { get; } =
        ToNode(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    title = new { type = "string" },
                    description = new { type = new[] { "string", "null" } },
                    servings = new { type = new[] { "number", "null" } },
                    yield = new { type = new[] { "string", "null" } },
                    sections = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                name = new { type = new[] { "string", "null" } },
                                ingredients = new
                                {
                                    type = "array",
                                    items = IngredientObjectSchema(includeIndexAndSection: false),
                                },
                                instructions = new
                                {
                                    type = "array",
                                    items = new { type = "string" },
                                },
                            },
                            required = new[] { "name", "ingredients", "instructions" },
                        },
                    },
                    timing = TimingSchema(),
                    tags = new { type = "array", items = new { type = "string" } },
                },
                required = new[]
                {
                    "title",
                    "description",
                    "servings",
                    "yield",
                    "sections",
                    "timing",
                    "tags",
                },
            }
        );

    public static JsonNode Stage2ShortenDescriptionSchema { get; } =
        ToNode(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new { description = new { type = "string" } },
                required = new[] { "description" },
            }
        );

    public static JsonNode Stage3SearchTermsSchema { get; } =
        ToNode(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    searchTerms = new { type = "array", items = new { type = "string" } },
                },
                required = new[] { "searchTerms" },
            }
        );

    private static object Stage3MatchObjectSchema(bool includeIngredientIndex)
    {
        var props = new Dictionary<string, object>();
        var req = new List<string>();
        if (includeIngredientIndex)
        {
            props["ingredientIndex"] = new { type = "integer" };
            req.Add("ingredientIndex");
        }

        props["matchType"] = new
        {
            type = "string",
            @enum = new[]
            {
                "existing_ingredient",
                "existing_recipe",
                "new_ingredient",
                "unresolved_recipe",
            },
        };
        props["id"] = new { type = new[] { "string", "null" } };
        props["name"] = new { type = new[] { "string", "null" } };
        props["category"] = new
        {
            type = new object[] { "string", "null" },
            @enum = new object?[]
            {
                "Produce",
                "MeatAndSeafood",
                "DairyAndEggs",
                "Refrigerated",
                "Bakery",
                "PantryDryGoods",
                "CannedAndJarred",
                "CondimentsAndSauces",
                "SpicesAndSeasonings",
                "Baking",
                "Snacks",
                "Beverages",
                "Frozen",
                "Other",
                null,
            },
        };

        req.AddRange(["matchType", "id", "name", "category"]);
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = props,
            required = req.ToArray(),
        };
    }

    public static JsonNode Stage3MatchIngredientSchema { get; } =
        ToNode(Stage3MatchObjectSchema(includeIngredientIndex: false));

    public static JsonNode Stage3MatchBatchSchema { get; } =
        ToNode(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    matches = new
                    {
                        type = "array",
                        items = Stage3MatchObjectSchema(includeIngredientIndex: true),
                    },
                },
                required = new[] { "matches" },
            }
        );

    public static JsonNode Stage4TokenizeStepSchema { get; } =
        ToNode(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new { tokens = StepTokenArraySchema() },
                required = new[] { "tokens" },
            }
        );

    public static JsonNode Stage4BatchStepsSchema { get; } =
        ToNode(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    steps = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                stepNumber = new { type = "integer" },
                                tokens = StepTokenArraySchema(),
                            },
                            required = new[] { "stepNumber", "tokens" },
                        },
                    },
                },
                required = new[] { "steps" },
            }
        );

    public static JsonNode Stage5PolishTargetedSchema { get; } =
        ToNode(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    title = new { type = new[] { "string", "null" } },
                    description = new { type = new[] { "string", "null" } },
                    servings = new { type = new[] { "number", "null" } },
                    yield = new { type = new[] { "string", "null" } },
                    timing = new { anyOf = new object[] { TimingSchema(), new { type = "null" } } },
                    tags = new
                    {
                        anyOf = new object[]
                        {
                            new { type = "array", items = new { type = "string" } },
                            new { type = "null" },
                        },
                    },
                    ingredientPatches = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                index = new { type = "integer" },
                                name = new { type = new[] { "string", "null" } },
                                quantity = new { type = new[] { "number", "null" } },
                                maxQuantity = new { type = new[] { "number", "null" } },
                                unit = new { type = new[] { "string", "null" } },
                                prepNote = new { type = new[] { "string", "null" } },
                                isOptional = new { type = new[] { "boolean", "null" } },
                                alternateMeasurements = new
                                {
                                    anyOf = new object[]
                                    {
                                        AlternateMeasurementsArraySchema(),
                                        new { type = "null" },
                                    },
                                },
                            },
                            required = new[]
                            {
                                "index",
                                "name",
                                "quantity",
                                "maxQuantity",
                                "unit",
                                "prepNote",
                                "isOptional",
                                "alternateMeasurements",
                            },
                        },
                    },
                    stepPatches = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                stepNumber = new { type = "integer" },
                                tokens = StepTokenArraySchema(),
                            },
                            required = new[] { "stepNumber", "tokens" },
                        },
                    },
                    polishNotes = new { type = "array", items = new { type = "string" } },
                },
                required = new[]
                {
                    "title",
                    "description",
                    "servings",
                    "yield",
                    "timing",
                    "tags",
                    "ingredientPatches",
                    "stepPatches",
                    "polishNotes",
                },
            }
        );

    private static object NullableCategorySchema() =>
        new
        {
            type = new object[] { "string", "null" },
            @enum = new object?[]
            {
                "Produce",
                "MeatAndSeafood",
                "DairyAndEggs",
                "Refrigerated",
                "Bakery",
                "PantryDryGoods",
                "CannedAndJarred",
                "CondimentsAndSauces",
                "SpicesAndSeasonings",
                "Baking",
                "Snacks",
                "Beverages",
                "Frozen",
                "Other",
                null,
            },
        };

    public static JsonNode Stage6RefineRecipeSchema { get; } =
        ToNode(
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    title = new { type = new[] { "string", "null" } },
                    description = new { type = new[] { "string", "null" } },
                    servings = new { type = new[] { "number", "null" } },
                    yield = new { type = new[] { "string", "null" } },
                    timing = new { anyOf = new object[] { TimingSchema(), new { type = "null" } } },
                    tags = new
                    {
                        anyOf = new object[]
                        {
                            new { type = "array", items = new { type = "string" } },
                            new { type = "null" },
                        },
                    },
                    ingredientPatches = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                index = new { type = "integer" },
                                displayName = new { type = new[] { "string", "null" } },
                                canonicalFoodName = new { type = new[] { "string", "null" } },
                                category = NullableCategorySchema(),
                                isPantryStaple = new { type = new[] { "boolean", "null" } },
                                quantity = new { type = new[] { "number", "null" } },
                                maxQuantity = new { type = new[] { "number", "null" } },
                                unit = new { type = new[] { "string", "null" } },
                                prepNote = new { type = new[] { "string", "null" } },
                                isOptional = new { type = new[] { "boolean", "null" } },
                                isRecipeReference = new { type = new[] { "boolean", "null" } },
                                referencedRecipeTitle = new { type = new[] { "string", "null" } },
                                alternateMeasurements = new
                                {
                                    anyOf = new object[]
                                    {
                                        AlternateMeasurementsArraySchema(),
                                        new { type = "null" },
                                    },
                                },
                                replacementPatches = new
                                {
                                    anyOf = new object[]
                                    {
                                        new
                                        {
                                            type = "array",
                                            items = new
                                            {
                                                type = "object",
                                                additionalProperties = false,
                                                properties = new
                                                {
                                                    replacementIndex = new { type = "integer" },
                                                    displayName = new
                                                    {
                                                        type = new[] { "string", "null" },
                                                    },
                                                    canonicalFoodName = new
                                                    {
                                                        type = new[] { "string", "null" },
                                                    },
                                                    category = NullableCategorySchema(),
                                                    isPantryStaple = new
                                                    {
                                                        type = new[] { "boolean", "null" },
                                                    },
                                                    quantity = new
                                                    {
                                                        type = new[] { "number", "null" },
                                                    },
                                                    maxQuantity = new
                                                    {
                                                        type = new[] { "number", "null" },
                                                    },
                                                    unit = new
                                                    {
                                                        type = new[] { "string", "null" },
                                                    },
                                                    prepNote = new
                                                    {
                                                        type = new[] { "string", "null" },
                                                    },
                                                },
                                                required = new[]
                                                {
                                                    "replacementIndex",
                                                    "displayName",
                                                    "canonicalFoodName",
                                                    "category",
                                                    "isPantryStaple",
                                                    "quantity",
                                                    "maxQuantity",
                                                    "unit",
                                                    "prepNote",
                                                },
                                            },
                                        },
                                        new { type = "null" },
                                    },
                                },
                            },
                            required = new[]
                            {
                                "index",
                                "displayName",
                                "canonicalFoodName",
                                "category",
                                "isPantryStaple",
                                "quantity",
                                "maxQuantity",
                                "unit",
                                "prepNote",
                                "isOptional",
                                "isRecipeReference",
                                "referencedRecipeTitle",
                                "alternateMeasurements",
                                "replacementPatches",
                            },
                        },
                    },
                    stepPatches = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                stepNumber = new { type = "integer" },
                                tokens = StepTokenArraySchema(),
                            },
                            required = new[] { "stepNumber", "tokens" },
                        },
                    },
                    refinementNotes = new { type = "array", items = new { type = "string" } },
                },
                required = new[]
                {
                    "title",
                    "description",
                    "servings",
                    "yield",
                    "timing",
                    "tags",
                    "ingredientPatches",
                    "stepPatches",
                    "refinementNotes",
                },
            }
        );
}
