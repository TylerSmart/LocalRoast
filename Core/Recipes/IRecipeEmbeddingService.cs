using App.Core.Ingestion;

namespace App.Core.Recipes;

public record MealPlanSearchBucketInput(
    string BucketName,
    string SemanticQuery,
    IReadOnlyList<string> SuggestedTags,
    int? MaxTotalTimeMinutes,
    int DesiredCount
);

public record MealPlanRetrievalRequest(
    string RawUserPrompt,
    int RequestedMealCount,
    IReadOnlyList<Guid> ExcludedRecipeIds,
    IReadOnlyList<Guid> RecentlyScheduledRecipeIds,
    IReadOnlyList<string> ExcludedTerms,
    IReadOnlyList<string> PrioritizeIngredients,
    int? GlobalMaxTotalTimeMinutes,
    IReadOnlyList<MealPlanSearchBucketInput> SearchBuckets,
    DateTimeOffset UtcNow
);

public record MealPlanRetrievalResult(
    int TotalEligiblePublishedCount,
    int PostExclusionEligibleCount,
    bool UsedEmbeddings,
    IReadOnlyList<LlmCookbookRecipeCandidate> Candidates,
    IReadOnlyList<string> UnmatchedBuckets
);

public interface IRecipeEmbeddingService
{
    /// <summary>
    /// Computes and upserts semantic recipe embeddings for the specified published recipe IDs
    /// (skipping rows whose <c>ContentHash</c> is already current and pruning any non-published IDs).
    /// </summary>
    Task<int> UpsertRecipeEmbeddingsAsync(
        EffectiveLlmConnectionConfig embeddingConfig,
        IReadOnlyCollection<Guid> recipeIds,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Finds up to <paramref name="maxBatchCount" /> published recipes missing a fresh embedding
    /// for the configured Embedding workflow model and generates/persists them in batch.
    /// Returns the number of recipes embedded (0 when up to date or no embedding model is configured).
    /// </summary>
    Task<int> SyncPendingEmbeddingsAsync(
        EffectiveLlmConnectionConfig config,
        int maxBatchCount = 64,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Executes Stage 2 of the meal planning RAG pipeline: hard SQL/catalog exclusions, hybrid
    /// vector + lexical bucket scoring, prioritized ingredient boosts, and perishable synergy.
    /// </summary>
    Task<MealPlanRetrievalResult> RetrieveMealPlanCandidatesAsync(
        EffectiveLlmConnectionConfig config,
        MealPlanRetrievalRequest request,
        CancellationToken cancellationToken = default
    );
}
