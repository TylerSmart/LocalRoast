using App.Core.Ingestion;
using App.Core.Recipes;

namespace App.Core.Mcp;

public interface IMcpCookbookService
{
    Task<IReadOnlyList<RecipeSummaryDto>> SearchRecipesAsync(
        McpRecipeSearchRequest request,
        CancellationToken cancellationToken = default
    );

    Task<McpRecipeDetailDto?> GetRecipeAsync(
        Guid? recipeId = null,
        string? slug = null,
        CancellationToken cancellationToken = default
    );

    Task<McpIngredientMatchResponseDto> MatchRecipesByIngredientNamesAsync(
        IReadOnlyList<string> ingredientNames,
        int limit = 15,
        CancellationToken cancellationToken = default
    );

    Task<McpHouseholdTaxonomyDto> GetHouseholdTaxonomyAsync(
        CancellationToken cancellationToken = default
    );

    Task<IngestionJobSummaryDto> EnqueueIngestionAsync(
        McpEnqueueIngestionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<RecipeDetailDto> SaveStructuredRecipeAsync(
        McpSaveStructuredRecipeRequest request,
        CancellationToken cancellationToken = default
    );
}
