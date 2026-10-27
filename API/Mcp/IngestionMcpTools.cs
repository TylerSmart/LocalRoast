using System.ComponentModel;
using System.Security.Claims;
using App.Core.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace App.API.Mcp;

[McpServerToolType]
public sealed class IngestionMcpTools(IMcpCookbookService mcpCookbookService)
{
    [McpServerTool(Name = "enqueue_recipe_ingestion")]
    [Description(
        "Submit a Web Recipe URL or raw text block into the async local LLM ingestion queue."
    )]
    public Task<CallToolResult> EnqueueRecipeIngestionAsync(
        [Description("Web recipe URL to scrape and extract (http/https)")] string? url = null,
        [Description("Raw text or markdown recipe block")] string? rawText = null,
        [Description("Optional title hint for raw text recipes")] string? titleHint = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpEnqueueIngestionRequest(
                    Url: url,
                    RawText: rawText,
                    TitleHint: titleHint
                );
                var job = await mcpCookbookService.EnqueueIngestionAsync(
                    request,
                    cancellationToken
                );
                return job;
            }
        );
    }

    [McpServerTool(Name = "save_structured_recipe")]
    [Description(
        "Directly create or update a structured recipe with ingredients, sections, steps, timers, and tags from an AI conversation."
    )]
    public Task<CallToolResult> SaveStructuredRecipeAsync(
        [Description("Title of the recipe")] string title,
        [Description("Optional existing Recipe ID to update")] Guid? recipeId = null,
        [Description("Short description or story")] string? description = null,
        [Description(
            "Publish immediately (true) or save to review inbox as draft (false, default)"
        )]
            bool publishImmediately = false,
        [Description("Base servings count (default 4)")] decimal baseServings = 4m,
        [Description("Yield display override (e.g. '12 muffins')")] string? yieldDisplay = null,
        [Description("Prep time in minutes")] int? prepTimeMinutes = null,
        [Description("Cook time in minutes")] int? cookTimeMinutes = null,
        [Description("Total time in minutes")] int? totalTimeMinutes = null,
        [Description("Recipe difficulty (Easy, Medium, Project)")] string? difficulty = null,
        [Description("Source URL")] string? sourceUrl = null,
        [Description("Source name or publication")] string? sourceName = null,
        [Description("Source author")] string? sourceAuthor = null,
        [Description("Cookbook title")] string? sourceBookTitle = null,
        [Description("Cookbook page number")] int? sourceBookPage = null,
        [Description("Structured ingredients")] McpStructuredIngredientInput[]? ingredients = null,
        [Description("Ordered instructions and steps")] McpStructuredStepInput[]? steps = null,
        [Description("Collection names, slugs, or IDs")] string[]? collections = null,
        [Description("Tag names, slugs, or IDs")] string[]? tags = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    )
    {
        return McpToolResultHelper.ExecuteAsync(
            user,
            async () =>
            {
                var request = new McpSaveStructuredRecipeRequest(
                    Title: title,
                    RecipeId: recipeId,
                    Description: description,
                    PublishImmediately: publishImmediately,
                    BaseServings: baseServings,
                    YieldDisplay: yieldDisplay,
                    PrepTimeMinutes: prepTimeMinutes,
                    CookTimeMinutes: cookTimeMinutes,
                    TotalTimeMinutes: totalTimeMinutes,
                    Difficulty: difficulty,
                    SourceUrl: sourceUrl,
                    SourceName: sourceName,
                    SourceAuthor: sourceAuthor,
                    SourceBookTitle: sourceBookTitle,
                    SourceBookPage: sourceBookPage,
                    Ingredients: ingredients,
                    Steps: steps,
                    Collections: collections,
                    Tags: tags
                );

                var recipe = await mcpCookbookService.SaveStructuredRecipeAsync(
                    request,
                    cancellationToken
                );
                return recipe;
            }
        );
    }
}
