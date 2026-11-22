namespace App.Core.Maintenance;

public sealed record ResetAppDataRequest(bool KeepIngestionJobs = false);

public sealed record ResetAppDataResultDto(
    int DeletedRecipesCount,
    int DeletedMealPlansCount,
    int DeletedShoppingListsCount,
    int DeletedIngestionJobsCount,
    int DeletedFoodItemsCount,
    int DeletedTagsCount,
    int DeletedCollectionsCount,
    DateTimeOffset ResetAt,
    int RestoredDefaultTagsCount = 0,
    int ResetIngestionJobsCount = 0
);

public sealed record ExportAppDataBackupRequest(bool IncludeMedia = true);

public sealed record ImportAppDataBackupResultDto(
    int ImportedRecipesCount,
    int ImportedFoodItemsCount,
    int ImportedTagsCount,
    int ImportedCollectionsCount,
    int ImportedMealPlansCount,
    int ImportedShoppingListsCount,
    int ImportedIngestionJobsCount,
    int ImportedCookLogsCount,
    int ImportedMediaFilesCount,
    DateTimeOffset ImportedAt
);
