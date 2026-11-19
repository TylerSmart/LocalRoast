using System.Text.Json.Nodes;

namespace App.Core.Ingestion;

public record LlmVisionImageInput(int PageNumber, string MimeType, string Base64Data);

public record LlmVisionExtractionContext(
    IReadOnlyList<LlmVisionImageInput> Images,
    string? SupplementalHintText = null,
    IReadOnlyList<string>? AvailableTagNames = null,
    IReadOnlyList<string>? AvailableFoodNames = null
);

public record LlmSubstitutionContext(
    string RecipeTitle,
    string? RecipeDescription,
    decimal TargetServings,
    string TargetIngredientName,
    string ScaledAmountDisplay,
    string? PreparationNote,
    IReadOnlyList<string> OtherRecipeIngredients,
    IReadOnlyList<string> RelevantStepInstructions,
    string? DietaryOrPantryNote
);

public record LlmSubstitutionOptionItem(
    string Label,
    string RatioAndAmount,
    string CulinaryNotes,
    string FlavorOrTextureImpact
);

public record LlmSubstitutionPayload(IReadOnlyList<LlmSubstitutionOptionItem>? Substitutions);

public record LlmKitchenQuestionContext(
    string Question,
    string? RecipeTitle,
    string? RecipeDescription,
    IReadOnlyList<string> RecipeIngredients,
    string? IngredientName,
    string? IngredientPreparationNote,
    int? StepNumber,
    string? StepInstruction
);

public record LlmCookbookRecipeCandidate(
    int CatalogIndex,
    Guid RecipeId,
    string Title,
    decimal BaseServings,
    int? TotalTimeMinutes,
    string? Difficulty,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> KeyIngredients,
    decimal? AverageRating,
    int TimesCooked,
    int? DaysSinceLastCooked
);

public record LlmMealPlanQueryAnalysisContext(
    string UserPrompt,
    int RequestedMealCount,
    string DefaultSlotName
);

public record LlmMealPlanSearchBucketItem(
    string? BucketName,
    string? SemanticQuery,
    IReadOnlyList<string>? SuggestedTags,
    int? MaxTotalTimeMinutes,
    int? DesiredCount
);

public record LlmMealPlanQueryAnalysisPayload(
    IReadOnlyList<string>? ExcludedTerms,
    IReadOnlyList<string>? PrioritizeIngredients,
    int? MaxTotalTimeMinutes,
    IReadOnlyList<LlmMealPlanSearchBucketItem>? SearchBuckets
);

public record LlmDraftMealPlanContext(
    DateOnly WeekStartDate,
    string UserPrompt,
    int RequestedMealCount,
    string DefaultSlotName,
    bool PlaceOnDeck,
    IReadOnlyList<string> ExistingWeekScheduleLines,
    IReadOnlyList<LlmCookbookRecipeCandidate> CookbookCatalog,
    IReadOnlyList<string>? BucketSummaries = null,
    IReadOnlyList<string>? PrioritizeIngredients = null,
    IReadOnlyList<string>? UnmatchedBuckets = null
);

public record LlmDraftMealProposalItem(
    int CatalogIndex,
    string? RecipeTitle,
    int? DayOffset,
    string? Slot,
    bool? IsOnDeck,
    decimal? TargetServings,
    string? Rationale
);

public record LlmDraftMealPlanPayload(
    IReadOnlyList<LlmDraftMealProposalItem>? Proposals,
    IReadOnlyList<string>? UnmetCravingNotes = null
);

public interface ILlmExtractionClient
{
    Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
        EffectiveLlmConnectionConfig config,
        int timeoutSeconds = 15,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Executes one structured JSON stage of the multi-stage recipe ingestion pipeline and
    /// deserializes the response into <typeparamref name="T" />.
    /// </summary>
    Task<LlmStructuredStageResult<T>> ExecuteStructuredStageAsync<T>(
        EffectiveLlmConnectionConfig config,
        string stageName,
        IReadOnlyList<LlmStageChatMessage> messages,
        JsonNode? jsonSchema,
        bool? overrideThinking = null,
        int maxTokens = 16384,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Generates L2-normalized embedding vectors for the supplied texts via the configured
    /// OpenAI-compatible <c>/v1/embeddings</c> endpoint.
    /// </summary>
    Task<float[][]> GenerateEmbeddingsAsync(
        EffectiveLlmConnectionConfig config,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Transcribes recipe images verbatim into canonical text with an "Ingredients:" section of
    /// "- " bullets and an "Instructions:" section of numbered paragraphs, or null when no
    /// recipe was found.
    /// </summary>
    Task<string?> TranscribeRecipeImagesAsync(
        EffectiveLlmConnectionConfig config,
        LlmVisionExtractionContext context,
        CancellationToken cancellationToken = default
    );

    Task<LlmSubstitutionPayload> SuggestIngredientSubstitutionsAsync(
        EffectiveLlmConnectionConfig config,
        LlmSubstitutionContext context,
        CancellationToken cancellationToken = default
    );

    Task<string> AnswerKitchenQuestionAsync(
        EffectiveLlmConnectionConfig config,
        LlmKitchenQuestionContext context,
        CancellationToken cancellationToken = default
    );

    Task<LlmMealPlanQueryAnalysisPayload> DecomposeMealPlanPromptAsync(
        EffectiveLlmConnectionConfig config,
        LlmMealPlanQueryAnalysisContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            new LlmMealPlanQueryAnalysisPayload(
                ExcludedTerms: [],
                PrioritizeIngredients: [],
                MaxTotalTimeMinutes: null,
                SearchBuckets:
                [
                    new LlmMealPlanSearchBucketItem(
                        BucketName: "Requested meals",
                        SemanticQuery: context.UserPrompt,
                        SuggestedTags: [],
                        MaxTotalTimeMinutes: null,
                        DesiredCount: context.RequestedMealCount
                    ),
                ]
            )
        );

    Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
        EffectiveLlmConnectionConfig config,
        LlmDraftMealPlanContext context,
        CancellationToken cancellationToken = default
    );
}
