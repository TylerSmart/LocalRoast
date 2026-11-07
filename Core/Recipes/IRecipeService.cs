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
}
