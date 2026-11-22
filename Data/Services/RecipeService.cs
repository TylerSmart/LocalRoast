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
        var q = BuildRecipeQuery(query);
        var recipes = await ProjectToSummary(q).ToListAsync(cancellationToken);
        return recipes.Count > 0 || string.IsNullOrWhiteSpace(query.Search)
            ? recipes
            : await GetFuzzyRecipeMatchesAsync(query, cancellationToken);
    }

    public async Task<RecipePagedResultDto> GetRecipesPagedAsync(
        RecipeQueryParameters query,
        CancellationToken cancellationToken = default
    )
    {
        var q = BuildRecipeQuery(query);
        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? 24, 1, 200);
        var isFuzzySearch = query.FuzzySearch && !string.IsNullOrWhiteSpace(query.Search);
        int? totalCount =
            query.IncludeTotalCount && !isFuzzySearch
                ? await q.CountAsync(cancellationToken)
                : null;

        if (isFuzzySearch)
        {
            var fuzzyMatches = await GetFuzzyRecipeMatchesAsync(query, cancellationToken);
            return CreateFuzzyPage(fuzzyMatches, query, page, pageSize);
        }

        if (
            (totalCount == 0 && !string.IsNullOrWhiteSpace(query.Search))
            || (
                totalCount is null
                && page == 1
                && !string.IsNullOrWhiteSpace(query.Search)
                && !await q.AnyAsync(cancellationToken)
            )
        )
        {
            var fuzzyMatches = await GetFuzzyRecipeMatchesAsync(query, cancellationToken);
            return CreateFuzzyPage(fuzzyMatches, query, page, pageSize, isFuzzySearch: true);
        }

        var skip = (int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize);
        var items = await ProjectToSummary(
                q.Skip(skip).Take(pageSize + (totalCount is null ? 1 : 0))
            )
            .ToListAsync(cancellationToken);

        var hasMore = totalCount.HasValue
            ? (long)page * pageSize < totalCount.Value
            : items.Count > pageSize;
        if (items.Count > pageSize)
        {
            items.RemoveAt(pageSize);
        }

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

        var sqlFiltered = baseQuery;
        if (searchTokens.Length > 0)
        {
            foreach (var token in searchTokens)
            {
                var currentToken = token;
                sqlFiltered = sqlFiltered.Where(r => r.Title.ToLower().Contains(currentToken));
            }
        }
        else
        {
            var lower = trimmed.ToLowerInvariant();
            sqlFiltered = sqlFiltered.Where(r => r.Title.ToLower().Contains(lower));
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

        var unsearchedQuery = BuildRecipeQuery(query with { Search = null });
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

        var matchedQuery = unsearchedQuery.Where(recipe => matchedIds.Contains(recipe.Id));
        var summaries = await ProjectToSummary(matchedQuery).ToListAsync(cancellationToken);
        var summariesById = summaries.ToDictionary(recipe => recipe.Id);
        return matchedIds.Select(id => summariesById[id]).ToArray();
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

    private IQueryable<Recipe> BuildRecipeQuery(RecipeQueryParameters query)
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
            var norm = query.Search.Trim().ToLowerInvariant();
            q = q.Where(r =>
                r.Title.ToLower().Contains(norm)
                || (r.Description != null && r.Description.ToLower().Contains(norm))
                || (r.SourceName != null && r.SourceName.ToLower().Contains(norm))
                || (r.SourceAuthor != null && r.SourceAuthor.ToLower().Contains(norm))
                || (r.SourceBookTitle != null && r.SourceBookTitle.ToLower().Contains(norm))
                || r.RecipeCollections.Any(rc => rc.Collection.NormalizedName.Contains(norm))
                || r.RecipeTags.Any(rt => rt.Tag.NormalizedName.Contains(norm))
                || r.Ingredients.Any(i =>
                    i.DisplayName.ToLower().Contains(norm)
                    || (
                        i.FoodItem != null
                        && (
                            i.FoodItem.NormalizedName.Contains(norm)
                            || i.FoodItem.Aliases.Any(a => a.NormalizedAlias.Contains(norm))
                        )
                    )
                )
                || r.CookLogs.Any(cl => cl.Notes != null && cl.Notes.ToLower().Contains(norm))
            );
        }

        return query.ForgottenFavoritesOnly
            ? q.OrderBy(r => r.LastCookedAt)
                .ThenByDescending(r => r.AverageRating)
                .ThenByDescending(r => r.TimesCooked)
                .ThenByDescending(r => r.ModifiedAt)
                .ThenByDescending(r => r.Id)
            : q.OrderByDescending(r => r.ModifiedAt).ThenByDescending(r => r.Id);
    }

    private static IQueryable<RecipeSummaryDto> ProjectToSummary(IQueryable<Recipe> query)
    {
        return query.Select(r => new RecipeSummaryDto(
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
            r.Ingredients.Count,
            r.Steps.Count,
            r.TimesCooked,
            r.AverageRating,
            r.CreatedAt,
            r.ModifiedAt,
            r.RecipeCollections.OrderBy(rc => rc.Collection.SortOrder)
                .ThenBy(rc => rc.Collection.Name)
                .Select(rc => new RecipeCollectionBadgeDto(
                    rc.Collection.Id,
                    rc.Collection.Name,
                    rc.Collection.Slug
                ))
                .ToList(),
            r.RecipeTags.OrderBy(rt => rt.Tag.Category)
                .ThenBy(rt => rt.Tag.SortOrder)
                .ThenBy(rt => rt.Tag.Name)
                .Select(rt => new RecipeTagBadgeDto(
                    rt.Tag.Id,
                    rt.Tag.Name,
                    rt.Tag.Slug,
                    rt.Tag.Category
                ))
                .ToList(),
            r.LastCookedAt,
            r.RatingCount,
            r.CookLogs.OrderByDescending(cl => cl.CookedAt)
                .ThenByDescending(cl => cl.CreatedAt)
                .Where(cl => cl.Notes != null && cl.Notes.Trim() != "")
                .Select(cl => cl.Notes)
                .FirstOrDefault()
        ));
    }

    public async Task<RecipeDetailDto?> GetRecipeByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var recipe = await dbContext
            .Recipes.AsNoTracking()
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
            .Include(r => r.IngestionJobs)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (recipe is null)
        {
            return null;
        }

        var linkedRecipeIds = recipe
            .Ingredients.Where(i => i.LinkedRecipeId.HasValue)
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .ToList();

        var subRecipes = new List<SubRecipePreviewDto>();
        if (linkedRecipeIds.Count > 0)
        {
            var rawSubRecipes = await dbContext
                .Recipes.AsNoTracking()
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
                subRecipes.Add(
                    new SubRecipePreviewDto(
                        sr.Id,
                        sr.Title,
                        sr.Slug,
                        sr.BaseServings,
                        sr.YieldDisplay,
                        sr.Ingredients.OrderBy(i => i.SortOrder).Select(MapIngredientDto).ToList(),
                        sr.Steps.OrderBy(s => s.StepNumber).Select(MapStepDto).ToList()
                    )
                );
            }
        }

        var orderedIngredients = recipe
            .Ingredients.OrderBy(i => i.SortOrder)
            .Select(MapIngredientDto)
            .ToList();

        var orderedSteps = recipe.Steps.OrderBy(s => s.StepNumber).Select(MapStepDto).ToList();

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

        var attachments = recipe
            .Attachments.OrderBy(a => a.SortOrder)
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

        var latestJob = recipe.IngestionJobs.OrderByDescending(j => j.CreatedAt).FirstOrDefault();
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
            attachments,
            recipe.RatingCount,
            recipe
                .CookLogs.OrderByDescending(c => c.CookedAt)
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

        var publishedRecipes = await dbContext
            .Recipes.AsNoTracking()
            .Where(r => r.Status == RecipeStatus.Published)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .Include(r => r.RecipeCollections)
                .ThenInclude(rc => rc.Collection)
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
            .ToListAsync(cancellationToken);

        var subRecipeIds = publishedRecipes
            .SelectMany(r => r.Ingredients)
            .Where(i => i.LinkedRecipeId.HasValue)
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .ToList();

        var subRecipeMap =
            subRecipeIds.Count > 0
                ? await dbContext
                    .Recipes.AsNoTracking()
                    .Where(r => subRecipeIds.Contains(r.Id))
                    .Include(r => r.Ingredients)
                        .ThenInclude(i => i.FoodItem)
                    .ToDictionaryAsync(r => r.Id, cancellationToken)
                : new Dictionary<Guid, Recipe>();

        var candidates = new List<MatchableRecipeCandidate>();

        foreach (var r in publishedRecipes)
        {
            var summary = new RecipeSummaryDto(
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
                r.Ingredients.Count,
                r.Steps.Count,
                r.TimesCooked,
                r.AverageRating,
                r.CreatedAt,
                r.ModifiedAt,
                r.RecipeCollections.OrderBy(rc => rc.Collection.SortOrder)
                    .ThenBy(rc => rc.Collection.Name)
                    .Select(rc => new RecipeCollectionBadgeDto(
                        rc.Collection.Id,
                        rc.Collection.Name,
                        rc.Collection.Slug
                    ))
                    .ToList(),
                r.RecipeTags.OrderBy(rt => rt.Tag.Category)
                    .ThenBy(rt => rt.Tag.SortOrder)
                    .ThenBy(rt => rt.Tag.Name)
                    .Select(rt => new RecipeTagBadgeDto(
                        rt.Tag.Id,
                        rt.Tag.Name,
                        rt.Tag.Slug,
                        rt.Tag.Category
                    ))
                    .ToList()
            );

            var matchableIngredients = new List<MatchableRecipeIngredient>();
            foreach (var ing in r.Ingredients)
            {
                if (
                    ing.LinkedRecipeId.HasValue
                    && subRecipeMap.TryGetValue(ing.LinkedRecipeId.Value, out var subRecipe)
                )
                {
                    foreach (var subIng in subRecipe.Ingredients)
                    {
                        matchableIngredients.Add(
                            new MatchableRecipeIngredient(
                                subIng.FoodItemId,
                                subIng.DisplayName,
                                subIng.IsOptional,
                                subIng.FoodItem?.IsPantryStaple ?? false
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
                            ing.FoodItem?.IsPantryStaple ?? false
                        )
                    );
                }
            }

            candidates.Add(new MatchableRecipeCandidate(summary, matchableIngredients));
        }

        return IngredientMatcher.RankRecipes(candidates, onHandMatchables);
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
