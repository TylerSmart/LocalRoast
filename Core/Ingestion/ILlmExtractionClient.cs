namespace App.Core.Ingestion;

public record LlmExtractionContext(
    string SourceContent,
    IReadOnlyList<string> AvailableTagNames,
    IReadOnlyList<string>? AvailableFoodNames = null
);

public record LlmVisionImageInput(int PageNumber, string MimeType, string Base64Data);

public record LlmVisionExtractionContext(
    IReadOnlyList<LlmVisionImageInput> Images,
    string? SupplementalHintText,
    IReadOnlyList<string> AvailableTagNames,
    IReadOnlyList<string>? AvailableFoodNames = null
);

public record LlmRecipeRepairContext(
    string? SourceContent,
    IReadOnlyList<LlmVisionImageInput> Images,
    LlmExtractedRecipePayload Draft,
    string? UserHint = null
);

public record LlmRecipeRepairResult(LlmExtractedRecipePayload Draft, IReadOnlyList<string> Notices);

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

public record LlmDraftMealPlanContext(
    DateOnly WeekStartDate,
    string UserPrompt,
    int RequestedMealCount,
    string DefaultSlotName,
    bool PlaceOnDeck,
    IReadOnlyList<string> ExistingWeekScheduleLines,
    IReadOnlyList<LlmCookbookRecipeCandidate> CookbookCatalog
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

public record LlmDraftMealPlanPayload(IReadOnlyList<LlmDraftMealProposalItem>? Proposals);

public interface ILlmExtractionClient
{
    Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
        EffectiveLlmConnectionConfig config,
        int timeoutSeconds = 15,
        CancellationToken cancellationToken = default
    );

    Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
        EffectiveLlmConnectionConfig config,
        LlmExtractionContext context,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Asks the text model to locate the recipe sections in an unstructured source and returns
    /// a canonical Ingredients/Instructions copy built only from verbatim source lines, or null
    /// when the sections could not be identified.
    /// </summary>
    Task<string?> StructureRecipeSourceAsync(
        EffectiveLlmConnectionConfig config,
        string sourceText,
        CancellationToken cancellationToken = default
    );

    Task<LlmExtractedRecipePayload> ExtractRecipeFromImagesAsync(
        EffectiveLlmConnectionConfig config,
        LlmVisionExtractionContext context,
        CancellationToken cancellationToken = default
    );

    Task<LlmRecipeRepairResult> RepairRecipeDraftAsync(
        EffectiveLlmConnectionConfig config,
        LlmRecipeRepairContext context,
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

    Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
        EffectiveLlmConnectionConfig config,
        LlmDraftMealPlanContext context,
        CancellationToken cancellationToken = default
    );
}
