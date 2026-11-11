namespace App.Core.Maintenance;

public sealed record ResetAppDataResultDto(
    int DeletedRecipesCount,
    int DeletedMealPlansCount,
    int DeletedShoppingListsCount,
    int DeletedIngestionJobsCount,
    int DeletedFoodItemsCount,
    int DeletedTagsCount,
    int DeletedCollectionsCount,
    DateTimeOffset ResetAt,
    int RestoredDefaultTagsCount = 0
);
