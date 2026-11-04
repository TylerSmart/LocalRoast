using System.Globalization;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Mcp;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Core.Shopping;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class McpMealAndShoppingService(
    AppDbContext dbContext,
    IMealPlanService mealPlanService,
    IShoppingListService shoppingListService,
    TimeProvider? timeProvider = null
) : IMcpMealAndShoppingService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<MealPlanRangeResponseDto> GetMealPlanAsync(
        McpGetMealPlanRequest request,
        CancellationToken cancellationToken = default
    )
    {
        DateOnly startDate;
        DateOnly endDate;

        if (
            string.IsNullOrWhiteSpace(request.StartDate)
            && string.IsNullOrWhiteSpace(request.EndDate)
        )
        {
            var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
            var sunday = MealPlanDateHelper.NormalizeToSunday(today);
            startDate = sunday;
            endDate = sunday.AddDays(6);
        }
        else if (
            !string.IsNullOrWhiteSpace(request.StartDate)
            && string.IsNullOrWhiteSpace(request.EndDate)
        )
        {
            startDate = ParseDateOnly(request.StartDate, "startDate");
            endDate = startDate.AddDays(6);
        }
        else if (
            string.IsNullOrWhiteSpace(request.StartDate)
            && !string.IsNullOrWhiteSpace(request.EndDate)
        )
        {
            throw new DomainValidationException("Provide startDate when endDate is specified.");
        }
        else
        {
            startDate = ParseDateOnly(request.StartDate!, "startDate");
            endDate = ParseDateOnly(request.EndDate!, "endDate");
        }

        return await mealPlanService.GetRangeAsync(
            startDate,
            endDate,
            request.IncludeOnDeck,
            cancellationToken
        );
    }

    public async Task<McpPlanMealsResultDto> PlanMealsAsync(
        McpPlanMealsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.Entries is null || request.Entries.Count == 0 || request.Entries.Count > 50)
        {
            throw new DomainValidationException("Provide between 1 and 50 meal entries to plan.");
        }

        var plannedEntries = new List<MealPlanEntryDto>();

        foreach (var item in request.Entries)
        {
            var entryType = ParseEntryType(item.EntryType);

            DateOnly? scheduledDate = !string.IsNullOrWhiteSpace(item.ScheduledDate)
                ? ParseDateOnly(item.ScheduledDate, "scheduledDate")
                : null;

            DateOnly? weekStartDate = !string.IsNullOrWhiteSpace(item.WeekStartDate)
                ? ParseDateOnly(item.WeekStartDate, "weekStartDate")
                : null;

            MealSlot? slot = null;
            if (item.IsOnDeck)
            {
                if (weekStartDate is null)
                {
                    weekStartDate = scheduledDate.HasValue
                        ? MealPlanDateHelper.NormalizeToSunday(scheduledDate.Value)
                        : MealPlanDateHelper.NormalizeToSunday(
                            DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime)
                        );
                }
                else
                {
                    weekStartDate = MealPlanDateHelper.NormalizeToSunday(weekStartDate.Value);
                }

                scheduledDate = null;
                slot = null;
            }
            else
            {
                if (scheduledDate is null)
                {
                    scheduledDate = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
                }

                weekStartDate = MealPlanDateHelper.NormalizeToSunday(scheduledDate.Value);
                slot = ParseSlot(item.Slot);
            }

            var resolvedRecipeId = item.RecipeId;
            if (!resolvedRecipeId.HasValue && !string.IsNullOrWhiteSpace(item.RecipeSlug))
            {
                var normSlug = item.RecipeSlug.Trim().ToLowerInvariant();
                resolvedRecipeId = await dbContext
                    .Recipes.AsNoTracking()
                    .Where(r => r.Status != RecipeStatus.Archived && r.Slug == normSlug)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!resolvedRecipeId.HasValue)
                {
                    throw new NotFoundException($"Recipe with slug '{item.RecipeSlug}' not found.");
                }
            }

            if (!resolvedRecipeId.HasValue && !string.IsNullOrWhiteSpace(item.RecipeTitle))
            {
                var normTitle = item.RecipeTitle.Trim().ToLowerInvariant();
                resolvedRecipeId = await dbContext
                    .Recipes.AsNoTracking()
                    .Where(r => r.Status != RecipeStatus.Archived && r.Title.ToLower() == normTitle)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!resolvedRecipeId.HasValue)
                {
                    throw new NotFoundException(
                        $"Recipe with title '{item.RecipeTitle}' not found."
                    );
                }
            }

            if (entryType == MealPlanEntryType.Recipe && !resolvedRecipeId.HasValue)
            {
                throw new DomainValidationException(
                    "A recipeId, recipeSlug, or recipeTitle is required for Recipe meal entries."
                );
            }

            var createReq = new CreateMealPlanEntryRequest(
                EntryType: entryType,
                IsOnDeck: item.IsOnDeck,
                WeekStartDate: weekStartDate,
                ScheduledDate: scheduledDate,
                Slot: slot,
                RecipeId: resolvedRecipeId,
                TargetServings: item.TargetServings,
                SourceMealPlanEntryId: item.SourceMealPlanEntryId,
                CustomTitle: item.CustomTitle?.Trim(),
                Notes: item.Notes?.Trim()
            );

            var created = await mealPlanService.CreateEntryAsync(createReq, cancellationToken);
            plannedEntries.Add(created);
        }

        return new McpPlanMealsResultDto(plannedEntries.Count, plannedEntries);
    }

    public async Task<McpRemoveOrRescheduleMealResultDto> RemoveOrRescheduleMealPlanEntryAsync(
        McpRemoveMealPlanEntryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.EntryId == Guid.Empty)
        {
            throw new DomainValidationException("A valid entryId is required.");
        }

        var existing = await mealPlanService.GetEntryByIdAsync(request.EntryId, cancellationToken);
        if (existing is null)
        {
            throw new NotFoundException("Meal plan entry not found.");
        }

        if (request.MoveToOnDeck)
        {
            var targetWeek = !string.IsNullOrWhiteSpace(request.WeekStartDate)
                ? MealPlanDateHelper.NormalizeToSunday(
                    ParseDateOnly(request.WeekStartDate, "weekStartDate")
                )
                : existing.WeekStartDate;

            var updated = await mealPlanService.MoveEntryAsync(
                request.EntryId,
                new MoveMealPlanEntryRequest(IsOnDeck: true, WeekStartDate: targetWeek),
                cancellationToken
            );

            return new McpRemoveOrRescheduleMealResultDto(
                "RescheduledToOnDeck",
                request.EntryId,
                false,
                updated
            );
        }

        if (!string.IsNullOrWhiteSpace(request.RescheduleToDate))
        {
            var targetDate = ParseDateOnly(request.RescheduleToDate, "rescheduleToDate");
            var targetSlot = !string.IsNullOrWhiteSpace(request.RescheduleToSlot)
                ? ParseSlot(request.RescheduleToSlot)
                : (existing.Slot ?? MealSlot.Dinner);

            var updated = await mealPlanService.MoveEntryAsync(
                request.EntryId,
                new MoveMealPlanEntryRequest(
                    IsOnDeck: false,
                    ScheduledDate: targetDate,
                    Slot: targetSlot
                ),
                cancellationToken
            );

            return new McpRemoveOrRescheduleMealResultDto(
                "Rescheduled",
                request.EntryId,
                false,
                updated
            );
        }

        var removed = await mealPlanService.DeleteEntryAsync(request.EntryId, cancellationToken);
        if (!removed)
        {
            throw new NotFoundException("Meal plan entry not found.");
        }

        return new McpRemoveOrRescheduleMealResultDto("Removed", request.EntryId, true, null);
    }

    public async Task<McpShoppingListsResponseDto> GetShoppingListsAsync(
        McpGetShoppingListsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var allSummaries = await shoppingListService.GetShoppingListsAsync(cancellationToken);
        var matchedSummaries = allSummaries.AsEnumerable();

        if (request.ShoppingListId.HasValue)
        {
            matchedSummaries = matchedSummaries.Where(s => s.Id == request.ShoppingListId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.ListName))
        {
            var normName = request.ListName.Trim().ToLowerInvariant();
            matchedSummaries = matchedSummaries.Where(s =>
                s.Name.Trim().ToLowerInvariant() == normName
            );
        }

        var summaryList = matchedSummaries.ToList();
        if (
            (request.ShoppingListId.HasValue || !string.IsNullOrWhiteSpace(request.ListName))
            && summaryList.Count == 0
        )
        {
            throw new NotFoundException("No shopping list matched the specified filter.");
        }

        var detailList = new List<ShoppingListDetailDto>();
        if (request.IncludeItems)
        {
            foreach (var summary in summaryList)
            {
                var detail = await shoppingListService.GetShoppingListByIdAsync(
                    summary.Id,
                    cancellationToken
                );
                if (detail is not null)
                {
                    detailList.Add(detail);
                }
            }
        }

        return new McpShoppingListsResponseDto(summaryList, detailList);
    }

    public async Task<ShoppingListGenerationResultDto> GenerateShoppingListFromMealPlanAsync(
        McpGenerateShoppingListRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var startDate = ParseDateOnly(request.StartDate, "startDate");
        var endDate = ParseDateOnly(request.EndDate, "endDate");

        var duplicatePolicy = DuplicateMealPolicy.SkipAlreadyImported;
        if (!string.IsNullOrWhiteSpace(request.DuplicatePolicy))
        {
            var cleanPolicy = request.DuplicatePolicy.Trim();
            if (
                string.Equals(
                    cleanPolicy,
                    "SkipAlreadyImported",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                duplicatePolicy = DuplicateMealPolicy.SkipAlreadyImported;
            }
            else if (
                string.Equals(
                    cleanPolicy,
                    "ReplaceAlreadyImported",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                duplicatePolicy = DuplicateMealPolicy.ReplaceAlreadyImported;
            }
            else if (Enum.TryParse<DuplicateMealPolicy>(cleanPolicy, true, out var parsedPolicy))
            {
                duplicatePolicy = parsedPolicy;
            }
            else
            {
                throw new DomainValidationException(
                    $"Invalid duplicate policy '{request.DuplicatePolicy}'. Allowed: SkipAlreadyImported, ReplaceAlreadyImported."
                );
            }
        }

        var targetListId = await ResolveTargetShoppingListIdAsync(
            request.ShoppingListId,
            request.ListName,
            request.CreateListIfMissing,
            cancellationToken
        );

        var genReq = new GenerateFromMealPlanRequest(
            StartDate: startDate,
            EndDate: endDate,
            IncludeOnDeck: request.IncludeOnDeck,
            IncludeSubRecipes: request.IncludeSubRecipes,
            IncludeOptionalIngredients: request.IncludeOptionalIngredients,
            DuplicatePolicy: duplicatePolicy,
            PromotePantryFoodItemIds: request.PromotePantryFoodItemIds
        );

        var result = await shoppingListService.GenerateFromMealPlanAsync(
            targetListId,
            genReq,
            cancellationToken
        );

        if (result is null)
        {
            throw new NotFoundException("Target shopping list not found.");
        }

        return result;
    }

    public async Task<McpUpsertShoppingItemsResultDto> UpsertShoppingListItemsAsync(
        McpUpsertShoppingListItemsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.Items is null || request.Items.Count == 0 || request.Items.Count > 100)
        {
            throw new DomainValidationException("Provide between 1 and 100 items to upsert.");
        }

        var targetListId = await ResolveTargetShoppingListIdAsync(
            request.ShoppingListId,
            request.ListName,
            request.CreateListIfMissing,
            cancellationToken
        );

        var affectedItems = new List<ShoppingListItemDto>();

        foreach (var item in request.Items)
        {
            if (string.IsNullOrWhiteSpace(item.DisplayName))
            {
                continue;
            }

            var (normUnit, customUnit) = RecipeDraftNormalizer.NormalizeUnit(item.Unit);

            if (item.ItemId.HasValue)
            {
                var updateReq = new UpdateShoppingListItemRequest(
                    DisplayName: item.DisplayName.Trim(),
                    Quantity: item.Quantity,
                    MaxQuantity: item.MaxQuantity,
                    Unit: normUnit,
                    CustomUnitName: customUnit,
                    Notes: item.Notes?.Trim(),
                    IsPantryStapleExcluded: false
                );

                var updated = await shoppingListService.UpdateItemAsync(
                    targetListId,
                    item.ItemId.Value,
                    updateReq,
                    cancellationToken
                );

                if (updated is not null)
                {
                    affectedItems.Add(updated);
                }
            }
            else
            {
                var addReq = new CreateShoppingListItemRequest(
                    DisplayName: item.DisplayName.Trim(),
                    Quantity: item.Quantity,
                    MaxQuantity: item.MaxQuantity,
                    Unit: normUnit,
                    CustomUnitName: customUnit,
                    FoodItemId: item.FoodItemId,
                    Notes: item.Notes?.Trim(),
                    ConsolidateIfCompatible: item.ConsolidateIfCompatible
                );

                var added = await shoppingListService.AddItemAsync(
                    targetListId,
                    addReq,
                    cancellationToken
                );

                if (added is not null)
                {
                    affectedItems.Add(added);
                }
            }
        }

        var refreshed = await shoppingListService.GetShoppingListByIdAsync(
            targetListId,
            cancellationToken
        );

        if (refreshed is null)
        {
            throw new NotFoundException("Target shopping list not found.");
        }

        return new McpUpsertShoppingItemsResultDto(refreshed, affectedItems);
    }

    public async Task<ShoppingListItemDto> SetShoppingListItemCheckedAsync(
        McpSetShoppingItemCheckedRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!request.ItemId.HasValue && string.IsNullOrWhiteSpace(request.ItemName))
        {
            throw new DomainValidationException("Provide either itemId or itemName.");
        }

        var targetListId = await ResolveTargetShoppingListIdAsync(
            request.ShoppingListId,
            request.ListName,
            false,
            cancellationToken
        );

        Guid resolvedItemId;

        if (request.ItemId.HasValue)
        {
            resolvedItemId = request.ItemId.Value;
        }
        else
        {
            var detail = await shoppingListService.GetShoppingListByIdAsync(
                targetListId,
                cancellationToken
            );

            if (detail is null)
            {
                throw new NotFoundException("Target shopping list not found.");
            }

            var normName = request.ItemName!.Trim().ToLowerInvariant();

            var primaryCandidates = request.IsChecked ? detail.ActiveItems : detail.CheckedItems;
            var secondaryCandidates = request.IsChecked ? detail.CheckedItems : detail.ActiveItems;

            var match = primaryCandidates.FirstOrDefault(i =>
                i.DisplayName.Trim().ToLowerInvariant() == normName
                || (i.FoodItemName != null && i.FoodItemName.Trim().ToLowerInvariant() == normName)
            );

            match ??= secondaryCandidates.FirstOrDefault(i =>
                i.DisplayName.Trim().ToLowerInvariant() == normName
                || (i.FoodItemName != null && i.FoodItemName.Trim().ToLowerInvariant() == normName)
            );

            match ??= detail.ExcludedPantryItems.FirstOrDefault(i =>
                i.DisplayName.Trim().ToLowerInvariant() == normName
                || (i.FoodItemName != null && i.FoodItemName.Trim().ToLowerInvariant() == normName)
            );

            if (match is null)
            {
                var allItems = detail
                    .ActiveItems.Concat(detail.CheckedItems)
                    .Concat(detail.ExcludedPantryItems)
                    .Where(i => i.DisplayName.ToLowerInvariant().Contains(normName))
                    .ToList();

                if (allItems.Count > 1)
                {
                    throw new DomainValidationException(
                        $"Ambiguous item name '{request.ItemName}'. Matches multiple items on list."
                    );
                }

                if (allItems.Count == 1)
                {
                    match = allItems[0];
                }
            }

            if (match is null)
            {
                throw new NotFoundException(
                    $"Item '{request.ItemName}' not found on target shopping list."
                );
            }

            resolvedItemId = match.Id;
        }

        var result = await shoppingListService.SetItemCheckedAsync(
            targetListId,
            resolvedItemId,
            new SetShoppingListItemCheckedRequest(request.IsChecked),
            cancellationToken
        );

        if (result is null)
        {
            throw new NotFoundException("Shopping list item not found.");
        }

        return result;
    }

    private async Task<Guid> ResolveTargetShoppingListIdAsync(
        Guid? shoppingListId,
        string? listName,
        bool createIfMissing,
        CancellationToken cancellationToken
    )
    {
        if (shoppingListId.HasValue)
        {
            var exists = await dbContext
                .ShoppingLists.AsNoTracking()
                .AnyAsync(l => l.Id == shoppingListId.Value, cancellationToken);

            if (!exists)
            {
                throw new NotFoundException(
                    $"Shopping list with ID '{shoppingListId.Value}' not found."
                );
            }

            return shoppingListId.Value;
        }

        if (!string.IsNullOrWhiteSpace(listName))
        {
            var normName = listName.Trim().ToLowerInvariant();
            var existing = await dbContext
                .ShoppingLists.AsNoTracking()
                .FirstOrDefaultAsync(l => l.NormalizedName == normName, cancellationToken);

            if (existing is not null)
            {
                return existing.Id;
            }

            if (createIfMissing)
            {
                var created = await shoppingListService.CreateShoppingListAsync(
                    new CreateShoppingListRequest(listName.Trim(), false),
                    cancellationToken
                );
                return created.Id;
            }

            throw new NotFoundException($"Shopping list '{listName.Trim()}' not found.");
        }

        var lists = await shoppingListService.GetShoppingListsAsync(cancellationToken);
        if (lists.Count > 0)
        {
            return lists[0].Id;
        }

        if (createIfMissing)
        {
            var created = await shoppingListService.CreateShoppingListAsync(
                new CreateShoppingListRequest("Weekly Groceries", true),
                cancellationToken
            );
            return created.Id;
        }

        throw new NotFoundException("No shopping lists exist.");
    }

    private static DateOnly ParseDateOnly(string raw, string fieldName)
    {
        if (
            string.IsNullOrWhiteSpace(raw)
            || !DateOnly.TryParseExact(
                raw.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date
            )
        )
        {
            throw new DomainValidationException(
                $"Invalid {fieldName} '{raw}'. Expected ISO format YYYY-MM-DD."
            );
        }

        return date;
    }

    private static MealPlanEntryType ParseEntryType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return MealPlanEntryType.Recipe;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "recipe" => MealPlanEntryType.Recipe,
            "leftovers" => MealPlanEntryType.Leftovers,
            "freeformnote" or "note" or "freeform" => MealPlanEntryType.FreeformNote,
            _ => Enum.TryParse<MealPlanEntryType>(raw.Trim(), true, out var parsed)
                ? parsed
                : throw new DomainValidationException(
                    $"Invalid entryType '{raw}'. Allowed: Recipe, Leftovers, FreeformNote."
                ),
        };
    }

    private static MealSlot ParseSlot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return MealSlot.Dinner;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "breakfast" => MealSlot.Breakfast,
            "lunch" => MealSlot.Lunch,
            "dinner" => MealSlot.Dinner,
            "snackprep" or "snack" or "prep" or "snack / prep" => MealSlot.SnackPrep,
            _ => Enum.TryParse<MealSlot>(raw.Trim(), true, out var parsed)
                ? parsed
                : throw new DomainValidationException(
                    $"Invalid slot '{raw}'. Allowed: Breakfast, Lunch, Dinner, SnackPrep."
                ),
        };
    }
}
