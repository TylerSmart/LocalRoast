using System.Text.RegularExpressions;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

internal static class IngestionCatalogMaterializer
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
        };
        dbContext.FoodItems.Add(newFoodItem);
        await dbContext.SaveChangesAsync(cancellationToken);
        return newFoodItem.Id;
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
}
