using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;

namespace IngestionPoc.Models;

public sealed record RecipeInputItem(
    string Id,
    string Title,
    string SourceKind,
    string NormalizedText,
    string? SourceReference = null,
    string? ProductionExtractedDraftJson = null,
    decimal? ProductionConfidenceScore = null,
    string? ProductionJobStatus = null
);

public sealed record PromptFileInfo(string Name, string FilePath, string Content, string Sha256);

public sealed class PromptSet
{
    public required PromptFileInfo ExtractRecipe { get; init; }
    public required PromptFileInfo ShortenDescription { get; init; }
    public required PromptFileInfo SearchTerms { get; init; }
    public required PromptFileInfo MatchIngredient { get; init; }
    public required PromptFileInfo MatchIngredientBatch { get; init; }
    public required PromptFileInfo TokenizeStep { get; init; }
    public required PromptFileInfo TokenizeStepsBatch { get; init; }
    public required PromptFileInfo PolishFull { get; init; }
    public required PromptFileInfo PolishTargeted { get; init; }

    public IReadOnlyList<PromptFileInfo> All =>
        [
            ExtractRecipe,
            ShortenDescription,
            SearchTerms,
            MatchIngredient,
            MatchIngredientBatch,
            TokenizeStep,
            TokenizeStepsBatch,
            PolishFull,
            PolishTargeted,
        ];

    public static PromptSet LoadFromDirectory(string directory)
    {
        return new PromptSet
        {
            ExtractRecipe = Load(directory, "01-extract-recipe.md"),
            ShortenDescription = Load(directory, "02-shorten-description.md"),
            SearchTerms = Load(directory, "03a-ingredient-search-terms.md"),
            MatchIngredient = Load(directory, "03b-ingredient-match.md"),
            MatchIngredientBatch = Load(directory, "03c-ingredient-match-batch.md"),
            TokenizeStep = Load(directory, "04-tokenize-step.md"),
            TokenizeStepsBatch = Load(directory, "04b-tokenize-steps-batch.md"),
            PolishFull = Load(directory, "05a-polish-full.md"),
            PolishTargeted = Load(directory, "05b-polish-targeted.md"),
        };
    }

    private static PromptFileInfo Load(string dir, string fileName)
    {
        var fullPath = Path.GetFullPath(Path.Combine(dir, fileName));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Missing prompt markdown file: {fullPath}", fullPath);
        }

        var content = File.ReadAllText(fullPath, Encoding.UTF8).Trim();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new PromptFileInfo(fileName, fullPath, content, hash);
    }
}

// Stage 1 DTOs
public sealed record Stage1RecipeDto(
    string Title,
    string? Description,
    decimal? Servings,
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
    decimal? Quantity,
    decimal? MaxQuantity,
    string? Unit,
    string? PrepNote,
    bool IsOptional,
    IReadOnlyList<Stage1ReplacementDto>? Replacements,
    IReadOnlyList<Stage1AlternateMeasurementDto>? AlternateMeasurements
);

public sealed record Stage1ReplacementDto(
    string Name,
    decimal? Quantity,
    string? Unit,
    string? PrepNote
);

public sealed record Stage1AlternateMeasurementDto(decimal Quantity, string? Unit);

public sealed record Stage1TimingDto(int? Prep, int? Cook, int? Total);

// Stage 2 DTO
public sealed record Stage2ShortenedDescriptionDto(string Description);

// Stage 3 DTOs
public sealed record Stage3SearchTermsDto(IReadOnlyList<string> SearchTerms);

public sealed record FoodItemCandidate(
    Guid Id,
    string Name,
    string NormalizedName,
    FoodCategory Category,
    bool IsPantryStaple,
    IReadOnlyList<string> Aliases,
    double Score,
    string MatchReason
);

public sealed record RecipeCandidate(
    Guid Id,
    string Title,
    string Slug,
    double Score,
    string MatchReason
);

public sealed record DbSearchCandidates(
    IReadOnlyList<string> SearchTermsUsed,
    IReadOnlyList<FoodItemCandidate> ExistingIngredients,
    IReadOnlyList<RecipeCandidate> ExistingRecipes
);

public sealed record Stage3MatchDecisionDto(
    string MatchType,
    string? Id,
    string? Name,
    string? Category
);

public sealed record Stage3BatchMatchEntryDto(
    int IngredientIndex,
    string MatchType,
    string? Id,
    string? Name,
    string? Category
);

public sealed record Stage3BatchMatchResponseDto(IReadOnlyList<Stage3BatchMatchEntryDto> Matches);

public enum Stage3Mode
{
    LlmTerms,
    VectorSingle,
    VectorBatched,
}

public sealed record RecipeQualityScorecard(
    int TotalScore,
    int IngredientScore,
    int StepScore,
    int ResolutionScore,
    int TimingScore,
    IReadOnlyList<string> Deductions
);

public sealed record ResolvedIngredientResult(
    int GlobalIndex,
    string? SectionName,
    Stage1IngredientDto Extracted,
    IReadOnlyList<string> SearchTerms,
    DbSearchCandidates DbCandidates,
    Stage3MatchDecisionDto Decision,
    IReadOnlyList<string> ValidationWarnings
);

// Stage 4 Token Models
public abstract record PocStepToken(string Type);

public sealed record PocTextToken(string Text) : PocStepToken("text");

public sealed record PocIngredientToken(
    int? IngredientIndex,
    string Name,
    decimal? Quantity,
    decimal? MaxQuantity,
    string? Unit
) : PocStepToken("ingredient");

public sealed record PocMixtureToken(
    string Name,
    decimal Quantity,
    decimal? MaxQuantity,
    string? Unit
) : PocStepToken("mixture");

public sealed record PocTimerToken(decimal Amount, decimal? MaxAmount, string Unit, string Label)
    : PocStepToken("timer");

public sealed record PocTemperatureToken(decimal Amount, decimal? MaxAmount, string Unit)
    : PocStepToken("temperature");

public sealed record PocDimensionToken(
    decimal Amount,
    decimal? MaxAmount,
    string Unit,
    bool Hyphenated
) : PocStepToken("dimension");

public sealed record PocFixedToken(
    string Name,
    decimal Amount,
    decimal? MaxAmount,
    string? Unit,
    bool Hyphenated
) : PocStepToken("fixed");

public sealed record StepTokenizeResult(
    int StepNumber,
    string? SectionName,
    string OriginalInstruction,
    JsonArray RawTokensJson,
    IReadOnlyList<PocStepToken> ParsedTokens,
    string RenderedOriginal,
    string RenderedScaled2x,
    string RenderedMetric1x,
    IReadOnlyList<string> ValidationWarnings
);

public enum Stage4Mode
{
    Single,
    Batched,
}

public sealed record Stage4BatchStepDto(int StepNumber, JsonArray Tokens);

public sealed record Stage4BatchResponseDto(IReadOnlyList<Stage4BatchStepDto> Steps);

public enum PolishMode
{
    None,
    Targeted,
    Full,
    Both,
}

// Stage 5 DTOs
public sealed record PolishFullIngredientDto(
    int Index,
    string? Section,
    string Name,
    decimal? Quantity,
    decimal? MaxQuantity,
    string? Unit,
    string? PrepNote,
    bool IsOptional,
    IReadOnlyList<Stage1ReplacementDto>? Replacements,
    IReadOnlyList<Stage1AlternateMeasurementDto>? AlternateMeasurements
);

public sealed record PolishFullStepDto(int StepNumber, string? SectionName, JsonArray Tokens);

public sealed record PolishFullResponseDto(
    string Title,
    string? Description,
    decimal? Servings,
    string? Yield,
    Stage1TimingDto? Timing,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PolishFullIngredientDto> Ingredients,
    IReadOnlyList<PolishFullStepDto> Steps,
    IReadOnlyList<string> PolishNotes
);

public sealed record PolishIngredientPatchDto(
    int Index,
    string? Name,
    decimal? Quantity,
    decimal? MaxQuantity,
    string? Unit,
    string? PrepNote,
    bool? IsOptional,
    IReadOnlyList<Stage1AlternateMeasurementDto>? AlternateMeasurements = null
);

public sealed record PolishStepPatchDto(int StepNumber, JsonArray Tokens);

public sealed record PolishTargetedResponseDto(
    string? Title,
    string? Description,
    decimal? Servings,
    string? Yield,
    Stage1TimingDto? Timing,
    IReadOnlyList<string>? Tags,
    IReadOnlyList<PolishIngredientPatchDto> IngredientPatches,
    IReadOnlyList<PolishStepPatchDto> StepPatches,
    IReadOnlyList<string> PolishNotes
);

public sealed record PolishSummaryMetrics(
    string Variant,
    bool Success,
    long ElapsedMs,
    int PolishNotesCount,
    int StepWarningsAfterPolish,
    int ProseOnlyIngredientsAfterPolish,
    IReadOnlyList<string> PolishNotes,
    string? ParseError
);

public static class PipelineSchemas
{
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
                            name = new { type = "string" },
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

    public static object Stage1ExtractRecipeSchema() =>
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
                            instructions = new { type = "array", items = new { type = "string" } },
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
        };

    public static object Stage2ShortenDescriptionSchema() =>
        new
        {
            type = "object",
            additionalProperties = false,
            properties = new { description = new { type = "string" } },
            required = new[] { "description" },
        };

    public static object Stage3SearchTermsSchema() =>
        new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                searchTerms = new { type = "array", items = new { type = "string" } },
            },
            required = new[] { "searchTerms" },
        };

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

    public static object Stage3MatchIngredientSchema() =>
        Stage3MatchObjectSchema(includeIngredientIndex: false);

    public static object Stage3MatchBatchSchema() =>
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
        };

    public static object Stage4TokenizeStepSchema() =>
        new
        {
            type = "object",
            additionalProperties = false,
            properties = new { tokens = StepTokenArraySchema() },
            required = new[] { "tokens" },
        };

    public static object Stage4BatchStepsSchema() =>
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
        };

    public static object Stage5PolishFullSchema() =>
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
                timing = TimingSchema(),
                tags = new { type = "array", items = new { type = "string" } },
                ingredients = new
                {
                    type = "array",
                    items = IngredientObjectSchema(includeIndexAndSection: true),
                },
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
                            sectionName = new { type = new[] { "string", "null" } },
                            tokens = StepTokenArraySchema(),
                        },
                        required = new[] { "stepNumber", "sectionName", "tokens" },
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
                "ingredients",
                "steps",
                "polishNotes",
            },
        };

    public static object Stage5PolishTargetedSchema() =>
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
        };
}

public static partial class StepTokenParserAndRenderer
{
    private static readonly HashSet<string> PluralUnitNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "drops",
        "smidgens",
        "pinches",
        "dashes",
        "teaspoons",
        "tablespoons",
        "fluid ounces",
        "cups",
        "pints",
        "quarts",
        "gallons",
        "milliliters",
        "centiliters",
        "deciliters",
        "liters",
        "ounces",
        "pounds",
        "milligrams",
        "grams",
        "kilograms",
        "inches",
        "feet",
        "yards",
        "millimeters",
        "centimeters",
        "meters",
        "seconds",
        "minutes",
        "hours",
        "days",
        "cloves",
        "cans",
        "slices",
        "bunches",
        "sticks",
        "sprigs",
        "leaves",
    };

    [GeneratedRegex(
        @"\b\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*(?:teaspoons?|tsp\.?|tablespoons?|tbsp\.?|cups?|pints?|quarts?|gallons?|fl(?:uid)?\s+oz\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|g\b|kilograms?|kg|milliliters?|ml|liters?|minutes?|mins?|seconds?|secs?|hours?|hrs?|degrees?|°\s*[FC]|deg\s*[FC]|inch(?:es)?|cm|mm)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex UntokenizedQuantityRegex();

    [GeneratedRegex(@"[a-zA-Z]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(
        @"^\s*(?:seconds?|minutes?|hours?|days?|secs?|mins?|hrs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTimeUnitPrefixRegex();

    [GeneratedRegex(
        @"^\s*(?:inch(?:es)?|feet|foot|yards?|millimeters?|centimeters?|meters?|mm\b|cm\b)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateDimUnitPrefixRegex();

    [GeneratedRegex(
        @"^\s*(?:°\s*[FC]|degrees?(?:\s+(?:F|C|Fahrenheit|Celsius))?\b|fahrenheit\b|celsius\b|[FC]\b)",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTempUnitPrefixRegex();

    public static List<PocStepToken> ParseTokens(
        JsonArray tokensArray,
        out List<string> parseErrors
    )
    {
        parseErrors = [];
        var result = new List<PocStepToken>(tokensArray.Count);

        for (var i = 0; i < tokensArray.Count; i++)
        {
            var node = tokensArray[i];
            if (node is null)
            {
                parseErrors.Add($"Token[{i}] was null.");
                continue;
            }

            if (node is JsonValue val && val.TryGetValue<string>(out var text))
            {
                result.Add(new PocTextToken(text));
                continue;
            }

            if (node is not JsonObject obj)
            {
                parseErrors.Add($"Token[{i}] was unexpected JSON node type {node.GetType().Name}.");
                continue;
            }

            var type = obj["type"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            switch (type)
            {
                case "ingredient":
                    result.Add(
                        new PocIngredientToken(
                            GetNullableInt(obj["ingredientIndex"]),
                            obj["name"]?.GetValue<string>() ?? string.Empty,
                            GetNullableDecimal(obj["quantity"]),
                            GetNullableDecimal(obj["maxQuantity"]),
                            GetNullableString(obj["unit"])
                        )
                    );
                    break;
                case "mixture":
                    result.Add(
                        new PocMixtureToken(
                            obj["name"]?.GetValue<string>() ?? string.Empty,
                            GetNullableDecimal(obj["quantity"]) ?? 0m,
                            GetNullableDecimal(obj["maxQuantity"]),
                            GetNullableString(obj["unit"])
                        )
                    );
                    break;
                case "timer":
                    result.Add(
                        new PocTimerToken(
                            GetNullableDecimal(obj["amount"]) ?? 0m,
                            GetNullableDecimal(obj["maxAmount"]),
                            GetNullableString(obj["unit"]) ?? "minute",
                            GetNullableString(obj["label"]) ?? string.Empty
                        )
                    );
                    break;
                case "temperature":
                    result.Add(
                        new PocTemperatureToken(
                            GetNullableDecimal(obj["amount"]) ?? 0m,
                            GetNullableDecimal(obj["maxAmount"]),
                            GetNullableString(obj["unit"]) ?? "fahrenheit"
                        )
                    );
                    break;
                case "dimension":
                    result.Add(
                        new PocDimensionToken(
                            GetNullableDecimal(obj["amount"]) ?? 0m,
                            GetNullableDecimal(obj["maxAmount"]),
                            GetNullableString(obj["unit"]) ?? "inch",
                            GetBool(obj["hyphenated"])
                        )
                    );
                    break;
                case "fixed":
                    result.Add(
                        new PocFixedToken(
                            obj["name"]?.GetValue<string>() ?? string.Empty,
                            GetNullableDecimal(obj["amount"]) ?? 0m,
                            GetNullableDecimal(obj["maxAmount"]),
                            GetNullableString(obj["unit"]),
                            GetBool(obj["hyphenated"])
                        )
                    );
                    break;
                default:
                    parseErrors.Add($"Token[{i}] has unknown type '{type}'.");
                    break;
            }
        }

        return result;
    }

    public static string Render(
        IReadOnlyList<PocStepToken> tokens,
        decimal scaleFactor,
        bool convertToMetric
    )
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            switch (token)
            {
                case PocTextToken t:
                    sb.Append(t.Text);
                    break;
                case PocIngredientToken ing:
                    sb.Append(
                        RenderAmountAndUnit(
                            ing.Quantity.HasValue ? ing.Quantity.Value * scaleFactor : null,
                            ing.MaxQuantity.HasValue ? ing.MaxQuantity.Value * scaleFactor : null,
                            ing.Unit,
                            convertToMetric,
                            hyphenated: false,
                            suffix: ing.Name
                        )
                    );
                    break;
                case PocMixtureToken mix:
                    sb.Append(
                        RenderAmountAndUnit(
                            mix.Quantity * scaleFactor,
                            mix.MaxQuantity.HasValue ? mix.MaxQuantity.Value * scaleFactor : null,
                            mix.Unit,
                            convertToMetric,
                            hyphenated: false,
                            suffix: mix.Name
                        )
                    );
                    break;
                case PocTimerToken timer:
                    sb.Append(
                        RenderAmountAndUnit(
                            timer.Amount,
                            timer.MaxAmount,
                            timer.Unit,
                            convertToMetric: false,
                            hyphenated: false,
                            suffix: null
                        )
                    );
                    break;
                case PocTemperatureToken temp:
                    sb.Append(RenderTemperature(temp, convertToMetric));
                    break;
                case PocDimensionToken dim:
                    sb.Append(
                        RenderAmountAndUnit(
                            dim.Amount,
                            dim.MaxAmount,
                            dim.Unit,
                            convertToMetric,
                            dim.Hyphenated,
                            suffix: null
                        )
                    );
                    break;
                case PocFixedToken fx:
                    var renderedMeasure = RenderAmountAndUnit(
                        fx.Amount,
                        fx.MaxAmount,
                        fx.Unit,
                        convertToMetric,
                        fx.Hyphenated,
                        suffix: null
                    );
                    sb.Append(
                        string.IsNullOrWhiteSpace(fx.Name)
                            ? renderedMeasure
                            : $"{renderedMeasure} {fx.Name}"
                    );
                    break;
            }
        }

        return sb.ToString();
    }

    public static List<string> ValidateStepTokens(
        string originalInstruction,
        IReadOnlyList<PocStepToken> tokens,
        int ingredientCount,
        IReadOnlyList<string> parseErrors
    )
    {
        var warnings = new List<string>(parseErrors);
        if (tokens.Count == 0)
        {
            warnings.Add("Tokens array is empty.");
            return warnings;
        }

        var rendered = Render(tokens, scaleFactor: 1m, convertToMetric: false);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            warnings.Add("Rendered step text is empty.");
            return warnings;
        }

        // Check if non-numeric prose words were dropped
        var origWords = WordRegex()
            .Matches(originalInstruction)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !IsUnitOrNumberWord(w))
            .ToHashSet();
        var renderedWords = WordRegex()
            .Matches(rendered)
            .Select(m => m.Value.ToLowerInvariant())
            .ToHashSet();

        var missingWords = origWords
            .Where(w => !renderedWords.Contains(w) && !IsTempOrTimeTextWord(w))
            .Take(8)
            .ToList();
        if (missingWords.Count >= 3)
        {
            warnings.Add(
                $"Possible dropped prose words ({missingWords.Count}): {string.Join(", ", missingWords)}"
            );
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (
                i + 1 < tokens.Count
                && tokens[i] is not PocTextToken
                && tokens[i + 1] is not PocTextToken
            )
            {
                warnings.Add(
                    $"Adjacent non-text tokens [{i}] ({tokens[i].Type}) and [{i + 1}] ({tokens[i + 1].Type}) with no separator string."
                );
            }
            else if (
                i + 1 < tokens.Count
                && tokens[i] is PocTextToken prevTxt
                && tokens[i + 1] is not PocTextToken
                && prevTxt.Text.Length > 0
                && char.IsLetterOrDigit(prevTxt.Text[^1])
            )
            {
                warnings.Add(
                    $"Missing space between text ending with '{prevTxt.Text[^1]}' and [{i + 1}] ({tokens[i + 1].Type})."
                );
            }
            else if (
                i + 1 < tokens.Count
                && tokens[i] is not PocTextToken
                && tokens[i + 1] is PocTextToken nextTxt
                && nextTxt.Text.Length > 0
                && char.IsLetterOrDigit(nextTxt.Text[0])
            )
            {
                warnings.Add(
                    $"Missing space between [{i}] ({tokens[i].Type}) and text starting with '{nextTxt.Text[0]}'."
                );
            }

            switch (tokens[i])
            {
                case PocTextToken txt:
                    var untok = UntokenizedQuantityRegex().Match(txt.Text);
                    if (untok.Success)
                    {
                        warnings.Add(
                            $"Untokenized amount/unit '{untok.Value}' left in plain text segment."
                        );
                    }
                    break;
                case PocIngredientToken ing:
                    if (
                        ing.IngredientIndex is < 0
                        || (
                            ing.IngredientIndex.HasValue
                            && ing.IngredientIndex.Value >= ingredientCount
                        )
                    )
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' has out-of-range ingredientIndex={ing.IngredientIndex} (count={ingredientCount})."
                        );
                    }
                    if (!ing.Quantity.HasValue)
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' has quantity=null; unquantified ingredient mentions should remain plain text."
                        );
                    }
                    if (
                        ing.Quantity.HasValue
                        && ing.MaxQuantity.HasValue
                        && ing.MaxQuantity.Value < ing.Quantity.Value
                    )
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' has inverted range {ing.Quantity}..{ing.MaxQuantity}."
                        );
                    }
                    if (
                        !string.IsNullOrWhiteSpace(ing.Unit)
                        && PluralUnitNames.Contains(ing.Unit.Trim())
                    )
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' used plural unit '{ing.Unit}' instead of singular."
                        );
                    }
                    break;
                case PocMixtureToken mix:
                    if (mix.MaxQuantity.HasValue && mix.MaxQuantity.Value < mix.Quantity)
                    {
                        warnings.Add(
                            $"Mixture token '{mix.Name}' has inverted range {mix.Quantity}..{mix.MaxQuantity}."
                        );
                    }
                    if (
                        (
                            string.IsNullOrWhiteSpace(mix.Unit)
                            || string.Equals(
                                mix.Unit.Trim(),
                                "unit",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        && mix.Quantity == 1m
                    )
                    {
                        warnings.Add(
                            $"Mixture token '{mix.Name}' has unit='{mix.Unit ?? "null"}' and quantity=1; unmeasured mixture references should stay plain text."
                        );
                    }
                    if (
                        !string.IsNullOrWhiteSpace(mix.Unit)
                        && PluralUnitNames.Contains(mix.Unit.Trim())
                    )
                    {
                        warnings.Add(
                            $"Mixture token '{mix.Name}' used plural unit '{mix.Unit}' instead of singular."
                        );
                    }
                    break;
                case PocTimerToken timer:
                    if (timer.Amount <= 0m)
                    {
                        warnings.Add(
                            $"Timer token '{timer.Label}' has invalid amount={timer.Amount}; amount must be > 0."
                        );
                    }
                    if (string.IsNullOrWhiteSpace(timer.Label))
                    {
                        warnings.Add($"Timer token [{i}] has an empty label.");
                    }
                    if (timer.MaxAmount.HasValue && timer.MaxAmount.Value < timer.Amount)
                    {
                        warnings.Add(
                            $"Timer token '{timer.Label}' has inverted range {timer.Amount}..{timer.MaxAmount} {timer.Unit}."
                        );
                    }
                    if (
                        i + 1 < tokens.Count
                        && tokens[i + 1] is PocTextToken trailingTimerTxt
                        && DuplicateTimeUnitPrefixRegex().IsMatch(trailingTimerTxt.Text)
                    )
                    {
                        warnings.Add(
                            $"Duplicate time unit '{DuplicateTimeUnitPrefixRegex().Match(trailingTimerTxt.Text).Value.Trim()}' after timer token [{i}]."
                        );
                    }
                    break;
                case PocDimensionToken dim:
                    if (dim.MaxAmount.HasValue && dim.MaxAmount.Value < dim.Amount)
                    {
                        warnings.Add(
                            $"Dimension token has inverted range {dim.Amount}..{dim.MaxAmount} {dim.Unit}."
                        );
                    }
                    if (
                        i + 1 < tokens.Count
                        && tokens[i + 1] is PocTextToken trailingDimTxt
                        && DuplicateDimUnitPrefixRegex().IsMatch(trailingDimTxt.Text)
                    )
                    {
                        warnings.Add(
                            $"Duplicate dimension unit '{DuplicateDimUnitPrefixRegex().Match(trailingDimTxt.Text).Value.Trim()}' after dimension token [{i}]."
                        );
                    }
                    break;
                case PocTemperatureToken temp:
                    if (temp.MaxAmount.HasValue && temp.MaxAmount.Value < temp.Amount)
                    {
                        warnings.Add(
                            $"Temperature token has inverted range {temp.Amount}..{temp.MaxAmount} {temp.Unit}."
                        );
                    }
                    if (
                        i + 1 < tokens.Count
                        && tokens[i + 1] is PocTextToken trailingTempTxt
                        && DuplicateTempUnitPrefixRegex().IsMatch(trailingTempTxt.Text)
                    )
                    {
                        warnings.Add(
                            $"Duplicate temperature unit '{DuplicateTempUnitPrefixRegex().Match(trailingTempTxt.Text).Value.Trim()}' after temperature token [{i}]."
                        );
                    }
                    break;
            }
        }

        return warnings;
    }

    private static string RenderTemperature(PocTemperatureToken temp, bool convertToMetric)
    {
        var amount = temp.Amount;
        var max = temp.MaxAmount;
        var isCelsius = string.Equals(temp.Unit, "celsius", StringComparison.OrdinalIgnoreCase);

        if (convertToMetric && !isCelsius)
        {
            amount = Math.Round((amount - 32m) * 5m / 9m, 0, MidpointRounding.AwayFromZero);
            if (max.HasValue)
            {
                max = Math.Round((max.Value - 32m) * 5m / 9m, 0, MidpointRounding.AwayFromZero);
            }
            isCelsius = true;
        }

        var suffix = isCelsius ? "°C" : "°F";
        return max.HasValue && max.Value > amount
            ? $"{FormatNum(amount, isCelsius)}–{FormatNum(max.Value, isCelsius)}{suffix}"
            : $"{FormatNum(amount, isCelsius)}{suffix}";
    }

    private static string RenderAmountAndUnit(
        decimal? qty,
        decimal? maxQty,
        string? unit,
        bool convertToMetric,
        bool hyphenated,
        string? suffix
    )
    {
        if (!qty.HasValue)
        {
            return suffix ?? string.Empty;
        }

        var q = qty.Value;
        var m = maxQty;
        var u = unit?.Trim().ToLowerInvariant();
        var isMetricOut = false;

        if (convertToMetric && !string.IsNullOrEmpty(u))
        {
            (q, m, u, isMetricOut) = ConvertUnitToMetric(q, m, u);
        }
        else if (
            u
            is "milliliter"
                or "centiliter"
                or "deciliter"
                or "liter"
                or "milligram"
                or "gram"
                or "kilogram"
                or "millimeter"
                or "centimeter"
                or "meter"
        )
        {
            isMetricOut = true;
        }

        var qtyStr =
            m.HasValue && m.Value > q
                ? $"{FormatNum(q, isMetricOut)} to {FormatNum(m.Value, isMetricOut)}"
                : FormatNum(q, isMetricOut);

        var unitDisplay = FormatUnitDisplay(u, m ?? q, hyphenated);
        var combined =
            string.IsNullOrEmpty(unitDisplay) ? qtyStr
            : hyphenated ? $"{qtyStr}-{unitDisplay}"
            : $"{qtyStr} {unitDisplay}";

        return string.IsNullOrWhiteSpace(suffix) ? combined : $"{combined} {suffix}";
    }

    private static (decimal Q, decimal? M, string Unit, bool IsMetric) ConvertUnitToMetric(
        decimal q,
        decimal? m,
        string u
    )
    {
        decimal? Scale(decimal factor) => m.HasValue ? m.Value * factor : null;

        switch (u)
        {
            case "teaspoon":
                return ConvertMl(q * 4.92892m, Scale(4.92892m));
            case "tablespoon":
                return ConvertMl(q * 14.7868m, Scale(14.7868m));
            case "fluid ounce":
                return ConvertMl(q * 29.5735m, Scale(29.5735m));
            case "cup":
                return ConvertMl(q * 236.588m, Scale(236.588m));
            case "pint":
                return ConvertMl(q * 473.176m, Scale(473.176m));
            case "quart":
                return ConvertMl(q * 946.353m, Scale(946.353m));
            case "gallon":
                return ConvertMl(q * 3785.41m, Scale(3785.41m));
            case "ounce":
                return ConvertGrams(q * 28.3495m, Scale(28.3495m));
            case "pound":
                return ConvertGrams(q * 453.592m, Scale(453.592m));
            case "inch":
                var cm = q * 2.54m;
                if (cm < 1m)
                {
                    return (
                        Math.Round(q * 25.4m, 0, MidpointRounding.AwayFromZero),
                        m.HasValue
                            ? Math.Round(m.Value * 25.4m, 0, MidpointRounding.AwayFromZero)
                            : null,
                        "millimeter",
                        true
                    );
                }
                return (
                    Math.Round(cm, 1, MidpointRounding.AwayFromZero),
                    m.HasValue
                        ? Math.Round(m.Value * 2.54m, 1, MidpointRounding.AwayFromZero)
                        : null,
                    "centimeter",
                    true
                );
            case "foot":
                return (
                    Math.Round(q * 30.48m, 0, MidpointRounding.AwayFromZero),
                    m.HasValue
                        ? Math.Round(m.Value * 30.48m, 0, MidpointRounding.AwayFromZero)
                        : null,
                    "centimeter",
                    true
                );
            case "yard":
                return (
                    Math.Round(q * 0.9144m, 2, MidpointRounding.AwayFromZero),
                    m.HasValue
                        ? Math.Round(m.Value * 0.9144m, 2, MidpointRounding.AwayFromZero)
                        : null,
                    "meter",
                    true
                );
            default:
                return (q, m, u, false);
        }
    }

    private static (decimal Q, decimal? M, string Unit, bool IsMetric) ConvertMl(
        decimal ml,
        decimal? maxMl
    )
    {
        if (ml >= 1000m)
        {
            return (
                Math.Round(ml / 1000m, 2, MidpointRounding.AwayFromZero),
                maxMl.HasValue
                    ? Math.Round(maxMl.Value / 1000m, 2, MidpointRounding.AwayFromZero)
                    : null,
                "liter",
                true
            );
        }
        return (
            Math.Round(ml, ml >= 10m ? 0 : 1, MidpointRounding.AwayFromZero),
            maxMl.HasValue
                ? Math.Round(maxMl.Value, maxMl.Value >= 10m ? 0 : 1, MidpointRounding.AwayFromZero)
                : null,
            "milliliter",
            true
        );
    }

    private static (decimal Q, decimal? M, string Unit, bool IsMetric) ConvertGrams(
        decimal g,
        decimal? maxG
    )
    {
        if (g >= 1000m)
        {
            return (
                Math.Round(g / 1000m, 2, MidpointRounding.AwayFromZero),
                maxG.HasValue
                    ? Math.Round(maxG.Value / 1000m, 2, MidpointRounding.AwayFromZero)
                    : null,
                "kilogram",
                true
            );
        }
        return (
            Math.Round(g, g >= 10m ? 0 : 1, MidpointRounding.AwayFromZero),
            maxG.HasValue
                ? Math.Round(maxG.Value, maxG.Value >= 10m ? 0 : 1, MidpointRounding.AwayFromZero)
                : null,
            "gram",
            true
        );
    }

    private static string FormatNum(decimal val, bool isMetric)
    {
        if (isMetric)
        {
            return val >= 10m
                ? Math.Round(val, 0, MidpointRounding.AwayFromZero)
                    .ToString("0", CultureInfo.InvariantCulture)
                : val.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return App.Core.Recipes.CulinaryMath.FormatNumber(val, isMetric: false);
    }

    private static string FormatUnitDisplay(string? unit, decimal effectiveQty, bool hyphenated)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return string.Empty;
        }

        if (hyphenated || effectiveQty <= 1.05m)
        {
            return unit switch
            {
                "millimeter" => "mm",
                "centimeter" => "cm",
                "milliliter" => "ml",
                "gram" => "g",
                "kilogram" => "kg",
                _ => unit,
            };
        }

        return unit switch
        {
            "inch" => "inches",
            "foot" => "feet",
            "pinch" => "pinches",
            "dash" => "dashes",
            "bunch" => "bunches",
            "box" => "boxes",
            "pouch" => "pouches",
            "leaf" => "leaves",
            "millimeter" => "mm",
            "centimeter" => "cm",
            "milliliter" => "ml",
            "gram" => "g",
            "kilogram" => "kg",
            _ => unit.EndsWith('s') ? unit : unit + "s",
        };
    }

    private static bool IsUnitOrNumberWord(string w) =>
        w
            is "teaspoon"
                or "teaspoons"
                or "tablespoon"
                or "tablespoons"
                or "cup"
                or "cups"
                or "ounce"
                or "ounces"
                or "pound"
                or "pounds"
                or "gram"
                or "grams"
                or "inch"
                or "inches"
                or "degree"
                or "degrees"
                or "fahrenheit"
                or "celsius"
                or "minute"
                or "minutes"
                or "second"
                or "seconds"
                or "hour"
                or "hours"
                or "between"
                or "about";

    private static bool IsTempOrTimeTextWord(string w) =>
        w is "half" or "quarter" or "third" or "third" or "one" or "two" or "three";

    private static int? GetNullableInt(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static decimal? GetNullableDecimal(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }
        if (v.TryGetValue<decimal>(out var d))
        {
            return d;
        }
        if (v.TryGetValue<double>(out var dbl))
        {
            return (decimal)dbl;
        }
        return null;
    }

    private static string? GetNullableString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool GetBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
