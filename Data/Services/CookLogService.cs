using App.Core.Exceptions;
using App.Core.Interfaces;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class CookLogService(
    AppDbContext dbContext,
    IMediaStorageService mediaStorageService,
    IUserContext? userContext = null,
    TimeProvider? timeProvider = null
) : ICookLogService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<CookLogDto>> GetCookLogsForRecipeAsync(
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

        var logs = await dbContext
            .CookLogs.AsNoTracking()
            .Where(c => c.RecipeId == recipeId)
            .OrderByDescending(c => c.CookedAt)
            .ThenByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        return logs.Select(MapDto).ToList();
    }

    public async Task<CookLogMutationResultDto> CreateCookLogAsync(
        Guid recipeId,
        UpsertCookLogRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var recipe = await dbContext
            .Recipes.Include(r => r.CookLogs)
            .FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);

        if (recipe is null)
        {
            throw new NotFoundException("Recipe not found.");
        }

        var (userId, displayName) = await ResolveActorAsync(cancellationToken);
        var cookedAt = request.CookedAt ?? _timeProvider.GetUtcNow();

        var log = new CookLog
        {
            RecipeId = recipeId,
            CookedByUserId = userId,
            CookedByDisplayName = displayName ?? "Household Member",
            CookedAt = cookedAt,
            ServingsMade = request.ServingsMade,
            Rating = request.Rating,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            PhotoImagePath = NormalizeMediaPath(request.PhotoImagePath),
            PhotoThumbnailPath = NormalizeMediaPath(request.PhotoThumbnailPath),
        };

        dbContext.CookLogs.Add(log);
        if (!recipe.CookLogs.Contains(log))
        {
            recipe.CookLogs.Add(log);
        }

        ApplyRecomputedStats(recipe, recipe.CookLogs.ToList(), out var latestNote);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new CookLogMutationResultDto(
            MapDto(log),
            new RecipeCookStatsSummaryDto(
                recipe.Id,
                recipe.TimesCooked,
                recipe.LastCookedAt,
                recipe.AverageRating,
                recipe.RatingCount,
                latestNote
            )
        );
    }

    public async Task<CookLogMutationResultDto?> UpdateCookLogAsync(
        Guid recipeId,
        Guid cookLogId,
        UpsertCookLogRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var recipe = await dbContext
            .Recipes.Include(r => r.CookLogs)
            .FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);

        if (recipe is null)
        {
            return null;
        }

        var log = recipe.CookLogs.FirstOrDefault(c => c.Id == cookLogId);
        if (log is null)
        {
            return null;
        }

        var oldPhoto = log.PhotoImagePath;
        var oldThumb = log.PhotoThumbnailPath;

        if (request.CookedAt.HasValue)
        {
            log.CookedAt = request.CookedAt.Value;
        }
        log.ServingsMade = request.ServingsMade;
        log.Rating = request.Rating;
        log.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        log.PhotoImagePath = NormalizeMediaPath(request.PhotoImagePath);
        log.PhotoThumbnailPath = NormalizeMediaPath(request.PhotoThumbnailPath);

        ApplyRecomputedStats(recipe, recipe.CookLogs.ToList(), out var latestNote);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(oldPhoto) && oldPhoto != log.PhotoImagePath)
        {
            await mediaStorageService.DeleteMediaFileAsync(oldPhoto, cancellationToken);
        }
        if (!string.IsNullOrEmpty(oldThumb) && oldThumb != log.PhotoThumbnailPath)
        {
            await mediaStorageService.DeleteMediaFileAsync(oldThumb, cancellationToken);
        }

        return new CookLogMutationResultDto(
            MapDto(log),
            new RecipeCookStatsSummaryDto(
                recipe.Id,
                recipe.TimesCooked,
                recipe.LastCookedAt,
                recipe.AverageRating,
                recipe.RatingCount,
                latestNote
            )
        );
    }

    public async Task<bool> DeleteCookLogAsync(
        Guid recipeId,
        Guid cookLogId,
        CancellationToken cancellationToken = default
    )
    {
        var recipe = await dbContext
            .Recipes.Include(r => r.CookLogs)
            .FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken);

        if (recipe is null)
        {
            return false;
        }

        var log = recipe.CookLogs.FirstOrDefault(c => c.Id == cookLogId);
        if (log is null)
        {
            return false;
        }

        var photoToDelete = log.PhotoImagePath;
        var thumbToDelete = log.PhotoThumbnailPath;

        recipe.CookLogs.Remove(log);
        dbContext.CookLogs.Remove(log);

        ApplyRecomputedStats(recipe, recipe.CookLogs.ToList(), out _);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrEmpty(photoToDelete))
        {
            await mediaStorageService.DeleteMediaFileAsync(photoToDelete, cancellationToken);
        }
        if (!string.IsNullOrEmpty(thumbToDelete))
        {
            await mediaStorageService.DeleteMediaFileAsync(thumbToDelete, cancellationToken);
        }

        return true;
    }

    private void ValidateRequest(UpsertCookLogRequest request)
    {
        if (request.Rating.HasValue && (request.Rating.Value < 1 || request.Rating.Value > 5))
        {
            throw new DomainValidationException("Rating must be between 1 and 5 stars.");
        }

        if (
            request.ServingsMade.HasValue
            && (request.ServingsMade.Value < 0.25m || request.ServingsMade.Value > 999m)
        )
        {
            throw new DomainValidationException("Servings made must be between 0.25 and 999.");
        }

        if (request.Notes is { Length: > 2000 })
        {
            throw new DomainValidationException("Notes cannot exceed 2000 characters.");
        }

        ValidatePhotoPath(request.PhotoImagePath);
        ValidatePhotoPath(request.PhotoThumbnailPath);

        if (request.CookedAt.HasValue)
        {
            var maxFuture = _timeProvider.GetUtcNow().AddHours(24);
            if (request.CookedAt.Value > maxFuture)
            {
                throw new DomainValidationException("Cook date cannot be in the future.");
            }
        }
    }

    private static void ValidatePhotoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var trimmed = path.Trim();
        if (
            !trimmed.StartsWith("/api/uploads/cook-logs/", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("..")
            || trimmed.Contains('\\')
        )
        {
            throw new DomainValidationException(
                "Invalid cook log photo path. Uploaded photo paths must start with '/api/uploads/cook-logs/'."
            );
        }
    }

    private static string? NormalizeMediaPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private async Task<(Guid? UserId, string? DisplayName)> ResolveActorAsync(
        CancellationToken cancellationToken
    )
    {
        if (userContext is null || string.IsNullOrWhiteSpace(userContext.UserId))
        {
            return (null, null);
        }

        if (Guid.TryParse(userContext.UserId, out var userGuid))
        {
            var user = await dbContext
                .Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userGuid, cancellationToken);
            return (userGuid, user?.Username);
        }

        return (null, null);
    }

    private static void ApplyRecomputedStats(
        Recipe recipe,
        List<CookLog> allLogs,
        out string? latestNote
    )
    {
        recipe.TimesCooked = allLogs.Count;
        recipe.LastCookedAt =
            allLogs.Count > 0 ? allLogs.Max(l => (DateTimeOffset?)l.CookedAt) : null;

        var rated = allLogs.Where(l => l.Rating.HasValue).Select(l => l.Rating!.Value).ToList();
        recipe.RatingCount = rated.Count;
        recipe.AverageRating =
            rated.Count > 0
                ? Math.Round((decimal)rated.Average(), 2, MidpointRounding.AwayFromZero)
                : null;

        latestNote = allLogs
            .Where(l => !string.IsNullOrWhiteSpace(l.Notes))
            .OrderByDescending(l => l.CookedAt)
            .ThenByDescending(l => l.CreatedAt)
            .Select(l => l.Notes)
            .FirstOrDefault();
    }

    private static CookLogDto MapDto(CookLog l) =>
        new(
            l.Id,
            l.RecipeId,
            l.CookedByUserId,
            l.CookedByDisplayName,
            l.CookedAt,
            l.ServingsMade,
            l.Rating,
            l.Notes,
            l.PhotoImagePath,
            l.PhotoThumbnailPath,
            l.CreatedAt,
            l.ModifiedAt
        );
}
