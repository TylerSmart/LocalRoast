using System.Text.RegularExpressions;
using App.Core.Exceptions;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed partial class RecipeService(
    AppDbContext dbContext,
    IMediaStorageService mediaStorageService
) : IRecipeService
{
    public async Task<IReadOnlyList<RecipeSummaryDto>> GetRecipesAsync(
        RecipeQueryParameters query,
        CancellationToken cancellationToken = default
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
            );
        }

        return await q.OrderByDescending(r => r.ModifiedAt)
            .Select(r => new RecipeSummaryDto(
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
            ))
            .ToListAsync(cancellationToken);
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
            .Include(r => r.Steps)
                .ThenInclude(s => s.IngredientReferences)
            .Include(r => r.Steps)
                .ThenInclude(s => s.Timers)
            .Include(r => r.RecipeCollections)
                .ThenInclude(rc => rc.Collection)
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
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
                .Include(r => r.Steps)
                    .ThenInclude(s => s.IngredientReferences)
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
            tags
        );
    }

    private static RecipeIngredientDto MapIngredientDto(RecipeIngredient i)
    {
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
            i.OriginalRawText
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
            s.IngredientReferences.Select(ir => ir.RecipeIngredientId).ToList(),
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
            Description = request.Description?.Trim(),
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
        };

        await ValidateAndBuildGraphAsync(recipe, request, cancellationToken);

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
            .Include(r => r.Steps)
                .ThenInclude(s => s.IngredientReferences)
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

        dbContext.RecipeStepIngredients.RemoveRange(
            recipe.Steps.SelectMany(s => s.IngredientReferences)
        );
        dbContext.RecipeStepTimers.RemoveRange(recipe.Steps.SelectMany(s => s.Timers));
        dbContext.RecipeSteps.RemoveRange(recipe.Steps);
        dbContext.RecipeIngredients.RemoveRange(recipe.Ingredients);

        recipe.Title = request.Title.Trim();
        recipe.Slug = slug;
        recipe.Description = request.Description?.Trim();
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

        await ValidateAndBuildGraphAsync(recipe, request, cancellationToken);

        if (request.CollectionIds is not null)
        {
            var distinctCollIds = request.CollectionIds.Distinct().ToList();
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
            dbContext.RecipeCollections.RemoveRange(recipe.RecipeCollections);
            foreach (var collId in distinctCollIds)
            {
                dbContext.RecipeCollections.Add(
                    new RecipeCollection { RecipeId = recipe.Id, CollectionId = collId }
                );
            }
        }

        if (request.TagIds is not null)
        {
            var distinctTagIds = request.TagIds.Distinct().ToList();
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
            dbContext.RecipeTags.RemoveRange(recipe.RecipeTags);
            foreach (var tagId in distinctTagIds)
            {
                dbContext.RecipeTags.Add(new RecipeTag { RecipeId = recipe.Id, TagId = tagId });
            }
        }

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
                .ThenInclude(s => s.IngredientReferences)
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

        dbContext.RecipeStepIngredients.RemoveRange(
            recipe.Steps.SelectMany(s => s.IngredientReferences)
        );

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

        if (request.CollectionIds is not null)
        {
            var distinctCollIds = request.CollectionIds.Distinct().ToList();
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
            dbContext.RecipeCollections.RemoveRange(recipe.RecipeCollections);
            foreach (var collId in distinctCollIds)
            {
                dbContext.RecipeCollections.Add(
                    new RecipeCollection { RecipeId = recipe.Id, CollectionId = collId }
                );
            }
        }

        if (request.TagIds is not null)
        {
            var distinctTagIds = request.TagIds.Distinct().ToList();
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
            dbContext.RecipeTags.RemoveRange(recipe.RecipeTags);
            foreach (var tagId in distinctTagIds)
            {
                dbContext.RecipeTags.Add(new RecipeTag { RecipeId = recipe.Id, TagId = tagId });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
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

                if (step.IngredientIndices is not null)
                {
                    foreach (var idx in step.IngredientIndices)
                    {
                        if (idx < 0 || idx >= ingCount)
                        {
                            throw new DomainValidationException(
                                $"Step ingredient index {idx} is out of bounds."
                            );
                        }
                    }
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
            var entity = new RecipeIngredient
            {
                RecipeId = recipe.Id,
                SortOrder = i,
                SectionName = input.SectionName?.Trim(),
                Quantity = input.Quantity,
                MaxQuantity = input.MaxQuantity,
                Unit = input.Unit,
                CustomUnitName =
                    input.Unit == MeasurementUnit.Custom ? input.CustomUnitName?.Trim() : null,
                FoodItemId = input.FoodItemId,
                LinkedRecipeId = input.LinkedRecipeId,
                DisplayName = input.DisplayName.Trim(),
                PreparationNote = input.PreparationNote?.Trim(),
                IsOptional = input.IsOptional,
                OriginalRawText = input.OriginalRawText?.Trim(),
            };
            dbContext.RecipeIngredients.Add(entity);
            createdIngredients.Add(entity);
        }

        for (var s = 0; s < stepsInput.Count; s++)
        {
            var sInput = stepsInput[s];
            var stepEntity = new RecipeStep
            {
                RecipeId = recipe.Id,
                StepNumber = s + 1,
                SectionName = sInput.SectionName?.Trim(),
                Instruction = sInput.Instruction.Trim(),
                ImagePath = ValidateAndNormalizeMediaPath(sInput.ImagePath),
            };

            if (sInput.IngredientIndices is not null)
            {
                var distinctIndices = sInput.IngredientIndices.Distinct().ToList();
                foreach (var idx in distinctIndices)
                {
                    if (idx >= 0 && idx < createdIngredients.Count)
                    {
                        stepEntity.IngredientReferences.Add(
                            new RecipeStepIngredient
                            {
                                RecipeStep = stepEntity,
                                RecipeIngredient = createdIngredients[idx],
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
                    stepEntity.Timers.Add(
                        new RecipeStepTimer
                        {
                            RecipeStep = stepEntity,
                            SortOrder = t,
                            DurationSeconds = timerInput.DurationSeconds,
                            Label = timerInput.Label.Trim(),
                        }
                    );
                }
            }

            dbContext.RecipeSteps.Add(stepEntity);
        }

        if (request.CollectionIds is { Count: > 0 })
        {
            var distinctCollIds = request.CollectionIds.Distinct().ToList();
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
            foreach (var collId in distinctCollIds)
            {
                dbContext.RecipeCollections.Add(
                    new RecipeCollection { RecipeId = recipe.Id, CollectionId = collId }
                );
            }
        }

        if (request.TagIds is { Count: > 0 })
        {
            var distinctTagIds = request.TagIds.Distinct().ToList();
            var existingCount = await dbContext.Tags.CountAsync(
                t => distinctTagIds.Contains(t.Id),
                cancellationToken
            );
            if (existingCount != distinctTagIds.Count)
            {
                throw new DomainValidationException("One or more referenced tags do not exist.");
            }
            foreach (var tagId in distinctTagIds)
            {
                dbContext.RecipeTags.Add(new RecipeTag { RecipeId = recipe.Id, TagId = tagId });
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
}
