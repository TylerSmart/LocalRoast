namespace App.Core.MealPlans;

public interface IMealPlanTemplateService
{
    Task<IReadOnlyList<MealPlanTemplateSummaryDto>> GetTemplatesAsync(
        CancellationToken cancellationToken = default
    );

    Task<MealPlanTemplateDetailDto?> GetTemplateByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanTemplateDetailDto> CreateTemplateAsync(
        UpsertMealPlanTemplateRequest request,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanTemplateDetailDto> CreateTemplateFromWeekAsync(
        CreateTemplateFromWeekRequest request,
        CancellationToken cancellationToken = default
    );

    Task<MealPlanTemplateDetailDto?> UpdateTemplateAsync(
        Guid id,
        UpsertMealPlanTemplateRequest request,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<MealPlanWeekDto?> ApplyTemplateToWeekAsync(
        Guid templateId,
        ApplyMealPlanTemplateRequest request,
        CancellationToken cancellationToken = default
    );
}
