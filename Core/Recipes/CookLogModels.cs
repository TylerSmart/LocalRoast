using System.ComponentModel.DataAnnotations;

namespace App.Core.Recipes;

public record CookLogDto(
    Guid Id,
    Guid RecipeId,
    Guid? CookedByUserId,
    string CookedByDisplayName,
    DateTimeOffset CookedAt,
    decimal? ServingsMade,
    int? Rating,
    string? Notes,
    string? PhotoImagePath,
    string? PhotoThumbnailPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt
);

public record RecipeCookStatsSummaryDto(
    Guid RecipeId,
    int TimesCooked,
    DateTimeOffset? LastCookedAt,
    decimal? AverageRating,
    int RatingCount,
    string? LatestCookNote
);

public record CookLogMutationResultDto(CookLogDto CookLog, RecipeCookStatsSummaryDto RecipeStats);

public record UpsertCookLogRequest(
    DateTimeOffset? CookedAt = null,
    [Range(typeof(decimal), "0.25", "999")] decimal? ServingsMade = null,
    [Range(1, 5)] int? Rating = null,
    [MaxLength(2000)] string? Notes = null,
    [MaxLength(500)] string? PhotoImagePath = null,
    [MaxLength(500)] string? PhotoThumbnailPath = null
);
