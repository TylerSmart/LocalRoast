using System.ComponentModel;
using System.Security.Claims;
using App.Core.Mcp;
using App.Core.Recipes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace App.API.Mcp;

[McpServerToolType]
public sealed class CookbookMcpTools(IMcpCookbookService mcpCookbookService)
{
    [McpServerTool(Name = "search_recipes", ReadOnly = true)]
    [Description(
        "Search household recipes by keyword query, tag, collection, max cooking time, minimum rating, difficulty, or forgotten favorites."
    )]
    public Task<CallToolResult> SearchRecipesAsync(
        [Description("Keyword query to search titles, descriptions, ingredients, sources, or tags")]
            string? query = null,
        [Description("Tag name, slug, or ID to filter by")] string? tag = null,
        [Description("Collection name, slug, or ID to filter by")] string? collection = null,
        [Description("Maximum total time in minutes")] int? maxTotalTimeMinutes = null,
        [Description("Minimum star rating (1.0 - 5.0)")] decimal? minRating = null,
        [Description("Recipe difficulty (Easy, Medium, Project)")] string? difficulty = null,
        [Description("Filter to favorites (>=4 stars or cooked >=2 times) not made recently")]
            bool forgottenFavoritesOnly = false,
        [Description("Include draft recipes in review inbox")] bool includeDrafts = false,
        [Description("Maximum number of recipes to return (1-100, default 25)")] int limit = 25,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                RecipeDifficulty? diff = null;
                if (
                    !string.IsNullOrWhiteSpace(difficulty)
                    && Enum.TryParse<RecipeDifficulty>(difficulty.Trim(), true, out var parsedDiff)
                )
                {
                    diff = parsedDiff;
                }

                var request = new McpRecipeSearchRequest(
                    Query: query,
                    Tag: tag,
                    Collection: collection,
                    MaxTotalTimeMinutes: maxTotalTimeMinutes,
                    MinRating: minRating,
                    Difficulty: diff,
                    ForgottenFavoritesOnly: forgottenFavoritesOnly,
                    IncludeDrafts: includeDrafts,
                    Limit: limit
                );

                var results = await mcpCookbookService.SearchRecipesAsync(
                    request,
                    cancellationToken
                );
                return results;
            }
        );
    }

    [McpServerTool(Name = "get_recipe", ReadOnly = true)]
    [Description(
        "Get a full structured recipe with ingredients, sections, steps, step ingredient callouts, timers, and cook history notes by recipe ID or slug."
    )]
    public Task<CallToolResult> GetRecipeAsync(
        [Description("Recipe unique ID (Guid)")] Guid? recipeId = null,
        [Description("Recipe kebab-case URL slug (e.g. 'garlic-butter-pasta')")]
            string? slug = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var detail = await mcpCookbookService.GetRecipeAsync(
                    recipeId,
                    slug,
                    cancellationToken
                );
                if (detail is null)
                {
                    throw new App.Core.Exceptions.NotFoundException("Recipe not found.");
                }

                return detail;
            }
        );
    }

    [McpServerTool(Name = "match_recipes_by_ingredients", ReadOnly = true)]
    [Description(
        "Rank saved recipes by matching a list of on-hand ingredient names, reporting resolved canonical food items, unresolved ingredients, and match scores."
    )]
    public Task<CallToolResult> MatchRecipesByIngredientsAsync(
        [Description(
            "List of on-hand ingredient names (e.g. ['chicken thighs', 'cilantro', 'garlic'])"
        )]
            string[] ingredients,
        [Description("Maximum number of matched recipes to return (default 15)")] int limit = 15,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var result = await mcpCookbookService.MatchRecipesByIngredientNamesAsync(
                    ingredients ?? [],
                    limit,
                    cancellationToken
                );
                return result;
            }
        );
    }

    [McpServerTool(Name = "list_collections_and_tags", ReadOnly = true)]
    [Description(
        "List all household collections, categorized tags, store aisles, and supported measurement unit codes."
    )]
    public Task<CallToolResult> ListCollectionsAndTagsAsync(
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var taxonomy = await mcpCookbookService.GetHouseholdTaxonomyAsync(
                    cancellationToken
                );
                return taxonomy;
            }
        );
    }
}
