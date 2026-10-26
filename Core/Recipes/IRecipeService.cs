namespace App.Core.Recipes;

public interface IRecipeService
{
    Task<IReadOnlyList<RecipeSummaryDto>> GetRecipesAsync(
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
}
