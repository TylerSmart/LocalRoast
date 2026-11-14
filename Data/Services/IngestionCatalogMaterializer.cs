using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public static class IngestionCatalogMaterializer
{
    public static string? ValidateOptionalMediaPath(string? path)
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

    public static async Task<Guid> ResolveOrCreateFoodItemAsync(
        AppDbContext dbContext,
        string proposedFoodItemName,
        bool suggestedIsPantryStaple,
        FoodCategory? suggestedCategory,
        CancellationToken cancellationToken
    )
    {
        var cleanName = proposedFoodItemName.Trim();

        if (!FoodNameSanitizer.IsValidFoodName(cleanName))
        {
            throw new DomainValidationException(
                $"Cannot create or resolve a food item with invalid placeholder name '{cleanName}'."
            );
        }

        if (cleanName.Length > 150)
        {
            cleanName = cleanName[..150].Trim();
        }

        var normName = cleanName.ToLowerInvariant();

        var singularNorm = RecipeDraftNormalizer.Singularize(normName);

        var candidateNorms = new[]
        {
            normName,
            singularNorm,
            $"{singularNorm}s",
            $"{singularNorm}es",
        }
            .Distinct()
            .ToArray();

        var existing =
            await dbContext.FoodItems.FirstOrDefaultAsync(
                f => candidateNorms.Contains(f.NormalizedName),
                cancellationToken
            )
            ?? await dbContext
                .FoodItemAliases.Where(a => candidateNorms.Contains(a.NormalizedAlias))
                .Select(a => a.FoodItem)
                .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing.Id;
        }

        var newFoodItem = new FoodItem
        {
            Name = cleanName,

            NormalizedName = normName,

            IsPantryStaple = suggestedIsPantryStaple,

            Category = suggestedCategory ?? FoodCategoryClassifier.Classify(cleanName),
        };

        dbContext.FoodItems.Add(newFoodItem);

        await dbContext.SaveChangesAsync(cancellationToken);

        return newFoodItem.Id;
    }

    public sealed record MatchedRecipeReference(Guid Id, string Title, string Slug);

    public static async Task<MatchedRecipeReference?> FindMatchingRecipeAsync(
        AppDbContext dbContext,
        string title,
        Guid? excludedRecipeId,
        CancellationToken cancellationToken
    )
    {
        var cleanTitle = RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(title) ?? title.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return null;
        }

        var normalizedTitle = cleanTitle.ToUpperInvariant();
        var normalizedStraight = NormalizeTitleComparisonKey(cleanTitle).ToUpperInvariant();

        var exact = await dbContext
            .Recipes.AsNoTracking()
            .Where(r => !excludedRecipeId.HasValue || r.Id != excludedRecipeId.Value)
            .Where(r =>
                r.Title.ToUpper() == normalizedTitle
                || r.Title.Replace("\u2019", "'").Replace("\u2018", "'").ToUpper()
                    == normalizedStraight
            )
            .Select(r => new MatchedRecipeReference(r.Id, r.Title, r.Slug))
            .FirstOrDefaultAsync(cancellationToken);

        if (exact is not null)
        {
            return exact;
        }

        var prefixUpper = normalizedStraight + " (";
        var candidates = await dbContext
            .Recipes.AsNoTracking()
            .Where(r => !excludedRecipeId.HasValue || r.Id != excludedRecipeId.Value)
            .Where(r =>
                r.Title.Replace("\u2019", "'")
                    .Replace("\u2018", "'")
                    .ToUpper()
                    .StartsWith(prefixUpper)
                || r.Title.EndsWith(")")
                || (normalizedStraight.EndsWith("PIE DOUGH") && r.Title.ToUpper().Contains("PIE"))
            )
            .Select(r => new MatchedRecipeReference(r.Id, r.Title, r.Slug))
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(c => IsRecipeReferenceTitleMatch(c.Title, cleanTitle));
    }

    public static bool IsRecipeReferenceTitleMatch(
        string publishedTitle,
        string cleanReferenceTitle
    )
    {
        var normPublished = NormalizeTitleComparisonKey(publishedTitle);
        var normRef = NormalizeTitleComparisonKey(cleanReferenceTitle);
        if (string.IsNullOrWhiteSpace(normPublished) || string.IsNullOrWhiteSpace(normRef))
        {
            return false;
        }

        if (string.Equals(normPublished, normRef, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normPublished.StartsWith(normRef + " (", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var strippedPublished = Regex.Replace(normPublished, @"\s*\([^()]+\)\s*$", "").Trim();
        if (
            !string.IsNullOrWhiteSpace(strippedPublished)
            && string.Equals(strippedPublished, normRef, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (normRef.EndsWith("pie dough", StringComparison.OrdinalIgnoreCase))
        {
            if (
                normRef.Contains("single-crust", StringComparison.OrdinalIgnoreCase)
                && normPublished.Contains("Single-Crust Pie", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
            if (
                normRef.Contains("double-crust", StringComparison.OrdinalIgnoreCase)
                && normPublished.Contains("Double-Crust Pie", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeTitleComparisonKey(string title) =>
        title.Replace('\u2019', '\'').Replace('\u2018', '\'').Trim();

    public static async Task<Guid?> ResolveRecipeReferenceAsync(
        AppDbContext dbContext,
        string title,
        Guid? excludedRecipeId,
        CancellationToken cancellationToken
    ) => (await FindMatchingRecipeAsync(dbContext, title, excludedRecipeId, cancellationToken))?.Id;

    public static async Task<int> BacklinkUnresolvedRecipeReferencesAsync(
        AppDbContext dbContext,
        Guid recipeId,
        string recipeTitle,
        CancellationToken cancellationToken = default
    )
    {
        var cleanTitle =
            RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(recipeTitle) ?? recipeTitle.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return 0;

        var waitingIngredients = await dbContext
            .RecipeIngredients.Where(ri =>
                ri.LinkedRecipeId == null
                && ri.UnresolvedRecipeTitle != null
                && ri.RecipeId != recipeId
            )
            .ToListAsync(cancellationToken);

        var updatedCount = 0;
        foreach (var ri in waitingIngredients)
        {
            var candidateTitle =
                RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(ri.UnresolvedRecipeTitle)
                ?? ri.UnresolvedRecipeTitle!.Trim();
            if (
                string.Equals(candidateTitle, cleanTitle, StringComparison.OrdinalIgnoreCase)
                || IsRecipeReferenceTitleMatch(recipeTitle, candidateTitle)
            )
            {
                ri.LinkedRecipeId = recipeId;
                ri.UnresolvedRecipeTitle = null;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return updatedCount;
    }

    public static async Task<int> RepairAllUnresolvedRecipeReferencesAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken = default
    )
    {
        var waitingIngredients = await dbContext
            .RecipeIngredients.Where(ri =>
                ri.LinkedRecipeId == null && ri.UnresolvedRecipeTitle != null
            )
            .ToListAsync(cancellationToken);

        if (waitingIngredients.Count == 0)
            return 0;

        var allRecipes = await dbContext
            .Recipes.AsNoTracking()
            .Select(r => new { r.Id, r.Title })
            .ToListAsync(cancellationToken);

        var recipeMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in allRecipes)
        {
            var clean =
                RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(r.Title) ?? r.Title.Trim();
            if (!recipeMap.ContainsKey(clean))
            {
                recipeMap[clean] = r.Id;
            }
            if (!recipeMap.ContainsKey(r.Title.Trim()))
            {
                recipeMap[r.Title.Trim()] = r.Id;
            }
        }

        var updatedCount = 0;
        foreach (var ri in waitingIngredients)
        {
            var candidateTitle =
                RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(ri.UnresolvedRecipeTitle)
                ?? ri.UnresolvedRecipeTitle!.Trim();
            Guid matchedId = Guid.Empty;
            var found =
                recipeMap.TryGetValue(candidateTitle, out matchedId)
                || recipeMap.TryGetValue(ri.UnresolvedRecipeTitle!.Trim(), out matchedId);

            if (!found)
            {
                var fuzzy = allRecipes.FirstOrDefault(r =>
                    r.Id != ri.RecipeId && IsRecipeReferenceTitleMatch(r.Title, candidateTitle)
                );
                if (fuzzy is not null)
                {
                    matchedId = fuzzy.Id;
                    found = true;
                }
            }

            if (found && matchedId != ri.RecipeId)
            {
                ri.LinkedRecipeId = matchedId;
                ri.UnresolvedRecipeTitle = null;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return updatedCount;
    }

    public static async Task<(
        IngestionExtractedDraftDto Draft,
        List<string> UnresolvedReferenceIssues
    )> ResolveDraftRecipeReferencesAsync(
        AppDbContext dbContext,
        IngestionExtractedDraftDto draft,
        Guid? excludedRecipeId,
        CancellationToken cancellationToken
    )
    {
        var unresolvedIssues = new List<string>();
        if (
            draft.Ingredients is not { Count: > 0 }
            || !draft.Ingredients.Any(i => i.IsRecipeReference)
        )
        {
            return (draft, unresolvedIssues);
        }

        var updatedIngredients = new List<IngestionDraftIngredientDto>(draft.Ingredients.Count);
        foreach (var ing in draft.Ingredients)
        {
            if (ing.IsRecipeReference && !string.IsNullOrWhiteSpace(ing.ReferencedRecipeTitle))
            {
                var cleanTitle =
                    RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(ing.ReferencedRecipeTitle)
                    ?? ing.ReferencedRecipeTitle.Trim();

                var matchedRecipe = await FindMatchingRecipeAsync(
                    dbContext,
                    cleanTitle,
                    excludedRecipeId,
                    cancellationToken
                );

                if (matchedRecipe is not null)
                {
                    updatedIngredients.Add(
                        ing with
                        {
                            MatchedRecipeId = matchedRecipe.Id,
                            MatchedRecipeTitle = matchedRecipe.Title,
                            MatchedRecipeSlug = matchedRecipe.Slug,
                        }
                    );
                    continue;
                }

                var unlinkedTitle = !string.IsNullOrWhiteSpace(cleanTitle)
                    ? cleanTitle
                    : (
                        !string.IsNullOrWhiteSpace(ing.DisplayName) ? ing.DisplayName : "sub-recipe"
                    );
                unresolvedIssues.Add(
                    $"Referenced recipe '{unlinkedTitle}' is not published yet and could not be linked."
                );
            }
            else if (ing.IsRecipeReference && !ing.MatchedRecipeId.HasValue)
            {
                var unlinkedTitle = !string.IsNullOrWhiteSpace(ing.DisplayName)
                    ? ing.DisplayName
                    : "sub-recipe";
                unresolvedIssues.Add(
                    $"Referenced recipe '{unlinkedTitle}' is missing a referenced recipe title."
                );
            }

            updatedIngredients.Add(ing);
        }

        return (draft with { Ingredients = updatedIngredients }, unresolvedIssues);
    }

    public static async Task<RecipeDetailDto> PublishDraftAsync(
        AppDbContext dbContext,
        IRecipeService recipeService,
        IMediaStorageService mediaStorageService,
        IngestionJob job,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken
    )
    {
        var ingredientsInput = new List<RecipeIngredientInput>();

        if (draft.Ingredients is not null)
        {
            foreach (var ing in draft.Ingredients)
            {
                var recipeTitle = ing.IsRecipeReference ? ing.ReferencedRecipeTitle?.Trim() : null;
                if (ing.IsRecipeReference && string.IsNullOrWhiteSpace(recipeTitle))
                {
                    throw new DomainValidationException(
                        "A title is required for a recipe component reference."
                    );
                }

                var foodItemId = ing.IsRecipeReference ? null : ing.MatchedFoodItemId;
                Guid? linkedRecipeId = null;

                if (!string.IsNullOrWhiteSpace(recipeTitle))
                {
                    linkedRecipeId =
                        ing.MatchedRecipeId
                        ?? await ResolveRecipeReferenceAsync(
                            dbContext,
                            recipeTitle,
                            job.ResultingRecipeId,
                            cancellationToken
                        );
                }

                if (
                    foodItemId == null
                    && !ing.IsRecipeReference
                    && ing.CreateNewFoodItem
                    && !string.IsNullOrWhiteSpace(ing.ProposedFoodItemName)
                )
                {
                    foodItemId = await ResolveOrCreateFoodItemAsync(
                        dbContext,
                        ing.ProposedFoodItemName,
                        ing.SuggestedIsPantryStaple,
                        ing.SuggestedCategory,
                        cancellationToken
                    );
                }

                var (cleanQty, cleanMaxQty) = RecipeDraftNormalizer.NormalizeQuantities(
                    ing.Quantity,
                    ing.MaxQuantity
                );

                var cleanUnit =
                    ing.Unit == MeasurementUnit.Custom
                    && (
                        string.IsNullOrWhiteSpace(ing.CustomUnitName)
                        || RecipeDraftNormalizer.IsDisallowedUnitPlaceholder(ing.CustomUnitName)
                    )
                        ? null
                        : ing.Unit;

                if (
                    (cleanUnit is MeasurementUnit.Pinch or MeasurementUnit.Dash)
                    && (cleanQty is null or <= 0.1m)
                )
                {
                    cleanQty = 1m;
                }

                ingredientsInput.Add(
                    new RecipeIngredientInput(
                        SectionName: ing.SectionName,
                        Quantity: cleanQty,
                        MaxQuantity: cleanMaxQty,
                        Unit: cleanUnit,
                        CustomUnitName: cleanUnit == MeasurementUnit.Custom
                            ? ing.CustomUnitName
                            : null,
                        FoodItemId: foodItemId,
                        LinkedRecipeId: linkedRecipeId,
                        DisplayName: string.IsNullOrWhiteSpace(recipeTitle)
                            ? ing.DisplayName
                            : recipeTitle,
                        PreparationNote: ing.PreparationNote,
                        IsOptional: ing.IsOptional,
                        OriginalRawText: ing.OriginalRawText,
                        AlternativeMeasurement: ing.AlternativeMeasurement,
                        PreferAlternativeMeasurement: ing.PreferAlternativeMeasurement,
                        UnresolvedRecipeTitle: linkedRecipeId.HasValue ? null : recipeTitle
                    )
                );
            }
        }

        var stepsInput = new List<RecipeStepInput>();
        if (draft.Steps is not null)
        {
            foreach (var s in draft.Steps)
            {
                stepsInput.Add(
                    new RecipeStepInput(
                        SectionName: s.SectionName,
                        Instruction: s.Instruction,
                        IngredientIndices: s.IngredientIndices,
                        Timers: s.Timers,
                        ImagePath: s.ImagePath,
                        IngredientAmounts: s.IngredientAmounts
                    )
                );
            }
        }

        var resolvedTagIds = await ResolveAndMaterializeTagsAsync(
            dbContext,
            draft.SuggestedTagIds,
            draft.ProposedTagNames,
            cancellationToken
        );

        var upsertRequest = new UpsertRecipeRequest(
            Title: draft.Title,
            Description: draft.Description != null && draft.Description.Length > 2000
                ? draft.Description[..2000].TrimEnd()
                : draft.Description,
            Status: RecipeStatus.Published,
            BaseServings: draft.BaseServings,
            YieldDisplay: draft.YieldDisplay,
            PrepTimeMinutes: draft.PrepTimeMinutes,
            CookTimeMinutes: draft.CookTimeMinutes,
            TotalTimeMinutes: draft.TotalTimeMinutes,
            Difficulty: draft.Difficulty,
            SourceUrl: draft.SourceUrl,
            SourceName: draft.SourceName,
            SourceAuthor: draft.SourceAuthor,
            SourceBookTitle: draft.SourceBookTitle,
            SourceBookPage: draft.SourceBookPage,
            Ingredients: ingredientsInput,
            Steps: stepsInput,
            CoverImagePath: draft.CoverImagePath,
            ThumbnailImagePath: draft.ThumbnailImagePath,
            CollectionIds: draft.SuggestedCollectionIds,
            TagIds: resolvedTagIds,
            ExtractedModel: job.ExtractedModel
        );

        RecipeDetailDto createdRecipe;

        if (job.ResultingRecipeId.HasValue)
        {
            var existingRecipe = await dbContext.Recipes.FirstOrDefaultAsync(
                r => r.Id == job.ResultingRecipeId.Value,
                cancellationToken
            );

            if (existingRecipe is not null)
            {
                var updated = await recipeService.UpdateRecipeAsync(
                    existingRecipe.Id,
                    upsertRequest,
                    cancellationToken
                );

                createdRecipe =
                    updated
                    ?? await recipeService.CreateRecipeAsync(upsertRequest, cancellationToken);
            }
            else
            {
                createdRecipe = await recipeService.CreateRecipeAsync(
                    upsertRequest,
                    cancellationToken
                );
            }
        }
        else
        {
            createdRecipe = await recipeService.CreateRecipeAsync(upsertRequest, cancellationToken);
        }

        await PromoteOrAttachSourceFilesAsync(
            dbContext,
            mediaStorageService,
            job,
            draft,
            createdRecipe.Id,
            cancellationToken
        );

        return createdRecipe;
    }

    public static async Task<int> PromoteWaitingRecipeReferenceJobsAsync(
        AppDbContext dbContext,
        IRecipeService recipeService,
        IMediaStorageService mediaStorageService,
        bool globalAutoPublish,
        CancellationToken cancellationToken = default
    )
    {
        var totalPromoted = 0;
        var visitedJobIds = new HashSet<Guid>();

        for (var pass = 0; pass < 3; pass++)
        {
            var waitingJobIds = await dbContext
                .IngestionJobs.AsNoTracking()
                .Where(j =>
                    j.Status == IngestionJobStatus.ReadyForReview
                    && j.StatusDetail != null
                    && j.StatusDetail.Contains("is not published yet")
                    && j.ExtractedDraftJson != null
                )
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.Id)
                .ToListAsync(cancellationToken);

            var candidateIds = waitingJobIds.Where(id => !visitedJobIds.Contains(id)).ToList();
            if (candidateIds.Count == 0)
            {
                break;
            }

            var promotedInPass = 0;
            foreach (var jobId in candidateIds)
            {
                visitedJobIds.Add(jobId);

                var waitingJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == jobId,
                    cancellationToken
                );
                if (
                    waitingJob is null
                    || waitingJob.Status != IngestionJobStatus.ReadyForReview
                    || string.IsNullOrWhiteSpace(waitingJob.ExtractedDraftJson)
                )
                {
                    continue;
                }

                IngestionExtractedDraftDto? draft;
                try
                {
                    draft = JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
                        waitingJob.ExtractedDraftJson,
                        JsonOptions
                    );
                }
                catch
                {
                    continue;
                }

                if (draft is null)
                {
                    continue;
                }

                var (resolvedDraft, refIssues) = await ResolveDraftRecipeReferencesAsync(
                    dbContext,
                    draft,
                    waitingJob.ResultingRecipeId,
                    cancellationToken
                );

                var diagSource = !string.IsNullOrWhiteSpace(waitingJob.StructuredSourceText)
                    ? waitingJob.StructuredSourceText
                    : waitingJob.NormalizedSourceText ?? waitingJob.RawText;
                var payload = IngestionDraftRepairMapper.ToPayload(resolvedDraft);
                var diagIssues = RecipeExtractionDiagnostics.GetReviewIssues(payload, diagSource);
                var qualityWarnings = RecipeExtractionDiagnostics.GetQualityWarnings(payload);

                var allIssues = diagIssues
                    .Concat(refIssues)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var updatedScore = RecipeDraftNormalizer.CalculateConfidenceScore(
                    resolvedDraft.ConfidenceScore,
                    resolvedDraft.Ingredients?.Count ?? 0,
                    resolvedDraft.Steps?.Count ?? 0,
                    validationIssueCount: allIssues.Count,
                    measurementWarningCount: resolvedDraft.MeasurementWarnings?.Count ?? 0,
                    qualityWarningCount: qualityWarnings.Count
                );

                resolvedDraft = resolvedDraft with { ConfidenceScore = updatedScore };

                var hasUnresolvedRefs =
                    resolvedDraft.Ingredients?.Any(i =>
                        i.IsRecipeReference && !i.MatchedRecipeId.HasValue
                    ) == true;

                var canAutoPublish =
                    allIssues.Count == 0
                    && !hasUnresolvedRefs
                    && (globalAutoPublish || waitingJob.AutoPublish)
                    && waitingJob.Channel is IngestionChannel.WebUrl or IngestionChannel.RawText
                    && !string.IsNullOrWhiteSpace(resolvedDraft.Title)
                    && (resolvedDraft.Ingredients?.Count ?? 0) >= 1
                    && (resolvedDraft.Steps?.Count ?? 0) >= 1
                    && updatedScore >= (waitingJob.AutoPublish ? 0.60m : 0.85m);

                if (canAutoPublish)
                {
                    var createdRecipe = await PublishDraftAsync(
                        dbContext,
                        recipeService,
                        mediaStorageService,
                        waitingJob,
                        resolvedDraft,
                        cancellationToken
                    );

                    waitingJob.Status = IngestionJobStatus.Published;
                    waitingJob.ResultingRecipeId = createdRecipe.Id;
                    waitingJob.ExtractedDraftJson = JsonSerializer.Serialize(
                        resolvedDraft,
                        JsonOptions
                    );
                    waitingJob.ConfidenceScore = updatedScore;
                    waitingJob.DisplayTitle = resolvedDraft.Title;
                    waitingJob.ProcessedAt = DateTimeOffset.UtcNow;
                    waitingJob.StatusDetail =
                        $"Auto-published after referenced recipe became available ({waitingJob.ExtractedModel ?? "auto-backlink"}).";

                    await dbContext.SaveChangesAsync(cancellationToken);

                    await BacklinkUnresolvedRecipeReferencesAsync(
                        dbContext,
                        createdRecipe.Id,
                        createdRecipe.Title,
                        cancellationToken
                    );

                    promotedInPass++;
                    totalPromoted++;
                }
                else if (resolvedDraft != draft)
                {
                    waitingJob.ExtractedDraftJson = JsonSerializer.Serialize(
                        resolvedDraft,
                        JsonOptions
                    );
                    waitingJob.ConfidenceScore = updatedScore;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            if (promotedInPass == 0)
            {
                break;
            }
        }

        return totalPromoted;
    }

    public static async Task<IReadOnlyList<Guid>> ResolveAndMaterializeTagsAsync(
        AppDbContext dbContext,
        IReadOnlyList<Guid>? suggestedTagIds,
        IReadOnlyList<string>? proposedTagNames,
        CancellationToken cancellationToken
    )
    {
        var resolvedIds = new List<Guid>();

        if (suggestedTagIds is { Count: > 0 })
        {
            var distinctInputIds = suggestedTagIds.Distinct().ToList();

            var existingIds = await dbContext
                .Tags.AsNoTracking()
                .Where(t => distinctInputIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            foreach (var id in existingIds)
            {
                if (!resolvedIds.Contains(id))
                {
                    resolvedIds.Add(id);
                }
            }
        }

        if (proposedTagNames is not { Count: > 0 })
        {
            return resolvedIds;
        }

        var allTags = await dbContext.Tags.ToListAsync(cancellationToken);

        foreach (var rawTag in proposedTagNames)
        {
            if (string.IsNullOrWhiteSpace(rawTag))
            {
                continue;
            }

            var cleanName = RecipeDraftNormalizer.ToTagDisplayCase(rawTag.Trim().TrimStart('#'));

            if (string.IsNullOrWhiteSpace(cleanName))
            {
                continue;
            }

            var normName = cleanName.ToLowerInvariant();

            var singularNorm = RecipeDraftNormalizer.Singularize(normName);

            var existingTag = allTags.FirstOrDefault(t =>
                t.NormalizedName == normName
                || RecipeDraftNormalizer.Singularize(t.NormalizedName) == singularNorm
            );

            if (existingTag is not null)
            {
                if (!resolvedIds.Contains(existingTag.Id))
                {
                    resolvedIds.Add(existingTag.Id);
                }

                continue;
            }

            var category = RecipeDraftNormalizer.InferTagCategory(cleanName);

            var slug = GenerateUniqueTagSlug(cleanName, category, allTags);

            var newTag = new Tag
            {
                Name = cleanName,

                NormalizedName = normName,

                Slug = slug,

                Category = category,

                SortOrder = 0,

                IsSystemSeeded = false,
            };

            dbContext.Tags.Add(newTag);

            await dbContext.SaveChangesAsync(cancellationToken);

            allTags.Add(newTag);

            resolvedIds.Add(newTag.Id);
        }

        return resolvedIds;
    }

    private static string GenerateUniqueTagSlug(
        string name,
        TagCategory category,
        IReadOnlyCollection<Tag> existingTags
    )
    {
        var catPrefix = category.ToString().ToLowerInvariant();

        var lower = name.Trim().ToLowerInvariant();

        var clean = Regex.Replace(lower, @"[^a-z0-9\s-]", "");

        var rawNameSlug = Regex.Replace(clean, @"[\s-]+", "-").Trim('-');

        var baseSlug = string.IsNullOrWhiteSpace(rawNameSlug)
            ? $"{catPrefix}-tag"
            : $"{catPrefix}-{rawNameSlug}";

        if (baseSlug.Length > 90)
        {
            baseSlug = baseSlug[..90].TrimEnd('-');
        }

        var slug = baseSlug;

        var counter = 2;

        while (
            existingTags.Any(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase))
        )
        {
            slug = $"{baseSlug}-{counter}";

            counter++;
        }

        return slug;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task PromoteOrAttachSourceFilesAsync(
        AppDbContext dbContext,
        IMediaStorageService mediaStorageService,
        IngestionJob job,
        IngestionExtractedDraftDto draft,
        Guid recipeId,
        CancellationToken cancellationToken
    )
    {
        if (!draft.RetainSourceAttachments)
        {
            if (!string.IsNullOrWhiteSpace(job.SourceFilesJson))
            {
                try
                {
                    var sourceFiles = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                        job.SourceFilesJson,
                        JsonOptions
                    );

                    if (sourceFiles is { Count: > 0 })
                    {
                        foreach (var sf in sourceFiles)
                        {
                            await mediaStorageService.DeleteMediaFileAsync(
                                sf.UrlPath,
                                cancellationToken
                            );
                        }
                    }
                }
                catch
                {
                    // Non-blocking cleanup failure
                }
            }

            return;
        }

        var hasAttachments = await dbContext.RecipeAttachments.AnyAsync(
            a => a.RecipeId == recipeId,
            cancellationToken
        );

        if (hasAttachments)
        {
            return;
        }

        // 1. Promote files from job.SourceFilesJson if present

        if (!string.IsNullOrWhiteSpace(job.SourceFilesJson))
        {
            try
            {
                var sourceFiles = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                    job.SourceFilesJson,
                    JsonOptions
                );

                if (sourceFiles is { Count: > 0 })
                {
                    foreach (var sf in sourceFiles)
                    {
                        var promoted =
                            await mediaStorageService.PromoteIngestionFileToAttachmentAsync(
                                sf.UrlPath,
                                cancellationToken
                            );

                        string label;

                        if (
                            sf.ContentType == "text/plain"
                            || sf.OriginalFileName.EndsWith(
                                ".txt",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            label = "Original Source Text";
                        }
                        else if (
                            sf.ContentType == "application/pdf"
                            || sf.OriginalFileName.EndsWith(
                                ".pdf",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            label = "Original PDF";
                        }
                        else
                        {
                            label =
                                sf.PageNumber == 1
                                    ? "Source Cover / Page"
                                    : $"Source Page {sf.PageNumber}";
                        }

                        dbContext.RecipeAttachments.Add(
                            new RecipeAttachment
                            {
                                Id = Guid.NewGuid(),

                                RecipeId = recipeId,

                                SortOrder = sf.PageNumber,

                                FileName = sf.OriginalFileName,

                                MediaUrlPath = promoted.MediaUrlPath,

                                ContentType = promoted.ContentType,

                                FileSizeBytes = promoted.FileSizeBytes,

                                Label = label,
                            }
                        );
                    }

                    await dbContext.SaveChangesAsync(cancellationToken);

                    return;
                }
            }
            catch
            {
                // Non-blocking attachment promotion failure
            }
        }

        // 2. Fallback: If no SourceFilesJson, but RawText or NormalizedSourceText exists,

        // create and attach a source text attachment file so the recipe retains original text!

        var rawTextContent = !string.IsNullOrWhiteSpace(job.RawText)
            ? job.RawText
            : job.NormalizedSourceText;

        if (!string.IsNullOrWhiteSpace(rawTextContent))
        {
            try
            {
                var title = !string.IsNullOrWhiteSpace(draft.Title)
                    ? draft.Title
                    : (!string.IsNullOrWhiteSpace(job.DisplayTitle) ? job.DisplayTitle : "Recipe");

                var cleanTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()))
                    .Trim();

                if (cleanTitle.Length > 80)
                    cleanTitle = cleanTitle[..80].Trim();

                var fileName = $"{cleanTitle} - Source Text.txt";

                var bytes = System.Text.Encoding.UTF8.GetBytes(rawTextContent);

                using var ms = new MemoryStream(bytes);

                var saved = await mediaStorageService.SaveDirectRecipeAttachmentAsync(
                    ms,
                    fileName,
                    "text/plain",
                    bytes.Length,
                    cancellationToken
                );

                dbContext.RecipeAttachments.Add(
                    new RecipeAttachment
                    {
                        Id = Guid.NewGuid(),

                        RecipeId = recipeId,

                        SortOrder = 1,

                        FileName = fileName,

                        MediaUrlPath = saved.MediaUrlPath,

                        ContentType = saved.ContentType,

                        FileSizeBytes = saved.FileSizeBytes,

                        Label = "Original Source Text",
                    }
                );

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // Non-blocking fallback attachment failure
            }
        }
    }
}
