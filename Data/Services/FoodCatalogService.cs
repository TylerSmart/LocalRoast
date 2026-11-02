using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class FoodCatalogService(AppDbContext dbContext) : IFoodCatalogService
{
    private static string NormalizeKey(string input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new DomainValidationException("Value cannot be empty.");
        }
        return trimmed.ToLowerInvariant();
    }

    public async Task<IReadOnlyList<StoreAisleDto>> GetStoreAislesAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await dbContext
            .StoreAisles.AsNoTracking()
            .OrderBy(a => a.SortOrder)
            .Select(a => new StoreAisleDto(a.Id, a.Name, a.SortOrder, a.IsSystemSeeded))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FoodItemDto>> GetFoodItemsAsync(
        FoodItemQueryParameters query,
        CancellationToken cancellationToken = default
    )
    {
        var q = dbContext.FoodItems.AsNoTracking().AsQueryable();

        if (query.StoreAisleId.HasValue)
        {
            q = q.Where(f => f.DefaultStoreAisleId == query.StoreAisleId.Value);
        }

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
                f.DefaultStoreAisleId,
                f.DefaultStoreAisle != null ? f.DefaultStoreAisle.Name : null,
                f.DefaultStoreAisle != null ? (int?)f.DefaultStoreAisle.SortOrder : null,
                f.IsPantryStaple,
                f.Aliases.Select(a => a.Alias).ToList(),
                f.RecipeIngredients.Select(ri => ri.RecipeId).Distinct().Count(),
                f.CreatedAt,
                f.ModifiedAt
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
                f.DefaultStoreAisleId,
                f.DefaultStoreAisle != null ? f.DefaultStoreAisle.Name : null,
                f.DefaultStoreAisle != null ? (int?)f.DefaultStoreAisle.SortOrder : null,
                f.IsPantryStaple,
                f.Aliases.Select(a => a.Alias).ToList(),
                f.RecipeIngredients.Select(ri => ri.RecipeId).Distinct().Count(),
                f.CreatedAt,
                f.ModifiedAt
            ))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<FoodItemDto> CreateFoodItemAsync(
        CreateFoodItemRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var normName = NormalizeKey(request.Name);

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

        if (request.DefaultStoreAisleId.HasValue)
        {
            var aisleExists = await dbContext.StoreAisles.AnyAsync(
                a => a.Id == request.DefaultStoreAisleId.Value,
                cancellationToken
            );
            if (!aisleExists)
            {
                throw new DomainValidationException("Selected store aisle does not exist.");
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
            DefaultStoreAisleId = request.DefaultStoreAisleId,
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

        var normName = NormalizeKey(request.Name);

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

        if (request.DefaultStoreAisleId.HasValue)
        {
            var aisleExists = await dbContext.StoreAisles.AnyAsync(
                a => a.Id == request.DefaultStoreAisleId.Value,
                cancellationToken
            );
            if (!aisleExists)
            {
                throw new DomainValidationException("Selected store aisle does not exist.");
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
        foodItem.DefaultStoreAisleId = request.DefaultStoreAisleId;
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

        var isReferenced = await dbContext.RecipeIngredients.AnyAsync(
            ri => ri.FoodItemId == id,
            cancellationToken
        );
        if (isReferenced)
        {
            throw new ConflictException(
                "Cannot delete a food item that is used in one or more recipes."
            );
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
}
