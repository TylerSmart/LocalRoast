using App.Core.Exceptions;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class MealPlanService(AppDbContext dbContext, TimeProvider? timeProvider = null)
    : IMealPlanService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<MealPlanWeekDto> GetWeekAsync(
        DateOnly weekDate,
        CancellationToken cancellationToken = default
    )
    {
        var weekSunday = MealPlanDateHelper.NormalizeToSunday(weekDate);
        var weekSaturday = weekSunday.AddDays(6);

        var entries = await dbContext
            .MealPlanEntries.AsNoTracking()
            .Include(e => e.Recipe)
            .Where(e =>
                (e.IsOnDeck && e.WeekStartDate >= weekSunday && e.WeekStartDate <= weekSaturday)
                || (!e.IsOnDeck && e.ScheduledDate >= weekSunday && e.ScheduledDate <= weekSaturday)
            )
            .ToListAsync(cancellationToken);

        var days = new List<MealPlanDayDto>(7);
        for (var offset = 0; offset < 7; offset++)
        {
            var date = weekSunday.AddDays(offset);
            var dayEntries = entries
                .Where(e => !e.IsOnDeck && e.ScheduledDate == date)
                .OrderBy(e => e.Slot)
                .ThenBy(e => e.SortOrder)
                .ThenBy(e => e.CreatedAt)
                .Select(MapEntryDto)
                .ToList();

            days.Add(new MealPlanDayDto(date, offset, dayEntries));
        }

        var onDeckEntries = entries
            .Where(e => e.IsOnDeck)
            .OrderBy(e => e.SortOrder)
            .ThenBy(e => e.CreatedAt)
            .Select(MapEntryDto)
            .ToList();

        return new MealPlanWeekDto(weekSunday, weekSaturday, days, onDeckEntries);
    }

    public async Task<MealPlanRangeResponseDto> GetRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        bool includeOnDeck = true,
        CancellationToken cancellationToken = default
    )
    {
        if (endDate < startDate)
        {
            throw new DomainValidationException(
                "End date must be greater than or equal to start date."
            );
        }

        if (endDate.DayNumber - startDate.DayNumber > 92)
        {
            throw new DomainValidationException("Maximum date range is 93 days.");
        }

        var rawScheduled = await dbContext
            .MealPlanEntries.AsNoTracking()
            .Include(e => e.Recipe)
            .Where(e => !e.IsOnDeck && e.ScheduledDate >= startDate && e.ScheduledDate <= endDate)
            .OrderBy(e => e.ScheduledDate)
            .ThenBy(e => e.Slot)
            .ThenBy(e => e.SortOrder)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        var scheduledEntries = rawScheduled.Select(MapEntryDto).ToList();

        IReadOnlyList<MealPlanEntryDto> onDeckEntries = [];
        if (includeOnDeck)
        {
            var firstSunday = MealPlanDateHelper.NormalizeToSunday(startDate);
            var lastSaturday = MealPlanDateHelper.NormalizeToSunday(endDate).AddDays(6);

            var rawOnDeck = await dbContext
                .MealPlanEntries.AsNoTracking()
                .Include(e => e.Recipe)
                .Where(e =>
                    e.IsOnDeck && e.WeekStartDate >= firstSunday && e.WeekStartDate <= lastSaturday
                )
                .OrderBy(e => e.WeekStartDate)
                .ThenBy(e => e.SortOrder)
                .ThenBy(e => e.CreatedAt)
                .ToListAsync(cancellationToken);

            onDeckEntries = rawOnDeck.Select(MapEntryDto).ToList();
        }

        return new MealPlanRangeResponseDto(startDate, endDate, scheduledEntries, onDeckEntries);
    }

    public async Task<MealPlanEntryDto?> GetEntryByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var entry = await dbContext
            .MealPlanEntries.AsNoTracking()
            .Include(e => e.Recipe)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        return entry is not null ? MapEntryDto(entry) : null;
    }

    public async Task<MealPlanEntryDto> CreateEntryAsync(
        CreateMealPlanEntryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var (weekStartDate, scheduledDate, slot) = ValidateAndResolvePlacement(
            request.IsOnDeck,
            request.WeekStartDate,
            request.ScheduledDate,
            request.Slot
        );

        var (
            entryType,
            recipeId,
            targetServings,
            isLeftovers,
            sourceMealPlanEntryId,
            customTitle,
            recipe
        ) = await ValidateAndResolveEntryDetailsAsync(
            request.EntryType,
            request.RecipeId,
            request.TargetServings,
            request.SourceMealPlanEntryId,
            request.CustomTitle,
            cancellationToken
        );

        var sortOrder = await GetNextSortOrderAsync(
            request.IsOnDeck,
            weekStartDate,
            scheduledDate,
            slot,
            cancellationToken
        );

        var entry = new MealPlanEntry
        {
            WeekStartDate = weekStartDate,
            IsOnDeck = request.IsOnDeck,
            ScheduledDate = scheduledDate,
            Slot = slot,
            SortOrder = sortOrder,
            EntryType = entryType,
            RecipeId = recipeId,
            TargetServings = targetServings,
            IsLeftovers = isLeftovers,
            SourceMealPlanEntryId = sourceMealPlanEntryId,
            CustomTitle = customTitle,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            Recipe = recipe,
        };

        dbContext.MealPlanEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        return MapEntryDto(entry);
    }

    public async Task<MealPlanEntryDto?> UpdateEntryAsync(
        Guid id,
        UpdateMealPlanEntryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var entry = await dbContext
            .MealPlanEntries.Include(e => e.Recipe)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (entry is null)
        {
            return null;
        }

        var (weekStartDate, scheduledDate, slot) = ValidateAndResolvePlacement(
            request.IsOnDeck,
            request.WeekStartDate,
            request.ScheduledDate,
            request.Slot
        );

        var (
            entryType,
            recipeId,
            targetServings,
            isLeftovers,
            sourceMealPlanEntryId,
            customTitle,
            recipe
        ) = await ValidateAndResolveEntryDetailsAsync(
            request.EntryType,
            request.RecipeId,
            request.TargetServings,
            request.SourceMealPlanEntryId,
            request.CustomTitle,
            cancellationToken,
            currentEntryId: id
        );

        var bucketChanged =
            entry.IsOnDeck != request.IsOnDeck
            || entry.WeekStartDate != weekStartDate
            || entry.ScheduledDate != scheduledDate
            || entry.Slot != slot;

        if (bucketChanged)
        {
            entry.SortOrder = await GetNextSortOrderAsync(
                request.IsOnDeck,
                weekStartDate,
                scheduledDate,
                slot,
                cancellationToken
            );
        }

        entry.WeekStartDate = weekStartDate;
        entry.IsOnDeck = request.IsOnDeck;
        entry.ScheduledDate = scheduledDate;
        entry.Slot = slot;
        entry.EntryType = entryType;
        entry.RecipeId = recipeId;
        entry.TargetServings = targetServings;
        entry.IsLeftovers = isLeftovers;
        entry.SourceMealPlanEntryId = sourceMealPlanEntryId;
        entry.CustomTitle = customTitle;
        entry.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        entry.Recipe = recipe;

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapEntryDto(entry);
    }

    public async Task<MealPlanEntryDto?> MoveEntryAsync(
        Guid id,
        MoveMealPlanEntryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var entry = await dbContext
            .MealPlanEntries.Include(e => e.Recipe)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (entry is null)
        {
            return null;
        }

        var sourceIsOnDeck = entry.IsOnDeck;
        var sourceWeekStart = entry.WeekStartDate;
        var sourceScheduledDate = entry.ScheduledDate;
        var sourceSlot = entry.Slot;

        bool destIsOnDeck;
        DateOnly destWeekStart;
        DateOnly? destScheduledDate;
        MealSlot? destSlot;

        if (request.IsOnDeck)
        {
            destIsOnDeck = true;
            destWeekStart = request.WeekStartDate.HasValue
                ? MealPlanDateHelper.NormalizeToSunday(request.WeekStartDate.Value)
                : (
                    request.ScheduledDate.HasValue
                        ? MealPlanDateHelper.NormalizeToSunday(request.ScheduledDate.Value)
                        : MealPlanDateHelper.NormalizeToSunday(entry.WeekStartDate)
                );
            destScheduledDate = null;
            destSlot = null;
        }
        else
        {
            destIsOnDeck = false;
            destScheduledDate = request.ScheduledDate ?? entry.ScheduledDate ?? entry.WeekStartDate;
            destSlot = request.Slot ?? entry.Slot ?? MealSlot.Dinner;
            destWeekStart = MealPlanDateHelper.NormalizeToSunday(destScheduledDate.Value);
        }

        var bucketChanged =
            sourceIsOnDeck != destIsOnDeck
            || sourceWeekStart != destWeekStart
            || sourceScheduledDate != destScheduledDate
            || sourceSlot != destSlot;

        var destPeersQuery = dbContext.MealPlanEntries.Where(e => e.Id != id);
        if (destIsOnDeck)
        {
            destPeersQuery = destPeersQuery.Where(e =>
                e.IsOnDeck && e.WeekStartDate == destWeekStart
            );
        }
        else
        {
            destPeersQuery = destPeersQuery.Where(e =>
                !e.IsOnDeck && e.ScheduledDate == destScheduledDate && e.Slot == destSlot
            );
        }

        var destPeers = await destPeersQuery
            .OrderBy(e => e.SortOrder)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        var insertIndex = Math.Clamp(
            request.TargetSortOrder ?? destPeers.Count,
            0,
            destPeers.Count
        );

        entry.IsOnDeck = destIsOnDeck;
        entry.WeekStartDate = destWeekStart;
        entry.ScheduledDate = destScheduledDate;
        entry.Slot = destSlot;

        destPeers.Insert(insertIndex, entry);
        for (var i = 0; i < destPeers.Count; i++)
        {
            destPeers[i].SortOrder = i;
        }

        if (bucketChanged)
        {
            var sourcePeersQuery = dbContext.MealPlanEntries.Where(e => e.Id != id);
            if (sourceIsOnDeck)
            {
                sourcePeersQuery = sourcePeersQuery.Where(e =>
                    e.IsOnDeck && e.WeekStartDate == sourceWeekStart
                );
            }
            else
            {
                sourcePeersQuery = sourcePeersQuery.Where(e =>
                    !e.IsOnDeck && e.ScheduledDate == sourceScheduledDate && e.Slot == sourceSlot
                );
            }

            var sourcePeers = await sourcePeersQuery
                .OrderBy(e => e.SortOrder)
                .ThenBy(e => e.CreatedAt)
                .ToListAsync(cancellationToken);

            for (var i = 0; i < sourcePeers.Count; i++)
            {
                sourcePeers[i].SortOrder = i;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapEntryDto(entry);
    }

    public async Task<MealPlanEntryDto?> CookTonightAsync(
        Guid id,
        CookTonightRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var targetDate =
            request.TargetDate ?? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        return await MoveEntryAsync(
            id,
            new MoveMealPlanEntryRequest(
                IsOnDeck: false,
                ScheduledDate: targetDate,
                Slot: request.Slot
            ),
            cancellationToken
        );
    }

    public async Task<bool> DeleteEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entry = await dbContext.MealPlanEntries.FirstOrDefaultAsync(
            e => e.Id == id,
            cancellationToken
        );

        if (entry is null)
        {
            return false;
        }

        var dependents = await dbContext
            .MealPlanEntries.Where(e => e.SourceMealPlanEntryId == id)
            .ToListAsync(cancellationToken);

        foreach (var dep in dependents)
        {
            dep.SourceMealPlanEntryId = null;
            if (string.IsNullOrWhiteSpace(dep.CustomTitle))
            {
                dep.CustomTitle = "Leftovers";
            }
        }

        dbContext.MealPlanEntries.Remove(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> ClearWeekAsync(
        DateOnly weekDate,
        bool includeOnDeck = false,
        CancellationToken cancellationToken = default
    )
    {
        var weekSunday = MealPlanDateHelper.NormalizeToSunday(weekDate);
        var weekSaturday = weekSunday.AddDays(6);

        var query = dbContext.MealPlanEntries.Where(e =>
            (e.IsOnDeck && e.WeekStartDate >= weekSunday && e.WeekStartDate <= weekSaturday)
            || (!e.IsOnDeck && e.ScheduledDate >= weekSunday && e.ScheduledDate <= weekSaturday)
        );

        if (!includeOnDeck)
        {
            query = query.Where(e => !e.IsOnDeck);
        }

        var entriesToDelete = await query.ToListAsync(cancellationToken);
        if (entriesToDelete.Count == 0)
        {
            return 0;
        }

        var deletedIds = entriesToDelete.Select(e => e.Id).ToHashSet();
        var dependents = await dbContext
            .MealPlanEntries.Where(e =>
                e.SourceMealPlanEntryId.HasValue
                && deletedIds.Contains(e.SourceMealPlanEntryId.Value)
            )
            .ToListAsync(cancellationToken);

        foreach (var dep in dependents)
        {
            dep.SourceMealPlanEntryId = null;
            if (string.IsNullOrWhiteSpace(dep.CustomTitle))
            {
                dep.CustomTitle = "Leftovers";
            }
        }

        dbContext.MealPlanEntries.RemoveRange(entriesToDelete);
        await dbContext.SaveChangesAsync(cancellationToken);

        return entriesToDelete.Count;
    }

    public async Task<MealPlanWeekDto> ApplyDraftEntriesAsync(
        ApplyDraftMealPlanRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Entries is null || request.Entries.Count == 0)
        {
            throw new DomainValidationException(
                "At least one draft meal plan entry must be provided."
            );
        }

        if (request.Entries.Count > 28)
        {
            throw new DomainValidationException(
                "A maximum of 28 draft meal plan entries can be applied at once."
            );
        }

        var weekSunday = MealPlanDateHelper.NormalizeToSunday(request.WeekStartDate);

        foreach (var entryRequest in request.Entries)
        {
            var req = entryRequest;
            if (req.IsOnDeck && !req.WeekStartDate.HasValue)
            {
                req = req with { WeekStartDate = weekSunday };
            }
            else if (!req.IsOnDeck && req.ScheduledDate.HasValue && !req.WeekStartDate.HasValue)
            {
                req = req with
                {
                    WeekStartDate = MealPlanDateHelper.NormalizeToSunday(req.ScheduledDate.Value),
                };
            }

            await CreateEntryAsync(req, cancellationToken);
        }

        return await GetWeekAsync(weekSunday, cancellationToken);
    }

    private static (
        DateOnly WeekStartDate,
        DateOnly? ScheduledDate,
        MealSlot? Slot
    ) ValidateAndResolvePlacement(
        bool isOnDeck,
        DateOnly? weekStartDate,
        DateOnly? scheduledDate,
        MealSlot? slot
    )
    {
        if (isOnDeck)
        {
            var rawWeek = weekStartDate ?? scheduledDate;
            if (!rawWeek.HasValue)
            {
                throw new DomainValidationException(
                    "WeekStartDate is required when placing an entry in the On-Deck tray."
                );
            }

            return (MealPlanDateHelper.NormalizeToSunday(rawWeek.Value), null, null);
        }

        if (!scheduledDate.HasValue || !slot.HasValue || !Enum.IsDefined(slot.Value))
        {
            throw new DomainValidationException(
                "ScheduledDate and a valid Slot are required for scheduled entries."
            );
        }

        var sunday = MealPlanDateHelper.NormalizeToSunday(scheduledDate.Value);
        return (sunday, scheduledDate.Value, slot.Value);
    }

    private async Task<(
        MealPlanEntryType EntryType,
        Guid? RecipeId,
        decimal? TargetServings,
        bool IsLeftovers,
        Guid? SourceMealPlanEntryId,
        string? CustomTitle,
        Recipe? Recipe
    )> ValidateAndResolveEntryDetailsAsync(
        MealPlanEntryType entryType,
        Guid? requestedRecipeId,
        decimal? requestedTargetServings,
        Guid? requestedSourceEntryId,
        string? requestedCustomTitle,
        CancellationToken cancellationToken,
        Guid? currentEntryId = null
    )
    {
        switch (entryType)
        {
            case MealPlanEntryType.Recipe:
            {
                if (!requestedRecipeId.HasValue)
                {
                    throw new DomainValidationException("RecipeId is required for recipe entries.");
                }

                var recipe = await dbContext.Recipes.FirstOrDefaultAsync(
                    r => r.Id == requestedRecipeId.Value,
                    cancellationToken
                );
                if (recipe is null)
                {
                    throw new DomainValidationException("Selected recipe was not found.");
                }

                if (recipe.Status == RecipeStatus.Archived)
                {
                    throw new DomainValidationException("Cannot plan an archived recipe.");
                }

                var targetServings = requestedTargetServings ?? recipe.BaseServings;
                if (targetServings is < 0.25m or > 999m)
                {
                    throw new DomainValidationException(
                        "Target servings must be between 0.25 and 999."
                    );
                }

                var customTitle = string.IsNullOrWhiteSpace(requestedCustomTitle)
                    ? null
                    : requestedCustomTitle.Trim();

                return (
                    MealPlanEntryType.Recipe,
                    recipe.Id,
                    targetServings,
                    false,
                    null,
                    customTitle,
                    recipe
                );
            }

            case MealPlanEntryType.Leftovers:
            {
                var isLeftovers = true;
                Guid? recipeId = requestedRecipeId;
                decimal? targetServings = requestedTargetServings;
                Guid? sourceId = requestedSourceEntryId;
                string customTitle;
                Recipe? recipe = null;

                if (sourceId.HasValue)
                {
                    if (currentEntryId.HasValue && sourceId.Value == currentEntryId.Value)
                    {
                        throw new DomainValidationException(
                            "An entry cannot reference itself as source leftovers."
                        );
                    }

                    var sourceEntry = await dbContext
                        .MealPlanEntries.Include(s => s.Recipe)
                        .FirstOrDefaultAsync(s => s.Id == sourceId.Value, cancellationToken);

                    if (sourceEntry is null)
                    {
                        throw new DomainValidationException("Source meal plan entry not found.");
                    }

                    recipeId ??= sourceEntry.RecipeId;
                    targetServings ??= sourceEntry.TargetServings;
                    recipe = sourceEntry.Recipe;

                    if (string.IsNullOrWhiteSpace(requestedCustomTitle))
                    {
                        var title = ResolveTitle(sourceEntry);
                        customTitle = Truncate(
                            title.StartsWith("Leftovers", StringComparison.OrdinalIgnoreCase)
                                ? title
                                : $"Leftovers: {title}",
                            200
                        );
                    }
                    else
                    {
                        customTitle = requestedCustomTitle.Trim();
                    }
                }
                else if (recipeId.HasValue)
                {
                    recipe = await dbContext.Recipes.FirstOrDefaultAsync(
                        r => r.Id == recipeId.Value,
                        cancellationToken
                    );
                    if (recipe is null)
                    {
                        throw new DomainValidationException("Referenced recipe not found.");
                    }

                    targetServings ??= recipe.BaseServings;
                    customTitle = string.IsNullOrWhiteSpace(requestedCustomTitle)
                        ? Truncate($"Leftovers: {recipe.Title}", 200)
                        : requestedCustomTitle.Trim();
                }
                else
                {
                    customTitle = string.IsNullOrWhiteSpace(requestedCustomTitle)
                        ? "Leftovers"
                        : requestedCustomTitle.Trim();
                }

                if (targetServings.HasValue && targetServings.Value is < 0.25m or > 999m)
                {
                    throw new DomainValidationException(
                        "Target servings must be between 0.25 and 999."
                    );
                }

                return (
                    MealPlanEntryType.Leftovers,
                    recipeId,
                    targetServings,
                    isLeftovers,
                    sourceId,
                    customTitle,
                    recipe
                );
            }

            case MealPlanEntryType.FreeformNote:
            {
                if (string.IsNullOrWhiteSpace(requestedCustomTitle))
                {
                    throw new DomainValidationException("Title is required for notes.");
                }

                var title = requestedCustomTitle.Trim();
                if (title.Length > 200)
                {
                    title = title[..200];
                }

                return (MealPlanEntryType.FreeformNote, null, null, false, null, title, null);
            }

            default:
                throw new DomainValidationException($"Unsupported entry type: {entryType}");
        }
    }

    private async Task<int> GetNextSortOrderAsync(
        bool isOnDeck,
        DateOnly weekStartDate,
        DateOnly? scheduledDate,
        MealSlot? slot,
        CancellationToken cancellationToken
    )
    {
        if (isOnDeck)
        {
            var max = await dbContext
                .MealPlanEntries.Where(e => e.IsOnDeck && e.WeekStartDate == weekStartDate)
                .Select(e => (int?)e.SortOrder)
                .MaxAsync(cancellationToken);

            return (max ?? -1) + 1;
        }
        else
        {
            var max = await dbContext
                .MealPlanEntries.Where(e =>
                    !e.IsOnDeck && e.ScheduledDate == scheduledDate && e.Slot == slot
                )
                .Select(e => (int?)e.SortOrder)
                .MaxAsync(cancellationToken);

            return (max ?? -1) + 1;
        }
    }

    private static string ResolveTitle(MealPlanEntry entry)
    {
        return entry.EntryType switch
        {
            MealPlanEntryType.Recipe => !string.IsNullOrWhiteSpace(entry.CustomTitle)
                ? entry.CustomTitle
                : (entry.Recipe?.Title ?? "Scheduled Recipe"),
            MealPlanEntryType.Leftovers => !string.IsNullOrWhiteSpace(entry.CustomTitle)
                ? entry.CustomTitle
                : (entry.Recipe is not null ? $"Leftovers: {entry.Recipe.Title}" : "Leftovers"),
            MealPlanEntryType.FreeformNote => entry.CustomTitle ?? "Note",
            _ => "Meal",
        };
    }

    private static MealPlanEntryDto MapEntryDto(MealPlanEntry e)
    {
        var recipeBadge = e.Recipe is not null
            ? new MealPlanRecipeBadgeDto(
                e.Recipe.Id,
                e.Recipe.Title,
                e.Recipe.Slug,
                e.Recipe.BaseServings,
                e.Recipe.TotalTimeMinutes,
                e.Recipe.Difficulty,
                e.Recipe.ThumbnailImagePath
            )
            : null;

        return new MealPlanEntryDto(
            e.Id,
            e.WeekStartDate,
            e.IsOnDeck,
            e.ScheduledDate,
            e.Slot,
            e.SortOrder,
            e.EntryType,
            e.IsLeftovers,
            e.TargetServings,
            ResolveTitle(e),
            e.CustomTitle,
            e.Notes,
            e.SourceMealPlanEntryId,
            recipeBadge,
            e.CreatedAt,
            e.CreatedBy,
            e.ModifiedAt,
            e.ModifiedBy
        );
    }

    private static string Truncate(string str, int maxLen)
    {
        return str.Length <= maxLen ? str : str[..maxLen];
    }
}
