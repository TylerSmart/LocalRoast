using System.ComponentModel.DataAnnotations;

namespace App.Core.Recipes;

public record ShareLinkDto(
    Guid Id,
    Guid RecipeId,
    string Token,
    string? Label,
    bool IncludeCookLogs,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    bool IsActive,
    int ViewCount,
    DateTimeOffset? LastAccessedAt,
    DateTimeOffset CreatedAt,
    string CreatedBy
);

public record CreateShareLinkRequest(
    [MaxLength(120)] string? Label = null,
    bool IncludeCookLogs = false,
    DateTimeOffset? ExpiresAt = null
);

public record PublicRecipeTagBadgeDto(string Name, TagCategory Category);

public record PublicRecipeIngredientDto(
    Guid Id,
    int SortOrder,
    string? SectionName,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    bool IsPantryStaple,
    Guid? LinkedSubRecipeId,
    string? LinkedSubRecipeTitle,
    string DisplayName,
    string? PreparationNote,
    bool IsOptional
);

public record PublicSubRecipeDto(
    Guid Id,
    string Title,
    decimal BaseServings,
    string? YieldDisplay,
    IReadOnlyList<PublicRecipeIngredientDto> Ingredients,
    IReadOnlyList<RecipeStepDto> Steps
);

public record PublicSharedRecipeDto(
    string Title,
    string? Description,
    decimal BaseServings,
    string? YieldDisplay,
    int? PrepTimeMinutes,
    int? CookTimeMinutes,
    int? TotalTimeMinutes,
    RecipeDifficulty? Difficulty,
    string? SourceUrl,
    string? SourceName,
    string? SourceAuthor,
    string? SourceBookTitle,
    int? SourceBookPage,
    string? CoverImagePath,
    IReadOnlyList<PublicRecipeTagBadgeDto> Tags,
    IReadOnlyList<PublicRecipeIngredientDto> Ingredients,
    IReadOnlyList<RecipeStepDto> Steps,
    IReadOnlyList<PublicSubRecipeDto> LinkedSubRecipes,
    bool IncludeCookLogs,
    DateTimeOffset? ExpiresAt
);
