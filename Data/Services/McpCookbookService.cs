using System.Globalization;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Mcp;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class McpCookbookService(
    AppDbContext dbContext,
    IRecipeService recipeService,
    IIngestionService ingestionService,
    ITaxonomyService taxonomyService,
    IFoodCatalogService foodCatalogService,
    TimeProvider? timeProvider = null
) : IMcpCookbookService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<RecipeSummaryDto>> SearchRecipesAsync(
        McpRecipeSearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var limit = Math.Clamp(request.Limit, 1, 100);
        var q = dbContext.Recipes.AsNoTracking().AsQueryable();

        if (request.IncludeDrafts)
        {
            q = q.Where(r => r.Status != RecipeStatus.Archived);
        }
        else
        {
            q = q.Where(r => r.Status == RecipeStatus.Published);
        }

        if (request.Difficulty.HasValue)
        {
            q = q.Where(r => r.Difficulty == request.Difficulty.Value);
        }

        if (request.MaxTotalTimeMinutes.HasValue)
        {
            q = q.Where(r =>
                r.TotalTimeMinutes != null
                && r.TotalTimeMinutes <= request.MaxTotalTimeMinutes.Value
            );
        }

        if (request.MinRating.HasValue)
        {
            q = q.Where(r => r.AverageRating != null && r.AverageRating >= request.MinRating.Value);
        }

        if (request.ForgottenFavoritesOnly)
        {
            var days = Math.Clamp(request.ForgottenDaysThreshold, 1, 3650);
            var cutoff = _timeProvider.GetUtcNow().AddDays(-days);

            q = q.Where(r =>
                ((r.AverageRating != null && r.AverageRating >= 4.0m) || r.TimesCooked >= 2)
                && (r.LastCookedAt == null || r.LastCookedAt <= cutoff)
            );
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            var tagStr = request.Tag.Trim();
            if (Guid.TryParse(tagStr, out var tagId))
            {
                q = q.Where(r => r.RecipeTags.Any(rt => rt.TagId == tagId));
            }
            else
            {
                var normTag = tagStr.ToLowerInvariant();
                q = q.Where(r =>
                    r.RecipeTags.Any(rt =>
                        rt.Tag.Slug == normTag
                        || rt.Tag.NormalizedName == normTag
                        || rt.Tag.Slug.EndsWith($"-{normTag}")
                    )
                );
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Collection))
        {
            var colStr = request.Collection.Trim();
            if (Guid.TryParse(colStr, out var colId))
            {
                q = q.Where(r => r.RecipeCollections.Any(rc => rc.CollectionId == colId));
            }
            else
            {
                var normCol = colStr.ToLowerInvariant();
                q = q.Where(r =>
                    r.RecipeCollections.Any(rc =>
                        rc.Collection.Slug == normCol || rc.Collection.NormalizedName == normCol
                    )
                );
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var norm = request.Query.Trim().ToLowerInvariant();
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
            .Take(limit)
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

    public async Task<McpRecipeDetailDto?> GetRecipeAsync(
        Guid? recipeId = null,
        string? slug = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!recipeId.HasValue && string.IsNullOrWhiteSpace(slug))
        {
            throw new DomainValidationException("Provide either recipeId or slug.");
        }

        var resolvedId = recipeId;
        if (!resolvedId.HasValue && !string.IsNullOrWhiteSpace(slug))
        {
            var normSlug = slug.Trim().ToLowerInvariant();
            resolvedId = await dbContext
                .Recipes.AsNoTracking()
                .Where(r => r.Slug == normSlug)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (!resolvedId.HasValue)
            {
                return null;
            }
        }

        var detail = await recipeService.GetRecipeByIdAsync(resolvedId!.Value, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var ingredientMap = detail.Ingredients.ToDictionary(i => i.Id);

        var mcpSteps = detail
            .Steps.Select(s =>
            {
                var callouts = s
                    .IngredientIds.Where(ingredientMap.ContainsKey)
                    .Select(id =>
                    {
                        var ing = ingredientMap[id];
                        return new McpStepIngredientCalloutDto(
                            IngredientId: ing.Id,
                            DisplayName: ing.DisplayName,
                            Quantity: ing.Quantity,
                            MaxQuantity: ing.MaxQuantity,
                            Unit: ing.Unit,
                            CustomUnitName: ing.CustomUnitName,
                            PreparationNote: ing.PreparationNote
                        );
                    })
                    .ToList();

                return new McpRecipeStepDetailDto(
                    StepNumber: s.StepNumber,
                    SectionName: s.SectionName,
                    Instruction: s.Instruction,
                    StepIngredients: callouts,
                    Timers: s.Timers
                );
            })
            .ToList();

        return new McpRecipeDetailDto(
            Id: detail.Id,
            Title: detail.Title,
            Slug: detail.Slug,
            Description: detail.Description,
            Status: detail.Status,
            BaseServings: detail.BaseServings,
            YieldDisplay: detail.YieldDisplay,
            PrepTimeMinutes: detail.PrepTimeMinutes,
            CookTimeMinutes: detail.CookTimeMinutes,
            TotalTimeMinutes: detail.TotalTimeMinutes,
            Difficulty: detail.Difficulty,
            SourceUrl: detail.SourceUrl,
            SourceName: detail.SourceName,
            SourceAuthor: detail.SourceAuthor,
            SourceBookTitle: detail.SourceBookTitle,
            SourceBookPage: detail.SourceBookPage,
            CoverImagePath: detail.CoverImagePath,
            ThumbnailImagePath: detail.ThumbnailImagePath,
            TimesCooked: detail.TimesCooked,
            LastCookedAt: detail.LastCookedAt,
            AverageRating: detail.AverageRating,
            Ingredients: detail.Ingredients,
            Steps: mcpSteps,
            LinkedSubRecipes: detail.LinkedSubRecipes,
            CreatedAt: detail.CreatedAt,
            CreatedBy: detail.CreatedBy,
            ModifiedAt: detail.ModifiedAt,
            ModifiedBy: detail.ModifiedBy,
            Collections: detail.Collections,
            Tags: detail.Tags,
            Attachments: detail.Attachments,
            CookHistoryNotes: []
        );
    }

    public async Task<McpIngredientMatchResponseDto> MatchRecipesByIngredientNamesAsync(
        IReadOnlyList<string> ingredientNames,
        int limit = 15,
        CancellationToken cancellationToken = default
    )
    {
        if (ingredientNames is null || ingredientNames.Count == 0)
        {
            throw new DomainValidationException("Provide at least one ingredient name.");
        }

        var nonBlank = ingredientNames
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (nonBlank.Count == 0)
        {
            throw new DomainValidationException("Provide at least one non-empty ingredient name.");
        }

        var foodItems = await dbContext
            .FoodItems.AsNoTracking()
            .Include(f => f.Aliases)
            .ToListAsync(cancellationToken);

        var resolved = new List<McpResolvedFoodItemDto>();
        var unresolved = new List<string>();
        var matchedFoodIds = new List<Guid>();

        foreach (var rawName in nonBlank)
        {
            var match = FindMatchingFood(rawName, foodItems);
            if (match is not null)
            {
                resolved.Add(
                    new McpResolvedFoodItemDto(rawName, match.Id, match.Name, match.IsPantryStaple)
                );
                matchedFoodIds.Add(match.Id);
            }
            else
            {
                unresolved.Add(rawName);
            }
        }

        var matches = new List<IngredientMatchResultDto>();
        if (matchedFoodIds.Count > 0)
        {
            var request = new IngredientMatchRequest(matchedFoodIds.Distinct().ToList());
            var results = await recipeService.MatchRecipesByIngredientsAsync(
                request,
                cancellationToken
            );
            matches = results.Take(Math.Clamp(limit, 1, 50)).ToList();
        }

        return new McpIngredientMatchResponseDto(resolved, unresolved, matches);
    }

    public async Task<McpHouseholdTaxonomyDto> GetHouseholdTaxonomyAsync(
        CancellationToken cancellationToken = default
    )
    {
        var collections = await taxonomyService.GetCollectionsAsync(cancellationToken);
        var tags = await taxonomyService.GetTagsAsync(null, cancellationToken);
        var aisles = await foodCatalogService.GetStoreAislesAsync(cancellationToken);

        var units = new[]
        {
            "tsp",
            "tbsp",
            "fl_oz",
            "cup",
            "pt",
            "qt",
            "gal",
            "ml",
            "l",
            "g",
            "kg",
            "oz",
            "lb",
            "pinch",
            "dash",
            "clove",
            "can",
            "bunch",
            "slice",
            "whole",
            "custom",
        };

        return new McpHouseholdTaxonomyDto(collections, tags, aisles, units);
    }

    public async Task<IngestionJobSummaryDto> EnqueueIngestionAsync(
        McpEnqueueIngestionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var hasUrl = !string.IsNullOrWhiteSpace(request.Url);
        var hasText = !string.IsNullOrWhiteSpace(request.RawText);

        if ((!hasUrl && !hasText) || (hasUrl && hasText))
        {
            throw new DomainValidationException("Provide either 'url' or 'rawText' (not both).");
        }

        if (hasUrl)
        {
            var jobs = await ingestionService.EnqueueWebUrlsAsync(
                new EnqueueWebUrlIngestionRequest([request.Url!.Trim()]),
                cancellationToken
            );
            return jobs[0];
        }

        return await ingestionService.EnqueueRawTextAsync(
            new EnqueueRawTextIngestionRequest(request.RawText!.Trim(), request.TitleHint?.Trim()),
            cancellationToken
        );
    }

    public async Task<RecipeDetailDto> SaveStructuredRecipeAsync(
        McpSaveStructuredRecipeRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new DomainValidationException("Recipe title is required.");
        }

        RecipeDifficulty? difficulty = null;
        if (!string.IsNullOrWhiteSpace(request.Difficulty))
        {
            if (
                Enum.TryParse<RecipeDifficulty>(
                    request.Difficulty.Trim(),
                    true,
                    out var parsedDifficulty
                )
            )
            {
                difficulty = parsedDifficulty;
            }
            else
            {
                throw new DomainValidationException(
                    $"Invalid recipe difficulty '{request.Difficulty}'. Allowed values: Easy, Medium, Project."
                );
            }
        }

        var storeAisles = await dbContext.StoreAisles.ToListAsync(cancellationToken);
        var otherAisle = storeAisles.FirstOrDefault(a => a.NormalizedName == "other");

        var allFoodItems = await dbContext
            .FoodItems.Include(f => f.Aliases)
            .ToListAsync(cancellationToken);

        var ingredientsInput = new List<RecipeIngredientInput>();
        if (request.Ingredients is { Count: > 0 })
        {
            foreach (var ing in request.Ingredients)
            {
                if (string.IsNullOrWhiteSpace(ing.DisplayName))
                {
                    continue;
                }

                var (unit, customUnit) = RecipeDraftNormalizer.NormalizeUnit(ing.Unit);
                var candidateName = !string.IsNullOrWhiteSpace(ing.CanonicalFoodName)
                    ? ing.CanonicalFoodName.Trim()
                    : ing.DisplayName.Trim();

                var matchedFood = FindMatchingFood(candidateName, allFoodItems);
                Guid foodItemId;

                if (matchedFood is not null)
                {
                    foodItemId = matchedFood.Id;
                }
                else
                {
                    var proposedName = ToTitleCase(candidateName);
                    var normName = proposedName.ToLowerInvariant();

                    var existingByName = allFoodItems.FirstOrDefault(f =>
                        f.NormalizedName == normName
                        || f.Aliases.Any(a => a.NormalizedAlias == normName)
                    );

                    if (existingByName is not null)
                    {
                        foodItemId = existingByName.Id;
                    }
                    else
                    {
                        var suggestedAisleId =
                            ResolveSuggestedAisle(ing.SuggestedAisleName, storeAisles)
                            ?? otherAisle?.Id;

                        var newFoodItem = new FoodItem
                        {
                            Name = proposedName,
                            NormalizedName = normName,
                            DefaultStoreAisleId = suggestedAisleId,
                            IsPantryStaple = ing.IsPantryStaple,
                        };

                        dbContext.FoodItems.Add(newFoodItem);
                        await dbContext.SaveChangesAsync(cancellationToken);
                        allFoodItems.Add(newFoodItem);
                        foodItemId = newFoodItem.Id;
                    }
                }

                ingredientsInput.Add(
                    new RecipeIngredientInput(
                        SectionName: string.IsNullOrWhiteSpace(ing.SectionName)
                            ? null
                            : ing.SectionName.Trim(),
                        Quantity: ing.Quantity,
                        MaxQuantity: ing.MaxQuantity,
                        Unit: unit,
                        CustomUnitName: customUnit,
                        FoodItemId: foodItemId,
                        LinkedRecipeId: null,
                        DisplayName: ing.DisplayName.Trim(),
                        PreparationNote: string.IsNullOrWhiteSpace(ing.PreparationNote)
                            ? null
                            : ing.PreparationNote.Trim(),
                        IsOptional: ing.IsOptional,
                        OriginalRawText: string.IsNullOrWhiteSpace(ing.OriginalRawText)
                            ? null
                            : ing.OriginalRawText.Trim()
                    )
                );
            }
        }

        var stepsInput = new List<RecipeStepInput>();
        if (request.Steps is { Count: > 0 })
        {
            var ingCount = ingredientsInput.Count;
            for (var i = 0; i < request.Steps.Count; i++)
            {
                var s = request.Steps[i];
                var instruction = s.Instruction?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    continue;
                }

                var timers = new List<RecipeStepTimerInput>();
                if (s.Timers is { Count: > 0 })
                {
                    foreach (var t in s.Timers)
                    {
                        if (t.DurationSeconds >= 1 && !string.IsNullOrWhiteSpace(t.Label))
                        {
                            timers.Add(new RecipeStepTimerInput(t.DurationSeconds, t.Label.Trim()));
                        }
                    }
                }

                if (timers.Count == 0)
                {
                    timers.AddRange(StepTimerExtractor.DetectTimers(instruction, i + 1));
                }

                var validIndices = (s.IngredientIndices ?? [])
                    .Where(idx => idx >= 0 && idx < ingCount)
                    .Distinct()
                    .OrderBy(idx => idx)
                    .ToList();

                stepsInput.Add(
                    new RecipeStepInput(
                        SectionName: string.IsNullOrWhiteSpace(s.SectionName)
                            ? null
                            : s.SectionName.Trim(),
                        Instruction: instruction,
                        IngredientIndices: validIndices,
                        Timers: timers,
                        ImagePath: null
                    )
                );
            }
        }

        var collectionIds = new List<Guid>();
        if (request.Collections is { Count: > 0 })
        {
            var existingCollections = await dbContext.Collections.ToListAsync(cancellationToken);
            foreach (var colRef in request.Collections)
            {
                if (string.IsNullOrWhiteSpace(colRef))
                {
                    continue;
                }

                var clean = colRef.Trim();
                if (Guid.TryParse(clean, out var cid))
                {
                    if (existingCollections.Any(c => c.Id == cid) && !collectionIds.Contains(cid))
                    {
                        collectionIds.Add(cid);
                    }
                }
                else
                {
                    var norm = clean.ToLowerInvariant();
                    var matched = existingCollections.FirstOrDefault(c =>
                        c.Slug == norm || c.NormalizedName == norm
                    );
                    if (matched is not null && !collectionIds.Contains(matched.Id))
                    {
                        collectionIds.Add(matched.Id);
                    }
                }
            }
        }

        var tagIds = new List<Guid>();
        if (request.Tags is { Count: > 0 })
        {
            var existingTags = await dbContext.Tags.ToListAsync(cancellationToken);
            foreach (var tagRef in request.Tags)
            {
                if (string.IsNullOrWhiteSpace(tagRef))
                {
                    continue;
                }

                var clean = tagRef.Trim();
                if (Guid.TryParse(clean, out var tid))
                {
                    if (existingTags.Any(t => t.Id == tid) && !tagIds.Contains(tid))
                    {
                        tagIds.Add(tid);
                    }
                }
                else
                {
                    var norm = clean.ToLowerInvariant();
                    var matched = existingTags.FirstOrDefault(t =>
                        t.Slug == norm || t.NormalizedName == norm || t.Slug.EndsWith($"-{norm}")
                    );

                    if (matched is not null)
                    {
                        if (!tagIds.Contains(matched.Id))
                        {
                            tagIds.Add(matched.Id);
                        }
                    }
                    else if (clean.Length <= 80)
                    {
                        var createdTag = await taxonomyService.CreateTagAsync(
                            new UpsertTagRequest(clean, TagCategory.Custom),
                            cancellationToken
                        );
                        existingTags.Add(
                            new Tag
                            {
                                Id = createdTag.Id,
                                Name = createdTag.Name,
                                Slug = createdTag.Slug,
                                NormalizedName = createdTag.Name.ToLowerInvariant(),
                                Category = createdTag.Category,
                            }
                        );
                        tagIds.Add(createdTag.Id);
                    }
                }
            }
        }

        var status = request.PublishImmediately ? RecipeStatus.Published : RecipeStatus.DraftReview;

        string? coverImagePath = null;
        string? thumbnailImagePath = null;

        if (request.RecipeId.HasValue)
        {
            var existingRecipe = await dbContext
                .Recipes.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == request.RecipeId.Value, cancellationToken);

            if (existingRecipe is null)
            {
                throw new NotFoundException("Recipe not found.");
            }

            coverImagePath = existingRecipe.CoverImagePath;
            thumbnailImagePath = existingRecipe.ThumbnailImagePath;

            var updateRequest = new UpsertRecipeRequest(
                Title: request.Title.Trim(),
                Description: request.Description?.Trim(),
                Status: status,
                BaseServings: request.BaseServings,
                YieldDisplay: request.YieldDisplay?.Trim(),
                PrepTimeMinutes: request.PrepTimeMinutes,
                CookTimeMinutes: request.CookTimeMinutes,
                TotalTimeMinutes: request.TotalTimeMinutes,
                Difficulty: difficulty,
                SourceUrl: request.SourceUrl?.Trim(),
                SourceName: request.SourceName?.Trim(),
                SourceAuthor: request.SourceAuthor?.Trim(),
                SourceBookTitle: request.SourceBookTitle?.Trim(),
                SourceBookPage: request.SourceBookPage,
                Ingredients: ingredientsInput,
                Steps: stepsInput,
                CoverImagePath: coverImagePath,
                ThumbnailImagePath: thumbnailImagePath,
                CollectionIds: collectionIds,
                TagIds: tagIds
            );

            var updated = await recipeService.UpdateRecipeAsync(
                request.RecipeId.Value,
                updateRequest,
                cancellationToken
            );
            return updated!;
        }

        var createRequest = new UpsertRecipeRequest(
            Title: request.Title.Trim(),
            Description: request.Description?.Trim(),
            Status: status,
            BaseServings: request.BaseServings,
            YieldDisplay: request.YieldDisplay?.Trim(),
            PrepTimeMinutes: request.PrepTimeMinutes,
            CookTimeMinutes: request.CookTimeMinutes,
            TotalTimeMinutes: request.TotalTimeMinutes,
            Difficulty: difficulty,
            SourceUrl: request.SourceUrl?.Trim(),
            SourceName: request.SourceName?.Trim(),
            SourceAuthor: request.SourceAuthor?.Trim(),
            SourceBookTitle: request.SourceBookTitle?.Trim(),
            SourceBookPage: request.SourceBookPage,
            Ingredients: ingredientsInput,
            Steps: stepsInput,
            CoverImagePath: null,
            ThumbnailImagePath: null,
            CollectionIds: collectionIds,
            TagIds: tagIds
        );

        return await recipeService.CreateRecipeAsync(createRequest, cancellationToken);
    }

    private static FoodItem? FindMatchingFood(string rawName, IEnumerable<FoodItem> items)
    {
        var norm = rawName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(norm))
        {
            return null;
        }

        var direct = items.FirstOrDefault(f =>
            f.NormalizedName == norm || f.Aliases.Any(a => a.NormalizedAlias == norm)
        );
        if (direct is not null)
        {
            return direct;
        }

        var singular = Singularize(norm);
        if (singular != norm)
        {
            var singMatch = items.FirstOrDefault(f =>
                f.NormalizedName == singular || f.Aliases.Any(a => a.NormalizedAlias == singular)
            );
            if (singMatch is not null)
            {
                return singMatch;
            }
        }

        return null;
    }

    private static string Singularize(string word)
    {
        if (word.EndsWith("ies") && word.Length > 4)
        {
            return word[..^3] + "y";
        }
        if (
            (
                word.EndsWith("oes")
                || word.EndsWith("ses")
                || word.EndsWith("xes")
                || word.EndsWith("ches")
                || word.EndsWith("shes")
            )
            && word.Length > 4
        )
        {
            return word[..^2];
        }
        if (word.EndsWith('s') && !word.EndsWith("ss") && word.Length > 3)
        {
            return word[..^1];
        }
        return word;
    }

    private static Guid? ResolveSuggestedAisle(string? name, IEnumerable<StoreAisle> aisles)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var norm = name.Trim().ToLowerInvariant();
        var direct = aisles.FirstOrDefault(a =>
            a.NormalizedName == norm
            || a.NormalizedName.Contains(norm)
            || norm.Contains(a.NormalizedName)
        );
        return direct?.Id;
    }

    private static string ToTitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i].ToLowerInvariant();
            words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(" ", words);
    }
}
