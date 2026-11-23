using System.ComponentModel.DataAnnotations;
using App.Core.Ingestion;
using App.Core.MealPlans;
using App.Core.Recipes;

namespace App.Core.AiHelpers;

public record AiHelperStatusDto(
    bool IsAvailable,
    LlmProvider Provider,
    string? TextModel,
    int LatencyMs,
    string? UnavailableReason,
    string? RefineModel = null
);

public record SuggestIngredientSubstitutionsRequest(
    Guid RecipeId,
    Guid RecipeIngredientId,
    [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
    UnitSystem TargetSystem = UnitSystem.Original,
    [MaxLength(300)] string? DietaryOrPantryNote = null
);

public record IngredientSubstitutionOptionDto(
    string Label,
    string RatioAndAmount,
    string CulinaryNotes,
    string FlavorOrTextureImpact
);

public record IngredientSubstitutionResponseDto(
    Guid RecipeId,
    string RecipeTitle,
    Guid RecipeIngredientId,
    string IngredientDisplayName,
    string ScaledAmountDisplay,
    string ModelUsed,
    IReadOnlyList<IngredientSubstitutionOptionDto> Substitutions
);

public record AskKitchenQuestionRequest(
    [Required, MaxLength(600)] string Question,
    Guid? RecipeId = null,
    Guid? RecipeIngredientId = null,
    Guid? RecipeStepId = null
);

public record KitchenAnswerDto(string Answer, string ModelUsed);

public record DraftMealLeftoverSlotRequest(
    DateOnly ScheduledDate,
    MealSlot Slot,
    [Range(typeof(decimal), "0.25", "999")] decimal Servings = 4
);

public record DraftMealCookSlotRequest(
    bool IsOnDeck = false,
    DateOnly? ScheduledDate = null,
    MealSlot? Slot = null,
    [Range(typeof(decimal), "0.25", "999")] decimal Servings = 4,
    IReadOnlyList<DraftMealLeftoverSlotRequest>? Leftovers = null
);

public record DraftMealLeftoverSlotDto(DateOnly ScheduledDate, MealSlot Slot, decimal Servings);

public record DraftWeeklyMealPlanRequest(
    DateOnly? WeekStartDate = null,
    [MaxLength(600)] string? Prompt = null,
    [Range(1, 28)] int MealCount = 4,
    MealSlot DefaultSlot = MealSlot.Dinner,
    bool PlaceOnDeck = false,
    [Range(typeof(decimal), "0.25", "999")] decimal ServingsPerMeal = 4,
    DateOnly? TodayDate = null,
    [MaxLength(150)] IReadOnlyList<Guid>? ExcludedRecipeIds = null,
    IReadOnlyList<DraftMealCookSlotRequest>? RequestedSlots = null
);

public record DraftMealPlanCandidateDto(
    Guid RecipeId,
    MealPlanRecipeBadgeDto Recipe,
    bool IsOnDeck,
    int? DayOffset,
    DateOnly? ScheduledDate,
    MealSlot? Slot,
    decimal TargetServings,
    string Rationale,
    IReadOnlyList<DateOnly>? LeftoverDates = null,
    decimal CookServings = 0,
    IReadOnlyList<DraftMealLeftoverSlotDto>? LeftoverSlots = null
);

public record DraftMealPlanResponseDto(
    DateOnly WeekStartDate,
    DateOnly WeekEndDate,
    string ModelUsed,
    int ConsideredCookbookRecipeCount,
    IReadOnlyList<DraftMealPlanCandidateDto> Candidates,
    IReadOnlyList<string>? UnmetCravingNotes = null,
    bool UsedEmbeddings = false
);
