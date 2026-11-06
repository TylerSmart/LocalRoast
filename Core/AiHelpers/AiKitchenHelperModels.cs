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
    string? UnavailableReason
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

public record DraftWeeklyMealPlanRequest(
    DateOnly? WeekStartDate = null,
    [Required, MaxLength(600)] string Prompt = "",
    [Range(1, 14)] int MealCount = 4,
    MealSlot DefaultSlot = MealSlot.Dinner,
    bool PlaceOnDeck = false
);

public record DraftMealPlanCandidateDto(
    Guid RecipeId,
    MealPlanRecipeBadgeDto Recipe,
    bool IsOnDeck,
    int? DayOffset,
    DateOnly? ScheduledDate,
    MealSlot? Slot,
    decimal TargetServings,
    string Rationale
);

public record DraftMealPlanResponseDto(
    DateOnly WeekStartDate,
    DateOnly WeekEndDate,
    string ModelUsed,
    int ConsideredCookbookRecipeCount,
    IReadOnlyList<DraftMealPlanCandidateDto> Candidates
);
