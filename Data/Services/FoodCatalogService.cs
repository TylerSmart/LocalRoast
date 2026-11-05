using System.Text.Json;
using App.Core.Common;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Shopping;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class FoodCatalogService(
    AppDbContext dbContext,
    IShoppingListService? shoppingListService = null,
    IShoppingRealtimeNotifier? shoppingRealtimeNotifier = null,
    TimeProvider? timeProvider = null
) : IFoodCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string NormalizeKey(string input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new DomainValidationException("Value cannot be empty.");
        }
        return trimmed.ToLowerInvariant();
    }

    public async Task<PagedResultDto<FoodItemDto>> GetFoodItemsPagedAsync(
        FoodItemQueryParameters query,
        CancellationToken cancellationToken = default
    )
    {
        var q = dbContext.FoodItems.AsNoTracking().AsQueryable();

        if (query.IsPantryStaple.HasValue)
        {
            q = q.Where(f => f.IsPantryStaple == query.IsPantryStaple.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var norm = query.Search.Trim().ToLowerInvariant();
            q = q.Where(f =>
                f.NormalizedName.Contains(norm)
                || f.Aliases.Any(a => a.NormalizedAlias.Contains(norm))
            );
        }

        var totalCount = await q.CountAsync(cancellationToken);
        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? 30, 1, 200);

        var items = await q.OrderBy(f => f.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new FoodItemDto(
                f.Id,
                f.Name,
                f.IsPantryStaple,
                f.Aliases.Select(a => a.Alias).ToList(),
                f.RecipeIngredients.Select(ri => ri.RecipeId).Distinct().Count(),
                f.CreatedAt,
                f.ModifiedAt,
                f.ShoppingListItems.Count()
            ))
            .ToListAsync(cancellationToken);

        return new PagedResultDto<FoodItemDto>(items, totalCount, page, pageSize);
    }

    public async Task<IReadOnlyList<FoodItemDto>> GetFoodItemsAsync(
        FoodItemQueryParameters query,
        CancellationToken cancellationToken = default
    )
    {
        var q = dbContext.FoodItems.AsNoTracking().AsQueryable();

        if (query.IsPantryStaple.HasValue)
        {
            q = q.Where(f => f.IsPantryStaple == query.IsPantryStaple.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var norm = query.Search.Trim().ToLowerInvariant();
            q = q.Where(f =>
                f.NormalizedName.Contains(norm)
                || f.Aliases.Any(a => a.NormalizedAlias.Contains(norm))
            );
        }

        return await q.OrderBy(f => f.Name)
            .Select(f => new FoodItemDto(
                f.Id,
                f.Name,
                f.IsPantryStaple,
                f.Aliases.Select(a => a.Alias).ToList(),
                f.RecipeIngredients.Select(ri => ri.RecipeId).Distinct().Count(),
                f.CreatedAt,
                f.ModifiedAt,
                f.ShoppingListItems.Count()
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<FoodItemDto?> GetFoodItemByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        return await dbContext
            .FoodItems.AsNoTracking()
            .Where(f => f.Id == id)
            .Select(f => new FoodItemDto(
                f.Id,
                f.Name,
                f.IsPantryStaple,
                f.Aliases.Select(a => a.Alias).ToList(),
                f.RecipeIngredients.Select(ri => ri.RecipeId).Distinct().Count(),
                f.CreatedAt,
                f.ModifiedAt,
                f.ShoppingListItems.Count()
            ))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<FoodItemDto> CreateFoodItemAsync(
        CreateFoodItemRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var cleanName = request.Name?.Trim();
        if (!FoodNameSanitizer.IsValidFoodName(cleanName))
        {
            throw new DomainValidationException(
                $"Food item name cannot be a placeholder or invalid name like '{request.Name}'."
            );
        }
        var normName = NormalizeKey(cleanName);

        var nameCollides =
            await dbContext.FoodItems.AnyAsync(f => f.NormalizedName == normName, cancellationToken)
            || await dbContext.FoodItemAliases.AnyAsync(
                a => a.NormalizedAlias == normName,
                cancellationToken
            );

        if (nameCollides)
        {
            throw new ConflictException(
                $"A food item or alias with the name '{request.Name}' already exists."
            );
        }

        var cleanAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.Aliases is not null)
        {
            foreach (var alias in request.Aliases)
            {
                var trimmed = alias?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    var normAlias = trimmed.ToLowerInvariant();
                    if (normAlias != normName)
                    {
                        cleanAliases.Add(trimmed);
                    }
                }
            }
        }

        foreach (var alias in cleanAliases)
        {
            var normAlias = alias.ToLowerInvariant();
            var aliasCollides =
                await dbContext.FoodItems.AnyAsync(
                    f => f.NormalizedName == normAlias,
                    cancellationToken
                )
                || await dbContext.FoodItemAliases.AnyAsync(
                    a => a.NormalizedAlias == normAlias,
                    cancellationToken
                );

            if (aliasCollides)
            {
                throw new ConflictException(
                    $"Alias '{alias}' collides with an existing food item or alias."
                );
            }
        }

        var foodItem = new FoodItem
        {
            Name = request.Name.Trim(),
            NormalizedName = normName,
            IsPantryStaple = request.IsPantryStaple,
        };

        foreach (var alias in cleanAliases)
        {
            foodItem.Aliases.Add(
                new FoodItemAlias { Alias = alias, NormalizedAlias = alias.ToLowerInvariant() }
            );
        }

        dbContext.FoodItems.Add(foodItem);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (await GetFoodItemByIdAsync(foodItem.Id, cancellationToken))!;
    }

    public async Task<FoodItemDto?> UpdateFoodItemAsync(
        Guid id,
        UpdateFoodItemRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var foodItem = await dbContext
            .FoodItems.Include(f => f.Aliases)
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (foodItem is null)
        {
            return null;
        }

        var cleanName = request.Name?.Trim();
        if (!FoodNameSanitizer.IsValidFoodName(cleanName))
        {
            throw new DomainValidationException(
                $"Food item name cannot be a placeholder or invalid name like '{request.Name}'."
            );
        }
        var normName = NormalizeKey(cleanName);

        if (normName != foodItem.NormalizedName)
        {
            var nameCollides =
                await dbContext.FoodItems.AnyAsync(
                    f => f.Id != id && f.NormalizedName == normName,
                    cancellationToken
                )
                || await dbContext.FoodItemAliases.AnyAsync(
                    a => a.FoodItemId != id && a.NormalizedAlias == normName,
                    cancellationToken
                );

            if (nameCollides)
            {
                throw new ConflictException(
                    $"A food item or alias with the name '{request.Name}' already exists."
                );
            }
        }

        var cleanAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.Aliases is not null)
        {
            foreach (var alias in request.Aliases)
            {
                var trimmed = alias?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    var normAlias = trimmed.ToLowerInvariant();
                    if (normAlias != normName)
                    {
                        cleanAliases.Add(trimmed);
                    }
                }
            }
        }

        foreach (var alias in cleanAliases)
        {
            var normAlias = alias.ToLowerInvariant();
            var aliasCollides =
                await dbContext.FoodItems.AnyAsync(
                    f => f.Id != id && f.NormalizedName == normAlias,
                    cancellationToken
                )
                || await dbContext.FoodItemAliases.AnyAsync(
                    a => a.FoodItemId != id && a.NormalizedAlias == normAlias,
                    cancellationToken
                );

            if (aliasCollides)
            {
                throw new ConflictException(
                    $"Alias '{alias}' collides with an existing food item or alias."
                );
            }
        }

        foodItem.Name = request.Name.Trim();
        foodItem.NormalizedName = normName;
        foodItem.IsPantryStaple = request.IsPantryStaple;

        var existingAliases = foodItem.Aliases.ToList();
        foreach (var existing in existingAliases)
        {
            if (!cleanAliases.Contains(existing.Alias))
            {
                dbContext.FoodItemAliases.Remove(existing);
            }
        }

        foreach (var alias in cleanAliases)
        {
            if (
                !foodItem.Aliases.Any(a =>
                    a.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                foodItem.Aliases.Add(
                    new FoodItemAlias { Alias = alias, NormalizedAlias = alias.ToLowerInvariant() }
                );
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetFoodItemByIdAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteFoodItemAsync(
        Guid id,
        bool unlinkFromRecipes = false,
        CancellationToken cancellationToken = default
    )
    {
        var foodItem = await dbContext.FoodItems.FirstOrDefaultAsync(
            f => f.Id == id,
            cancellationToken
        );
        if (foodItem is null)
        {
            return false;
        }

        var linkedIngredients = await dbContext
            .RecipeIngredients.Where(ri => ri.FoodItemId == id)
            .ToListAsync(cancellationToken);

        if (linkedIngredients.Count > 0)
        {
            if (!unlinkFromRecipes)
            {
                throw new ConflictException(
                    "Cannot delete a food item that is used in one or more recipes."
                );
            }

            foreach (var ri in linkedIngredients)
            {
                ri.FoodItemId = null;
            }
        }

        var linkedShoppingItems = await dbContext
            .ShoppingListItems.Where(i => i.FoodItemId == id)
            .ToListAsync(cancellationToken);
        foreach (var item in linkedShoppingItems)
        {
            item.FoodItemId = null;
        }

        dbContext.FoodItems.Remove(foodItem);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PagedResultDto<FoodItemRecipeUsageDto>> GetFoodItemRecipesPagedAsync(
        Guid foodItemId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default
    )
    {
        var exists = await dbContext.FoodItems.AnyAsync(f => f.Id == foodItemId, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException($"Food item '{foodItemId}' was not found.");
        }

        var q = dbContext
            .Recipes.AsNoTracking()
            .Where(r => r.Ingredients.Any(i => i.FoodItemId == foodItemId));

        var totalCount = await q.CountAsync(cancellationToken);
        var pageNum = Math.Max(1, page);
        var size = Math.Clamp(pageSize, 1, 100);

        var items = await q.OrderBy(r => r.Title)
            .Skip((pageNum - 1) * size)
            .Take(size)
            .Select(r => new FoodItemRecipeUsageDto(
                r.Id,
                r.Title,
                r.Slug,
                r.ThumbnailImagePath,
                r.Status,
                r.Ingredients.Where(i => i.FoodItemId == foodItemId)
                    .Select(i => i.DisplayName)
                    .ToList()
            ))
            .ToListAsync(cancellationToken);

        return new PagedResultDto<FoodItemRecipeUsageDto>(items, totalCount, pageNum, size);
    }

    public async Task<IReadOnlyList<FoodItemRecipeUsageDto>> GetFoodItemRecipesAsync(
        Guid foodItemId,
        CancellationToken cancellationToken = default
    )
    {
        var exists = await dbContext.FoodItems.AnyAsync(f => f.Id == foodItemId, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException($"Food item '{foodItemId}' was not found.");
        }

        return await dbContext
            .Recipes.AsNoTracking()
            .Where(r => r.Ingredients.Any(i => i.FoodItemId == foodItemId))
            .OrderBy(r => r.Title)
            .Select(r => new FoodItemRecipeUsageDto(
                r.Id,
                r.Title,
                r.Slug,
                r.ThumbnailImagePath,
                r.Status,
                r.Ingredients.Where(i => i.FoodItemId == foodItemId)
                    .Select(i => i.DisplayName)
                    .ToList()
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<UnlinkFoodItemRecipesResultDto> UnlinkFoodItemRecipesAsync(
        Guid foodItemId,
        CancellationToken cancellationToken = default
    )
    {
        var foodItem = await dbContext.FoodItems.FirstOrDefaultAsync(
            f => f.Id == foodItemId,
            cancellationToken
        );
        if (foodItem is null)
        {
            throw new NotFoundException($"Food item '{foodItemId}' was not found.");
        }

        var linkedIngredients = await dbContext
            .RecipeIngredients.Where(ri => ri.FoodItemId == foodItemId)
            .ToListAsync(cancellationToken);

        var distinctRecipeCount = linkedIngredients.Select(ri => ri.RecipeId).Distinct().Count();

        foreach (var ri in linkedIngredients)
        {
            ri.FoodItemId = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new UnlinkFoodItemRecipesResultDto(
            foodItemId,
            distinctRecipeCount,
            linkedIngredients.Count
        );
    }

    public async Task<MergeFoodItemsResultDto> MergeFoodItemsAsync(
        MergeFoodItemsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TargetFoodItemId == Guid.Empty)
        {
            throw new DomainValidationException("Target food item ID must be specified.");
        }

        var distinctSourceIds = (request.SourceFoodItemIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (distinctSourceIds.Count == 0)
        {
            throw new DomainValidationException(
                "At least one source food item ID must be specified."
            );
        }

        if (distinctSourceIds.Contains(request.TargetFoodItemId))
        {
            throw new DomainValidationException("Cannot merge a food item into itself.");
        }

        var target = await dbContext
            .FoodItems.Include(f => f.Aliases)
            .FirstOrDefaultAsync(f => f.Id == request.TargetFoodItemId, cancellationToken);

        if (target is null)
        {
            throw new NotFoundException("Target food item not found.");
        }

        var sources = await dbContext
            .FoodItems.Include(f => f.Aliases)
            .Where(f => distinctSourceIds.Contains(f.Id))
            .ToListAsync(cancellationToken);

        if (sources.Count != distinctSourceIds.Count)
        {
            throw new NotFoundException("One or more source food items not found.");
        }

        await using var tx = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // Re-point RecipeIngredients
        var repointedRecipeIngredients = await dbContext
            .RecipeIngredients.Where(ri =>
                ri.FoodItemId.HasValue && distinctSourceIds.Contains(ri.FoodItemId.Value)
            )
            .ToListAsync(cancellationToken);

        foreach (var ri in repointedRecipeIngredients)
        {
            ri.FoodItemId = target.Id;
        }

        var repointedRecipeIngredientCount = repointedRecipeIngredients.Count;

        // Re-point ShoppingListItems & consolidate
        var sourceShoppingItems = await dbContext
            .ShoppingListItems.Include(i => i.Sources)
            .Where(i => i.FoodItemId.HasValue && distinctSourceIds.Contains(i.FoodItemId.Value))
            .ToListAsync(cancellationToken);

        var repointedShoppingListItemCount = sourceShoppingItems.Count;
        var affectedListIds = sourceShoppingItems.Select(i => i.ShoppingListId).Distinct().ToList();

        var sourceNames = sources.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sourceShoppingItems)
        {
            item.FoodItemId = target.Id;
            if (sourceNames.Contains(item.DisplayName))
            {
                item.DisplayName = target.Name;
            }
        }

        var consolidatedCount = 0;
        if (affectedListIds.Count > 0)
        {
            var targetShoppingItems = await dbContext
                .ShoppingListItems.Include(i => i.Sources)
                .Where(i => affectedListIds.Contains(i.ShoppingListId) && i.FoodItemId == target.Id)
                .ToListAsync(cancellationToken);

            var allListItemsOnAffected = sourceShoppingItems
                .Concat(targetShoppingItems)
                .DistinctBy(i => i.Id)
                .ToList();

            var groups = allListItemsOnAffected.GroupBy(item =>
                (
                    item.ShoppingListId,
                    item.IsChecked,
                    item.IsPantryStapleExcluded,
                    ConsolidationKey: ShoppingConsolidator.BuildConsolidationKey(
                        target.Id,
                        target.Name,
                        ShoppingConsolidator.GetDimension(item.Unit, item.Quantity),
                        item.Unit,
                        item.CustomUnitName
                    )
                )
            );

            foreach (var group in groups)
            {
                var itemList = group.ToList();
                if (itemList.Count > 1)
                {
                    var inputs = new List<ConsolidatableIngredientInput>();
                    foreach (var item in itemList)
                    {
                        if (item.Sources.Count > 0)
                        {
                            foreach (var s in item.Sources)
                            {
                                inputs.Add(
                                    new ConsolidatableIngredientInput(
                                        target.Id,
                                        target.Name,
                                        target.IsPantryStaple,
                                        s.ContributedQuantity ?? item.Quantity,
                                        s.ContributedMaxQuantity ?? item.MaxQuantity,
                                        item.Unit,
                                        item.CustomUnitName,
                                        item.Notes,
                                        s.SourceKind,
                                        s.MealPlanEntryId,
                                        s.RecipeId,
                                        s.SourceLabel,
                                        s.ScheduledDate
                                    )
                                );
                            }
                        }
                        else
                        {
                            inputs.Add(
                                new ConsolidatableIngredientInput(
                                    target.Id,
                                    target.Name,
                                    target.IsPantryStaple,
                                    item.Quantity,
                                    item.MaxQuantity,
                                    item.Unit,
                                    item.CustomUnitName,
                                    item.Notes,
                                    ShoppingListSourceKind.Manual,
                                    null,
                                    null,
                                    "Manual",
                                    null
                                )
                            );
                        }
                    }

                    var consolidatedBuckets = ShoppingConsolidator.Consolidate(inputs);
                    if (consolidatedBuckets.Count > 0)
                    {
                        var bucket = consolidatedBuckets[0];
                        var surviving =
                            itemList.FirstOrDefault(i => !sourceShoppingItems.Contains(i))
                            ?? itemList.First();

                        surviving.Quantity = bucket.Quantity;
                        surviving.MaxQuantity = bucket.MaxQuantity;
                        surviving.Unit = bucket.Unit;
                        surviving.CustomUnitName = bucket.CustomUnitName;
                        surviving.Dimension = bucket.Dimension;
                        surviving.Notes = bucket.MergedNotes;
                        surviving.DisplayName = target.Name;

                        dbContext.ShoppingListItemSources.RemoveRange(surviving.Sources);
                        surviving.Sources.Clear();
                        foreach (var src in bucket.Sources)
                        {
                            var newSource = new ShoppingListItemSource
                            {
                                ShoppingListItemId = surviving.Id,
                                SourceKind = src.SourceKind,
                                MealPlanEntryId = src.MealPlanEntryId,
                                RecipeId = src.RecipeId,
                                SourceLabel = src.SourceLabel,
                                ScheduledDate = src.ScheduledDate,
                                ContributedQuantity = src.QuantityInConsolidatedUnit,
                                ContributedMaxQuantity = src.MaxQuantityInConsolidatedUnit,
                            };
                            dbContext.ShoppingListItemSources.Add(newSource);
                        }

                        foreach (var redundant in itemList.Where(i => i.Id != surviving.Id))
                        {
                            dbContext.ShoppingListItemSources.RemoveRange(redundant.Sources);
                            dbContext.ShoppingListItems.Remove(redundant);
                            consolidatedCount++;
                        }
                    }
                }
            }
        }

        // Review inbox drafts hygiene
        var readyJobs = await dbContext
            .IngestionJobs.Where(j =>
                j.Status == IngestionJobStatus.ReadyForReview && j.ExtractedDraftJson != null
            )
            .ToListAsync(cancellationToken);

        foreach (var job in readyJobs)
        {
            try
            {
                var draft = JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
                    job.ExtractedDraftJson!,
                    JsonOptions
                );

                if (
                    draft?.Ingredients is not null
                    && draft.Ingredients.Any(i =>
                        i.MatchedFoodItemId.HasValue
                        && distinctSourceIds.Contains(i.MatchedFoodItemId.Value)
                    )
                )
                {
                    var updatedIngredients = draft
                        .Ingredients.Select(ing =>
                        {
                            if (
                                ing.MatchedFoodItemId.HasValue
                                && distinctSourceIds.Contains(ing.MatchedFoodItemId.Value)
                            )
                            {
                                return ing with
                                {
                                    MatchedFoodItemId = target.Id,
                                    MatchedFoodItemName = target.Name,
                                };
                            }
                            return ing;
                        })
                        .ToList();

                    draft = draft with { Ingredients = updatedIngredients };
                    job.ExtractedDraftJson = JsonSerializer.Serialize(draft, JsonOptions);
                }
            }
            catch
            {
                // Ignore parse errors on drafts
            }
        }

        // Aliases & source removal
        var addedAliases = new List<string>();
        var knownNormKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            target.NormalizedName,
        };

        foreach (var a in target.Aliases)
        {
            knownNormKeys.Add(a.NormalizedAlias);
        }

        if (request.KeepSourceNamesAsAliases)
        {
            foreach (var source in sources)
            {
                var sourceAliases = source.Aliases.ToList();
                foreach (var alias in sourceAliases)
                {
                    if (knownNormKeys.Add(alias.NormalizedAlias))
                    {
                        alias.FoodItemId = target.Id;
                        addedAliases.Add(alias.Alias);
                    }
                    else
                    {
                        dbContext.FoodItemAliases.Remove(alias);
                    }
                }
            }

            foreach (var source in sources)
            {
                if (knownNormKeys.Add(source.NormalizedName))
                {
                    var newAlias = new FoodItemAlias
                    {
                        FoodItemId = target.Id,
                        Alias = source.Name,
                        NormalizedAlias = source.NormalizedName,
                    };
                    dbContext.FoodItemAliases.Add(newAlias);
                    addedAliases.Add(source.Name);
                }
            }
        }
        else
        {
            foreach (var source in sources)
            {
                foreach (var alias in source.Aliases)
                {
                    dbContext.FoodItemAliases.Remove(alias);
                }
            }
        }

        dbContext.FoodItems.RemoveRange(sources);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (tx is not null)
        {
            await tx.CommitAsync(cancellationToken);
        }

        // Broadcast realtime SignalR updates if affected
        if (
            affectedListIds.Count > 0
            && shoppingListService is not null
            && shoppingRealtimeNotifier is not null
        )
        {
            var now = timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
            var catalogSummary = await shoppingListService.GetShoppingListsAsync(cancellationToken);

            foreach (var listId in affectedListIds)
            {
                var listDetail = await shoppingListService.GetShoppingListByIdAsync(
                    listId,
                    cancellationToken
                );
                if (listDetail is not null)
                {
                    var listEvt = new ShoppingListRealtimeEventDto(
                        ShoppingRealtimeEventType.ListUpdated,
                        listId,
                        listDetail,
                        null,
                        null,
                        null,
                        "System",
                        now
                    );
                    var catEvt = new ShoppingListsCatalogChangedDto(
                        ShoppingRealtimeEventType.ListUpdated,
                        listId,
                        catalogSummary,
                        now
                    );
                    await shoppingRealtimeNotifier.NotifyListChangedAsync(
                        listEvt,
                        catEvt,
                        cancellationToken
                    );
                }
            }
        }

        var targetDto = (await GetFoodItemByIdAsync(target.Id, cancellationToken))!;
        return new MergeFoodItemsResultDto(
            targetDto,
            sources.Count,
            repointedRecipeIngredientCount,
            repointedShoppingListItemCount,
            consolidatedCount,
            addedAliases
        );
    }
}
