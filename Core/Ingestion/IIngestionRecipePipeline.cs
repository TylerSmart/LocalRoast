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

public sealed record CatalogRecipeLookupItem(Guid Id, string Title, string NormalizedTitle);

public sealed record Stage3IngredientCandidateDto(
    Guid Id,
    string Name,
    FoodCategory Category,
    bool IsPantryStaple,
    double Score,
    string MatchSource
);

public sealed record Stage3RecipeCandidateDto(
    Guid Id,
    string Title,
    double Score,
    string MatchSource
);

public sealed record Stage3SearchQueryItem(int Key, string Name, string? PrepNote, string? Unit);

public sealed record Stage3ItemCandidatesDto(
    IReadOnlyList<Stage3IngredientCandidateDto> FoodCandidates,
    IReadOnlyList<Stage3RecipeCandidateDto> RecipeCandidates
);

public sealed record Stage3CatalogSearchResult(
    IReadOnlyDictionary<int, Stage3ItemCandidatesDto> CandidatesByKey,
    bool UsedEmbeddings
);

public interface ICatalogVectorSearchService
{
    Task<Stage3CatalogSearchResult> SearchAsync(
        EffectiveLlmConnectionConfig config,
        IReadOnlyList<Stage3SearchQueryItem> queryItems,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogRecipeLookupItem> catalogRecipes,
        CancellationToken cancellationToken = default
    );
}

public sealed record IngestionPipelineRequest(
    string SourceText,
    EffectiveLlmConnectionConfig Config,
    Guid? ExcludeRecipeId = null,
    string? SourceUrl = null,
    string? SourceName = null,
    string? SourceAuthor = null,
    string? SourceBookTitle = null,
    int? SourceBookPage = null,
    string? CoverImagePath = null,
    string? ThumbnailImagePath = null,
    bool RetainSourceAttachments = false,
    IReadOnlyList<Guid>? SuggestedCollectionIds = null,
    Func<string, Task>? OnStageProgress = null
);

public sealed record IngestionPipelineResult(
    IngestionExtractedDraftDto Draft,
    bool IsPlausibleRecipe,
    string ExtractedModel
);

public interface IIngestionRecipePipeline
{
    Task<IngestionPipelineResult> RunAsync(
        IngestionPipelineRequest request,
        CancellationToken cancellationToken = default
    );
}
