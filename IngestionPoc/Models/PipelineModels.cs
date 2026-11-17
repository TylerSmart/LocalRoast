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
        @"^\s*(?:to\s+)?(?:seconds?|minutes?|hours?|days?|weeks?|months?|years?|secs?|mins?|hrs?|wks?|mos?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTimeUnitPrefixRegex();

    [GeneratedRegex(@"^\s*\d+\.\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadStepNumberRegex();

    [GeneratedRegex(@"\b(?:each|both)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DistributiveQuantifierRegex();

    [GeneratedRegex(
        @"\b(?:between\s+)?(?:\d+(?:\s+\d+/\d+|\.\d+|/\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|twenty|thirty|forty|fifty|sixty|half|halves|third|thirds|quarter|quarters|dozen|couple)(?:\s*-?\s*(?:to|or|and|–|-)\s*(?:\d+(?:\s+\d+/\d+|\.\d+|/\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|twenty|thirty|forty|fifty|sixty|half|halves|third|thirds|quarter|quarters|dozen|couple))?\b|\b(?:a|an)\s+(?:[a-z]+\s+)?(?:pinch|dash|smidgen|drop|handful|splash|drizzle|second|minute|hour|day|inch|foot|cup|clove|can|sprig|slice|sheet|bunch|leaf|stalk|stick)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ExplicitQuantityCueRegex();

    [GeneratedRegex(@"[a-zA-Z]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex NormalizedWordRegex();

    [GeneratedRegex(@"^\s+([a-zA-Z]{2,}(?:\s+[a-zA-Z]{2,}){0,3})", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingWordsPrefixRegex();

    [GeneratedRegex(
        @"\b\d+(?:\s+\d+/\d+|\.\d+|/\d+)?\s+by\s+\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex MultiAxisByDimensionRegex();

    [GeneratedRegex(@"\bby\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordByRegex();

    [GeneratedRegex(
        @"\b(one|two|three|four|five|six|seven|eight|nine|ten|half)\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TrailingSpelledNumberRegex();

    [GeneratedRegex(
        @"\b(?:rotate|rotated|rotating)\b[^.\n]{0,80}(?:90|180)\s+degrees?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RotationAngleRegex();

    private static readonly (string CanonicalUnit, Regex Pattern)[] UnitMeasurementPatterns =
    [
        (
            "teaspoon",
            new Regex(
                @"(?:\b|\d)(?:teaspoons?|tsps?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "tablespoon",
            new Regex(
                @"(?:\b|\d)(?:tablespoons?|tbsps?\.?|tbs\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "fluid ounce",
            new Regex(
                @"(?:\b|\d)fl(?:uid)?\s+oz\.?\b|\bfluid\s+ounces?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "cup",
            new Regex(@"\b(?:cups?|c\.)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "pint",
            new Regex(
                @"(?:\b|\d)(?:pints?|pts?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "quart",
            new Regex(
                @"(?:\b|\d)(?:quarts?|qts?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "gallon",
            new Regex(
                @"(?:\b|\d)(?:gallons?|gals?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "ounce",
            new Regex(
                @"(?:\b|\d)(?:ounces?|ozs?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "pound",
            new Regex(
                @"(?:\b|\d)(?:pounds?|lbs?\.?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "milligram",
            new Regex(
                @"(?:\b|\d)(?:milligrams?|mg)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "gram",
            new Regex(
                @"(?:\b|\d)(?:grams?|g)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "kilogram",
            new Regex(
                @"(?:\b|\d)(?:kilograms?|kgs?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "milliliter",
            new Regex(
                @"(?:\b|\d)(?:milliliters?|millilitres?|ml)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "liter",
            new Regex(
                @"\b(?:liters?|litres?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            "clove",
            new Regex(@"\bcloves?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        ("can", new Regex(@"\bcans?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (
            "pinch",
            new Regex(@"\bpinch(?:es)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "dash",
            new Regex(@"\bdash(?:es)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "stick",
            new Regex(@"\bsticks?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "sprig",
            new Regex(@"\bsprigs?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "slice",
            new Regex(@"\bslices?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            "bunch",
            new Regex(@"\bbunch(?:es)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        ("head", new Regex(@"\bheads?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    [GeneratedRegex(
        @"^\s*-?\s*(?:inch(?:es)?|feet|foot|yards?|millimeters?|centimeters?|meters?|mm\b|cm\b)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateDimUnitPrefixRegex();

    [GeneratedRegex(
        @"^\s+-(?:wide|thick|long|deep|diameter|round|square)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LeadingSpaceHyphenSuffixRegex();

    [GeneratedRegex(@"\bup to\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingUpToRegex();

    [GeneratedRegex(
        @"\b([a-zA-Z]{3,}(?:\s+[a-zA-Z]{2,}){0,3})\s+\1\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RepeatedAdjacentWordRegex();

    [GeneratedRegex(
        @"-?\s*inch(?:es)?-(wide|thick|long|deep|diameter)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DimensionQualifierSuffixRegex();

    [GeneratedRegex(
        @"^\s*(?:°\s*[FC]|degrees?(?:\s+(?:F|C|Fahrenheit|Celsius))?\b|fahrenheit\b|celsius\b|[FC]\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DuplicateTempUnitPrefixRegex();

    public static JsonArray SanitizeRawTokens(JsonArray tokensArray)
    {
        var sanitized = (JsonArray)tokensArray.DeepClone();
        for (var i = 0; i + 1 < sanitized.Count; i++)
        {
            if (
                sanitized[i] is not JsonObject tokObj
                || sanitized[i + 1] is not JsonValue textVal
                || !textVal.TryGetValue<string>(out var nextText)
            )
            {
                continue;
            }

            var tokType = tokObj["type"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            if (tokType == "timer")
            {
                var unit = (GetNullableString(tokObj["unit"]) ?? "minute")
                    .Trim()
                    .ToLowerInvariant();
                var match = DuplicateTimeUnitPrefixRegex().Match(nextText);
                if (
                    match.Success
                    && TimerUnitMatchesWord(unit, match.Value.Trim().ToLowerInvariant())
                )
                {
                    sanitized[i + 1] = JsonValue.Create(nextText[match.Length..]);
                }
            }
            else if (tokType == "temperature" && !nextText.TrimStart().StartsWith('-'))
            {
                var unit = (GetNullableString(tokObj["unit"]) ?? "fahrenheit")
                    .Trim()
                    .ToLowerInvariant();
                var match = DuplicateTempUnitPrefixRegex().Match(nextText);
                if (
                    match.Success
                    && TempUnitMatchesWord(unit, match.Value.Trim().ToLowerInvariant())
                )
                {
                    sanitized[i + 1] = JsonValue.Create(nextText[match.Length..]);
                }
            }
        }

        return sanitized;
    }

    private static bool TempUnitMatchesWord(string unit, string phrase)
    {
        var compact = phrase
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        if (compact is "degree" or "degrees")
        {
            return true;
        }

        return unit switch
        {
            "fahrenheit" => compact
                is "°f"
                    or "f"
                    or "fahrenheit"
                    or "degreef"
                    or "degreesf"
                    or "degreefahrenheit"
                    or "degreesfahrenheit",
            "celsius" => compact
                is "°c"
                    or "c"
                    or "celsius"
                    or "degreec"
                    or "degreesc"
                    or "degreecelsius"
                    or "degreescelsius",
            _ => false,
        };
    }

    private static bool TimerUnitMatchesWord(string unit, string word) =>
        unit switch
        {
            "second" or "seconds" => word is "second" or "seconds" or "sec" or "secs",
            "minute" or "minutes" => word is "minute" or "minutes" or "min" or "mins",
            "hour" or "hours" => word is "hour" or "hours" or "hr" or "hrs",
            "day" or "days" => word is "day" or "days",
            _ => string.Equals(unit, word, StringComparison.OrdinalIgnoreCase),
        };

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

        var origTrimmed = originalInstruction.TrimEnd();
        var rendTrimmed = rendered.TrimEnd();
        if (
            origTrimmed.EndsWith(".)", StringComparison.Ordinal)
            && !rendTrimmed.EndsWith(".)", StringComparison.Ordinal)
        )
        {
            warnings.Add(
                "Rendered step swapped or dropped trailing '.)' closing punctuation (source ends with '.)', not ').')."
            );
        }
        else if (origTrimmed.EndsWith('.') && !rendTrimmed.EndsWith('.'))
        {
            warnings.Add("Rendered step dropped trailing period '.'.");
        }

        var origClean = LeadStepNumberRegex().Replace(originalInstruction, string.Empty);
        var hasDistributive = DistributiveQuantifierRegex().IsMatch(origClean);
        var origCueCount = ExplicitQuantityCueRegex().Matches(origClean).Count;
        var quantifiedTokenCount = tokens.Count(t =>
            t
                is PocIngredientToken
                    or PocMixtureToken
                    or PocFixedToken
                    or PocTimerToken
                    or PocDimensionToken
                    or PocTemperatureToken
        );
        if (!hasDistributive && quantifiedTokenCount > origCueCount)
        {
            warnings.Add(
                $"Invented unprinted quantities: step emitted {quantifiedTokenCount} quantitative tokens but original instruction only contains {origCueCount} explicit numeric cues; leave unquantified mentions as plain text without pulling numbers from the ingredient list."
            );
        }

        foreach (var (unitName, unitPattern) in UnitMeasurementPatterns)
        {
            var tokenUnitCount = tokens.Count(t =>
                t switch
                {
                    PocIngredientToken ing => string.Equals(
                        ing.Unit?.Trim(),
                        unitName,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    PocMixtureToken mix => string.Equals(
                        mix.Unit?.Trim(),
                        unitName,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    PocFixedToken fx => string.Equals(
                        fx.Unit?.Trim(),
                        unitName,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    _ => false,
                }
            );
            if (tokenUnitCount == 0)
            {
                continue;
            }

            var origUnitCount = unitPattern.Matches(origClean).Count;
            if (tokenUnitCount > origUnitCount && (origUnitCount == 0 || !hasDistributive))
            {
                warnings.Add(
                    $"Invented unit '{unitName}' ({tokenUnitCount} token(s) vs {origUnitCount} in original instruction); unquantified mentions must stay plain text."
                );
            }
        }

        var origNormWordCounts = CountNormalizedWords(originalInstruction);
        var renderedNormWordCounts = CountNormalizedWords(rendered);

        if (
            MultiAxisByDimensionRegex().IsMatch(originalInstruction)
            && WordByRegex().Matches(originalInstruction).Count
                > WordByRegex().Matches(rendered).Count
        )
        {
            warnings.Add(
                "Dropped 'by' between multi-axis dimensions ('A by B'); emit TWO separate dimension tokens separated by ' by ' instead of collapsing into a 'to' range."
            );
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            decimal? tokenPrimaryVal = tokens[i] switch
            {
                PocIngredientToken ing => ing.Quantity,
                PocMixtureToken mix => mix.Quantity,
                PocFixedToken fx => fx.Amount,
                PocTimerToken tm => tm.Amount,
                PocDimensionToken dm => dm.Amount,
                _ => null,
            };
            if (
                i > 0
                && tokens[i - 1] is PocTextToken prevSpelledTxt
                && tokenPrimaryVal.HasValue
                && TryMatchSpelledNumber(
                    prevSpelledTxt.Text,
                    tokenPrimaryVal.Value,
                    out var wordNum
                )
            )
            {
                warnings.Add(
                    $"Duplicate spelled number '{wordNum}' right before [{i}]; consume '{wordNum}' inside the token so output does not read '{wordNum} {tokenPrimaryVal.Value}'."
                );
            }
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
                    foreach (Match untok in UntokenizedQuantityRegex().Matches(txt.Text))
                    {
                        var isRotationAngle = false;
                        foreach (Match rot in RotationAngleRegex().Matches(txt.Text))
                        {
                            if (
                                untok.Index >= rot.Index
                                && untok.Index + untok.Length <= rot.Index + rot.Length
                            )
                            {
                                isRotationAngle = true;
                                break;
                            }
                        }

                        if (!isRotationAngle)
                        {
                            warnings.Add(
                                $"Untokenized amount/unit '{untok.Value}' left in plain text segment."
                            );
                            break;
                        }
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
                    else if (ing.Quantity.Value <= 0m)
                    {
                        warnings.Add(
                            $"Ingredient token '{ing.Name}' has invalid quantity={ing.Quantity.Value}; quantity must be > 0 (for 'up to X', set quantity=X and maxQuantity=null)."
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
                        i > 0
                        && tokens[i - 1] is PocTextToken prevIngTxt
                        && TrailingUpToRegex().IsMatch(prevIngTxt.Text)
                        && ing.Quantity.HasValue
                        && ing.MaxQuantity.HasValue
                        && ing.MaxQuantity.Value > ing.Quantity.Value
                    )
                    {
                        warnings.Add(
                            $"Preceding text ends with 'up to', so set quantity={ing.MaxQuantity.Value} and maxQuantity=null instead of fabricated range {ing.Quantity.Value}..{ing.MaxQuantity.Value}."
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
                    CheckUnitRepeatedInName(ing.Unit, ing.Name, warnings);
                    CheckBoundaryWordDuplication(
                        i,
                        ing.Name,
                        tokens,
                        origNormWordCounts,
                        renderedNormWordCounts,
                        warnings
                    );
                    break;
                case PocMixtureToken mix:
                    if (mix.Quantity <= 0m)
                    {
                        warnings.Add(
                            $"Mixture token '{mix.Name}' has invalid quantity={mix.Quantity}; quantity must be > 0."
                        );
                    }
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
                    CheckUnitRepeatedInName(mix.Unit, mix.Name, warnings);
                    CheckBoundaryWordDuplication(
                        i,
                        mix.Name,
                        tokens,
                        origNormWordCounts,
                        renderedNormWordCounts,
                        warnings
                    );
                    break;
                case PocFixedToken fx:
                    if (fx.Amount <= 0m)
                    {
                        warnings.Add(
                            $"Fixed token '{fx.Name}' has invalid amount={fx.Amount}; amount must be > 0."
                        );
                    }
                    if (fx.MaxAmount.HasValue && fx.MaxAmount.Value < fx.Amount)
                    {
                        warnings.Add(
                            $"Fixed token '{fx.Name}' has inverted range {fx.Amount}..{fx.MaxAmount} {fx.Unit}."
                        );
                    }
                    CheckUnitRepeatedInName(fx.Unit, fx.Name, warnings);
                    CheckBoundaryWordDuplication(
                        i,
                        fx.Name,
                        tokens,
                        origNormWordCounts,
                        renderedNormWordCounts,
                        warnings
                    );
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
                        i > 0
                        && tokens[i - 1] is PocTextToken prevTimerTxt
                        && TrailingUpToRegex().IsMatch(prevTimerTxt.Text)
                        && timer.MaxAmount.HasValue
                        && timer.MaxAmount.Value > timer.Amount
                    )
                    {
                        warnings.Add(
                            $"Preceding text ends with 'up to', so set amount={timer.MaxAmount.Value} and maxAmount=null instead of fabricated range {timer.Amount}..{timer.MaxAmount.Value}."
                        );
                    }
                    if (
                        i + 1 < tokens.Count
                        && tokens[i + 1] is PocTextToken trailingTimerTxt
                        && DuplicateTimeUnitPrefixRegex().IsMatch(trailingTimerTxt.Text)
                    )
                    {
                        warnings.Add(
                            $"Duplicate or leftover time unit '{DuplicateTimeUnitPrefixRegex().Match(trailingTimerTxt.Text).Value.Trim()}' after timer token [{i}] (if 'to minutes'/'to hours', absorb into maxAmount and remove trailing fragment; if source says 'weeks'/'months', delete the timer token and merge into one plain text phrase without repeating 'weeks')."
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
                        i > 0
                        && tokens[i - 1] is PocTextToken prevDimTxt
                        && TrailingUpToRegex().IsMatch(prevDimTxt.Text)
                        && dim.MaxAmount.HasValue
                        && dim.MaxAmount.Value > dim.Amount
                    )
                    {
                        warnings.Add(
                            $"Preceding text ends with 'up to', so set amount={dim.MaxAmount.Value} and maxAmount=null instead of fabricated range {dim.Amount}..{dim.MaxAmount.Value}."
                        );
                    }
                    if (dim.Hyphenated)
                    {
                        var checkVal = dim.MaxAmount ?? dim.Amount;
                        var isMetricDim =
                            dim.Unit.Trim().ToLowerInvariant()
                            is "millimeter"
                                or "centimeter"
                                or "meter";
                        var checkStr = FormatNum(checkVal, isMetricDim);
                        var unitPat = GetDimensionUnitPattern(dim.Unit);
                        var esc = Regex.Escape(checkStr);
                        if (
                            Regex.IsMatch(
                                originalInstruction,
                                $@"(?:^|\W){esc}\s+{unitPat}",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                            )
                            && !Regex.IsMatch(
                                originalInstruction,
                                $@"(?:^|\W){esc}(?:\s+to\s+[^-]+)?-",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                            )
                        )
                        {
                            warnings.Add(
                                $"Dimension token [{i}] set hyphenated=true, but source '{checkStr} {dim.Unit}' is unhyphenated; set hyphenated=false."
                            );
                        }
                    }
                    if (i + 1 < tokens.Count && tokens[i + 1] is PocTextToken trailingDimTxt)
                    {
                        if (DuplicateDimUnitPrefixRegex().IsMatch(trailingDimTxt.Text))
                        {
                            warnings.Add(
                                $"Duplicate dimension unit '{DuplicateDimUnitPrefixRegex().Match(trailingDimTxt.Text).Value.Trim()}' after dimension token [{i}] (hyphenated dimension already renders '-inch')."
                            );
                        }
                        else if (
                            dim.Hyphenated
                            && LeadingSpaceHyphenSuffixRegex().IsMatch(trailingDimTxt.Text)
                        )
                        {
                            warnings.Add(
                                $"Extra space before hyphenated dimension suffix '{trailingDimTxt.Text.TrimStart().Split(' ')[0]}' after dimension token [{i}]; attach '-wide'/'-thick' with no leading space."
                            );
                        }
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

        foreach (Match dup in RepeatedAdjacentWordRegex().Matches(rendered))
        {
            var word = dup.Groups[1].Value;
            if (
                !word.Equals("that", StringComparison.OrdinalIgnoreCase)
                && !word.Equals("had", StringComparison.OrdinalIgnoreCase)
            )
            {
                warnings.Add(
                    $"Duplicate adjacent phrase '{dup.Value}' in rendered step; consume unit/noun cleanly inside token."
                );
            }
        }

        foreach (Match qual in DimensionQualifierSuffixRegex().Matches(originalInstruction))
        {
            var suffix = qual.Groups[1].Value;
            if (
                !Regex.IsMatch(
                    rendered,
                    $@"-{Regex.Escape(suffix)}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                )
            )
            {
                warnings.Add(
                    $"Dropped dimension qualifier '-{suffix}' from rendered step; preserve '-{suffix}' right after the hyphenated dimension token."
                );
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

    public static bool TryFindIncompatibleAlternateMeasurement(
        decimal? primaryQty,
        decimal? primaryMaxQty,
        string? primaryUnit,
        IReadOnlyList<Stage1AlternateMeasurementDto>? alternates,
        out string? reason
    )
    {
        reason = null;
        if (
            !primaryQty.HasValue
            || primaryQty.Value <= 0m
            || alternates is not { Count: > 0 }
            || !TryGetVolumeOrWeightFactor(primaryUnit, out var primaryDim, out var primaryFactor)
        )
        {
            return false;
        }

        var minPrimaryBase = primaryQty.Value * primaryFactor;
        var maxPrimaryBase = (primaryMaxQty ?? primaryQty.Value) * primaryFactor;
        foreach (var alt in alternates)
        {
            if (
                alt.Quantity <= 0m
                || !TryGetVolumeOrWeightFactor(alt.Unit, out var altDim, out var altFactor)
                || !string.Equals(altDim, primaryDim, StringComparison.Ordinal)
            )
            {
                continue;
            }

            var altBase = alt.Quantity * altFactor;
            if (altBase < minPrimaryBase * 0.70m || altBase > maxPrimaryBase * 1.45m)
            {
                reason =
                    $"{alt.Quantity} {alt.Unit} is not equivalent to {primaryQty.Value} {primaryUnit} (sum 'A plus B' ingredients into primary quantity instead of splitting into alternateMeasurements).";
                return true;
            }
        }

        return false;
    }

    private static bool TryGetVolumeOrWeightFactor(
        string? rawUnit,
        out string dimension,
        out decimal baseFactor
    )
    {
        switch (rawUnit?.Trim().ToLowerInvariant())
        {
            case "drop" or "drops":
                dimension = "volume";
                baseFactor = 0.05m;
                return true;
            case "smidgen" or "smidgens":
                dimension = "volume";
                baseFactor = 0.15m;
                return true;
            case "pinch" or "pinches":
                dimension = "volume";
                baseFactor = 0.308m;
                return true;
            case "dash" or "dashes":
                dimension = "volume";
                baseFactor = 0.616m;
                return true;
            case "teaspoon" or "teaspoons":
                dimension = "volume";
                baseFactor = 4.92892m;
                return true;
            case "tablespoon" or "tablespoons":
                dimension = "volume";
                baseFactor = 14.7868m;
                return true;
            case "fluid ounce" or "fluid ounces":
                dimension = "volume";
                baseFactor = 29.5735m;
                return true;
            case "cup" or "cups":
                dimension = "volume";
                baseFactor = 236.588m;
                return true;
            case "pint" or "pints":
                dimension = "volume";
                baseFactor = 473.176m;
                return true;
            case "quart" or "quarts":
                dimension = "volume";
                baseFactor = 946.353m;
                return true;
            case "gallon" or "gallons":
                dimension = "volume";
                baseFactor = 3785.41m;
                return true;
            case "milliliter" or "milliliters":
                dimension = "volume";
                baseFactor = 1m;
                return true;
            case "centiliter" or "centiliters":
                dimension = "volume";
                baseFactor = 10m;
                return true;
            case "deciliter" or "deciliters":
                dimension = "volume";
                baseFactor = 100m;
                return true;
            case "liter" or "liters":
                dimension = "volume";
                baseFactor = 1000m;
                return true;
            case "milligram" or "milligrams":
                dimension = "weight";
                baseFactor = 0.001m;
                return true;
            case "gram" or "grams":
                dimension = "weight";
                baseFactor = 1m;
                return true;
            case "kilogram" or "kilograms":
                dimension = "weight";
                baseFactor = 1000m;
                return true;
            case "ounce" or "ounces":
                dimension = "weight";
                baseFactor = 28.3495m;
                return true;
            case "pound" or "pounds":
                dimension = "weight";
                baseFactor = 453.592m;
                return true;
            default:
                dimension = string.Empty;
                baseFactor = 0m;
                return false;
        }
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
            case "tablespoon":
            case "fluid ounce":
            case "cup":
            case "pint":
            case "quart":
            case "gallon":
            case "ounce":
            case "pound":
                if (TryGetVolumeOrWeightFactor(u, out var dim, out var factor))
                {
                    return dim == "volume"
                        ? ConvertMl(q * factor, Scale(factor))
                        : ConvertGrams(q * factor, Scale(factor));
                }
                return (q, m, u, false);
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

    private static void CheckUnitRepeatedInName(string? unit, string name, List<string> warnings)
    {
        var u = unit?.Trim();
        if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (
            Regex.IsMatch(
                name,
                $@"\b{Regex.Escape(u)}(?:e?s)?\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            warnings.Add(
                $"Token '{name}' repeats unit '{u}' inside its name (renders '{u} {name}'); keep unit ONLY in `unit` or set unit=null."
            );
        }
    }

    private static void CheckBoundaryWordDuplication(
        int tokenIndex,
        string tokenName,
        IReadOnlyList<PocStepToken> tokens,
        Dictionary<string, int> origWordCounts,
        Dictionary<string, int> renderedWordCounts,
        List<string> warnings
    )
    {
        if (
            tokenIndex + 1 >= tokens.Count
            || tokens[tokenIndex + 1] is not PocTextToken trailingTxt
            || string.IsNullOrWhiteSpace(tokenName)
        )
        {
            return;
        }

        var prefixMatch = TrailingWordsPrefixRegex().Match(trailingTxt.Text);
        if (!prefixMatch.Success)
        {
            return;
        }

        var tokenWords = NormalizedWordRegex()
            .Matches(tokenName)
            .Select(m => SimpleSingular(m.Value))
            .ToList();
        if (tokenWords.Count == 0)
        {
            return;
        }

        var lastTokWord = tokenWords[^1];
        if (
            origWordCounts.GetValueOrDefault(lastTokWord)
            >= renderedWordCounts.GetValueOrDefault(lastTokWord)
        )
        {
            return;
        }

        foreach (Match wMatch in NormalizedWordRegex().Matches(prefixMatch.Groups[1].Value))
        {
            if (SimpleSingular(wMatch.Value) == lastTokWord)
            {
                warnings.Add(
                    $"Duplicate/split noun '{lastTokWord}' after token [{tokenIndex}] ('{tokenName}' followed by '{prefixMatch.Groups[1].Value}'); remove the trailing duplicate or absorb leading qualifier like 'more {lastTokWord}' INTO token.name (do not leave ' more {lastTokWord}' or ' of the {lastTokWord}' after the token)."
                );
                return;
            }
        }
    }

    private static Dictionary<string, int> CountNormalizedWords(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in NormalizedWordRegex().Matches(text))
        {
            var s = SimpleSingular(m.Value);
            counts[s] = counts.GetValueOrDefault(s) + 1;
        }
        return counts;
    }

    private static string SimpleSingular(string word)
    {
        var lower = word.ToLowerInvariant();
        return lower switch
        {
            "leaves" => "leaf",
            "inches" => "inch",
            "feet" => "foot",
            _ when lower.EndsWith("ies", StringComparison.Ordinal) && lower.Length > 4 =>
                string.Concat(lower.AsSpan(0, lower.Length - 3), "y"),
            _ when lower.EndsWith('s')
                    && lower.Length > 3
                    && !lower.EndsWith("ss", StringComparison.Ordinal) => lower[..^1],
            _ => lower,
        };
    }

    private static bool TryMatchSpelledNumber(string prevText, decimal val, out string word)
    {
        word = string.Empty;
        var m = TrailingSpelledNumberRegex().Match(prevText);
        if (!m.Success)
        {
            return false;
        }

        word = m.Groups[1].Value;
        decimal? spelledVal = word.ToLowerInvariant() switch
        {
            "half" => 0.5m,
            "one" => 1m,
            "two" => 2m,
            "three" => 3m,
            "four" => 4m,
            "five" => 5m,
            "six" => 6m,
            "seven" => 7m,
            "eight" => 8m,
            "nine" => 9m,
            "ten" => 10m,
            _ => null,
        };
        return spelledVal.HasValue && spelledVal.Value == val;
    }

    private static string GetDimensionUnitPattern(string unit) =>
        unit.Trim().ToLowerInvariant() switch
        {
            "inch" => @"inch(?:es)?\b",
            "foot" => @"(?:feet|foot)\b",
            "yard" => @"yards?\b",
            "millimeter" => @"(?:millimeters?|millimetres?|mm)\b",
            "centimeter" => @"(?:centimeters?|centimetres?|cm)\b",
            "meter" => @"(?:meters?|metres?)\b",
            _ => Regex.Escape(unit.Trim()) + @"\b",
        };

    private static string? GetNullableString(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
        {
            return null;
        }

        var trimmed = s.Trim();
        return
            trimmed.Length == 0
            || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            ? null
            : s;
    }

    private static bool GetBool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
