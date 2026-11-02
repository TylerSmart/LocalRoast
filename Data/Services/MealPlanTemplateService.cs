using App.Core.Exceptions;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class MealPlanTemplateService(
    AppDbContext dbContext,
    IMealPlanService mealPlanService
) : IMealPlanTemplateService
{
    public async Task<IReadOnlyList<MealPlanTemplateSummaryDto>> GetTemplatesAsync(
        CancellationToken cancellationToken = default
    )
    {
        return await dbContext
            .MealPlanTemplates.AsNoTracking()
            .Include(t => t.Items)
            .OrderBy(t => t.Name)
            .Select(t => new MealPlanTemplateSummaryDto(
                t.Id,
                t.Name,
                t.Description,
                t.Items.Count,
                t.Items.Count(i => !i.IsOnDeck),
                t.Items.Count(i => i.IsOnDeck),
                t.CreatedAt,
                t.ModifiedAt
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<MealPlanTemplateDetailDto?> GetTemplateByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var template = await dbContext
            .MealPlanTemplates.AsNoTracking()
            .Include(t => t.Items)
                .ThenInclude(i => i.Recipe)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return template is not null ? MapTemplateDetailDto(template) : null;
    }

    public async Task<MealPlanTemplateDetailDto> CreateTemplateAsync(
        UpsertMealPlanTemplateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var trimmedName = ValidateName(request.Name);
        var normalizedName = trimmedName.ToUpperInvariant();

        var exists = await dbContext.MealPlanTemplates.AnyAsync(
            t => t.NormalizedName == normalizedName,
            cancellationToken
        );
        if (exists)
        {
            throw new ConflictException(
                $"A meal plan template named '{trimmedName}' already exists."
            );
        }

        var template = new MealPlanTemplate
        {
            Name = trimmedName,
            NormalizedName = normalizedName,
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? null
                : request.Description.Trim(),
        };

        if (request.Items is not null && request.Items.Count > 0)
        {
            template.Items = await BuildTemplateItemsAsync(
                template.Id,
                request.Items,
                cancellationToken
            );
        }

        dbContext.MealPlanTemplates.Add(template);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (await GetTemplateByIdAsync(template.Id, cancellationToken))!;
    }

    public async Task<MealPlanTemplateDetailDto> CreateTemplateFromWeekAsync(
        CreateTemplateFromWeekRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var trimmedName = ValidateName(request.Name);
        var normalizedName = trimmedName.ToUpperInvariant();

        var exists = await dbContext.MealPlanTemplates.AnyAsync(
            t => t.NormalizedName == normalizedName,
            cancellationToken
        );
        if (exists)
        {
            throw new ConflictException(
                $"A meal plan template named '{trimmedName}' already exists."
            );
        }

        var weekMonday = MealPlanDateHelper.NormalizeToMonday(request.WeekStartDate);

        var query = dbContext
            .MealPlanEntries.AsNoTracking()
            .Include(e => e.Recipe)
            .Where(e => e.WeekStartDate == weekMonday);

        if (!request.IncludeOnDeck)
        {
            query = query.Where(e => !e.IsOnDeck);
        }

        var entries = await query.ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            throw new DomainValidationException(
                "The selected week has no meal plan entries to template."
            );
        }

        var template = new MealPlanTemplate
        {
            Name = trimmedName,
            NormalizedName = normalizedName,
            Description = string.IsNullOrWhiteSpace(request.Description)
                ? null
                : request.Description.Trim(),
        };

        var items = new List<MealPlanTemplateItem>(entries.Count);
        foreach (var entry in entries)
        {
            var dayOffset = entry.IsOnDeck
                ? (int?)null
                : entry.ScheduledDate!.Value.DayNumber - weekMonday.DayNumber;

            items.Add(
                new MealPlanTemplateItem
                {
                    TemplateId = template.Id,
                    IsOnDeck = entry.IsOnDeck,
                    DayOffset = dayOffset,
                    Slot = entry.Slot,
                    SortOrder = entry.SortOrder,
                    EntryType = entry.EntryType,
                    RecipeId = entry.RecipeId,
                    TargetServings = entry.TargetServings,
                    IsLeftovers = entry.IsLeftovers,
                    CustomTitle = entry.CustomTitle,
                    Notes = entry.Notes,
                }
            );
        }

        template.Items = items;
        dbContext.MealPlanTemplates.Add(template);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (await GetTemplateByIdAsync(template.Id, cancellationToken))!;
    }

    public async Task<MealPlanTemplateDetailDto?> UpdateTemplateAsync(
        Guid id,
        UpsertMealPlanTemplateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var template = await dbContext
            .MealPlanTemplates.Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (template is null)
        {
            return null;
        }

        var trimmedName = ValidateName(request.Name);
        var normalizedName = trimmedName.ToUpperInvariant();

        if (template.NormalizedName != normalizedName)
        {
            var exists = await dbContext.MealPlanTemplates.AnyAsync(
                t => t.NormalizedName == normalizedName && t.Id != id,
                cancellationToken
            );
            if (exists)
            {
                throw new ConflictException(
                    $"A meal plan template named '{trimmedName}' already exists."
                );
            }
        }

        template.Name = trimmedName;
        template.NormalizedName = normalizedName;
        template.Description = string.IsNullOrWhiteSpace(request.Description)
            ? null
            : request.Description.Trim();

        dbContext.MealPlanTemplateItems.RemoveRange(template.Items);

        if (request.Items is not null && request.Items.Count > 0)
        {
            template.Items = await BuildTemplateItemsAsync(
                template.Id,
                request.Items,
                cancellationToken
            );
        }
        else
        {
            template.Items = [];
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return (await GetTemplateByIdAsync(id, cancellationToken))!;
    }

    public async Task<bool> DeleteTemplateAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var template = await dbContext.MealPlanTemplates.FirstOrDefaultAsync(
            t => t.Id == id,
            cancellationToken
        );

        if (template is null)
        {
            return false;
        }

        dbContext.MealPlanTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<MealPlanWeekDto?> ApplyTemplateToWeekAsync(
        Guid templateId,
        ApplyMealPlanTemplateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var template = await dbContext
            .MealPlanTemplates.AsNoTracking()
            .Include(t => t.Items)
                .ThenInclude(i => i.Recipe)
            .FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken);

        if (template is null)
        {
            return null;
        }

        var targetMonday = MealPlanDateHelper.NormalizeToMonday(request.TargetWeekStartDate);

        if (request.Mode == ApplyTemplateMode.Replace)
        {
            await mealPlanService.ClearWeekAsync(
                targetMonday,
                includeOnDeck: true,
                cancellationToken
            );
        }

        var existingEntries = await dbContext
            .MealPlanEntries.Where(e => e.WeekStartDate == targetMonday)
            .ToListAsync(cancellationToken);

        var sortedItems = template
            .Items.OrderBy(i => i.IsOnDeck)
            .ThenBy(i => i.DayOffset)
            .ThenBy(i => i.Slot)
            .ThenBy(i => i.SortOrder)
            .ToList();

        foreach (var item in sortedItems)
        {
            var scheduledDate = item.IsOnDeck
                ? (DateOnly?)null
                : targetMonday.AddDays(item.DayOffset!.Value);

            if (request.Mode == ApplyTemplateMode.Merge)
            {
                var isDuplicate = existingEntries.Any(e =>
                    e.IsOnDeck == item.IsOnDeck
                    && e.ScheduledDate == scheduledDate
                    && e.Slot == item.Slot
                    && e.EntryType == item.EntryType
                    && e.RecipeId == item.RecipeId
                    && string.Equals(
                        e.CustomTitle ?? string.Empty,
                        item.CustomTitle ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

                if (isDuplicate)
                {
                    continue;
                }
            }

            int nextSortOrder;
            if (item.IsOnDeck)
            {
                var bucket = existingEntries.Where(e => e.IsOnDeck).ToList();
                nextSortOrder = bucket.Count > 0 ? bucket.Max(e => e.SortOrder) + 1 : 0;
            }
            else
            {
                var bucket = existingEntries
                    .Where(e =>
                        !e.IsOnDeck && e.ScheduledDate == scheduledDate && e.Slot == item.Slot
                    )
                    .ToList();
                nextSortOrder = bucket.Count > 0 ? bucket.Max(e => e.SortOrder) + 1 : 0;
            }

            var newEntry = new MealPlanEntry
            {
                WeekStartDate = targetMonday,
                IsOnDeck = item.IsOnDeck,
                ScheduledDate = scheduledDate,
                Slot = item.Slot,
                SortOrder = nextSortOrder,
                EntryType = item.EntryType,
                RecipeId = item.RecipeId,
                TargetServings = item.TargetServings,
                IsLeftovers = item.IsLeftovers,
                CustomTitle = item.CustomTitle,
                Notes = item.Notes,
            };

            existingEntries.Add(newEntry);
            dbContext.MealPlanEntries.Add(newEntry);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await mealPlanService.GetWeekAsync(targetMonday, cancellationToken);
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException("Template name is required.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length is < 2 or > 120)
        {
            throw new DomainValidationException(
                "Template name must be between 2 and 120 characters."
            );
        }

        return trimmed;
    }

    private async Task<List<MealPlanTemplateItem>> BuildTemplateItemsAsync(
        Guid templateId,
        IReadOnlyList<MealPlanTemplateItemInput> inputs,
        CancellationToken cancellationToken
    )
    {
        var items = new List<MealPlanTemplateItem>(inputs.Count);
        var sortOrders = new Dictionary<string, int>();

        foreach (var input in inputs)
        {
            if (input.IsOnDeck)
            {
                if (input.DayOffset.HasValue || input.Slot.HasValue)
                {
                    throw new DomainValidationException(
                        "On-Deck template items cannot have a DayOffset or Slot."
                    );
                }
            }
            else
            {
                if (!input.DayOffset.HasValue || input.DayOffset.Value is < 0 or > 6)
                {
                    throw new DomainValidationException(
                        "DayOffset must be between 0 (Monday) and 6 (Sunday) for scheduled template items."
                    );
                }

                if (!input.Slot.HasValue || !Enum.IsDefined(input.Slot.Value))
                {
                    throw new DomainValidationException(
                        "Slot is required for scheduled template items."
                    );
                }
            }

            Guid? recipeId = null;
            decimal? targetServings = null;
            var isLeftovers = false;
            string? customTitle = null;

            switch (input.EntryType)
            {
                case MealPlanEntryType.Recipe:
                {
                    if (!input.RecipeId.HasValue)
                    {
                        throw new DomainValidationException(
                            "RecipeId is required for recipe template items."
                        );
                    }

                    var recipe = await dbContext.Recipes.FirstOrDefaultAsync(
                        r => r.Id == input.RecipeId.Value,
                        cancellationToken
                    );
                    if (recipe is null)
                    {
                        throw new DomainValidationException("Selected recipe was not found.");
                    }

                    if (recipe.Status == RecipeStatus.Archived)
                    {
                        throw new DomainValidationException(
                            "Cannot include an archived recipe in a template."
                        );
                    }

                    recipeId = recipe.Id;
                    targetServings = input.TargetServings ?? recipe.BaseServings;
                    if (targetServings is < 0.25m or > 999m)
                    {
                        throw new DomainValidationException(
                            "Target servings must be between 0.25 and 999."
                        );
                    }

                    customTitle = string.IsNullOrWhiteSpace(input.CustomTitle)
                        ? null
                        : input.CustomTitle.Trim();
                    break;
                }

                case MealPlanEntryType.Leftovers:
                {
                    isLeftovers = true;
                    if (input.RecipeId.HasValue)
                    {
                        var recipe = await dbContext.Recipes.FirstOrDefaultAsync(
                            r => r.Id == input.RecipeId.Value,
                            cancellationToken
                        );
                        if (recipe is null)
                        {
                            throw new DomainValidationException("Referenced recipe not found.");
                        }

                        recipeId = recipe.Id;
                        targetServings = input.TargetServings ?? recipe.BaseServings;
                        customTitle = string.IsNullOrWhiteSpace(input.CustomTitle)
                            ? $"Leftovers: {recipe.Title}"
                            : input.CustomTitle.Trim();
                    }
                    else
                    {
                        targetServings = input.TargetServings;
                        customTitle = string.IsNullOrWhiteSpace(input.CustomTitle)
                            ? "Leftovers"
                            : input.CustomTitle.Trim();
                    }

                    if (targetServings.HasValue && targetServings.Value is < 0.25m or > 999m)
                    {
                        throw new DomainValidationException(
                            "Target servings must be between 0.25 and 999."
                        );
                    }
                    break;
                }

                case MealPlanEntryType.FreeformNote:
                {
                    if (string.IsNullOrWhiteSpace(input.CustomTitle))
                    {
                        throw new DomainValidationException(
                            "Title is required for notes in a template."
                        );
                    }

                    customTitle = input.CustomTitle.Trim();
                    if (customTitle.Length > 200)
                    {
                        customTitle = customTitle[..200];
                    }
                    break;
                }

                default:
                    throw new DomainValidationException(
                        $"Unsupported entry type: {input.EntryType}"
                    );
            }

            var bucketKey = input.IsOnDeck ? "ondeck" : $"{input.DayOffset}:{input.Slot}";

            var sortOrder = sortOrders.TryGetValue(bucketKey, out var current) ? current : 0;
            sortOrders[bucketKey] = sortOrder + 1;

            items.Add(
                new MealPlanTemplateItem
                {
                    TemplateId = templateId,
                    IsOnDeck = input.IsOnDeck,
                    DayOffset = input.IsOnDeck ? null : input.DayOffset,
                    Slot = input.IsOnDeck ? null : input.Slot,
                    SortOrder = sortOrder,
                    EntryType = input.EntryType,
                    RecipeId = recipeId,
                    TargetServings = targetServings,
                    IsLeftovers = isLeftovers,
                    CustomTitle = customTitle,
                    Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim(),
                }
            );
        }

        return items;
    }

    private static string ResolveItemTitle(MealPlanTemplateItem item)
    {
        return item.EntryType switch
        {
            MealPlanEntryType.Recipe => !string.IsNullOrWhiteSpace(item.CustomTitle)
                ? item.CustomTitle
                : (item.Recipe?.Title ?? "Scheduled Recipe"),
            MealPlanEntryType.Leftovers => !string.IsNullOrWhiteSpace(item.CustomTitle)
                ? item.CustomTitle
                : (item.Recipe is not null ? $"Leftovers: {item.Recipe.Title}" : "Leftovers"),
            MealPlanEntryType.FreeformNote => item.CustomTitle ?? "Note",
            _ => "Meal",
        };
    }

    private static MealPlanTemplateDetailDto MapTemplateDetailDto(MealPlanTemplate t)
    {
        var items = t
            .Items.OrderBy(i => i.IsOnDeck)
            .ThenBy(i => i.DayOffset)
            .ThenBy(i => i.Slot)
            .ThenBy(i => i.SortOrder)
            .ThenBy(i => i.CreatedAt)
            .Select(i =>
            {
                var recipeBadge = i.Recipe is not null
                    ? new MealPlanRecipeBadgeDto(
                        i.Recipe.Id,
                        i.Recipe.Title,
                        i.Recipe.Slug,
                        i.Recipe.BaseServings,
                        i.Recipe.TotalTimeMinutes,
                        i.Recipe.Difficulty,
                        i.Recipe.ThumbnailImagePath
                    )
                    : null;

                return new MealPlanTemplateItemDto(
                    i.Id,
                    i.IsOnDeck,
                    i.DayOffset,
                    i.Slot,
                    i.SortOrder,
                    i.EntryType,
                    i.IsLeftovers,
                    i.TargetServings,
                    ResolveItemTitle(i),
                    i.CustomTitle,
                    i.Notes,
                    recipeBadge
                );
            })
            .ToList();

        return new MealPlanTemplateDetailDto(
            t.Id,
            t.Name,
            t.Description,
            items,
            t.CreatedAt,
            t.CreatedBy,
            t.ModifiedAt,
            t.ModifiedBy
        );
    }
}
