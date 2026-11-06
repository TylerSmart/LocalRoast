namespace App.Core.AiHelpers;

public interface IAiKitchenHelperService
{
    Task<AiHelperStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IngredientSubstitutionResponseDto> SuggestIngredientSubstitutionsAsync(
        SuggestIngredientSubstitutionsRequest request,
        CancellationToken cancellationToken = default
    );

    Task<KitchenAnswerDto> AskKitchenQuestionAsync(
        AskKitchenQuestionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<DraftMealPlanResponseDto> DraftWeeklyMealPlanAsync(
        DraftWeeklyMealPlanRequest request,
        CancellationToken cancellationToken = default
    );
}
