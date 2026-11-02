using System.ComponentModel.DataAnnotations;

namespace App.Core.MealPlans;

public sealed record MealPlanTemplateItemDto(
    Guid Id,
    bool IsOnDeck,
    int? DayOffset,
    MealSlot? Slot,
    int SortOrder,
    MealPlanEntryType EntryType,
    bool IsLeftovers,
    decimal? TargetServings,
    string DisplayTitle,
    string? CustomTitle,
    string? Notes,
    MealPlanRecipeBadgeDto? Recipe
);

public sealed record MealPlanTemplateSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    int ItemCount,
    int ScheduledItemCount,
    int OnDeckItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt
);

public sealed record MealPlanTemplateDetailDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<MealPlanTemplateItemDto> Items,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset ModifiedAt,
    string ModifiedBy
);

public sealed record MealPlanTemplateItemInput(
    bool IsOnDeck = false,
    [Range(0, 6)] int? DayOffset = null,
    MealSlot? Slot = null,
    MealPlanEntryType EntryType = MealPlanEntryType.Recipe,
    Guid? RecipeId = null,
    [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
    [MaxLength(200)] string? CustomTitle = null,
    [MaxLength(500)] string? Notes = null
);

public sealed record UpsertMealPlanTemplateRequest(
    [Required, MinLength(2), MaxLength(120)] string Name,
    [MaxLength(500)] string? Description = null,
    IReadOnlyList<MealPlanTemplateItemInput>? Items = null
);

public sealed record CreateTemplateFromWeekRequest(
    [Required] DateOnly WeekStartDate,
    [Required, MinLength(2), MaxLength(120)] string Name,
    [MaxLength(500)] string? Description = null,
    bool IncludeOnDeck = true
);

public sealed record ApplyMealPlanTemplateRequest(
    [Required] DateOnly TargetWeekStartDate,
    ApplyTemplateMode Mode = ApplyTemplateMode.Merge
);
