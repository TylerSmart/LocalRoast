namespace App.Core.Recipes;

public interface ICookLogService
{
    Task<IReadOnlyList<CookLogDto>> GetCookLogsForRecipeAsync(
        Guid recipeId,
        CancellationToken cancellationToken = default
    );

    Task<CookLogMutationResultDto> CreateCookLogAsync(
        Guid recipeId,
        UpsertCookLogRequest request,
        CancellationToken cancellationToken = default
    );

    Task<CookLogMutationResultDto?> UpdateCookLogAsync(
        Guid recipeId,
        Guid cookLogId,
        UpsertCookLogRequest request,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteCookLogAsync(
        Guid recipeId,
        Guid cookLogId,
        CancellationToken cancellationToken = default
    );
}
