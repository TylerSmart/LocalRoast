using System.ComponentModel.DataAnnotations;
using App.Core.Recipes;

namespace App.Core.MealPlans;

public sealed record MealPlanRecipeBadgeDto(
    Guid Id,
    string Title,
    string Slug,
    decimal BaseServings,
    int? TotalTimeMinutes,
    RecipeDifficulty? Difficulty,
    string? ThumbnailImagePath
);

public sealed record MealPlanEntryDto(
    Guid Id,
    DateOnly WeekStartDate,
    bool IsOnDeck,
    DateOnly? ScheduledDate,
    MealSlot? Slot,
    int SortOrder,
    MealPlanEntryType EntryType,
    bool IsLeftovers,
    decimal? TargetServings,
    string DisplayTitle,
    string? CustomTitle,
    string? Notes,
    Guid? SourceMealPlanEntryId,
    MealPlanRecipeBadgeDto? Recipe,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

public sealed record MealPlanDayDto(
    DateOnly Date,
    int DayOffset,
    IReadOnlyList<MealPlanEntryDto> Entries
);

public sealed record MealPlanWeekDto(
    DateOnly WeekStartDate,
    DateOnly WeekEndDate,
    IReadOnlyList<MealPlanDayDto> Days,
    IReadOnlyList<MealPlanEntryDto> OnDeckEntries
);

public sealed record MealPlanRangeResponseDto(
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<MealPlanEntryDto> ScheduledEntries,
    IReadOnlyList<MealPlanEntryDto> OnDeckEntries
);

public sealed record CreateMealPlanEntryRequest(
    MealPlanEntryType EntryType = MealPlanEntryType.Recipe,
    bool IsOnDeck = false,
    DateOnly? WeekStartDate = null,
    DateOnly? ScheduledDate = null,
    MealSlot? Slot = null,
    Guid? RecipeId = null,
    [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
    Guid? SourceMealPlanEntryId = null,
    [MaxLength(200)] string? CustomTitle = null,
    [MaxLength(500)] string? Notes = null
);

public sealed record UpdateMealPlanEntryRequest(
    MealPlanEntryType EntryType = MealPlanEntryType.Recipe,
    bool IsOnDeck = false,
    DateOnly? WeekStartDate = null,
    DateOnly? ScheduledDate = null,
    MealSlot? Slot = null,
    Guid? RecipeId = null,
    [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
    Guid? SourceMealPlanEntryId = null,
    [MaxLength(200)] string? CustomTitle = null,
    [MaxLength(500)] string? Notes = null
);

public sealed record MoveMealPlanEntryRequest(
    bool IsOnDeck = false,
    DateOnly? WeekStartDate = null,
    DateOnly? ScheduledDate = null,
    MealSlot? Slot = null,
    [Range(0, 1000)] int? TargetSortOrder = null
);

public sealed record CookTonightRequest(
    DateOnly? TargetDate = null,
    MealSlot Slot = MealSlot.Dinner
);
