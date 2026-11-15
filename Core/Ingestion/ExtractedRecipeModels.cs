using System.Text.Json.Serialization;
using App.Core.FoodCatalog;

namespace App.Core.Ingestion;

public record CatalogFoodLookupItem(
    Guid Id,
    string Name,
    string NormalizedName,
    bool IsPantryStaple,
    IReadOnlyList<string> NormalizedAliases,
    FoodCategory? Category = null
);

public record CatalogTagLookupItem(Guid Id, string Name, string NormalizedName, string Slug);

public sealed record ExtractedRecipeDto(
    string Title,
    string? Description = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? Servings = null,
    string? YieldDisplay = null,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? PrepTimeMinutes = null,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? CookTimeMinutes = null,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))]
        int? TotalTimeMinutes = null,
    string? Difficulty = null,
    string? SourceName = null,
    string? SourceAuthor = null,
    IReadOnlyList<string>? ProposedTags = null,
    IReadOnlyList<ExtractedIngredientDto>? Ingredients = null,
    IReadOnlyList<ExtractedStepDto>? Steps = null,
    RecipeQualityAuditDto? QualityAudit = null
);

public sealed record ExtractedIngredientDto(
    string RefId,
    string? SectionName = null,
    string CanonicalFoodItem = "",
    string? DisplayName = null,
    string? Category = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? Quantity = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? MaxQuantity = null,
    string? Unit = null,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? AltQuantity = null,
    string? AltUnit = null,
    string? PreparationNotes = null,
    bool IsOptional = false,
    bool IsRecipeReference = false,
    string? ReferencedRecipeTitle = null,
    string RawText = ""
);

public sealed record ExtractedStepDto(
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int StepNumber = 1,
    string? SectionName = null,
    string TemplateText = "",
    IReadOnlyList<StepIngredientTokenDto>? IngredientTokens = null,
    IReadOnlyList<StepTimerTokenDto>? TimerTokens = null,
    IReadOnlyList<StepTemperatureTokenDto>? TemperatureTokens = null
);

public sealed record StepIngredientTokenDto(
    string TokenKey,
    string IngredientRefId,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
        decimal? StepQuantity = null,
    string? StepUnit = null,
    string? FallbackDisplay = null
);

public sealed record StepTimerTokenDto(
    string TokenKey,
    [property: JsonConverter(typeof(FlexibleIntJsonConverter))] int DurationSeconds = 0,
    string? Label = null
);

public sealed record StepTemperatureTokenDto(
    string TokenKey,
    [property: JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))] decimal? Value = null,
    string? BaseUnit = null
);

public sealed record RecipeQualityAuditDto(
    bool IsPlausibleRecipe = true,
    IReadOnlyList<string>? Warnings = null
);

public sealed record RecipeValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings
)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record EntityCandidateOption(Guid Id, string Name, double Score);

public sealed record AmbiguousIngredientCandidate(
    string RefId,
    string CanonicalFoodItem,
    string RawText,
    IReadOnlyList<EntityCandidateOption> Candidates
);

public sealed record AmbiguousTagCandidate(
    string ProposedTag,
    IReadOnlyList<EntityCandidateOption> Candidates
);

public sealed record ResolvedIngredientMatch(
    string RefId,
    Guid? MatchedFoodItemId,
    string? MatchedFoodItemName,
    FoodCategory? MatchedCategory,
    bool IsPantryStaple,
    bool IsNewFoodItem
);

public sealed record ResolvedTagMatch(
    string ProposedTag,
    Guid? MatchedTagId,
    string? MatchedTagName
);

public sealed record EntityMatchResult(
    IReadOnlyDictionary<string, ResolvedIngredientMatch> IngredientMatches,
    IReadOnlyDictionary<string, ResolvedTagMatch> TagMatches,
    IReadOnlyList<AmbiguousIngredientCandidate> AmbiguousIngredients,
    IReadOnlyList<AmbiguousTagCandidate> AmbiguousTags
);

public sealed record LlmEntityDisambiguationContext(
    string RecipeTitle,
    IReadOnlyList<AmbiguousIngredientCandidate> AmbiguousIngredients,
    IReadOnlyList<AmbiguousTagCandidate> AmbiguousTags
);

public sealed record LlmIngredientDisambiguationDecision(string RefId, Guid? MatchedFoodItemId);

public sealed record LlmTagDisambiguationDecision(string ProposedTag, Guid? MatchedTagId);

public sealed record LlmEntityDisambiguationResult(
    IReadOnlyList<LlmIngredientDisambiguationDecision>? IngredientDecisions = null,
    IReadOnlyList<LlmTagDisambiguationDecision>? TagDecisions = null
);
