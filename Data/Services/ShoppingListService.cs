using App.Core.Exceptions;
using App.Core.Interfaces;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Core.Shopping;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class ShoppingListService(
    AppDbContext dbContext,
    IUserContext? userContext = null,
    TimeProvider? timeProvider = null
) : IShoppingListService
{
    public async Task<IReadOnlyList<ShoppingListSummaryDto>> GetShoppingListsAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await dbContext
            .ShoppingLists.AsNoTracking()
            .OrderByDescending(l => l.IsPinned)
            .ThenByDescending(l => l.ModifiedAt)
            .ThenBy(l => l.Name)
            .Select(l => new ShoppingListSummaryDto(
                l.Id,
                l.Name,
                l.IsPinned,
                l.Items.Count(i => !i.IsPantryStapleExcluded && !i.IsChecked),
                l.Items.Count(i => !i.IsPantryStapleExcluded && i.IsChecked),
                l.Items.Count(i => i.IsPantryStapleExcluded),
                l.CreatedAt,
                l.ModifiedAt
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<ShoppingListDetailDto?> GetShoppingListByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var list = await dbContext
            .ShoppingLists.AsNoTracking()
            .Include(l => l.Items)
                .ThenInclude(i => i.FoodItem)
            .Include(l => l.Items)
                .ThenInclude(i => i.Sources)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

        if (list is null)
        {
            return null;
        }

        var activeItems = list
            .Items.Where(i => !i.IsPantryStapleExcluded && !i.IsChecked)
            .OrderBy(i => i.DisplayName)
            .ThenBy(i => i.Dimension)
            .ThenBy(i => i.SortOrder)
            .Select(MapItemDto)
            .ToList();

        var checkedItems = list
            .Items.Where(i => !i.IsPantryStapleExcluded && i.IsChecked)
            .OrderByDescending(i => i.CheckedAt)
            .ThenBy(i => i.DisplayName)
            .Select(MapItemDto)
            .ToList();

        var excludedPantryItems = list
            .Items.Where(i => i.IsPantryStapleExcluded)
            .OrderBy(i => i.DisplayName)
            .ThenBy(i => i.Dimension)
            .Select(MapItemDto)
            .ToList();

        return new ShoppingListDetailDto(
            list.Id,
            list.Name,
            list.IsPinned,
            activeItems,
            checkedItems,
            excludedPantryItems,
            list.CreatedAt,
            list.ModifiedAt
        );
    }

    public async Task<ShoppingListDetailDto> CreateShoppingListAsync(
        CreateShoppingListRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var trimmedName = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 150)
        {
            throw new DomainValidationException(
                "Shopping list name must be between 1 and 150 characters."
            );
        }

        var normName = trimmedName.ToLowerInvariant();
        var exists = await dbContext.ShoppingLists.AnyAsync(
            l => l.NormalizedName == normName,
            cancellationToken
        );
        if (exists)
        {
            throw new ConflictException(
                $"A shopping list with the name '{trimmedName}' already exists."
            );
        }

        var list = new ShoppingList
        {
            Name = trimmedName,
            NormalizedName = normName,
            IsPinned = request.IsPinned,
        };

        dbContext.ShoppingLists.Add(list);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (await GetShoppingListByIdAsync(list.Id, cancellationToken))!;
    }

    public async Task<ShoppingListDetailDto?> UpdateShoppingListAsync(
        Guid id,
        UpdateShoppingListRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var list = await dbContext.ShoppingLists.FirstOrDefaultAsync(
            l => l.Id == id,
            cancellationToken
        );
        if (list is null)
        {
            return null;
        }

        var trimmedName = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 150)
        {
            throw new DomainValidationException(
                "Shopping list name must be between 1 and 150 characters."
            );
        }

        var normName = trimmedName.ToLowerInvariant();
        if (normName != list.NormalizedName)
        {
            var exists = await dbContext.ShoppingLists.AnyAsync(
                l => l.Id != id && l.NormalizedName == normName,
                cancellationToken
            );
            if (exists)
            {
                throw new ConflictException(
                    $"A shopping list with the name '{trimmedName}' already exists."
                );
            }
        }

        list.Name = trimmedName;
        list.NormalizedName = normName;
        list.IsPinned = request.IsPinned;

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetShoppingListByIdAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteShoppingListAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var list = await dbContext.ShoppingLists.FirstOrDefaultAsync(
            l => l.Id == id,
            cancellationToken
        );
        if (list is null)
        {
            return false;
        }

        dbContext.ShoppingLists.Remove(list);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ShoppingListItemDto?> AddItemAsync(
        Guid shoppingListId,
        CreateShoppingListItemRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var listExists = await dbContext.ShoppingLists.AnyAsync(
            l => l.Id == shoppingListId,
            cancellationToken
        );
        if (!listExists)
        {
            return null;
        }

        var trimmedDisplayName = request.DisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedDisplayName) || trimmedDisplayName.Length > 200)
        {
            throw new DomainValidationException(
                "Item display name must be between 1 and 200 characters."
            );
        }

        if (request.Quantity.HasValue && request.Quantity.Value <= 0m)
        {
            throw new DomainValidationException("Quantity must be greater than zero.");
        }

        if (
            request.Quantity.HasValue
            && request.MaxQuantity.HasValue
            && request.MaxQuantity.Value < request.Quantity.Value
        )
        {
            throw new DomainValidationException("Max quantity cannot be less than quantity.");
        }

        var foodItemId = request.FoodItemId;
        if (!foodItemId.HasValue)
        {
            var normName = trimmedDisplayName.ToLowerInvariant();
            var matchedFoodItem = await dbContext
                .FoodItems.Where(f =>
                    f.NormalizedName == normName
                    || f.Aliases.Any(a => a.NormalizedAlias == normName)
                )
                .Select(f => new { f.Id, f.Name })
                .FirstOrDefaultAsync(cancellationToken);

            if (matchedFoodItem is not null)
            {
                foodItemId = matchedFoodItem.Id;
            }
        }

        var dim = ShoppingConsolidator.GetDimension(request.Unit, request.Quantity);

        if (request.ConsolidateIfCompatible)
        {
            var key = ShoppingConsolidator.BuildConsolidationKey(
                foodItemId,
                trimmedDisplayName,
                dim,
                request.Unit,
                request.CustomUnitName
            );

            var existingItems = await dbContext
                .ShoppingListItems.Include(i => i.Sources)
                .Where(i =>
                    i.ShoppingListId == shoppingListId && !i.IsPantryStapleExcluded && !i.IsChecked
                )
                .ToListAsync(cancellationToken);

            var match = existingItems.FirstOrDefault(i =>
                ShoppingConsolidator.BuildConsolidationKey(
                    i.FoodItemId,
                    i.DisplayName,
                    i.Dimension,
                    i.Unit,
                    i.CustomUnitName
                ) == key
            );

            if (match is not null)
            {
                var inputExisting = new ConsolidatableIngredientInput(
                    match.FoodItemId,
                    match.DisplayName,
                    false,
                    match.Quantity,
                    match.MaxQuantity,
                    match.Unit,
                    match.CustomUnitName,
                    match.Notes,
                    ShoppingListSourceKind.Manual,
                    null,
                    null,
                    "Existing",
                    null
                );

                var inputNew = new ConsolidatableIngredientInput(
                    foodItemId,
                    trimmedDisplayName,
                    false,
                    request.Quantity,
                    request.MaxQuantity,
                    request.Unit,
                    request.CustomUnitName,
                    request.Notes,
                    ShoppingListSourceKind.Manual,
                    null,
                    null,
                    "Manual",
                    null
                );

                var consolidated = ShoppingConsolidator.Consolidate([inputExisting, inputNew]);
                if (consolidated.Count > 0)
                {
                    var bucket = consolidated[0];
                    match.Quantity = bucket.Quantity;
                    match.MaxQuantity = bucket.MaxQuantity;
                    match.Unit = bucket.Unit;
                    match.CustomUnitName = bucket.CustomUnitName;
                    match.Dimension = bucket.Dimension;
                    match.Notes = bucket.MergedNotes;

                    await dbContext.SaveChangesAsync(cancellationToken);
                    return await GetItemDtoByIdAsync(match.Id, cancellationToken);
                }
            }
        }

        var maxSort =
            await dbContext
                .ShoppingListItems.Where(i =>
                    i.ShoppingListId == shoppingListId && !i.IsPantryStapleExcluded && !i.IsChecked
                )
                .Select(i => (int?)i.SortOrder)
                .MaxAsync(cancellationToken)
            ?? -1;

        var newItem = new ShoppingListItem
        {
            ShoppingListId = shoppingListId,
            FoodItemId = foodItemId,
            DisplayName = trimmedDisplayName,
            Quantity = request.Quantity,
            MaxQuantity = request.MaxQuantity,
            Unit = request.Unit,
            CustomUnitName =
                request.Unit == MeasurementUnit.Custom ? request.CustomUnitName?.Trim() : null,
            Dimension = dim,
            Notes = request.Notes?.Trim(),
            IsPantryStapleExcluded = false,
            IsChecked = false,
            SortOrder = maxSort + 1,
        };

        dbContext.ShoppingListItems.Add(newItem);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetItemDtoByIdAsync(newItem.Id, cancellationToken);
    }

    public async Task<ShoppingListItemDto?> UpdateItemAsync(
        Guid shoppingListId,
        Guid itemId,
        UpdateShoppingListItemRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = await dbContext
            .ShoppingListItems.Include(i => i.Sources)
            .Include(i => i.FoodItem)
            .FirstOrDefaultAsync(
                i => i.Id == itemId && i.ShoppingListId == shoppingListId,
                cancellationToken
            );

        if (item is null)
        {
            return null;
        }

        var trimmedDisplayName = request.DisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedDisplayName) || trimmedDisplayName.Length > 200)
        {
            throw new DomainValidationException(
                "Item display name must be between 1 and 200 characters."
            );
        }

        if (request.Quantity.HasValue && request.Quantity.Value <= 0m)
        {
            throw new DomainValidationException("Quantity must be greater than zero.");
        }

        if (
            request.Quantity.HasValue
            && request.MaxQuantity.HasValue
            && request.MaxQuantity.Value < request.Quantity.Value
        )
        {
            throw new DomainValidationException("Max quantity cannot be less than quantity.");
        }

        item.DisplayName = trimmedDisplayName;
        item.Quantity = request.Quantity;
        item.MaxQuantity = request.MaxQuantity;
        item.Unit = request.Unit;
        item.CustomUnitName =
            request.Unit == MeasurementUnit.Custom ? request.CustomUnitName?.Trim() : null;
        item.Dimension = ShoppingConsolidator.GetDimension(request.Unit, request.Quantity);
        item.Notes = request.Notes?.Trim();
        item.IsPantryStapleExcluded = request.IsPantryStapleExcluded;

        await dbContext.SaveChangesAsync(cancellationToken);

        return MapItemDto(item);
    }

    public async Task<ShoppingListItemDto?> SetItemCheckedAsync(
        Guid shoppingListId,
        Guid itemId,
        SetShoppingListItemCheckedRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = await dbContext
            .ShoppingListItems.Include(i => i.Sources)
            .Include(i => i.FoodItem)
            .FirstOrDefaultAsync(
                i => i.Id == itemId && i.ShoppingListId == shoppingListId,
                cancellationToken
            );

        if (item is null)
        {
            return null;
        }

        var (actorId, actorName) = await ResolveActorAsync(cancellationToken);

        if (request.IsChecked)
        {
            item.IsChecked = true;
            item.CheckedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
            item.CheckedByUserId = actorId;
            item.CheckedByDisplayName = actorName;
            item.IsPantryStapleExcluded = false;
        }
        else
        {
            item.IsChecked = false;
            item.CheckedAt = null;
            item.CheckedByUserId = null;
            item.CheckedByDisplayName = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return MapItemDto(item);
    }

    public async Task<ShoppingListItemDto?> PromotePantryStapleItemAsync(
        Guid shoppingListId,
        Guid itemId,
        CancellationToken cancellationToken = default
    )
    {
        var item = await dbContext
            .ShoppingListItems.Include(i => i.Sources)
            .Include(i => i.FoodItem)
            .FirstOrDefaultAsync(
                i => i.Id == itemId && i.ShoppingListId == shoppingListId,
                cancellationToken
            );

        if (item is null)
        {
            return null;
        }

        if (!item.IsPantryStapleExcluded)
        {
            return MapItemDto(item);
        }

        var targetKey = ShoppingConsolidator.BuildConsolidationKey(
            item.FoodItemId,
            item.DisplayName,
            item.Dimension,
            item.Unit,
            item.CustomUnitName
        );

        var activePeers = await dbContext
            .ShoppingListItems.Include(i => i.Sources)
            .Include(i => i.FoodItem)
            .Where(i =>
                i.ShoppingListId == shoppingListId
                && !i.IsPantryStapleExcluded
                && !i.IsChecked
                && i.Id != itemId
            )
            .ToListAsync(cancellationToken);

        var matchingActive = activePeers.FirstOrDefault(i =>
            ShoppingConsolidator.BuildConsolidationKey(
                i.FoodItemId,
                i.DisplayName,
                i.Dimension,
                i.Unit,
                i.CustomUnitName
            ) == targetKey
        );

        if (matchingActive is not null)
        {
            var inputs = new List<ConsolidatableIngredientInput>();

            inputs.Add(
                new ConsolidatableIngredientInput(
                    matchingActive.FoodItemId,
                    matchingActive.DisplayName,
                    false,
                    matchingActive.Quantity,
                    matchingActive.MaxQuantity,
                    matchingActive.Unit,
                    matchingActive.CustomUnitName,
                    matchingActive.Notes,
                    ShoppingListSourceKind.Manual,
                    null,
                    null,
                    "Existing",
                    null
                )
            );

            inputs.Add(
                new ConsolidatableIngredientInput(
                    item.FoodItemId,
                    item.DisplayName,
                    false,
                    item.Quantity,
                    item.MaxQuantity,
                    item.Unit,
                    item.CustomUnitName,
                    item.Notes,
                    ShoppingListSourceKind.Manual,
                    null,
                    null,
                    "Promoted",
                    null
                )
            );

            var consolidated = ShoppingConsolidator.Consolidate(inputs);
            if (consolidated.Count > 0)
            {
                var bucket = consolidated[0];
                matchingActive.Quantity = bucket.Quantity;
                matchingActive.MaxQuantity = bucket.MaxQuantity;
                matchingActive.Unit = bucket.Unit;
                matchingActive.CustomUnitName = bucket.CustomUnitName;
                matchingActive.Dimension = bucket.Dimension;
                matchingActive.Notes = bucket.MergedNotes;

                foreach (var src in item.Sources.ToList())
                {
                    matchingActive.Sources.Add(
                        new ShoppingListItemSource
                        {
                            SourceKind = src.SourceKind,
                            MealPlanEntryId = src.MealPlanEntryId,
                            RecipeId = src.RecipeId,
                            SourceLabel = src.SourceLabel,
                            ScheduledDate = src.ScheduledDate,
                            ContributedQuantity = src.ContributedQuantity,
                            ContributedMaxQuantity = src.ContributedMaxQuantity,
                        }
                    );
                }

                dbContext.ShoppingListItems.Remove(item);
                await dbContext.SaveChangesAsync(cancellationToken);

                return MapItemDto(matchingActive);
            }
        }

        item.IsPantryStapleExcluded = false;
        item.IsChecked = false;

        var maxSort =
            await dbContext
                .ShoppingListItems.Where(i =>
                    i.ShoppingListId == shoppingListId && !i.IsPantryStapleExcluded && !i.IsChecked
                )
                .Select(i => (int?)i.SortOrder)
                .MaxAsync(cancellationToken)
            ?? -1;
        item.SortOrder = maxSort + 1;

        await dbContext.SaveChangesAsync(cancellationToken);

        return MapItemDto(item);
    }

    public async Task<bool> DeleteItemAsync(
        Guid shoppingListId,
        Guid itemId,
        CancellationToken cancellationToken = default
    )
    {
        var item = await dbContext.ShoppingListItems.FirstOrDefaultAsync(
            i => i.Id == itemId && i.ShoppingListId == shoppingListId,
            cancellationToken
        );

        if (item is null)
        {
            return false;
        }

        dbContext.ShoppingListItems.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ClearCheckedItemsResultDto?> ClearCheckedItemsAsync(
        Guid shoppingListId,
        CancellationToken cancellationToken = default
    )
    {
        var listExists = await dbContext.ShoppingLists.AnyAsync(
            l => l.Id == shoppingListId,
            cancellationToken
        );
        if (!listExists)
        {
            return null;
        }

        var checkedItems = await dbContext
            .ShoppingListItems.Where(i =>
                i.ShoppingListId == shoppingListId && !i.IsPantryStapleExcluded && i.IsChecked
            )
            .ToListAsync(cancellationToken);

        var count = checkedItems.Count;
        dbContext.ShoppingListItems.RemoveRange(checkedItems);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ClearCheckedItemsResultDto(count);
    }

    public async Task<ShoppingListGenerationPreviewDto?> PreviewGenerateFromMealPlanAsync(
        Guid shoppingListId,
        GenerateFromMealPlanRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateDateRange(request.StartDate, request.EndDate);

        var listExists = await dbContext.ShoppingLists.AnyAsync(
            l => l.Id == shoppingListId,
            cancellationToken
        );
        if (!listExists)
        {
            return null;
        }

        var extraction = await ExtractMealPlanDataAsync(shoppingListId, request, cancellationToken);

        var activeCandidates = extraction
            .ConsolidatedBuckets.Where(b => !extraction.IsPantryStapleExcluded(b))
            .Select(MapCandidateDto)
            .ToList();

        var excludedPantryCandidates = extraction
            .ConsolidatedBuckets.Where(extraction.IsPantryStapleExcluded)
            .Select(MapCandidateDto)
            .ToList();

        return new ShoppingListGenerationPreviewDto(
            request.StartDate,
            request.EndDate,
            extraction.EligibleMealsCount,
            extraction.NewMealsToImportCount,
            extraction.IgnoredLeftoversCount,
            extraction.IgnoredNotesCount,
            extraction.AlreadyImportedMeals,
            activeCandidates,
            excludedPantryCandidates
        );
    }

    public async Task<ShoppingListGenerationResultDto?> GenerateFromMealPlanAsync(
        Guid shoppingListId,
        GenerateFromMealPlanRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateDateRange(request.StartDate, request.EndDate);

        var listExists = await dbContext.ShoppingLists.AnyAsync(
            l => l.Id == shoppingListId,
            cancellationToken
        );
        if (!listExists)
        {
            return null;
        }

        var extraction = await ExtractMealPlanDataAsync(shoppingListId, request, cancellationToken);

        if (
            request.DuplicatePolicy == DuplicateMealPolicy.ReplaceAlreadyImported
            && extraction.AlreadyImportedMeals.Count > 0
        )
        {
            var entryIdsToReplace = extraction
                .AlreadyImportedMeals.Select(m => m.MealPlanEntryId)
                .ToHashSet();

            var sourcesToRemove = await dbContext
                .ShoppingListItemSources.Include(s => s.ShoppingListItem)
                .Where(s =>
                    s.ShoppingListItem.ShoppingListId == shoppingListId
                    && s.MealPlanEntryId.HasValue
                    && entryIdsToReplace.Contains(s.MealPlanEntryId.Value)
                )
                .ToListAsync(cancellationToken);

            var affectedItemIds = sourcesToRemove
                .Select(s => s.ShoppingListItemId)
                .Distinct()
                .ToList();

            dbContext.ShoppingListItemSources.RemoveRange(sourcesToRemove);
            await dbContext.SaveChangesAsync(cancellationToken);

            var itemsToCheck = await dbContext
                .ShoppingListItems.Include(i => i.Sources)
                .Where(i => affectedItemIds.Contains(i.Id))
                .ToListAsync(cancellationToken);

            foreach (var item in itemsToCheck)
            {
                if (item.Sources.Count == 0)
                {
                    dbContext.ShoppingListItems.Remove(item);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var activeCount = 0;
        var excludedCount = 0;

        foreach (var bucket in extraction.ConsolidatedBuckets)
        {
            var isExcluded = extraction.IsPantryStapleExcluded(bucket);
            if (isExcluded)
            {
                excludedCount++;
            }
            else
            {
                activeCount++;
            }

            await MergeBucketIntoListAsync(shoppingListId, bucket, isExcluded, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var updatedDetail = (await GetShoppingListByIdAsync(shoppingListId, cancellationToken))!;

        return new ShoppingListGenerationResultDto(
            updatedDetail,
            extraction.NewMealsToImportCount,
            extraction.SkippedDuplicateMealsCount,
            extraction.IgnoredLeftoversCount,
            extraction.IgnoredNotesCount,
            activeCount,
            excludedCount
        );
    }

    public async Task<ShoppingListGenerationResultDto?> AddRecipeIngredientsAsync(
        Guid shoppingListId,
        AddRecipeToShoppingListRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var listExists = await dbContext.ShoppingLists.AnyAsync(
            l => l.Id == shoppingListId,
            cancellationToken
        );
        if (!listExists)
        {
            return null;
        }

        var recipe = await dbContext
            .Recipes.AsNoTracking()
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .FirstOrDefaultAsync(r => r.Id == request.RecipeId, cancellationToken);

        if (recipe is null)
        {
            throw new DomainValidationException("Referenced recipe not found.");
        }

        var targetServings = request.TargetServings ?? recipe.BaseServings;
        var scale = recipe.BaseServings > 0m ? targetServings / recipe.BaseServings : 1m;

        var allInputs = new List<ConsolidatableIngredientInput>();
        var visitedRecipes = new HashSet<Guid> { recipe.Id };

        await CollectRecipeInputsAsync(
            recipe,
            scale,
            request.IncludeSubRecipes,
            request.IncludeOptionalIngredients,
            recipe.Title,
            null,
            null,
            ShoppingListSourceKind.SingleRecipe,
            visitedRecipes,
            allInputs,
            cancellationToken
        );

        var consolidated = ShoppingConsolidator.Consolidate(allInputs);
        var activeCount = 0;
        var excludedCount = 0;

        foreach (var bucket in consolidated)
        {
            var isExcluded =
                bucket.IsPantryStaple
                && !(
                    request.PromotePantryFoodItemIds?.Contains(bucket.FoodItemId ?? Guid.Empty)
                    ?? false
                );

            if (isExcluded)
            {
                excludedCount++;
            }
            else
            {
                activeCount++;
            }

            await MergeBucketIntoListAsync(shoppingListId, bucket, isExcluded, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var updatedDetail = (await GetShoppingListByIdAsync(shoppingListId, cancellationToken))!;

        return new ShoppingListGenerationResultDto(
            updatedDetail,
            1,
            0,
            0,
            0,
            activeCount,
            excludedCount
        );
    }

    private static void ValidateDateRange(DateOnly startDate, DateOnly endDate)
    {
        if (endDate < startDate)
        {
            throw new DomainValidationException(
                "End date must be greater than or equal to start date."
            );
        }

        if ((endDate.DayNumber - startDate.DayNumber) > 92)
        {
            throw new DomainValidationException("Date range cannot exceed 92 days.");
        }
    }

    private sealed record MealPlanExtractionResult(
        int EligibleMealsCount,
        int NewMealsToImportCount,
        int SkippedDuplicateMealsCount,
        int IgnoredLeftoversCount,
        int IgnoredNotesCount,
        IReadOnlyList<AlreadyImportedMealDto> AlreadyImportedMeals,
        IReadOnlyList<ConsolidatedShoppingBucket> ConsolidatedBuckets,
        Func<ConsolidatedShoppingBucket, bool> IsPantryStapleExcluded
    );

    private async Task<MealPlanExtractionResult> ExtractMealPlanDataAsync(
        Guid shoppingListId,
        GenerateFromMealPlanRequest request,
        CancellationToken cancellationToken
    )
    {
        var firstMonday = MealPlanDateHelper.NormalizeToMonday(request.StartDate);
        var lastMonday = MealPlanDateHelper.NormalizeToMonday(request.EndDate);

        var q = dbContext.MealPlanEntries.AsNoTracking().Include(e => e.Recipe).AsQueryable();

        if (request.IncludeOnDeck)
        {
            q = q.Where(e =>
                (
                    !e.IsOnDeck
                    && e.ScheduledDate >= request.StartDate
                    && e.ScheduledDate <= request.EndDate
                ) || (e.IsOnDeck && e.WeekStartDate >= firstMonday && e.WeekStartDate <= lastMonday)
            );
        }
        else
        {
            q = q.Where(e =>
                !e.IsOnDeck
                && e.ScheduledDate >= request.StartDate
                && e.ScheduledDate <= request.EndDate
            );
        }

        var rawEntries = await q.ToListAsync(cancellationToken);

        var ignoredLeftovers = rawEntries.Count(e =>
            e.IsLeftovers || e.EntryType == MealPlanEntryType.Leftovers
        );
        var ignoredNotes = rawEntries.Count(e =>
            !e.IsLeftovers && (e.EntryType == MealPlanEntryType.FreeformNote || e.RecipeId == null)
        );

        var candidateEntries = rawEntries
            .Where(e =>
                !e.IsLeftovers && e.EntryType == MealPlanEntryType.Recipe && e.RecipeId.HasValue
            )
            .ToList();

        var existingSources = await dbContext
            .ShoppingListItemSources.AsNoTracking()
            .Where(s =>
                s.ShoppingListItem.ShoppingListId == shoppingListId && s.MealPlanEntryId != null
            )
            .Select(s => s.MealPlanEntryId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var alreadyImportedSet = existingSources.ToHashSet();

        var alreadyImportedMeals = candidateEntries
            .Where(e => alreadyImportedSet.Contains(e.Id))
            .Select(e => new AlreadyImportedMealDto(
                e.Id,
                e.Recipe?.Title ?? e.CustomTitle ?? "Meal",
                e.ScheduledDate,
                e.IsOnDeck
            ))
            .ToList();

        List<MealPlanEntry> entriesToProcess;
        int skippedDuplicates;

        if (request.DuplicatePolicy == DuplicateMealPolicy.SkipAlreadyImported)
        {
            entriesToProcess = candidateEntries
                .Where(e => !alreadyImportedSet.Contains(e.Id))
                .ToList();
            skippedDuplicates = alreadyImportedMeals.Count;
        }
        else
        {
            entriesToProcess = candidateEntries;
            skippedDuplicates = 0;
        }

        var allInputs = new List<ConsolidatableIngredientInput>();
        var recipeIds = entriesToProcess.Select(e => e.RecipeId!.Value).Distinct().ToList();

        var recipesMap = await dbContext
            .Recipes.AsNoTracking()
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r, cancellationToken);

        foreach (var entry in entriesToProcess)
        {
            if (!recipesMap.TryGetValue(entry.RecipeId!.Value, out var recipe))
            {
                continue;
            }

            var targetServings = entry.TargetServings ?? recipe.BaseServings;
            var scale = recipe.BaseServings > 0m ? targetServings / recipe.BaseServings : 1m;
            var label = ShoppingConsolidator.FormatMealPlanSourceLabel(
                entry.ScheduledDate,
                entry.IsOnDeck,
                recipe.Title
            );

            var visited = new HashSet<Guid> { recipe.Id };

            await CollectRecipeInputsAsync(
                recipe,
                scale,
                request.IncludeSubRecipes,
                request.IncludeOptionalIngredients,
                label,
                entry.Id,
                entry.ScheduledDate,
                ShoppingListSourceKind.MealPlanEntry,
                visited,
                allInputs,
                cancellationToken
            );
        }

        var buckets = ShoppingConsolidator.Consolidate(allInputs);

        bool IsExcluded(ConsolidatedShoppingBucket b) =>
            b.IsPantryStaple
            && !(request.PromotePantryFoodItemIds?.Contains(b.FoodItemId ?? Guid.Empty) ?? false);

        return new MealPlanExtractionResult(
            candidateEntries.Count,
            entriesToProcess.Count,
            skippedDuplicates,
            ignoredLeftovers,
            ignoredNotes,
            alreadyImportedMeals,
            buckets,
            IsExcluded
        );
    }

    private async Task CollectRecipeInputsAsync(
        Recipe recipe,
        decimal scaleFactor,
        bool includeSubRecipes,
        bool includeOptional,
        string sourceLabel,
        Guid? mealPlanEntryId,
        DateOnly? scheduledDate,
        ShoppingListSourceKind sourceKind,
        HashSet<Guid> visitedRecipes,
        List<ConsolidatableIngredientInput> targetInputs,
        CancellationToken cancellationToken
    )
    {
        foreach (var ing in recipe.Ingredients.OrderBy(i => i.SortOrder))
        {
            if (!includeOptional && ing.IsOptional)
            {
                continue;
            }

            if (ing.LinkedRecipeId.HasValue && includeSubRecipes)
            {
                var subRecipeId = ing.LinkedRecipeId.Value;
                if (!visitedRecipes.Contains(subRecipeId))
                {
                    visitedRecipes.Add(subRecipeId);
                    var subRecipe = await dbContext
                        .Recipes.AsNoTracking()
                        .Include(r => r.Ingredients)
                            .ThenInclude(i => i.FoodItem)
                        .FirstOrDefaultAsync(r => r.Id == subRecipeId, cancellationToken);

                    if (subRecipe is not null)
                    {
                        var subScale = scaleFactor * (ing.Quantity ?? 1m);
                        await CollectRecipeInputsAsync(
                            subRecipe,
                            subScale,
                            includeSubRecipes,
                            includeOptional,
                            $"{sourceLabel} ({subRecipe.Title})",
                            mealPlanEntryId,
                            scheduledDate,
                            sourceKind,
                            visitedRecipes,
                            targetInputs,
                            cancellationToken
                        );
                    }
                }
                continue;
            }

            var foodItemId = ing.FoodItemId;
            var foodItemName = ing.FoodItem?.Name;
            var isPantryStaple = ing.FoodItem?.IsPantryStaple ?? false;

            if (foodItemId is null)
            {
                var normName = ing.DisplayName.Trim().ToLowerInvariant();
                var matched = await dbContext
                    .FoodItems.AsNoTracking()
                    .Where(f =>
                        f.NormalizedName == normName
                        || f.Aliases.Any(a => a.NormalizedAlias == normName)
                    )
                    .Select(f => new
                    {
                        f.Id,
                        f.Name,
                        f.IsPantryStaple,
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (matched is not null)
                {
                    foodItemId = matched.Id;
                    foodItemName = matched.Name;
                    isPantryStaple = matched.IsPantryStaple;
                }
            }

            var qty = ing.Quantity.HasValue ? ing.Quantity.Value * scaleFactor : (decimal?)null;
            var maxQty = ing.MaxQuantity.HasValue
                ? ing.MaxQuantity.Value * scaleFactor
                : (decimal?)null;

            targetInputs.Add(
                new ConsolidatableIngredientInput(
                    foodItemId,
                    foodItemName ?? ing.DisplayName,
                    isPantryStaple,
                    qty,
                    maxQty,
                    ing.Unit,
                    ing.CustomUnitName,
                    ing.PreparationNote,
                    sourceKind,
                    mealPlanEntryId,
                    recipe.Id,
                    sourceLabel,
                    scheduledDate
                )
            );
        }
    }

    private async Task MergeBucketIntoListAsync(
        Guid shoppingListId,
        ConsolidatedShoppingBucket bucket,
        bool isPantryStapleExcluded,
        CancellationToken cancellationToken
    )
    {
        var existingItems = await dbContext
            .ShoppingListItems.Include(i => i.Sources)
            .Where(i =>
                i.ShoppingListId == shoppingListId
                && i.IsPantryStapleExcluded == isPantryStapleExcluded
                && !i.IsChecked
            )
            .ToListAsync(cancellationToken);

        var match = existingItems.FirstOrDefault(i =>
            ShoppingConsolidator.BuildConsolidationKey(
                i.FoodItemId,
                i.DisplayName,
                i.Dimension,
                i.Unit,
                i.CustomUnitName
            ) == bucket.ConsolidationKey
        );

        if (match is not null)
        {
            var inputs = new List<ConsolidatableIngredientInput>
            {
                new(
                    match.FoodItemId,
                    match.DisplayName,
                    isPantryStapleExcluded,
                    match.Quantity,
                    match.MaxQuantity,
                    match.Unit,
                    match.CustomUnitName,
                    match.Notes,
                    ShoppingListSourceKind.Manual,
                    null,
                    null,
                    "Existing",
                    null
                ),
                new(
                    bucket.FoodItemId,
                    bucket.DisplayName,
                    isPantryStapleExcluded,
                    bucket.Quantity,
                    bucket.MaxQuantity,
                    bucket.Unit,
                    bucket.CustomUnitName,
                    bucket.MergedNotes,
                    ShoppingListSourceKind.Manual,
                    null,
                    null,
                    "New",
                    null
                ),
            };

            var consolidated = ShoppingConsolidator.Consolidate(inputs);
            if (consolidated.Count > 0)
            {
                var combined = consolidated[0];
                match.Quantity = combined.Quantity;
                match.MaxQuantity = combined.MaxQuantity;
                match.Unit = combined.Unit;
                match.CustomUnitName = combined.CustomUnitName;
                match.Dimension = combined.Dimension;
                match.Notes = combined.MergedNotes;
            }

            foreach (var src in bucket.Sources)
            {
                match.Sources.Add(
                    new ShoppingListItemSource
                    {
                        SourceKind = src.SourceKind,
                        MealPlanEntryId = src.MealPlanEntryId,
                        RecipeId = src.RecipeId,
                        SourceLabel = src.SourceLabel,
                        ScheduledDate = src.ScheduledDate,
                        ContributedQuantity = src.QuantityInConsolidatedUnit,
                        ContributedMaxQuantity = src.MaxQuantityInConsolidatedUnit,
                    }
                );
            }
        }
        else
        {
            var maxSort =
                await dbContext
                    .ShoppingListItems.Where(i =>
                        i.ShoppingListId == shoppingListId
                        && i.IsPantryStapleExcluded == isPantryStapleExcluded
                        && !i.IsChecked
                    )
                    .Select(i => (int?)i.SortOrder)
                    .MaxAsync(cancellationToken)
                ?? -1;

            var newItem = new ShoppingListItem
            {
                ShoppingListId = shoppingListId,
                FoodItemId = bucket.FoodItemId,
                DisplayName = bucket.DisplayName,
                Quantity = bucket.Quantity,
                MaxQuantity = bucket.MaxQuantity,
                Unit = bucket.Unit,
                CustomUnitName = bucket.CustomUnitName,
                Dimension = bucket.Dimension,
                Notes = bucket.MergedNotes,
                IsPantryStapleExcluded = isPantryStapleExcluded,
                IsChecked = false,
                SortOrder = maxSort + 1,
            };

            foreach (var src in bucket.Sources)
            {
                newItem.Sources.Add(
                    new ShoppingListItemSource
                    {
                        SourceKind = src.SourceKind,
                        MealPlanEntryId = src.MealPlanEntryId,
                        RecipeId = src.RecipeId,
                        SourceLabel = src.SourceLabel,
                        ScheduledDate = src.ScheduledDate,
                        ContributedQuantity = src.QuantityInConsolidatedUnit,
                        ContributedMaxQuantity = src.MaxQuantityInConsolidatedUnit,
                    }
                );
            }

            dbContext.ShoppingListItems.Add(newItem);
        }
    }

    private async Task<ShoppingListItemDto?> GetItemDtoByIdAsync(
        Guid itemId,
        CancellationToken cancellationToken
    )
    {
        var item = await dbContext
            .ShoppingListItems.AsNoTracking()
            .Include(i => i.FoodItem)
            .Include(i => i.Sources)
            .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);

        return item is not null ? MapItemDto(item) : null;
    }

    private static ShoppingListItemDto MapItemDto(ShoppingListItem i)
    {
        var formatted = CulinaryMath.ScaleAndConvert(
            i.Quantity,
            i.MaxQuantity,
            i.Unit,
            i.CustomUnitName,
            1m,
            UnitSystem.Original
        );

        var sources = i
            .Sources.Select(s => new ShoppingListItemSourceDto(
                s.Id,
                s.SourceKind,
                s.MealPlanEntryId,
                s.RecipeId,
                s.SourceLabel,
                s.ScheduledDate,
                s.ContributedQuantity,
                s.ContributedMaxQuantity
            ))
            .ToList();

        return new ShoppingListItemDto(
            i.Id,
            i.ShoppingListId,
            i.FoodItemId,
            i.FoodItem?.Name,
            i.DisplayName,
            i.Quantity,
            i.MaxQuantity,
            i.Unit,
            i.CustomUnitName,
            formatted.UnitLabel,
            formatted.FormattedAmount,
            i.Dimension,
            i.Notes,
            i.IsPantryStapleExcluded,
            i.IsChecked,
            i.CheckedAt,
            i.CheckedByUserId,
            i.CheckedByDisplayName,
            i.SortOrder,
            sources,
            i.CreatedAt,
            i.ModifiedAt
        );
    }

    private static ShoppingListCandidateItemDto MapCandidateDto(ConsolidatedShoppingBucket b)
    {
        var formatted = CulinaryMath.ScaleAndConvert(
            b.Quantity,
            b.MaxQuantity,
            b.Unit,
            b.CustomUnitName,
            1m,
            UnitSystem.Original
        );

        var labels = b.Sources.Select(s => s.SourceLabel).Distinct().ToList();

        return new ShoppingListCandidateItemDto(
            b.FoodItemId,
            b.DisplayName,
            b.IsPantryStaple,
            b.Quantity,
            b.MaxQuantity,
            b.Unit,
            b.CustomUnitName,
            formatted.UnitLabel,
            formatted.FormattedAmount,
            b.Dimension,
            b.MergedNotes,
            labels
        );
    }

    private async Task<(Guid? UserId, string? DisplayName)> ResolveActorAsync(
        CancellationToken cancellationToken
    )
    {
        if (userContext is null)
        {
            return (null, null);
        }

        try
        {
            var userIdStr = userContext.UserId;
            if (Guid.TryParse(userIdStr, out var userGuid))
            {
                var user = await dbContext
                    .Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userGuid, cancellationToken);
                return (userGuid, user?.Username);
            }
        }
        catch
        {
            // ignored
        }

        return (null, null);
    }
}
