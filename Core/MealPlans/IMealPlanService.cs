namespace App.Core.MealPlans;

public interface IMealPlanService
{
    Task<MealPlanWeekDto> GetWeekAsync(
        DateOnly weekDate,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanRangeResponseDto> GetRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        bool includeOnDeck = true,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanEntryDto?> GetEntryByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanEntryDto> CreateEntryAsync(
        CreateMealPlanEntryRequest request,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanEntryDto?> UpdateEntryAsync(
        Guid id,
        UpdateMealPlanEntryRequest request,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanEntryDto?> MoveEntryAsync(
        Guid id,
        MoveMealPlanEntryRequest request,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanEntryDto?> CookTonightAsync(
        Guid id,
        CookTonightRequest request,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteEntryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> ClearWeekAsync(
        DateOnly weekDate,
        bool includeOnDeck = false,
        CancellationToken cancellationToken = default
    );
}
