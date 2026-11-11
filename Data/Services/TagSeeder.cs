using System.Text.RegularExpressions;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public static class TagSeeder
{
    public static async Task EnsureBuiltInTagsSeededAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken = default
    )
    {
        var existingTags = await dbContext
            .Tags.Select(t => new
            {
                t.NormalizedName,
                t.Category,
                t.Slug,
            })
            .ToListAsync(cancellationToken);

        var existingSet = new HashSet<(string NormName, TagCategory Category)>(
            existingTags.Select(t => (t.NormalizedName, t.Category))
        );

        var existingSlugs = new HashSet<string>(
            existingTags.Select(t => t.Slug),
            StringComparer.OrdinalIgnoreCase
        );

        var addedAny = false;
        foreach (var def in BuiltInTags.All)
        {
            var norm = def.Name.Trim().ToLowerInvariant();
            if (existingSet.Contains((norm, def.Category)))
            {
                continue;
            }

            var catPrefix = def.Category.ToString().ToLowerInvariant();
            var rawSlug = Regex.Replace(def.Name.Trim().ToLowerInvariant(), @"[^a-z0-9\s-]", "");
            var nameSlug = Regex.Replace(rawSlug, @"[\s-]+", "-").Trim('-');
            var baseSlug = string.IsNullOrWhiteSpace(nameSlug)
                ? $"{catPrefix}-tag"
                : $"{catPrefix}-{nameSlug}";

            var slug = baseSlug;
            var counter = 2;
            while (existingSlugs.Contains(slug))
            {
                slug = $"{baseSlug}-{counter++}";
            }
            existingSlugs.Add(slug);

            dbContext.Tags.Add(
                new Tag
                {
                    Id = Guid.NewGuid(),
                    Name = def.Name,
                    NormalizedName = norm,
                    Slug = slug,
                    Category = def.Category,
                    SortOrder = def.SortOrder,
                    IsSystemSeeded = true,
                    CreatedBy = "System",
                    ModifiedBy = "System",
                }
            );

            existingSet.Add((norm, def.Category));
            addedAny = true;
        }

        if (addedAny)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
