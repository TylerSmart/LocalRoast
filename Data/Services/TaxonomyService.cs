using System.Text.RegularExpressions;
using App.Core.Exceptions;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class TaxonomyService(AppDbContext dbContext) : ITaxonomyService
{
    public async Task<IReadOnlyList<CollectionDto>> GetCollectionsAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await dbContext
            .Collections.AsNoTracking()
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .Select(c => new CollectionDto(
                c.Id,
                c.Name,
                c.Slug,
                c.Description,
                c.SortOrder,
                c.IsSystemSeeded,
                c.RecipeCollections.Count(rc => rc.Recipe.Status != RecipeStatus.Archived)
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<CollectionDto> CreateCollectionAsync(
        UpsertCollectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateCollectionRequest(request);

        var norm = request.Name.Trim().ToLowerInvariant();
        var exists = await dbContext.Collections.AnyAsync(
            c => c.NormalizedName == norm,
            cancellationToken
        );
        if (exists)
        {
            throw new ConflictException(
                $"A collection with the name '{request.Name.Trim()}' already exists."
            );
        }

        var slug = await GenerateUniqueCollectionSlugAsync(
            request.Name.Trim(),
            null,
            cancellationToken
        );

        var collection = new Collection
        {
            Name = request.Name.Trim(),
            NormalizedName = norm,
            Slug = slug,
            Description = request.Description?.Trim(),
            SortOrder = request.SortOrder,
            IsSystemSeeded = false,
        };

        dbContext.Collections.Add(collection);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CollectionDto(
            collection.Id,
            collection.Name,
            collection.Slug,
            collection.Description,
            collection.SortOrder,
            collection.IsSystemSeeded,
            0
        );
    }

    public async Task<CollectionDto?> UpdateCollectionAsync(
        Guid id,
        UpsertCollectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateCollectionRequest(request);

        var collection = await dbContext
            .Collections.Include(c => c.RecipeCollections)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (collection is null)
        {
            return null;
        }

        var norm = request.Name.Trim().ToLowerInvariant();
        var exists = await dbContext.Collections.AnyAsync(
            c => c.Id != id && c.NormalizedName == norm,
            cancellationToken
        );
        if (exists)
        {
            throw new ConflictException(
                $"A collection with the name '{request.Name.Trim()}' already exists."
            );
        }

        var slug = collection.Slug;
        if (
            !string.Equals(collection.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase)
        )
        {
            slug = await GenerateUniqueCollectionSlugAsync(
                request.Name.Trim(),
                id,
                cancellationToken
            );
        }

        collection.Name = request.Name.Trim();
        collection.NormalizedName = norm;
        collection.Slug = slug;
        collection.Description = request.Description?.Trim();
        collection.SortOrder = request.SortOrder;

        await dbContext.SaveChangesAsync(cancellationToken);

        var recipeCount = await dbContext.RecipeCollections.CountAsync(
            rc => rc.CollectionId == id && rc.Recipe.Status != RecipeStatus.Archived,
            cancellationToken
        );

        return new CollectionDto(
            collection.Id,
            collection.Name,
            collection.Slug,
            collection.Description,
            collection.SortOrder,
            collection.IsSystemSeeded,
            recipeCount
        );
    }

    public async Task<bool> DeleteCollectionAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var collection = await dbContext.Collections.FirstOrDefaultAsync(
            c => c.Id == id,
            cancellationToken
        );
        if (collection is null)
        {
            return false;
        }

        dbContext.Collections.Remove(collection);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<TagDto>> GetTagsAsync(
        TagCategory? category = null,
        CancellationToken cancellationToken = default
    )
    {
        var q = dbContext.Tags.AsNoTracking().AsQueryable();

        if (category.HasValue)
        {
            q = q.Where(t => t.Category == category.Value);
        }

        return await q.OrderBy(t => t.Category)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .Select(t => new TagDto(
                t.Id,
                t.Name,
                t.Slug,
                t.Category,
                t.SortOrder,
                t.IsSystemSeeded,
                t.RecipeTags.Count(rt => rt.Recipe.Status != RecipeStatus.Archived)
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<TagDto> CreateTagAsync(
        UpsertTagRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateTagRequest(request);

        var norm = request.Name.Trim().ToLowerInvariant();
        var exists = await dbContext.Tags.AnyAsync(
            t => t.Category == request.Category && t.NormalizedName == norm,
            cancellationToken
        );
        if (exists)
        {
            throw new ConflictException(
                $"A tag with the name '{request.Name.Trim()}' already exists in category '{request.Category}'."
            );
        }

        var slug = await GenerateUniqueTagSlugAsync(
            request.Name.Trim(),
            request.Category,
            null,
            cancellationToken
        );

        var tag = new Tag
        {
            Name = request.Name.Trim(),
            NormalizedName = norm,
            Slug = slug,
            Category = request.Category,
            SortOrder = request.SortOrder,
            IsSystemSeeded = false,
        };

        dbContext.Tags.Add(tag);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new TagDto(
            tag.Id,
            tag.Name,
            tag.Slug,
            tag.Category,
            tag.SortOrder,
            tag.IsSystemSeeded,
            0
        );
    }

    public async Task<TagDto?> UpdateTagAsync(
        Guid id,
        UpsertTagRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ValidateTagRequest(request);

        var tag = await dbContext.Tags.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tag is null)
        {
            return null;
        }

        var norm = request.Name.Trim().ToLowerInvariant();
        var exists = await dbContext.Tags.AnyAsync(
            t => t.Id != id && t.Category == request.Category && t.NormalizedName == norm,
            cancellationToken
        );
        if (exists)
        {
            throw new ConflictException(
                $"A tag with the name '{request.Name.Trim()}' already exists in category '{request.Category}'."
            );
        }

        var slug = tag.Slug;
        if (
            !string.Equals(tag.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase)
            || tag.Category != request.Category
        )
        {
            slug = await GenerateUniqueTagSlugAsync(
                request.Name.Trim(),
                request.Category,
                id,
                cancellationToken
            );
        }

        tag.Name = request.Name.Trim();
        tag.NormalizedName = norm;
        tag.Slug = slug;
        tag.Category = request.Category;
        tag.SortOrder = request.SortOrder;

        await dbContext.SaveChangesAsync(cancellationToken);

        var recipeCount = await dbContext.RecipeTags.CountAsync(
            rt => rt.TagId == id && rt.Recipe.Status != RecipeStatus.Archived,
            cancellationToken
        );

        return new TagDto(
            tag.Id,
            tag.Name,
            tag.Slug,
            tag.Category,
            tag.SortOrder,
            tag.IsSystemSeeded,
            recipeCount
        );
    }

    public async Task<bool> DeleteTagAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tag = await dbContext.Tags.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tag is null)
        {
            return false;
        }

        dbContext.Tags.Remove(tag);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void ValidateCollectionRequest(UpsertCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new DomainValidationException("Collection name is required.");
        }
    }

    private static void ValidateTagRequest(UpsertTagRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new DomainValidationException("Tag name is required.");
        }
    }

    private async Task<string> GenerateUniqueCollectionSlugAsync(
        string name,
        Guid? currentId,
        CancellationToken cancellationToken
    )
    {
        var baseSlug = ToKebabCase(name);
        if (string.IsNullOrWhiteSpace(baseSlug))
        {
            baseSlug = "collection";
        }

        var slug = baseSlug;
        var counter = 2;

        while (
            await dbContext.Collections.AnyAsync(
                c => c.Slug == slug && (!currentId.HasValue || c.Id != currentId.Value),
                cancellationToken
            )
        )
        {
            slug = $"{baseSlug}-{counter}";
            counter++;
        }

        return slug;
    }

    private async Task<string> GenerateUniqueTagSlugAsync(
        string name,
        TagCategory category,
        Guid? currentId,
        CancellationToken cancellationToken
    )
    {
        var catPrefix = category.ToString().ToLowerInvariant();
        var rawNameSlug = ToKebabCase(name);
        var baseSlug = string.IsNullOrWhiteSpace(rawNameSlug)
            ? $"{catPrefix}-tag"
            : $"{catPrefix}-{rawNameSlug}";

        var slug = baseSlug;
        var counter = 2;

        while (
            await dbContext.Tags.AnyAsync(
                t => t.Slug == slug && (!currentId.HasValue || t.Id != currentId.Value),
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
