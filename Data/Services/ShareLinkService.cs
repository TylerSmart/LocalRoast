using App.Core.Exceptions;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class ShareLinkService(AppDbContext dbContext, TimeProvider? timeProvider = null)
    : IShareLinkService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<ShareLinkDto>> GetShareLinksForRecipeAsync(
        Guid recipeId,
        CancellationToken cancellationToken = default
    )
    {
        var recipeExists = await dbContext
            .Recipes.AsNoTracking()
            .AnyAsync(r => r.Id == recipeId, cancellationToken);
        if (!recipeExists)
        {
            throw new NotFoundException("Recipe not found.");
        }

        var now = _timeProvider.GetUtcNow();
        var links = await dbContext
            .ShareLinks.AsNoTracking()
            .Where(s => s.RecipeId == recipeId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        return links
            .Select(s => new ShareLinkDto(
                s.Id,
                s.RecipeId,
                s.Token,
                s.Label,
                s.IncludeCookLogs,
                s.ExpiresAt,
                s.RevokedAt,
                s.RevokedAt is null && (!s.ExpiresAt.HasValue || s.ExpiresAt.Value > now),
                s.ViewCount,
                s.LastAccessedAt,
                s.CreatedAt,
                s.CreatedBy
            ))
            .ToList();
    }

    public async Task<ShareLinkDto> CreateShareLinkAsync(
        Guid recipeId,
        CreateShareLinkRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var recipe = await dbContext
            .Recipes.AsNoTracking()
            .Select(r => new { r.Id, r.Status })
            .FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);

        if (recipe is null)
        {
            throw new NotFoundException("Recipe not found.");
        }

        if (recipe.Status != RecipeStatus.Published)
        {
            throw new DomainValidationException(
                "Only published recipes can be shared externally. Publish this recipe before creating a share link."
            );
        }

        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        if (label is { Length: > 120 })
        {
            throw new DomainValidationException("Share link label cannot exceed 120 characters.");
        }

        var now = _timeProvider.GetUtcNow();
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= now)
        {
            throw new DomainValidationException(
                "Share link expiration date must be in the future."
            );
        }

        var token = string.Empty;
        for (var i = 0; i < 5; i++)
        {
            var candidate = ShareTokenGenerator.GenerateToken();
            if (!await dbContext.ShareLinks.AnyAsync(s => s.Token == candidate, cancellationToken))
            {
                token = candidate;
                break;
            }
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Failed to generate a unique share token.");
        }

        var link = new ShareLink
        {
            RecipeId = recipeId,
            Token = token,
            Label = label,
            IncludeCookLogs = request.IncludeCookLogs,
            ExpiresAt = request.ExpiresAt,
        };

        dbContext.ShareLinks.Add(link);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ShareLinkDto(
            link.Id,
            link.RecipeId,
            link.Token,
            link.Label,
            link.IncludeCookLogs,
            link.ExpiresAt,
            link.RevokedAt,
            true,
            link.ViewCount,
            link.LastAccessedAt,
            link.CreatedAt,
            link.CreatedBy
        );
    }

    public async Task<ShareLinkDto?> RevokeShareLinkAsync(
        Guid recipeId,
        Guid shareLinkId,
        CancellationToken cancellationToken = default
    )
    {
        var link = await dbContext.ShareLinks.FirstOrDefaultAsync(
            s => s.Id == shareLinkId && s.RecipeId == recipeId,
            cancellationToken
        );

        if (link is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        if (link.RevokedAt is null)
        {
            link.RevokedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new ShareLinkDto(
            link.Id,
            link.RecipeId,
            link.Token,
            link.Label,
            link.IncludeCookLogs,
            link.ExpiresAt,
            link.RevokedAt,
            false,
            link.ViewCount,
            link.LastAccessedAt,
            link.CreatedAt,
            link.CreatedBy
        );
    }

    public async Task<bool> DeleteShareLinkAsync(
        Guid recipeId,
        Guid shareLinkId,
        CancellationToken cancellationToken = default
    )
    {
        var link = await dbContext.ShareLinks.FirstOrDefaultAsync(
            s => s.Id == shareLinkId && s.RecipeId == recipeId,
            cancellationToken
        );

        if (link is null)
        {
            return false;
        }

        dbContext.ShareLinks.Remove(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PublicSharedRecipeDto?> GetPublicSharedRecipeByTokenAsync(
        string token,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
        if (!ShareTokenGenerator.IsWellFormedToken(trimmed))
        {
            return null;
        }

        var shareLink = await dbContext.ShareLinks.FirstOrDefaultAsync(
            s => s.Token == trimmed,
            cancellationToken
        );

        if (shareLink is null || !string.Equals(shareLink.Token, trimmed, StringComparison.Ordinal))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        if (
            shareLink.RevokedAt.HasValue
            || (shareLink.ExpiresAt.HasValue && shareLink.ExpiresAt.Value <= now)
        )
        {
            return null;
        }

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
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
            .FirstOrDefaultAsync(
                r => r.Id == shareLink.RecipeId && r.Status == RecipeStatus.Published,
                cancellationToken
            );

        if (recipe is null)
        {
            return null;
        }

        var linkedRecipeIds = recipe
            .Ingredients.Where(i =>
                i.LinkedRecipeId.HasValue
                && i.LinkedRecipe != null
                && i.LinkedRecipe.Status == RecipeStatus.Published
            )
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .ToList();

        var subRecipes = new List<PublicSubRecipeDto>();
        if (linkedRecipeIds.Count > 0)
        {
            var rawSubRecipes = await dbContext
                .Recipes.AsNoTracking()
                .Where(r => linkedRecipeIds.Contains(r.Id) && r.Status == RecipeStatus.Published)
                .Include(r => r.Ingredients)
                    .ThenInclude(i => i.FoodItem)
                .Include(r => r.Steps)
                    .ThenInclude(s => s.IngredientReferences)
                .Include(r => r.Steps)
                    .ThenInclude(s => s.Timers)
                .ToListAsync(cancellationToken);

            foreach (var sr in rawSubRecipes)
            {
                subRecipes.Add(
                    new PublicSubRecipeDto(
                        sr.Id,
                        sr.Title,
                        sr.BaseServings,
                        sr.YieldDisplay,
                        sr.Ingredients.OrderBy(i => i.SortOrder)
                            .Select(MapPublicIngredientDto)
                            .ToList(),
                        sr.Steps.OrderBy(s => s.StepNumber).Select(MapStepDto).ToList()
                    )
                );
            }
        }

        shareLink.ViewCount += 1;
        shareLink.LastAccessedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var orderedIngredients = recipe
            .Ingredients.OrderBy(i => i.SortOrder)
            .Select(MapPublicIngredientDto)
            .ToList();

        var orderedSteps = recipe.Steps.OrderBy(s => s.StepNumber).Select(MapStepDto).ToList();

        var tags = recipe
            .RecipeTags.OrderBy(rt => rt.Tag.Category)
            .ThenBy(rt => rt.Tag.SortOrder)
            .ThenBy(rt => rt.Tag.Name)
            .Select(rt => new PublicRecipeTagBadgeDto(rt.Tag.Name, rt.Tag.Category))
            .ToList();

        return new PublicSharedRecipeDto(
            recipe.Title,
            recipe.Description,
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
            tags,
            orderedIngredients,
            orderedSteps,
            subRecipes,
            shareLink.IncludeCookLogs,
            shareLink.ExpiresAt
        );
    }

    private static PublicRecipeIngredientDto MapPublicIngredientDto(RecipeIngredient i)
    {
        var isPublishedSubRecipe =
            i.LinkedRecipe != null && i.LinkedRecipe.Status == RecipeStatus.Published;
        return new PublicRecipeIngredientDto(
            i.Id,
            i.SortOrder,
            i.SectionName,
            i.Quantity,
            i.MaxQuantity,
            i.Unit,
            i.CustomUnitName,
            i.FoodItem?.IsPantryStaple ?? false,
            isPublishedSubRecipe ? i.LinkedRecipeId : null,
            isPublishedSubRecipe ? i.LinkedRecipe?.Title : null,
            i.DisplayName,
            i.PreparationNote,
            i.IsOptional
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
}
