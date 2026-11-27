using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.MealPlans;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed partial class RecipeService(
    AppDbContext dbContext,
    IMediaStorageService mediaStorageService,
    TimeProvider? timeProvider = null,
    IIngestionQueueSignal? queueSignal = null,
    ILlmSettingsService? llmSettingsService = null
) : IRecipeService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<RecipeSummaryDto>> GetRecipesAsync(
        RecipeQueryParameters query,
        CancellationToken cancellationToken = default
    )
    {
        var q = await BuildRecipeQueryAsync(query, cancellationToken);
        var recipeIds = await q.Select(r => r.Id).ToListAsync(cancellationToken);
        if (recipeIds.Count == 0 && !string.IsNullOrWhiteSpace(query.Search))
        {
            return await GetFuzzyRecipeMatchesAsync(query, cancellationToken);
        }

        return await HydrateSummariesByIdsAsync(recipeIds, cancellationToken);
    }

    public async Task<RecipePagedResultDto> GetRecipesPagedAsync(
        RecipeQueryParameters query,
        CancellationToken cancellationToken = default
    )
    {
        var isFuzzySearch = query.FuzzySearch && !string.IsNullOrWhiteSpace(query.Search);
        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? 24, 1, 200);

        if (isFuzzySearch)
        {
            var fuzzyMatches = await GetFuzzyRecipeMatchesAsync(query, cancellationToken);
            return CreateFuzzyPage(fuzzyMatches, query, page, pageSize);
        }

        var q = await BuildRecipeQueryAsync(query, cancellationToken);
        int? totalCount = query.IncludeTotalCount ? await q.CountAsync(cancellationToken) : null;

        if (totalCount == 0 && !string.IsNullOrWhiteSpace(query.Search))
        {
            var fuzzyMatches = await GetFuzzyRecipeMatchesAsync(query, cancellationToken);
            return CreateFuzzyPage(fuzzyMatches, query, page, pageSize, isFuzzySearch: true);
        }

        var skip = (int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize);
        var pageIds = await q.Select(r => r.Id)
            .Skip(skip)
            .Take(pageSize + (totalCount is null ? 1 : 0))
            .ToListAsync(cancellationToken);

        if (
            totalCount is null
            && page == 1
            && pageIds.Count == 0
            && !string.IsNullOrWhiteSpace(query.Search)
        )
        {
            var fuzzyMatches = await GetFuzzyRecipeMatchesAsync(query, cancellationToken);
            return CreateFuzzyPage(fuzzyMatches, query, page, pageSize, isFuzzySearch: true);
        }

        var hasMore = totalCount.HasValue
            ? (long)page * pageSize < totalCount.Value
            : pageIds.Count > pageSize;
        if (pageIds.Count > pageSize)
        {
            pageIds.RemoveAt(pageSize);
        }

        var items = await HydrateSummariesByIdsAsync(pageIds, cancellationToken);
        return new RecipePagedResultDto(items, totalCount, page, pageSize, hasMore);
    }

    public async Task<IReadOnlyList<RecipeAutocompleteItemDto>> SearchRecipeAutocompleteAsync(
        string? query = null,
        Guid? excludeRecipeId = null,
        int limit = 20,
        CancellationToken cancellationToken = default
    )
    {
        var effectiveLimit = Math.Clamp(limit, 1, 50);

        var excludedIds = new HashSet<Guid>();
        if (excludeRecipeId.HasValue)
        {
            excludedIds.Add(excludeRecipeId.Value);

            var edges = await dbContext
                .RecipeIngredients.AsNoTracking()
                .Where(ri => ri.LinkedRecipeId != null)
                .Select(ri => new { ri.RecipeId, LinkedId = ri.LinkedRecipeId!.Value })
                .ToListAsync(cancellationToken);

            var parentsByChild = new Dictionary<Guid, List<Guid>>();
            foreach (var edge in edges)
            {
                if (!parentsByChild.TryGetValue(edge.LinkedId, out var parents))
                {
                    parents = [];
                    parentsByChild[edge.LinkedId] = parents;
                }
                parents.Add(edge.RecipeId);
            }

            var queue = new Queue<Guid>();
            queue.Enqueue(excludeRecipeId.Value);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (parentsByChild.TryGetValue(current, out var parents))
                {
                    foreach (var parentId in parents)
                    {
                        if (excludedIds.Add(parentId))
                        {
                            queue.Enqueue(parentId);
                        }
                    }
                }
            }
        }

        var baseQuery = dbContext
            .Recipes.AsNoTracking()
            .Where(r => r.Status != RecipeStatus.Archived);

        if (excludedIds.Count > 0)
        {
            baseQuery = baseQuery.Where(r => !excludedIds.Contains(r.Id));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return await baseQuery
                .OrderBy(r => r.Status == RecipeStatus.Published ? 0 : 1)
                .ThenBy(r => r.Title)
                .Take(effectiveLimit)
                .Select(r => new RecipeAutocompleteItemDto(
                    r.Id,
                    r.Title,
                    r.Slug,
                    r.Status,
                    r.BaseServings,
                    r.YieldDisplay
                ))
                .ToListAsync(cancellationToken);
        }

        var trimmed = query.Trim();
        var normalizedQuery = NormalizeAutocompleteTitle(trimmed);
        var searchTokens = NormalizeSearchTokens(trimmed);

        var isRelational = dbContext.Database.IsRelational();
        var sqlFiltered = baseQuery;
        if (searchTokens.Length > 0)
        {
            foreach (var token in searchTokens)
            {
                var currentToken = token;
                sqlFiltered = isRelational
                    ? sqlFiltered.Where(r => r.Title.Contains(currentToken))
                    : sqlFiltered.Where(r => r.Title.ToLower().Contains(currentToken));
            }
        }
        else
        {
            var lower = trimmed.ToLowerInvariant();
            sqlFiltered = isRelational
                ? sqlFiltered.Where(r => r.Title.Contains(trimmed))
                : sqlFiltered.Where(r => r.Title.ToLower().Contains(lower));
        }

        var matched = await sqlFiltered
            .Select(r => new RecipeAutocompleteItemDto(
                r.Id,
                r.Title,
                r.Slug,
                r.Status,
                r.BaseServings,
                r.YieldDisplay
            ))
            .ToListAsync(cancellationToken);

        if (matched.Count == 0 && searchTokens.Length > 0)
        {
            var allCandidates = await baseQuery
                .Select(r => new RecipeAutocompleteItemDto(
                    r.Id,
                    r.Title,
                    r.Slug,
                    r.Status,
                    r.BaseServings,
                    r.YieldDisplay
                ))
                .ToListAsync(cancellationToken);

            return allCandidates
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Rank = GetFallbackAutocompleteRank(
                        candidate.Title,
                        normalizedQuery,
                        searchTokens
                    ),
                })
                .Where(x => x.Rank.HasValue)
                .OrderBy(x => x.Rank!.Value)
                .ThenBy(x => x.Candidate.Status == RecipeStatus.Published ? 0 : 1)
                .ThenBy(x => x.Candidate.Title.Length)
                .ThenBy(x => x.Candidate.Title, StringComparer.OrdinalIgnoreCase)
                .Take(effectiveLimit)
                .Select(x => x.Candidate)
                .ToList();
        }

        return matched
            .OrderBy(r => GetAutocompleteMatchRank(r.Title, normalizedQuery, searchTokens))
            .ThenBy(r => r.Status == RecipeStatus.Published ? 0 : 1)
            .ThenBy(r => r.Title.Length)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Take(effectiveLimit)
            .ToList();
    }

    private static string NormalizeAutocompleteTitle(string value) =>
        string.Join(' ', NormalizeSearchTokens(value));

    private static int GetAutocompleteMatchRank(
        string title,
        string normalizedQuery,
        string[] searchTokens
    )
    {
        var normalizedTitle = NormalizeAutocompleteTitle(title);
        if (string.Equals(normalizedTitle, normalizedQuery, StringComparison.Ordinal))
        {
            return 0;
        }

        if (
            !string.IsNullOrEmpty(normalizedQuery)
            && normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal)
        )
        {
            return 1;
        }

        if (
            !string.IsNullOrEmpty(normalizedQuery)
            && normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal)
        )
        {
            return 2;
        }

        if (
            searchTokens.Length > 0
            && searchTokens.All(token => normalizedTitle.Contains(token, StringComparison.Ordinal))
        )
        {
            return 3;
        }

        return 4;
    }

    private static int? GetFallbackAutocompleteRank(
        string title,
        string normalizedQuery,
        string[] searchTokens
    )
    {
        var normalizedTitle = NormalizeAutocompleteTitle(title);
        if (
            searchTokens.Length > 0
            && searchTokens.All(token => normalizedTitle.Contains(token, StringComparison.Ordinal))
        )
        {
            return GetAutocompleteMatchRank(title, normalizedQuery, searchTokens);
        }

        if (searchTokens.All(token => token.Length < 5))
        {
            return null;
        }

        var editDistance = GetTitleMatchDistance(searchTokens, title);
        return editDistance.HasValue ? 5 + editDistance.Value : null;
    }

    private async Task<IReadOnlyList<RecipeSummaryDto>> GetFuzzyRecipeMatchesAsync(
        RecipeQueryParameters query,
        CancellationToken cancellationToken
    )
    {
        var searchTokens = NormalizeSearchTokens(query.Search);
        if (searchTokens.Length == 0 || searchTokens.All(token => token.Length < 5))
        {
            return [];
        }

        var unsearchedQuery = await BuildRecipeQueryAsync(
            query with
            {
                Search = null,
            },
            cancellationToken
        );
        var candidates = await unsearchedQuery
            .Select(recipe => new { recipe.Id, recipe.Title })
            .ToListAsync(cancellationToken);

        var matchedIds = candidates
            .Select(
                (candidate, order) =>
                    new
                    {
                        candidate.Id,
                        Order = order,
                        EditDistance = GetTitleMatchDistance(searchTokens, candidate.Title),
                    }
            )
            .Where(match => match.EditDistance.HasValue)
            .OrderBy(match => match.EditDistance)
            .ThenBy(match => match.Order)
            .Select(match => match.Id)
            .ToArray();

        if (matchedIds.Length == 0)
        {
            return [];
        }

        return await HydrateSummariesByIdsAsync(matchedIds, cancellationToken);
    }

    private static RecipePagedResultDto CreateFuzzyPage(
        IReadOnlyList<RecipeSummaryDto> matches,
        RecipeQueryParameters query,
        int page,
        int pageSize,
        bool isFuzzySearch = true
    )
    {
        var skip = (int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize);
        var items = matches.Skip(skip).Take(pageSize).ToArray();
        return new RecipePagedResultDto(
            items,
            query.IncludeTotalCount ? matches.Count : null,
            page,
            pageSize,
            (long)page * pageSize < matches.Count,
            isFuzzySearch
        );
    }

    private static string[] NormalizeSearchTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            normalized.Append(
                char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' '
            );
        }

        return normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static int? GetTitleMatchDistance(string[] searchTokens, string title)
    {
        var titleTokens = NormalizeSearchTokens(title);
        if (searchTokens.Length == 0 || searchTokens.Length > titleTokens.Length)
        {
            return null;
        }

        var usedTitleTokens = new bool[titleTokens.Length];
        var totalEditDistance = 0;
        var fuzzyTokenCount = 0;

        foreach (var searchToken in searchTokens)
        {
            var exactMatchIndex = -1;
            for (var index = 0; index < titleTokens.Length; index++)
            {
                if (!usedTitleTokens[index] && titleTokens[index] == searchToken)
                {
                    exactMatchIndex = index;
                    break;
                }
            }

            if (exactMatchIndex >= 0)
            {
                usedTitleTokens[exactMatchIndex] = true;
                continue;
            }

            if (searchToken.Length < 5 || fuzzyTokenCount > 0)
            {
                return null;
            }

            var closestMatchIndex = -1;
            var closestEditDistance = 2;
            for (var index = 0; index < titleTokens.Length; index++)
            {
                if (usedTitleTokens[index])
                {
                    continue;
                }

                var editDistance = GetEditDistanceAtMostOne(searchToken, titleTokens[index]);
                if (editDistance < closestEditDistance)
                {
                    closestMatchIndex = index;
                    closestEditDistance = editDistance;
                }
            }

            if (closestMatchIndex < 0)
            {
                return null;
            }

            usedTitleTokens[closestMatchIndex] = true;
            fuzzyTokenCount++;
            totalEditDistance += closestEditDistance;
        }

        return totalEditDistance;
    }

    private static int GetEditDistanceAtMostOne(string left, string right)
    {
        if (left == right)
        {
            return 0;
        }

        if (Math.Abs(left.Length - right.Length) > 1)
        {
            return 2;
        }

        if (left.Length == right.Length)
        {
            var differences = 0;
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index] && ++differences > 1)
                {
                    return 2;
                }
            }

            return differences;
        }

        var shorter = left.Length < right.Length ? left : right;
        var longer = left.Length < right.Length ? right : left;
        var shorterIndex = 0;
        var longerIndex = 0;
        var hasSkippedCharacter = false;

        while (shorterIndex < shorter.Length && longerIndex < longer.Length)
        {
            if (shorter[shorterIndex] == longer[longerIndex])
            {
                shorterIndex++;
                longerIndex++;
            }
            else if (hasSkippedCharacter)
            {
                return 2;
            }
            else
            {
                hasSkippedCharacter = true;
                longerIndex++;
            }
        }

        return 1;
    }

    private async Task<IQueryable<Recipe>> BuildRecipeQueryAsync(
        RecipeQueryParameters query,
        CancellationToken cancellationToken
    )
    {
        var q = dbContext.Recipes.AsNoTracking().AsQueryable();

        if (query.Status.HasValue)
        {
            q = q.Where(r => r.Status == query.Status.Value);
        }
        else
        {
            q = q.Where(r => r.Status != RecipeStatus.Archived);
        }

        if (query.Difficulty.HasValue)
        {
            q = q.Where(r => r.Difficulty == query.Difficulty.Value);
        }

        if (query.MaxTotalTimeMinutes.HasValue)
        {
            q = q.Where(r =>
                r.TotalTimeMinutes != null && r.TotalTimeMinutes <= query.MaxTotalTimeMinutes.Value
            );
        }

        if (query.MinRating.HasValue)
        {
            q = q.Where(r => r.AverageRating != null && r.AverageRating >= query.MinRating.Value);
        }

        if (query.ForgottenFavoritesOnly)
        {
            var days = Math.Clamp(query.ForgottenDaysThreshold, 1, 3650);
            var cutoff = _timeProvider.GetUtcNow().AddDays(-days);

            q = q.Where(r =>
                ((r.AverageRating != null && r.AverageRating >= 4.0m) || r.TimesCooked >= 2)
                && (r.LastCookedAt == null || r.LastCookedAt <= cutoff)
            );
        }

        if (query.FoodItemId.HasValue)
        {
            q = q.Where(r => r.Ingredients.Any(i => i.FoodItemId == query.FoodItemId.Value));
        }

        if (query.CollectionId.HasValue)
        {
            q = q.Where(r =>
                r.RecipeCollections.Any(rc => rc.CollectionId == query.CollectionId.Value)
            );
        }

        if (query.TagIds is { Length: > 0 })
        {
            foreach (var tagId in query.TagIds.Distinct())
            {
                var currentTagId = tagId;
                q = q.Where(r => r.RecipeTags.Any(rt => rt.TagId == currentTagId));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var trimmed = query.Search.Trim();
            var norm = trimmed.ToLowerInvariant();
            var isRelational = dbContext.Database.IsRelational();

            var childMatchedIds = new HashSet<Guid>();

            var matchingCollectionIds = await dbContext
                .Collections.AsNoTracking()
                .Where(c => c.NormalizedName.Contains(norm))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
            if (matchingCollectionIds.Count > 0)
            {
                var ids = await dbContext
                    .RecipeCollections.AsNoTracking()
                    .Where(rc => matchingCollectionIds.Contains(rc.CollectionId))
                    .Select(rc => rc.RecipeId)
                    .ToListAsync(cancellationToken);
                childMatchedIds.UnionWith(ids);
            }

            var matchingTagIds = await dbContext
                .Tags.AsNoTracking()
                .Where(t => t.NormalizedName.Contains(norm))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
            if (matchingTagIds.Count > 0)
            {
                var ids = await dbContext
                    .RecipeTags.AsNoTracking()
                    .Where(rt => matchingTagIds.Contains(rt.TagId))
                    .Select(rt => rt.RecipeId)
                    .ToListAsync(cancellationToken);
                childMatchedIds.UnionWith(ids);
            }

            var matchingFoodIds = await dbContext
                .FoodItems.AsNoTracking()
                .Where(f => f.NormalizedName.Contains(norm))
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);
            var aliasFoodIds = await dbContext
                .FoodItemAliases.AsNoTracking()
                .Where(a => a.NormalizedAlias.Contains(norm))
                .Select(a => a.FoodItemId)
                .ToListAsync(cancellationToken);
            var combinedFoodIds = matchingFoodIds.Concat(aliasFoodIds).Distinct().ToList();

            var ingRecipeIds =
                combinedFoodIds.Count > 0
                    ? await dbContext
                        .RecipeIngredients.AsNoTracking()
                        .Where(ri =>
                            (
                                ri.FoodItemId.HasValue
                                && combinedFoodIds.Contains(ri.FoodItemId.Value)
                            )
                            || (
                                isRelational
                                    ? ri.DisplayName.Contains(trimmed)
                                    : ri.DisplayName.ToLower().Contains(norm)
                            )
                        )
                        .Select(ri => ri.RecipeId)
                        .Distinct()
                        .ToListAsync(cancellationToken)
                    : await dbContext
                        .RecipeIngredients.AsNoTracking()
                        .Where(ri =>
                            isRelational
                                ? ri.DisplayName.Contains(trimmed)
                                : ri.DisplayName.ToLower().Contains(norm)
                        )
                        .Select(ri => ri.RecipeId)
                        .Distinct()
                        .ToListAsync(cancellationToken);
            childMatchedIds.UnionWith(ingRecipeIds);

            var cookLogRecipeIds = await dbContext
                .CookLogs.AsNoTracking()
                .Where(cl =>
                    cl.Notes != null
                    && (
                        isRelational
                            ? cl.Notes.Contains(trimmed)
                            : cl.Notes.ToLower().Contains(norm)
                    )
                )
                .Select(cl => cl.RecipeId)
                .Distinct()
                .ToListAsync(cancellationToken);
            childMatchedIds.UnionWith(cookLogRecipeIds);

            var childMatchedArray = childMatchedIds.ToArray();
            if (isRelational)
            {
                q =
                    childMatchedArray.Length > 0
                        ? q.Where(r =>
                            r.Title.Contains(trimmed)
                            || (r.Description != null && r.Description.Contains(trimmed))
                            || (r.SourceName != null && r.SourceName.Contains(trimmed))
                            || (r.SourceAuthor != null && r.SourceAuthor.Contains(trimmed))
                            || (r.SourceBookTitle != null && r.SourceBookTitle.Contains(trimmed))
                            || childMatchedArray.Contains(r.Id)
                        )
                        : q.Where(r =>
                            r.Title.Contains(trimmed)
                            || (r.Description != null && r.Description.Contains(trimmed))
                            || (r.SourceName != null && r.SourceName.Contains(trimmed))
                            || (r.SourceAuthor != null && r.SourceAuthor.Contains(trimmed))
                            || (r.SourceBookTitle != null && r.SourceBookTitle.Contains(trimmed))
                        );
            }
            else
            {
                q =
                    childMatchedArray.Length > 0
                        ? q.Where(r =>
                            r.Title.ToLower().Contains(norm)
                            || (r.Description != null && r.Description.ToLower().Contains(norm))
                            || (r.SourceName != null && r.SourceName.ToLower().Contains(norm))
                            || (r.SourceAuthor != null && r.SourceAuthor.ToLower().Contains(norm))
                            || (
                                r.SourceBookTitle != null
                                && r.SourceBookTitle.ToLower().Contains(norm)
                            )
                            || childMatchedArray.Contains(r.Id)
                        )
                        : q.Where(r =>
                            r.Title.ToLower().Contains(norm)
                            || (r.Description != null && r.Description.ToLower().Contains(norm))
                            || (r.SourceName != null && r.SourceName.ToLower().Contains(norm))
                            || (r.SourceAuthor != null && r.SourceAuthor.ToLower().Contains(norm))
                            || (
                                r.SourceBookTitle != null
                                && r.SourceBookTitle.ToLower().Contains(norm)
                            )
                        );
            }
        }

        return query.ForgottenFavoritesOnly
            ? q.OrderBy(r => r.LastCookedAt)
                .ThenByDescending(r => r.AverageRating)
                .ThenByDescending(r => r.TimesCooked)
                .ThenByDescending(r => r.ModifiedAt)
                .ThenByDescending(r => r.Id)
            : q.OrderByDescending(r => r.ModifiedAt).ThenByDescending(r => r.Id);
    }

    private async Task<IReadOnlyList<RecipeSummaryDto>> HydrateSummariesByIdsAsync(
        IReadOnlyList<Guid> orderedIds,
        CancellationToken cancellationToken
    )
    {
        if (orderedIds.Count == 0)
        {
            return [];
        }

        var distinctIds = orderedIds.Distinct().ToArray();

        var rows = await dbContext
            .Recipes.AsNoTracking()
            .Where(r => distinctIds.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Slug,
                r.Description,
                r.Status,
                r.BaseServings,
                r.YieldDisplay,
                r.PrepTimeMinutes,
                r.CookTimeMinutes,
                r.TotalTimeMinutes,
                r.Difficulty,
                r.SourceName,
                r.ThumbnailImagePath,
                IngredientCount = r.Ingredients.Count,
                StepCount = r.Steps.Count,
                r.TimesCooked,
                r.AverageRating,
                r.CreatedAt,
                r.ModifiedAt,
                r.LastCookedAt,
                r.RatingCount,
                LastCookNote = r
                    .CookLogs.OrderByDescending(cl => cl.CookedAt)
                    .ThenByDescending(cl => cl.CreatedAt)
                    .Where(cl => cl.Notes != null && cl.Notes.Trim() != "")
                    .Select(cl => cl.Notes)
                    .FirstOrDefault(),
            })
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        var collectionBadges = (
            await dbContext
                .RecipeCollections.AsNoTracking()
                .Where(rc => distinctIds.Contains(rc.RecipeId))
                .OrderBy(rc => rc.Collection.SortOrder)
                .ThenBy(rc => rc.Collection.Name)
                .Select(rc => new
                {
                    rc.RecipeId,
                    Badge = new RecipeCollectionBadgeDto(
                        rc.Collection.Id,
                        rc.Collection.Name,
                        rc.Collection.Slug
                    ),
                })
                .ToListAsync(cancellationToken)
        ).ToLookup(x => x.RecipeId, x => x.Badge);

        var tagBadges = (
            await dbContext
                .RecipeTags.AsNoTracking()
                .Where(rt => distinctIds.Contains(rt.RecipeId))
                .OrderBy(rt => rt.Tag.Category)
                .ThenBy(rt => rt.Tag.SortOrder)
                .ThenBy(rt => rt.Tag.Name)
                .Select(rt => new
                {
                    rt.RecipeId,
                    Badge = new RecipeTagBadgeDto(
                        rt.Tag.Id,
                        rt.Tag.Name,
                        rt.Tag.Slug,
                        rt.Tag.Category
                    ),
                })
                .ToListAsync(cancellationToken)
        ).ToLookup(x => x.RecipeId, x => x.Badge);

        var results = new List<RecipeSummaryDto>(orderedIds.Count);
        foreach (var id in orderedIds)
        {
            if (!rows.TryGetValue(id, out var r))
            {
                continue;
            }

            results.Add(
                new RecipeSummaryDto(
                    r.Id,
                    r.Title,
                    r.Slug,
                    r.Description,
                    r.Status,
                    r.BaseServings,
                    r.YieldDisplay,
                    r.PrepTimeMinutes,
                    r.CookTimeMinutes,
                    r.TotalTimeMinutes,
                    r.Difficulty,
                    r.SourceName,
                    r.ThumbnailImagePath,
                    r.IngredientCount,
                    r.StepCount,
                    r.TimesCooked,
                    r.AverageRating,
                    r.CreatedAt,
                    r.ModifiedAt,
                    collectionBadges[id].ToList(),
                    tagBadges[id].ToList(),
                    r.LastCookedAt,
                    r.RatingCount,
                    r.LastCookNote
                )
            );
        }

        return results;
    }

    public async Task<IReadOnlyList<RecipeDetailDto>> GetRandomRecipesAsync(
        RandomRecipeQueryParameters query,
        CancellationToken cancellationToken = default
    )
    {
        if (query.Status == RecipeStatus.Archived)
        {
            return [];
        }

        var effectiveCount = Math.Clamp(query.Count, 1, 25);
        var effectiveStatus = query.Status ?? RecipeStatus.Published;

        var candidateIds = await dbContext
            .Recipes.AsNoTracking()
            .Where(r => r.Status != RecipeStatus.Archived && r.Status == effectiveStatus)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (query.ExcludeIds is { Length: > 0 })
        {
            var excluded = query.ExcludeIds.ToHashSet();
            candidateIds.RemoveAll(excluded.Contains);
        }

        if (candidateIds.Count == 0)
        {
            return [];
        }

        var takeCount = Math.Min(effectiveCount, candidateIds.Count);
        for (var i = 0; i < takeCount; i++)
        {
            var swapIndex = Random.Shared.Next(i, candidateIds.Count);
            (candidateIds[i], candidateIds[swapIndex]) = (candidateIds[swapIndex], candidateIds[i]);
        }

        var selectedIds = candidateIds.GetRange(0, takeCount);

        var recipes = await dbContext
            .Recipes.AsNoTracking()
            .AsSplitQuery()
            .Where(r => selectedIds.Contains(r.Id))
            .Include(r => r.RecipeCollections)
                .ThenInclude(rc => rc.Collection)
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
            .ToListAsync(cancellationToken);

        var ingredients = await dbContext
            .RecipeIngredients.AsNoTracking()
            .AsSplitQuery()
            .Where(i => selectedIds.Contains(i.RecipeId))
            .Include(i => i.FoodItem)
            .Include(i => i.LinkedRecipe)
            .Include(i => i.Replacements)
                .ThenInclude(rep => rep.FoodItem)
            .ToListAsync(cancellationToken);

        var steps = await dbContext
            .RecipeSteps.AsNoTracking()
            .Where(s => selectedIds.Contains(s.RecipeId))
            .Include(s => s.Timers)
            .ToListAsync(cancellationToken);

        var attachments = await dbContext
            .RecipeAttachments.AsNoTracking()
            .Where(a => selectedIds.Contains(a.RecipeId))
            .ToListAsync(cancellationToken);

        var cookLogs = await dbContext
            .CookLogs.AsNoTracking()
            .Where(c => selectedIds.Contains(c.RecipeId))
            .ToListAsync(cancellationToken);

        var ingestionJobs = await dbContext
            .IngestionJobs.AsNoTracking()
            .Where(j =>
                j.ResultingRecipeId.HasValue && selectedIds.Contains(j.ResultingRecipeId.Value)
            )
            .Select(j => new RecipeIngestionJobMetadata(
                j.Id,
                j.ResultingRecipeId,
                j.CreatedAt,
                j.ExtractedModel,
                j.RefinedModel
            ))
            .ToListAsync(cancellationToken);

        var linkedRecipeIds = ingredients
            .Where(i => i.LinkedRecipeId.HasValue)
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .ToList();

        var subRecipesById = await LoadSubRecipesByIdAsync(linkedRecipeIds, cancellationToken);

        var recipesById = recipes.ToDictionary(r => r.Id);
        var ingredientsByRecipe = ingredients.ToLookup(i => i.RecipeId);
        var stepsByRecipe = steps.ToLookup(s => s.RecipeId);
        var attachmentsByRecipe = attachments.ToLookup(a => a.RecipeId);
        var cookLogsByRecipe = cookLogs.ToLookup(c => c.RecipeId);
        var jobsByRecipe = ingestionJobs
            .Where(j => j.ResultingRecipeId.HasValue)
            .ToLookup(j => j.ResultingRecipeId!.Value);

        var results = new List<RecipeDetailDto>(selectedIds.Count);
        foreach (var id in selectedIds)
        {
            if (!recipesById.TryGetValue(id, out var recipe))
            {
                continue;
            }

            results.Add(
                MapRecipeDetailDto(
                    recipe,
                    ingredientsByRecipe[id],
                    stepsByRecipe[id],
                    attachmentsByRecipe[id],
                    cookLogsByRecipe[id],
                    jobsByRecipe[id],
                    subRecipesById
                )
            );
        }

        return results;
    }

    public async Task<RecipeDetailDto?> GetRecipeByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var recipe = await dbContext
            .Recipes.AsNoTracking()
            .AsSplitQuery()
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.LinkedRecipe)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.Replacements)
                    .ThenInclude(rep => rep.FoodItem)
            .Include(r => r.Steps)
                .ThenInclude(s => s.Timers)
            .Include(r => r.RecipeCollections)
                .ThenInclude(rc => rc.Collection)
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
            .Include(r => r.Attachments)
            .Include(r => r.CookLogs)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recipe is null)
        {
            return null;
        }

        var ingestionJobs = await dbContext
            .IngestionJobs.AsNoTracking()
            .Where(j => j.ResultingRecipeId == id)
            .Select(j => new RecipeIngestionJobMetadata(
                j.Id,
                j.ResultingRecipeId,
                j.CreatedAt,
                j.ExtractedModel,
                j.RefinedModel
            ))
            .ToListAsync(cancellationToken);

        var linkedRecipeIds = recipe
            .Ingredients.Where(i => i.LinkedRecipeId.HasValue)
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .ToList();

        var subRecipesById = await LoadSubRecipesByIdAsync(linkedRecipeIds, cancellationToken);

        return MapRecipeDetailDto(
            recipe,
            recipe.Ingredients,
            recipe.Steps,
            recipe.Attachments,
            recipe.CookLogs,
            ingestionJobs,
            subRecipesById
        );
    }

    private async Task<Dictionary<Guid, SubRecipePreviewDto>> LoadSubRecipesByIdAsync(
        IReadOnlyCollection<Guid> linkedRecipeIds,
        CancellationToken cancellationToken
    )
    {
        var subRecipesById = new Dictionary<Guid, SubRecipePreviewDto>();
        if (linkedRecipeIds.Count == 0)
        {
            return subRecipesById;
        }

        var rawSubRecipes = await dbContext
            .Recipes.AsNoTracking()
            .AsSplitQuery()
            .Where(r => linkedRecipeIds.Contains(r.Id))
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.LinkedRecipe)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.Replacements)
                    .ThenInclude(rep => rep.FoodItem)
            .Include(r => r.Steps)
                .ThenInclude(s => s.Timers)
            .ToListAsync(cancellationToken);

        foreach (var sr in rawSubRecipes)
        {
            subRecipesById[sr.Id] = new SubRecipePreviewDto(
                sr.Id,
                sr.Title,
                sr.Slug,
                sr.BaseServings,
                sr.YieldDisplay,
                sr.Ingredients.OrderBy(i => i.SortOrder).Select(MapIngredientDto).ToList(),
                sr.Steps.OrderBy(s => s.StepNumber).Select(MapStepDto).ToList()
            );
        }

        return subRecipesById;
    }

    private sealed record RecipeIngestionJobMetadata(
        Guid Id,
        Guid? ResultingRecipeId,
        DateTimeOffset CreatedAt,
        string? ExtractedModel,
        string? RefinedModel
    );

    private static RecipeDetailDto MapRecipeDetailDto(
        Recipe recipe,
        IEnumerable<RecipeIngredient> ingredients,
        IEnumerable<RecipeStep> steps,
        IEnumerable<RecipeAttachment> attachments,
        IEnumerable<CookLog> cookLogs,
        IEnumerable<RecipeIngestionJobMetadata> ingestionJobs,
        IReadOnlyDictionary<Guid, SubRecipePreviewDto> subRecipesById
    )
    {
        var orderedIngredients = ingredients
            .OrderBy(i => i.SortOrder)
            .Select(MapIngredientDto)
            .ToList();

        var subRecipes = orderedIngredients
            .Where(i => i.LinkedRecipeId.HasValue)
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .Where(subRecipesById.ContainsKey)
            .Select(id => subRecipesById[id])
            .ToList();

        var orderedSteps = steps.OrderBy(s => s.StepNumber).Select(MapStepDto).ToList();

        var collections = recipe
            .RecipeCollections.OrderBy(rc => rc.Collection.SortOrder)
            .ThenBy(rc => rc.Collection.Name)
            .Select(rc => new RecipeCollectionBadgeDto(
                rc.Collection.Id,
                rc.Collection.Name,
                rc.Collection.Slug
            ))
            .ToList();

        var tags = recipe
            .RecipeTags.OrderBy(rt => rt.Tag.Category)
            .ThenBy(rt => rt.Tag.SortOrder)
            .ThenBy(rt => rt.Tag.Name)
            .Select(rt => new RecipeTagBadgeDto(
                rt.Tag.Id,
                rt.Tag.Name,
                rt.Tag.Slug,
                rt.Tag.Category
            ))
            .ToList();

        var orderedAttachments = attachments
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.CreatedAt)
            .Select(a => new RecipeAttachmentDto(
                a.Id,
                a.SortOrder,
                a.FileName,
                a.MediaUrlPath,
                a.ContentType,
                a.FileSizeBytes,
                a.Label,
                a.CreatedAt
            ))
            .ToList();

        var latestJob = ingestionJobs.OrderByDescending(j => j.CreatedAt).FirstOrDefault();
        var extractedModel = recipe.ExtractedModel ?? latestJob?.ExtractedModel;
        var refinedModel = recipe.RefinedModel ?? latestJob?.RefinedModel;
        var ingestionJobId = latestJob?.Id;

        return new RecipeDetailDto(
            recipe.Id,
            recipe.Title,
            recipe.Slug,
            recipe.Description,
            recipe.Status,
            recipe.BaseServings,
            recipe.YieldDisplay,
            recipe.PrepTimeMinutes,
            recipe.CookTimeMinutes,
            recipe.TotalTimeMinutes,
            recipe.Difficulty,
            recipe.SourceUrl,
            recipe.SourceName,
            recipe.SourceAuthor,
            recipe.SourceBookTitle,
            recipe.SourceBookPage,
            recipe.CoverImagePath,
            recipe.ThumbnailImagePath,
            recipe.TimesCooked,
            recipe.LastCookedAt,
            recipe.AverageRating,
            orderedIngredients,
            orderedSteps,
            subRecipes,
            recipe.CreatedAt,
            recipe.CreatedBy,
            recipe.ModifiedAt,
            recipe.ModifiedBy,
            collections,
            tags,
            orderedAttachments,
            recipe.RatingCount,
            cookLogs
                .OrderByDescending(c => c.CookedAt)
                .ThenByDescending(c => c.CreatedAt)
                .Select(c => new CookLogDto(
                    c.Id,
                    c.RecipeId,
                    c.CookedByUserId,
                    c.CookedByDisplayName,
                    c.CookedAt,
                    c.ServingsMade,
                    c.Rating,
                    c.Notes,
                    c.PhotoImagePath,
                    c.PhotoThumbnailPath,
                    c.CreatedAt,
                    c.ModifiedAt
                ))
                .ToList(),
            ExtractedModel: extractedModel,
            IngestionJobId: ingestionJobId,
            RefinedModel: refinedModel,
            RefinementStatus: recipe.RefinementStatus,
            RefinementStatusDetail: recipe.RefinementStatusDetail
        );
    }

    private static RecipeIngredientDto MapIngredientDto(RecipeIngredient i)
    {
        var replacements =
            i.Replacements.Count > 0
                ? i
                    .Replacements.OrderBy(r => r.SortOrder)
                    .Select(r => new RecipeIngredientReplacementDto(
                        r.Id,
                        r.SortOrder,
                        r.DisplayName,
                        r.Quantity,
                        r.MaxQuantity,
                        r.Unit,
                        r.CustomUnitName,
                        r.FoodItemId,
                        r.FoodItem?.Name,
                        r.FoodItem?.IsPantryStaple ?? false,
                        r.PreparationNote
                    ))
                    .ToList()
                : null;

        return new RecipeIngredientDto(
            i.Id,
            i.SortOrder,
            i.SectionName,
            i.Quantity,
            i.MaxQuantity,
            i.Unit,
            i.CustomUnitName,
            i.FoodItemId,
            i.FoodItem?.Name,
            i.FoodItem?.IsPantryStaple ?? false,
            i.LinkedRecipeId,
            i.LinkedRecipe?.Title,
            i.LinkedRecipe?.Slug,
            i.DisplayName,
            i.PreparationNote,
            i.IsOptional,
            i.OriginalRawText,
            i.AlternativeQuantity.HasValue && i.AlternativeUnit.HasValue
                ? new RecipeMeasurement(
                    i.AlternativeQuantity,
                    i.AlternativeUnit,
                    i.AlternativeMaxQuantity,
                    i.AlternativeCustomUnitName
                )
                : null,
            i.PreferAlternativeMeasurement,
            i.UnresolvedRecipeTitle,
            replacements
        );
    }

    private static RecipeStepDto MapStepDto(RecipeStep s)
    {
        return new RecipeStepDto(
            s.Id,
            s.StepNumber,
            s.SectionName,
            s.Instruction,
            s.ImagePath,
            s.Timers.OrderBy(t => t.SortOrder)
                .Select(t => new RecipeStepTimerDto(t.Id, t.SortOrder, t.DurationSeconds, t.Label))
                .ToList()
        );
    }

    public async Task<RecipeDetailDto> CreateRecipeAsync(
        UpsertRecipeRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateUpsertRequest(request);

        var slug = await GenerateUniqueSlugAsync(request.Title, null, cancellationToken);
        var totalTime = ComputeTotalTime(request);

        var recipe = new Recipe
        {
            Title = request.Title.Trim(),
            Slug = slug,
            Description = ClampDescription(request.Description),
            Status = request.Status,
            BaseServings = request.BaseServings,
            YieldDisplay = request.YieldDisplay?.Trim(),
            PrepTimeMinutes = request.PrepTimeMinutes,
            CookTimeMinutes = request.CookTimeMinutes,
            TotalTimeMinutes = totalTime,
            Difficulty = request.Difficulty,
            SourceUrl = request.SourceUrl?.Trim(),
            SourceName = request.SourceName?.Trim(),
            SourceAuthor = request.SourceAuthor?.Trim(),
            SourceBookTitle = request.SourceBookTitle?.Trim(),
            SourceBookPage = request.SourceBookPage,
            CoverImagePath = ValidateAndNormalizeMediaPath(request.CoverImagePath),
            ThumbnailImagePath = ValidateAndNormalizeMediaPath(request.ThumbnailImagePath),
            ExtractedModel = request.ExtractedModel,
            RefinedModel = string.IsNullOrWhiteSpace(request.RefinedModel)
                ? null
                : request.RefinedModel.Trim(),
        };

        await ValidateAndBuildGraphAsync(recipe, request, cancellationToken);
        await SyncTaxonomyAsync(recipe, request.CollectionIds, request.TagIds, cancellationToken);

        dbContext.Recipes.Add(recipe);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entries = ex.Entries.Select(e => $"{e.Entity.GetType().Name} (State: {e.State})");
            throw new InvalidOperationException(
                $"Recipe Update Concurrency failed on: {string.Join(", ", entries)}",
                ex
            );
        }

        await IngestionCatalogMaterializer.BacklinkUnresolvedRecipeReferencesAsync(
            dbContext,
            recipe.Id,
            recipe.Title,
            cancellationToken
        );

        if (recipe.Status == RecipeStatus.Published)
        {
            queueSignal?.NotifyBackgroundWorkAvailable();
        }

        return (await GetRecipeByIdAsync(recipe.Id, cancellationToken))!;
    }

    public async Task<RecipeDetailDto?> UpdateRecipeAsync(
        Guid id,
        UpsertRecipeRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateUpsertRequest(request);

        var recipe = await dbContext
            .Recipes.Include(r => r.Ingredients)
                .ThenInclude(i => i.Replacements)
            .Include(r => r.Steps)
                .ThenInclude(s => s.Timers)
            .Include(r => r.RecipeCollections)
            .Include(r => r.RecipeTags)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recipe is null)
        {
            return null;
        }

        var oldCover = recipe.CoverImagePath;
        var oldThumb = recipe.ThumbnailImagePath;
        var oldStepImages = recipe
            .Steps.Where(s => !string.IsNullOrEmpty(s.ImagePath))
            .Select(s => s.ImagePath!)
            .ToList();

        var slug = recipe.Slug;
        if (
            !string.Equals(
                recipe.Title.Trim(),
                request.Title.Trim(),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            slug = await GenerateUniqueSlugAsync(request.Title, id, cancellationToken);
        }

        var totalTime = ComputeTotalTime(request);

        dbContext.RecipeStepTimers.RemoveRange(recipe.Steps.SelectMany(s => s.Timers).ToList());
        dbContext.RecipeSteps.RemoveRange(recipe.Steps.ToList());
        dbContext.RecipeIngredientReplacements.RemoveRange(
            recipe.Ingredients.SelectMany(i => i.Replacements).ToList()
        );
        dbContext.RecipeIngredients.RemoveRange(recipe.Ingredients.ToList());

        recipe.Title = request.Title.Trim();
        recipe.Slug = slug;
        recipe.Description = ClampDescription(request.Description);
        recipe.Status = request.Status;
        recipe.BaseServings = request.BaseServings;
        recipe.YieldDisplay = request.YieldDisplay?.Trim();
        recipe.PrepTimeMinutes = request.PrepTimeMinutes;
        recipe.CookTimeMinutes = request.CookTimeMinutes;
        recipe.TotalTimeMinutes = totalTime;
        recipe.Difficulty = request.Difficulty;
        recipe.SourceUrl = request.SourceUrl?.Trim();
        recipe.SourceName = request.SourceName?.Trim();
        recipe.SourceAuthor = request.SourceAuthor?.Trim();
        recipe.SourceBookTitle = request.SourceBookTitle?.Trim();
        recipe.SourceBookPage = request.SourceBookPage;
        recipe.CoverImagePath = ValidateAndNormalizeMediaPath(request.CoverImagePath);
        recipe.ThumbnailImagePath = ValidateAndNormalizeMediaPath(request.ThumbnailImagePath);
        if (request.ExtractedModel != null)
        {
            recipe.ExtractedModel = request.ExtractedModel;
        }
        if (request.RefinedModel != null)
        {
            recipe.RefinedModel = string.IsNullOrWhiteSpace(request.RefinedModel)
                ? null
                : request.RefinedModel.Trim();
        }
        if (recipe.RefinementStatus == RecipeRefinementStatus.Failed)
        {
            recipe.RefinementStatus = RecipeRefinementStatus.None;
            recipe.RefinementRequestedAt = null;
            recipe.RefinementStatusDetail = null;
        }

        await ValidateAndBuildGraphAsync(recipe, request, cancellationToken);
        await SyncTaxonomyAsync(recipe, request.CollectionIds, request.TagIds, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entries = ex.Entries.Select(e => $"{e.Entity.GetType().Name} (State: {e.State})");
            throw new InvalidOperationException(
                $"Recipe Update Concurrency failed on: {string.Join(", ", entries)}",
                ex
            );
        }

        await IngestionCatalogMaterializer.BacklinkUnresolvedRecipeReferencesAsync(
            dbContext,
            recipe.Id,
            recipe.Title,
            cancellationToken
        );

        if (recipe.Status == RecipeStatus.Published)
        {
            queueSignal?.NotifyBackgroundWorkAvailable();
        }
        else
        {
            var staleEmbeddings = await dbContext
                .RecipeEmbeddings.Where(e => e.RecipeId == recipe.Id)
                .ToListAsync(cancellationToken);
            if (staleEmbeddings.Count > 0)
            {
                dbContext.RecipeEmbeddings.RemoveRange(staleEmbeddings);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // Clean up media files removed
        if (!string.IsNullOrEmpty(oldCover) && oldCover != recipe.CoverImagePath)
        {
            await mediaStorageService.DeleteMediaFileAsync(oldCover, cancellationToken);
        }
        if (!string.IsNullOrEmpty(oldThumb) && oldThumb != recipe.ThumbnailImagePath)
        {
            await mediaStorageService.DeleteMediaFileAsync(oldThumb, cancellationToken);
        }
        var newStepImages = (request.Steps ?? [])
            .Where(s => !string.IsNullOrEmpty(s.ImagePath))
            .Select(s => s.ImagePath!)
            .ToHashSet();
        foreach (var oldStepImg in oldStepImages)
        {
            if (!newStepImages.Contains(oldStepImg))
            {
                await mediaStorageService.DeleteMediaFileAsync(oldStepImg, cancellationToken);
            }
        }

        return await GetRecipeByIdAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteRecipeAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var recipe = await dbContext
            .Recipes.Include(r => r.Steps)
            .Include(r => r.Attachments)
            .Include(r => r.CookLogs)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recipe is null)
        {
            return false;
        }

        var isLinkedAsSubRecipe = await dbContext.RecipeIngredients.AnyAsync(
            ri => ri.LinkedRecipeId == id && ri.RecipeId != id,
            cancellationToken
        );
        if (isLinkedAsSubRecipe)
        {
            throw new ConflictException(
                "Cannot delete a recipe while another recipe links to it as a sub-recipe."
            );
        }

        var cover = recipe.CoverImagePath;
        var thumb = recipe.ThumbnailImagePath;
        var stepImages = recipe
            .Steps.Where(s => !string.IsNullOrEmpty(s.ImagePath))
            .Select(s => s.ImagePath!)
            .ToList();
        var attachmentFiles = recipe.Attachments.Select(a => a.MediaUrlPath).ToList();
        var cookLogPhotos = await dbContext
            .CookLogs.Where(c => c.RecipeId == id)
            .Select(c => new { c.PhotoImagePath, c.PhotoThumbnailPath })
            .ToListAsync(cancellationToken);

        var shareLinks = await dbContext
            .ShareLinks.Where(s => s.RecipeId == id)
            .ToListAsync(cancellationToken);
        dbContext.ShareLinks.RemoveRange(shareLinks);

        var linkedJobs = await dbContext
            .IngestionJobs.Where(j => j.ResultingRecipeId == id)
            .ToListAsync(cancellationToken);
        foreach (var job in linkedJobs)
        {
            job.ResultingRecipeId = null;
            if (!string.IsNullOrEmpty(cover) && job.DownloadedCoverImagePath == cover)
            {
                job.DownloadedCoverImagePath = null;
            }
            if (!string.IsNullOrEmpty(thumb) && job.DownloadedThumbnailImagePath == thumb)
            {
                job.DownloadedThumbnailImagePath = null;
            }
        }

        var linkedMealEntries = await dbContext
            .MealPlanEntries.Where(e => e.RecipeId == id)
            .ToListAsync(cancellationToken);
        foreach (var entry in linkedMealEntries)
        {
            entry.RecipeId = null;
            if (string.IsNullOrWhiteSpace(entry.CustomTitle))
            {
                entry.CustomTitle = entry.IsLeftovers ? $"Leftovers: {recipe.Title}" : recipe.Title;
            }
            if (!entry.IsLeftovers)
            {
                entry.EntryType = MealPlanEntryType.FreeformNote;
            }
        }

        var linkedTemplateItems = await dbContext
            .MealPlanTemplateItems.Where(i => i.RecipeId == id)
            .ToListAsync(cancellationToken);
        foreach (var item in linkedTemplateItems)
        {
            item.RecipeId = null;
            if (string.IsNullOrWhiteSpace(item.CustomTitle))
            {
                item.CustomTitle = item.IsLeftovers ? $"Leftovers: {recipe.Title}" : recipe.Title;
            }
            if (!item.IsLeftovers)
            {
                item.EntryType = MealPlanEntryType.FreeformNote;
            }
        }

        dbContext.Recipes.Remove(recipe);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entries = ex.Entries.Select(e => $"{e.Entity.GetType().Name} (State: {e.State})");
            throw new InvalidOperationException(
                $"Recipe Update Concurrency failed on: {string.Join(", ", entries)}",
                ex
            );
        }

        await mediaStorageService.DeleteMediaFileAsync(cover, cancellationToken);
        await mediaStorageService.DeleteMediaFileAsync(thumb, cancellationToken);
        foreach (var stepImg in stepImages)
        {
            await mediaStorageService.DeleteMediaFileAsync(stepImg, cancellationToken);
        }
        foreach (var attachmentFile in attachmentFiles)
        {
            await mediaStorageService.DeleteMediaFileAsync(attachmentFile, cancellationToken);
        }
        foreach (var p in cookLogPhotos)
        {
            await mediaStorageService.DeleteMediaFileAsync(p.PhotoImagePath, cancellationToken);
            await mediaStorageService.DeleteMediaFileAsync(p.PhotoThumbnailPath, cancellationToken);
        }

        return true;
    }

    public async Task<RecipeDetailDto?> UpdateRecipeOrganizationAsync(
        Guid id,
        UpdateRecipeOrganizationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var recipe = await dbContext
            .Recipes.Include(r => r.RecipeCollections)
            .Include(r => r.RecipeTags)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recipe is null)
        {
            return null;
        }

        await SyncTaxonomyAsync(recipe, request.CollectionIds, request.TagIds, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (recipe.Status == RecipeStatus.Published)
        {
            queueSignal?.NotifyBackgroundWorkAvailable();
        }

        return await GetRecipeByIdAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<IngredientMatchResultDto>> MatchRecipesByIngredientsAsync(
        IngredientMatchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (
            request.FoodItemIds is null
            || request.FoodItemIds.Count == 0
            || request.FoodItemIds.Count > 20
        )
        {
            throw new DomainValidationException("Provide between 1 and 20 on-hand food item IDs.");
        }

        var distinctFoodItemIds = request.FoodItemIds.Distinct().ToList();
        var onHandFoodItems = await dbContext
            .FoodItems.AsNoTracking()
            .Where(f => distinctFoodItemIds.Contains(f.Id))
            .Include(f => f.Aliases)
            .ToListAsync(cancellationToken);

        if (onHandFoodItems.Count == 0)
        {
            throw new DomainValidationException(
                "None of the specified food items were found in the catalog."
            );
        }

        var onHandMatchables = onHandFoodItems
            .Select(f => new MatchableOnHandItem(
                f.Id,
                f.Name,
                f.NormalizedName,
                f.Aliases.Select(a => a.NormalizedAlias).ToList()
            ))
            .ToList();

        var publishedMeta = await dbContext
            .Recipes.AsNoTracking()
            .Where(r => r.Status == RecipeStatus.Published)
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Slug,
                r.Description,
                r.Status,
                r.BaseServings,
                r.YieldDisplay,
                r.PrepTimeMinutes,
                r.CookTimeMinutes,
                r.TotalTimeMinutes,
                r.Difficulty,
                r.SourceName,
                r.ThumbnailImagePath,
                r.TimesCooked,
                r.AverageRating,
                r.CreatedAt,
                r.ModifiedAt,
                r.LastCookedAt,
                r.RatingCount,
            })
            .ToListAsync(cancellationToken);

        if (publishedMeta.Count == 0)
        {
            return [];
        }

        var publishedIds = publishedMeta.Select(r => r.Id).ToArray();
        var flatIngredients = await dbContext
            .RecipeIngredients.AsNoTracking()
            .Where(i => publishedIds.Contains(i.RecipeId))
            .Select(i => new
            {
                i.RecipeId,
                i.FoodItemId,
                i.DisplayName,
                i.IsOptional,
                IsPantryStaple = i.FoodItem != null && i.FoodItem.IsPantryStaple,
                i.LinkedRecipeId,
            })
            .ToListAsync(cancellationToken);

        var subRecipeIds = flatIngredients
            .Where(i => i.LinkedRecipeId.HasValue)
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .ToArray();

        var subIngredientsLookup =
            subRecipeIds.Length > 0
                ? (
                    await dbContext
                        .RecipeIngredients.AsNoTracking()
                        .Where(i => subRecipeIds.Contains(i.RecipeId))
                        .Select(i => new
                        {
                            i.RecipeId,
                            i.FoodItemId,
                            i.DisplayName,
                            i.IsOptional,
                            IsPantryStaple = i.FoodItem != null && i.FoodItem.IsPantryStaple,
                        })
                        .ToListAsync(cancellationToken)
                ).ToLookup(i => i.RecipeId)
                : Enumerable
                    .Empty<Guid>()
                    .ToLookup(
                        id => id,
                        id => new
                        {
                            RecipeId = id,
                            FoodItemId = (Guid?)null,
                            DisplayName = string.Empty,
                            IsOptional = false,
                            IsPantryStaple = false,
                        }
                    );

        var ingredientsByRecipe = flatIngredients.ToLookup(i => i.RecipeId);
        var candidates = new List<MatchableRecipeCandidate>(publishedMeta.Count);

        foreach (var r in publishedMeta)
        {
            var stubSummary = new RecipeSummaryDto(
                r.Id,
                r.Title,
                r.Slug,
                r.Description,
                r.Status,
                r.BaseServings,
                r.YieldDisplay,
                r.PrepTimeMinutes,
                r.CookTimeMinutes,
                r.TotalTimeMinutes,
                r.Difficulty,
                r.SourceName,
                r.ThumbnailImagePath,
                0,
                0,
                r.TimesCooked,
                r.AverageRating,
                r.CreatedAt,
                r.ModifiedAt,
                [],
                [],
                r.LastCookedAt,
                r.RatingCount
            );

            var matchableIngredients = new List<MatchableRecipeIngredient>();
            foreach (var ing in ingredientsByRecipe[r.Id])
            {
                if (ing.LinkedRecipeId.HasValue)
                {
                    foreach (var subIng in subIngredientsLookup[ing.LinkedRecipeId.Value])
                    {
                        matchableIngredients.Add(
                            new MatchableRecipeIngredient(
                                subIng.FoodItemId,
                                subIng.DisplayName,
                                subIng.IsOptional,
                                subIng.IsPantryStaple
                            )
                        );
                    }
                }
                else
                {
                    matchableIngredients.Add(
                        new MatchableRecipeIngredient(
                            ing.FoodItemId,
                            ing.DisplayName,
                            ing.IsOptional,
                            ing.IsPantryStaple
                        )
                    );
                }
            }

            candidates.Add(new MatchableRecipeCandidate(stubSummary, matchableIngredients));
        }

        var ranked = IngredientMatcher.RankRecipes(candidates, onHandMatchables);
        if (ranked.Count == 0)
        {
            return ranked;
        }

        var hydratedSummaries = await HydrateSummariesByIdsAsync(
            ranked.Select(r => r.Recipe.Id).ToList(),
            cancellationToken
        );
        var hydratedById = hydratedSummaries.ToDictionary(s => s.Id);

        return ranked
            .Select(r =>
                hydratedById.TryGetValue(r.Recipe.Id, out var full) ? r with { Recipe = full } : r
            )
            .ToList();
    }

    private static string? ValidateAndNormalizeMediaPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (
            !trimmed.StartsWith("/api/uploads/", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("..")
            || trimmed.Contains('\\')
        )
        {
            throw new DomainValidationException(
                "Invalid media path. Uploaded media paths must start with '/api/uploads/'."
            );
        }

        return trimmed;
    }

    private static void ValidateUpsertRequest(UpsertRecipeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new DomainValidationException("Recipe title is required.");
        }

        if (request.BaseServings <= 0m)
        {
            throw new DomainValidationException("Base servings must be greater than zero.");
        }

        if (request.Ingredients is not null)
        {
            foreach (var ing in request.Ingredients)
            {
                if (string.IsNullOrWhiteSpace(ing.DisplayName))
                {
                    throw new DomainValidationException("Ingredient display name is required.");
                }

                if (ing.Quantity.HasValue && ing.Quantity.Value <= 0m)
                {
                    throw new DomainValidationException(
                        "Ingredient quantity must be greater than zero."
                    );
                }

                if (ing.MaxQuantity.HasValue)
                {
                    if (!ing.Quantity.HasValue)
                    {
                        throw new DomainValidationException(
                            "Maximum quantity requires a base quantity."
                        );
                    }
                    if (ing.MaxQuantity.Value < ing.Quantity.Value)
                    {
                        throw new DomainValidationException(
                            "Maximum quantity cannot be less than base quantity."
                        );
                    }
                }

                if (
                    ing.Unit == MeasurementUnit.Custom
                    && string.IsNullOrWhiteSpace(ing.CustomUnitName)
                )
                {
                    throw new DomainValidationException(
                        "Custom unit name is required when unit is Custom."
                    );
                }

                if (ing.FoodItemId.HasValue && ing.LinkedRecipeId.HasValue)
                {
                    throw new DomainValidationException(
                        "An ingredient cannot be both a FoodItem and a Sub-Recipe."
                    );
                }
                if (ing.AlternativeMeasurement is { } alternative)
                {
                    RecipeMeasurementValidation.Validate(alternative);
                    if (ing.Quantity is null or <= 0 || ing.Unit is null)
                    {
                        throw new DomainValidationException(
                            "An alternative measurement requires a measured primary quantity and unit."
                        );
                    }
                    RecipeMeasurementValidation.Validate(
                        new RecipeMeasurement(
                            ing.Quantity,
                            ing.Unit,
                            ing.MaxQuantity,
                            ing.CustomUnitName
                        )
                    );
                }
                else if (ing.PreferAlternativeMeasurement)
                {
                    throw new DomainValidationException(
                        "Provide an alternative measurement before selecting it as the default."
                    );
                }

                if (
                    !string.IsNullOrWhiteSpace(ing.UnresolvedRecipeTitle)
                    && (ing.FoodItemId.HasValue || ing.LinkedRecipeId.HasValue)
                )
                {
                    throw new DomainValidationException(
                        "An unresolved recipe reference cannot also be linked to a FoodItem or recipe."
                    );
                }

                if (ing.Replacements is not null)
                {
                    foreach (var rep in ing.Replacements)
                    {
                        if (string.IsNullOrWhiteSpace(rep.DisplayName))
                        {
                            throw new DomainValidationException(
                                "Ingredient replacement display name is required."
                            );
                        }
                        if (rep.Quantity.HasValue && rep.Quantity.Value <= 0m)
                        {
                            throw new DomainValidationException(
                                "Ingredient replacement quantity must be greater than zero."
                            );
                        }
                        if (rep.MaxQuantity.HasValue)
                        {
                            if (!rep.Quantity.HasValue)
                            {
                                throw new DomainValidationException(
                                    "Ingredient replacement maximum quantity requires a base quantity."
                                );
                            }
                            if (rep.MaxQuantity.Value < rep.Quantity.Value)
                            {
                                throw new DomainValidationException(
                                    "Ingredient replacement maximum quantity cannot be less than base quantity."
                                );
                            }
                        }
                        if (
                            rep.Unit == MeasurementUnit.Custom
                            && string.IsNullOrWhiteSpace(rep.CustomUnitName)
                        )
                        {
                            throw new DomainValidationException(
                                "Custom unit name is required when replacement unit is Custom."
                            );
                        }
                    }
                }
            }
        }

        if (request.Steps is not null)
        {
            var ingCount = request.Ingredients?.Count ?? 0;
            foreach (var step in request.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Instruction))
                {
                    throw new DomainValidationException("Step instruction is required.");
                }

                if (step.Timers is not null)
                {
                    foreach (var timer in step.Timers)
                    {
                        if (timer.DurationSeconds <= 0)
                        {
                            throw new DomainValidationException("Timer duration must be positive.");
                        }
                        if (string.IsNullOrWhiteSpace(timer.Label))
                        {
                            throw new DomainValidationException("Timer label is required.");
                        }
                    }
                }
            }
        }
    }

    private static int? ComputeTotalTime(UpsertRecipeRequest request)
    {
        if (request.TotalTimeMinutes.HasValue)
        {
            return request.TotalTimeMinutes.Value;
        }

        if (request.PrepTimeMinutes.HasValue || request.CookTimeMinutes.HasValue)
        {
            return (request.PrepTimeMinutes ?? 0) + (request.CookTimeMinutes ?? 0);
        }

        return null;
    }

    private async Task ValidateAndBuildGraphAsync(
        Recipe recipe,
        UpsertRecipeRequest request,
        CancellationToken cancellationToken
    )
    {
        var ingredientsInput = request.Ingredients ?? [];
        var stepsInput = request.Steps ?? [];

        var foodItemIds = ingredientsInput
            .Where(i => i.FoodItemId.HasValue)
            .Select(i => i.FoodItemId!.Value)
            .Concat(
                ingredientsInput
                    .SelectMany(i => i.Replacements ?? [])
                    .Where(r => r.FoodItemId.HasValue)
                    .Select(r => r.FoodItemId!.Value)
            )
            .Distinct()
            .ToList();

        if (foodItemIds.Count > 0)
        {
            var existingCount = await dbContext.FoodItems.CountAsync(
                f => foodItemIds.Contains(f.Id),
                cancellationToken
            );
            if (existingCount != foodItemIds.Count)
            {
                throw new DomainValidationException(
                    "One or more referenced food items do not exist."
                );
            }
        }

        var linkedRecipeIds = ingredientsInput
            .Where(i => i.LinkedRecipeId.HasValue)
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .ToList();

        if (linkedRecipeIds.Count > 0)
        {
            if (linkedRecipeIds.Contains(recipe.Id))
            {
                throw new DomainValidationException(
                    "A recipe cannot reference itself as a sub-recipe."
                );
            }

            var existingRecipes = await dbContext.Recipes.CountAsync(
                r => linkedRecipeIds.Contains(r.Id),
                cancellationToken
            );
            if (existingRecipes != linkedRecipeIds.Count)
            {
                throw new DomainValidationException(
                    "One or more referenced sub-recipes do not exist."
                );
            }

            await ValidateSubRecipeDagCycleAsync(recipe.Id, linkedRecipeIds, cancellationToken);
        }

        var createdIngredients = new List<RecipeIngredient>();
        for (var i = 0; i < ingredientsInput.Count; i++)
        {
            var input = ingredientsInput[i];
            var unit = input.Unit;
            var customUnitName =
                unit == MeasurementUnit.Custom ? input.CustomUnitName?.Trim() : null;
            if (
                unit == MeasurementUnit.Custom
                && (
                    string.IsNullOrWhiteSpace(customUnitName)
                    || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(customUnitName)
                )
            )
            {
                unit = null;
                customUnitName = null;
            }

            var quantity = input.Quantity;
            if (
                (unit is MeasurementUnit.Pinch or MeasurementUnit.Dash)
                && (quantity is null or <= 0.1m)
            )
            {
                quantity = 1m;
            }

            var entity = new RecipeIngredient
            {
                RecipeId = recipe.Id,
                SortOrder = i,
                SectionName = RecipeUnitAndSectionHelpers.IsGenericTopLevelSection(
                    input.SectionName
                )
                    ? null
                    : input.SectionName?.Trim(),
                Quantity = quantity,
                MaxQuantity = input.MaxQuantity,
                Unit = unit,
                CustomUnitName = customUnitName,
                AlternativeQuantity = input.AlternativeMeasurement?.Quantity,
                AlternativeMaxQuantity = input.AlternativeMeasurement?.MaxQuantity,
                AlternativeUnit = input.AlternativeMeasurement?.Unit,
                AlternativeCustomUnitName = input.AlternativeMeasurement?.CustomUnitName?.Trim(),
                PreferAlternativeMeasurement = input.PreferAlternativeMeasurement,
                FoodItemId = input.FoodItemId,
                LinkedRecipeId =
                    input.LinkedRecipeId
                    ?? (
                        !string.IsNullOrWhiteSpace(input.UnresolvedRecipeTitle)
                            ? await IngestionCatalogMaterializer.ResolveRecipeReferenceAsync(
                                dbContext,
                                input.UnresolvedRecipeTitle,
                                recipe.Id,
                                cancellationToken
                            )
                            : null
                    ),
                UnresolvedRecipeTitle =
                    (
                        input.LinkedRecipeId != null
                        || (
                            !string.IsNullOrWhiteSpace(input.UnresolvedRecipeTitle)
                            && await IngestionCatalogMaterializer.ResolveRecipeReferenceAsync(
                                dbContext,
                                input.UnresolvedRecipeTitle,
                                recipe.Id,
                                cancellationToken
                            ) != null
                        )
                    )
                        ? null
                        : (
                            string.IsNullOrWhiteSpace(input.UnresolvedRecipeTitle)
                                ? null
                                : input.UnresolvedRecipeTitle.Trim()
                        ),
                DisplayName = input.DisplayName.Trim(),
                PreparationNote = RecipeUnitAndSectionHelpers.CleanPreparationNote(
                    input.PreparationNote,
                    200
                ),
                IsOptional = input.IsOptional,
                OriginalRawText = input.OriginalRawText?.Trim(),
            };
            if (input.Replacements is { Count: > 0 })
            {
                for (var repIdx = 0; repIdx < input.Replacements.Count; repIdx++)
                {
                    var repInput = input.Replacements[repIdx];
                    var repUnit = repInput.Unit;
                    var repCustomUnit =
                        repUnit == MeasurementUnit.Custom ? repInput.CustomUnitName?.Trim() : null;
                    if (
                        repUnit == MeasurementUnit.Custom
                        && (
                            string.IsNullOrWhiteSpace(repCustomUnit)
                            || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(
                                repCustomUnit
                            )
                        )
                    )
                    {
                        repUnit = null;
                        repCustomUnit = null;
                    }

                    var repQty = repInput.Quantity;
                    if (
                        (repUnit is MeasurementUnit.Pinch or MeasurementUnit.Dash)
                        && (repQty is null or <= 0.1m)
                    )
                    {
                        repQty = 1m;
                    }

                    entity.Replacements.Add(
                        new RecipeIngredientReplacement
                        {
                            RecipeIngredient = entity,
                            SortOrder = repIdx,
                            DisplayName = repInput.DisplayName.Trim(),
                            Quantity = repQty,
                            MaxQuantity = repInput.MaxQuantity,
                            Unit = repUnit,
                            CustomUnitName = repCustomUnit,
                            FoodItemId = repInput.FoodItemId,
                            PreparationNote = RecipeUnitAndSectionHelpers.CleanPreparationNote(
                                repInput.PreparationNote,
                                300
                            ),
                        }
                    );
                }
            }

            dbContext.RecipeIngredients.Add(entity);
            createdIngredients.Add(entity);
        }

        var orderedIngredientIds = createdIngredients.Select(i => i.Id).ToList();

        for (var s = 0; s < stepsInput.Count; s++)
        {
            var sInput = stepsInput[s];
            var boundInstruction = StepInstructionParser.BindIngredientIds(
                sInput.Instruction.Trim(),
                orderedIngredientIds
            );
            var stepEntity = new RecipeStep
            {
                RecipeId = recipe.Id,
                StepNumber = s + 1,
                SectionName = RecipeUnitAndSectionHelpers.IsGenericTopLevelSection(
                    sInput.SectionName
                )
                    ? null
                    : sInput.SectionName?.Trim(),
                Instruction = boundInstruction,
                ImagePath = ValidateAndNormalizeMediaPath(sInput.ImagePath),
            };

            var tokens = StepInstructionParser.ExtractTokens(boundInstruction);

            foreach (var token in tokens)
            {
                if (token is StepTimerToken timerToken && timerToken.Seconds >= 1)
                {
                    var timerLabel = !string.IsNullOrWhiteSpace(timerToken.Label)
                        ? timerToken.Label.Trim()
                        : "Timer";

                    if (
                        !stepEntity.Timers.Any(t =>
                            t.DurationSeconds == timerToken.Seconds
                            && string.Equals(
                                t.Label,
                                timerLabel,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
                    {
                        stepEntity.Timers.Add(
                            new RecipeStepTimer
                            {
                                RecipeStep = stepEntity,
                                SortOrder = stepEntity.Timers.Count,
                                DurationSeconds = timerToken.Seconds,
                                Label = timerLabel,
                            }
                        );
                    }
                }
            }
            if (sInput.Timers is not null)
            {
                for (var t = 0; t < sInput.Timers.Count; t++)
                {
                    var timerInput = sInput.Timers[t];
                    if (timerInput.DurationSeconds < 1)
                    {
                        continue;
                    }

                    var label = !string.IsNullOrWhiteSpace(timerInput.Label)
                        ? timerInput.Label.Trim()
                        : "Timer";

                    var exactExisting = stepEntity.Timers.FirstOrDefault(existing =>
                        existing.DurationSeconds == timerInput.DurationSeconds
                        && string.Equals(existing.Label, label, StringComparison.OrdinalIgnoreCase)
                    );
                    if (exactExisting is not null)
                    {
                        continue;
                    }

                    // If an inline timer token already added a timer with the same duration whose label
                    // is a prefix of the disambiguated label (e.g. "Fry potatoes" vs "Fry potatoes (20m)"),
                    // update the existing timer's label instead of creating a duplicate row.
                    var prefixMatch = stepEntity.Timers.FirstOrDefault(existing =>
                        existing.DurationSeconds == timerInput.DurationSeconds
                        && (
                            label.StartsWith(
                                existing.Label + " ",
                                StringComparison.OrdinalIgnoreCase
                            )
                            || existing.Label.Equals("Timer", StringComparison.OrdinalIgnoreCase)
                            || existing.Label.StartsWith(
                                "Step ",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    );
                    if (prefixMatch is not null)
                    {
                        prefixMatch.Label = label;
                        continue;
                    }

                    stepEntity.Timers.Add(
                        new RecipeStepTimer
                        {
                            RecipeStep = stepEntity,
                            SortOrder = stepEntity.Timers.Count,
                            DurationSeconds = timerInput.DurationSeconds,
                            Label = label,
                        }
                    );
                }
            }

            dbContext.RecipeSteps.Add(stepEntity);
        }
    }

    public IReadOnlyList<DetectedStepAmountDto> DetectStepAmounts(string instruction) =>
        SourceMeasurementParser
            .FindSpans(instruction)
            .Where(span => span.Measurement.Quantity is not null)
            .Select(span => new DetectedStepAmountDto(
                span.Index,
                span.Length,
                instruction.Substring(span.Index, span.Length),
                span.Measurement,
                span.Alternative
            ))
            .ToList();

    private async Task SyncTaxonomyAsync(
        Recipe recipe,
        IReadOnlyList<Guid>? collectionIds,
        IReadOnlyList<Guid>? tagIds,
        CancellationToken cancellationToken
    )
    {
        if (collectionIds is not null)
        {
            var distinctCollIds = collectionIds.Distinct().ToList();
            if (distinctCollIds.Count > 0)
            {
                var existingCount = await dbContext.Collections.CountAsync(
                    c => distinctCollIds.Contains(c.Id),
                    cancellationToken
                );
                if (existingCount != distinctCollIds.Count)
                {
                    throw new DomainValidationException(
                        "One or more referenced collections do not exist."
                    );
                }
            }

            var existingColls = recipe.RecipeCollections.ToList();
            var collsToRemove = existingColls
                .Where(rc => !distinctCollIds.Contains(rc.CollectionId))
                .ToList();
            dbContext.RecipeCollections.RemoveRange(collsToRemove);

            var existingCollIds = existingColls.Select(rc => rc.CollectionId).ToHashSet();
            foreach (var collId in distinctCollIds)
            {
                if (!existingCollIds.Contains(collId))
                {
                    dbContext.RecipeCollections.Add(
                        new RecipeCollection { RecipeId = recipe.Id, CollectionId = collId }
                    );
                }
            }
        }

        if (tagIds is not null)
        {
            var distinctTagIds = tagIds.Distinct().ToList();
            if (distinctTagIds.Count > 0)
            {
                var existingCount = await dbContext.Tags.CountAsync(
                    t => distinctTagIds.Contains(t.Id),
                    cancellationToken
                );
                if (existingCount != distinctTagIds.Count)
                {
                    throw new DomainValidationException(
                        "One or more referenced tags do not exist."
                    );
                }
            }

            var existingTags = recipe.RecipeTags.ToList();
            var tagsToRemove = existingTags
                .Where(rt => !distinctTagIds.Contains(rt.TagId))
                .ToList();
            dbContext.RecipeTags.RemoveRange(tagsToRemove);

            var existingTagIds = existingTags.Select(rt => rt.TagId).ToHashSet();
            foreach (var tagId in distinctTagIds)
            {
                if (!existingTagIds.Contains(tagId))
                {
                    dbContext.RecipeTags.Add(new RecipeTag { RecipeId = recipe.Id, TagId = tagId });
                }
            }
        }
    }

    private async Task ValidateSubRecipeDagCycleAsync(
        Guid recipeId,
        IReadOnlyList<Guid> directSubRecipeIds,
        CancellationToken cancellationToken
    )
    {
        var existingEdges = await dbContext
            .RecipeIngredients.AsNoTracking()
            .Where(ri => ri.LinkedRecipeId != null && ri.RecipeId != recipeId)
            .Select(ri => new { ri.RecipeId, LinkedId = ri.LinkedRecipeId!.Value })
            .ToListAsync(cancellationToken);

        var graph = new Dictionary<Guid, HashSet<Guid>>();

        void AddEdge(Guid from, Guid to)
        {
            if (!graph.TryGetValue(from, out var set))
            {
                set = [];
                graph[from] = set;
            }
            set.Add(to);
        }

        foreach (var edge in existingEdges)
        {
            AddEdge(edge.RecipeId, edge.LinkedId);
        }

        foreach (var subId in directSubRecipeIds)
        {
            AddEdge(recipeId, subId);
        }

        // BFS traversal from each direct subId to check if recipeId is reachable
        var queue = new Queue<Guid>(directSubRecipeIds);
        var visited = new HashSet<Guid>(directSubRecipeIds);

        while (queue.Count > 0)
        {
            var curr = queue.Dequeue();
            if (curr == recipeId)
            {
                throw new ConflictException(
                    "Sub-recipe link would create a circular recipe dependency."
                );
            }

            if (graph.TryGetValue(curr, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (neighbor == recipeId)
                    {
                        throw new ConflictException(
                            "Sub-recipe link would create a circular recipe dependency."
                        );
                    }

                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }
    }

    private async Task<string> GenerateUniqueSlugAsync(
        string title,
        Guid? currentRecipeId,
        CancellationToken cancellationToken
    )
    {
        var rawSlug = ToKebabCase(title);
        var baseSlug = string.IsNullOrWhiteSpace(rawSlug) ? "recipe" : rawSlug;

        var slug = baseSlug;
        var counter = 2;

        while (
            await dbContext.Recipes.AnyAsync(
                r => r.Slug == slug && (!currentRecipeId.HasValue || r.Id != currentRecipeId.Value),
                cancellationToken
            )
        )
        {
            slug = $"{baseSlug}-{counter}";
            counter++;
        }

        return slug;
    }

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lower = value.Trim().ToLowerInvariant();
        var clean = Regex.Replace(lower, @"[^a-z0-9\s-]", "");
        var hyphens = Regex.Replace(clean, @"[\s-]+", "-");
        return hyphens.Trim('-');
    }

    public async Task<RecipeAttachmentDto?> AddRecipeAttachmentAsync(
        Guid recipeId,
        Stream content,
        string originalFileName,
        string? contentType,
        long contentLength,
        string? label = null,
        CancellationToken cancellationToken = default
    )
    {
        var recipeExists = await dbContext.Recipes.AnyAsync(
            r => r.Id == recipeId,
            cancellationToken
        );
        if (!recipeExists)
        {
            return null;
        }

        var maxSortOrder =
            await dbContext
                .RecipeAttachments.Where(a => a.RecipeId == recipeId)
                .Select(a => (int?)a.SortOrder)
                .MaxAsync(cancellationToken)
            ?? 0;

        var saved = await mediaStorageService.SaveDirectRecipeAttachmentAsync(
            content,
            originalFileName,
            contentType,
            contentLength,
            cancellationToken
        );

        var attachment = new RecipeAttachment
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            SortOrder = maxSortOrder + 1,
            FileName = Path.GetFileName(originalFileName),
            MediaUrlPath = saved.MediaUrlPath,
            ContentType = saved.ContentType,
            FileSizeBytes = saved.FileSizeBytes,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
        };

        dbContext.RecipeAttachments.Add(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new RecipeAttachmentDto(
            attachment.Id,
            attachment.SortOrder,
            attachment.FileName,
            attachment.MediaUrlPath,
            attachment.ContentType,
            attachment.FileSizeBytes,
            attachment.Label,
            attachment.CreatedAt
        );
    }

    private static string? ClampDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.Length > RecipeFieldLimits.MaxDescriptionLength
            ? trimmed[..RecipeFieldLimits.MaxDescriptionLength].TrimEnd()
            : trimmed;
    }

    public async Task<bool> DeleteRecipeAttachmentAsync(
        Guid recipeId,
        Guid attachmentId,
        CancellationToken cancellationToken = default
    )
    {
        var attachment = await dbContext.RecipeAttachments.FirstOrDefaultAsync(
            a => a.RecipeId == recipeId && a.Id == attachmentId,
            cancellationToken
        );

        if (attachment is null)
        {
            return false;
        }

        dbContext.RecipeAttachments.Remove(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);

        await mediaStorageService.DeleteMediaFileAsync(attachment.MediaUrlPath, cancellationToken);
        return true;
    }

    public async Task<RecipeDetailDto?> QueueRefinementNowAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var recipe = await dbContext.Recipes.FirstOrDefaultAsync(
            r => r.Id == id,
            cancellationToken
        );

        if (recipe is null)
        {
            return null;
        }

        if (recipe.Status == RecipeStatus.Archived)
        {
            throw new ConflictException("Archived recipes cannot be refined.");
        }

        var refineModelLabel = "refinement model";
        if (llmSettingsService is not null)
        {
            var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
            if (!config.HasRefinementModelConfigured)
            {
                throw new DomainValidationException(
                    "No Refinement model is configured in LLM Settings. Assign a Refinement workflow model first."
                );
            }

            refineModelLabel =
                config.ForRefinement().TextModel?.Trim()
                ?? config.RefineModel?.Trim()
                ?? refineModelLabel;
        }

        if (
            recipe.RefinementStatus == RecipeRefinementStatus.Running
            && queueSignal?.IsJobActive(recipe.Id) == true
        )
        {
            throw new ConflictException("Recipe refinement is already in progress.");
        }

        recipe.RefinementStatus = RecipeRefinementStatus.Queued;
        recipe.RefinementRequestedAt = _timeProvider.GetUtcNow();
        recipe.RefinementStatusDetail = $"Queued for refinement via {refineModelLabel}...";

        await dbContext.SaveChangesAsync(cancellationToken);
        queueSignal?.NotifyWorkAvailable();

        return await GetRecipeByIdAsync(id, cancellationToken);
    }

    public async Task<RecipeDetailDto?> DismissRefinementFailureAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var recipe = await dbContext.Recipes.FirstOrDefaultAsync(
            r => r.Id == id,
            cancellationToken
        );

        if (recipe is null)
        {
            return null;
        }

        if (recipe.RefinementStatus == RecipeRefinementStatus.Failed)
        {
            recipe.RefinementStatus = RecipeRefinementStatus.None;
            recipe.RefinementRequestedAt = null;
            recipe.RefinementStatusDetail = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return await GetRecipeByIdAsync(id, cancellationToken);
    }
}
