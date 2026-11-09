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

    public static async Task<Guid?> ResolveRecipeReferenceAsync(
        AppDbContext dbContext,
        string title,
        Guid? excludedRecipeId,
        CancellationToken cancellationToken
    )
    {
        var cleanTitle = RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(title) ?? title.Trim();
        var normalizedTitle = cleanTitle.ToUpperInvariant();
        return await dbContext
            .Recipes.AsNoTracking()
            .Where(r =>
                r.Title.ToUpper() == normalizedTitle
                && (!excludedRecipeId.HasValue || r.Id != excludedRecipeId.Value)
            )
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

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
            if (string.Equals(candidateTitle, cleanTitle, StringComparison.OrdinalIgnoreCase))
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
            if (
                recipeMap.TryGetValue(candidateTitle, out var matchedId)
                || recipeMap.TryGetValue(ri.UnresolvedRecipeTitle!.Trim(), out matchedId)
            )
            {
                if (matchedId != ri.RecipeId)
                {
                    ri.LinkedRecipeId = matchedId;
                    ri.UnresolvedRecipeTitle = null;
                    updatedCount++;
                }
            }
        }

        if (updatedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return updatedCount;
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
