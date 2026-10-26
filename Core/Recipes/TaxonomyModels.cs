using System.ComponentModel.DataAnnotations;

namespace App.Core.Recipes;

public record CollectionDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    int SortOrder,
    bool IsSystemSeeded,
    int RecipeCount
);

public record UpsertCollectionRequest(
    [Required, MaxLength(100)] string Name,
    [MaxLength(500)] string? Description = null,
    int SortOrder = 0
);

public record TagDto(
    Guid Id,
    string Name,
    string Slug,
    TagCategory Category,
    int SortOrder,
    bool IsSystemSeeded,
    int RecipeCount
);

public record UpsertTagRequest(
    [Required, MaxLength(80)] string Name,
    TagCategory Category = TagCategory.Custom,
    int SortOrder = 0
);
