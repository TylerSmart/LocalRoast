using App.Core.Common;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Mcp;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class McpFoodCatalogService(
    AppDbContext dbContext,
    IFoodCatalogService foodCatalogService
) : IMcpFoodCatalogService
{
    private const int MaxBatchUpdateCount = 500;

    public Task<PagedResultDto<FoodItemDto>> ListFoodItemsAsync(
        McpListFoodItemsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var parsedCategory = ParseOptionalCategory(request.Category);
        var parsedSortBy = ParseOptionalSortBy(request.SortBy);
        var parsedSortDirection = ParseOptionalSortDirection(request.SortDirection);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        return foodCatalogService.GetFoodItemsPagedAsync(
            new FoodItemQueryParameters(
                Search: request.Search,
                IsPantryStaple: request.IsPantryStaple,
                Page: page,
                PageSize: pageSize,
                Category: parsedCategory,
                SortBy: parsedSortBy,
                SortDirection: parsedSortDirection
            ),
            cancellationToken
        );
    }

    public Task<FoodItemDto> CreateFoodItemAsync(
        string name,
        bool isPantryStaple = false,
        string? category = null,
        IReadOnlyList<string>? aliases = null,
        CancellationToken cancellationToken = default
    )
    {
        var parsedCategory = ParseOptionalCategory(category);

        return foodCatalogService.CreateFoodItemAsync(
            new CreateFoodItemRequest(
                Name: name,
                IsPantryStaple: isPantryStaple,
                Aliases: aliases,
                Category: parsedCategory
            ),
            cancellationToken
        );
    }

    public async Task<McpUpdateFoodItemsResultDto> UpdateFoodItemsAsync(
        McpUpdateFoodItemsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.Items is not { Count: > 0 })
        {
            throw new DomainValidationException("At least one food item update is required.");
        }

        if (request.Items.Count > MaxBatchUpdateCount)
        {
            throw new DomainValidationException(
                $"Cannot update more than {MaxBatchUpdateCount} food items per call."
            );
        }

        var updatedItems = new List<FoodItemDto>(request.Items.Count);

        foreach (var input in request.Items)
        {
            var existing = await ResolveExistingFoodItemAsync(input, cancellationToken);

            var effectiveName = !string.IsNullOrWhiteSpace(input.Name)
                ? input.Name.Trim()
                : existing.Name;
            var effectivePantry = input.IsPantryStaple ?? existing.IsPantryStaple;
            var effectiveCategory = !string.IsNullOrWhiteSpace(input.Category)
                ? ParseOptionalCategory(input.Category)
                : existing.Category;
            var effectiveAliases = input.Aliases ?? existing.Aliases;

            var updated =
                await foodCatalogService.UpdateFoodItemAsync(
                    existing.Id,
                    new UpdateFoodItemRequest(
                        Name: effectiveName,
                        IsPantryStaple: effectivePantry,
                        Aliases: effectiveAliases,
                        Category: effectiveCategory
                    ),
                    cancellationToken
                ) ?? throw new NotFoundException($"Food item '{existing.Id}' was not found.");

            updatedItems.Add(updated);
        }

        return new McpUpdateFoodItemsResultDto(updatedItems.Count, updatedItems);
    }

    public Task<MergeFoodItemsResultDto> MergeFoodItemsAsync(
        MergeFoodItemsRequest request,
        CancellationToken cancellationToken = default
    ) => foodCatalogService.MergeFoodItemsAsync(request, cancellationToken);

    private async Task<FoodItemDto> ResolveExistingFoodItemAsync(
        McpFoodItemUpdateInput input,
        CancellationToken cancellationToken
    )
    {
        if (input.FoodItemId.HasValue)
        {
            return await foodCatalogService.GetFoodItemByIdAsync(
                    input.FoodItemId.Value,
                    cancellationToken
                )
                ?? throw new NotFoundException(
                    $"Food item '{input.FoodItemId.Value}' was not found."
                );
        }

        if (!string.IsNullOrWhiteSpace(input.CurrentName))
        {
            var norm = input.CurrentName.Trim().ToLowerInvariant();
            var matchedId = await dbContext
                .FoodItems.AsNoTracking()
                .Where(f =>
                    f.NormalizedName == norm || f.Aliases.Any(a => a.NormalizedAlias == norm)
                )
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (!matchedId.HasValue)
            {
                throw new NotFoundException($"Food item '{input.CurrentName}' was not found.");
            }

            return await foodCatalogService.GetFoodItemByIdAsync(matchedId.Value, cancellationToken)
                ?? throw new NotFoundException($"Food item '{input.CurrentName}' was not found.");
        }

        throw new DomainValidationException(
            "Each food item update requires either foodItemId or currentName."
        );
    }

    private static FoodCategory? ParseOptionalCategory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return FoodCategories.Parse(raw)
            ?? throw new DomainValidationException($"Unknown food category '{raw}'.");
    }

    private static FoodItemSortBy? ParseOptionalSortBy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return
            Enum.TryParse<FoodItemSortBy>(raw.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : throw new DomainValidationException($"Unknown sortBy '{raw}'.");
    }

    private static FoodItemSortDirection? ParseOptionalSortDirection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return
            Enum.TryParse<FoodItemSortDirection>(raw.Trim(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : throw new DomainValidationException($"Unknown sortDirection '{raw}'.");
    }
}
