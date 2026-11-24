namespace App.Core.Recipes;

public interface IRecipeService
{
    Task<IReadOnlyList<RecipeSummaryDto>> GetRecipesAsync(
        RecipeQueryParameters query,
        CancellationToken cancellationToken = default
    );

    Task<RecipePagedResultDto> GetRecipesPagedAsync(
        RecipeQueryParameters query,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<RecipeAutocompleteItemDto>> SearchRecipeAutocompleteAsync(
        string? query = null,
        Guid? excludeRecipeId = null,
        int limit = 20,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<RecipeDetailDto>> GetRandomRecipesAsync(
        RandomRecipeQueryParameters query,
        CancellationToken cancellationToken = default
    );

    Task<RecipeDetailDto?> GetRecipeByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<RecipeDetailDto> CreateRecipeAsync(
        UpsertRecipeRequest request,
        CancellationToken cancellationToken = default
    );

    Task<RecipeDetailDto?> UpdateRecipeAsync(
        Guid id,
        UpsertRecipeRequest request,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteRecipeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RecipeDetailDto?> UpdateRecipeOrganizationAsync(
        Guid id,
        UpdateRecipeOrganizationRequest request,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<IngredientMatchResultDto>> MatchRecipesByIngredientsAsync(
        IngredientMatchRequest request,
        CancellationToken cancellationToken = default
    );

    Task<RecipeAttachmentDto?> AddRecipeAttachmentAsync(
        Guid recipeId,
        Stream content,
        string originalFileName,
        string? contentType,
        long contentLength,
        string? label = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Finds the measured amounts in a step instruction without persisting anything.</summary>
    IReadOnlyList<DetectedStepAmountDto> DetectStepAmounts(string instruction);

    Task<bool> DeleteRecipeAttachmentAsync(
        Guid recipeId,
        Guid attachmentId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Queues an immediate Stage 6 refinement run for a recipe (ingested or manually created),
    /// running even while background ingestion is paused or idle refinement is disabled.
    /// </summary>
    Task<RecipeDetailDto?> QueueRefinementNowAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    /// <summary>Clears a failed refinement status banner on a recipe.</summary>
    Task<RecipeDetailDto?> DismissRefinementFailureAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );
}
