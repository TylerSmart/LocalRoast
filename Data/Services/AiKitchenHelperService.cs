using App.Core.AiHelpers;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.MealPlans;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class AiKitchenHelperService(
    AppDbContext dbContext,
    ILlmSettingsService llmSettingsService,
    ILlmExtractionClient llmClient,
    TimeProvider? timeProvider = null
) : IAiKitchenHelperService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AiHelperStatusDto> GetStatusAsync(
        CancellationToken cancellationToken = default
    )
    {
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            return new AiHelperStatusDto(
                IsAvailable: false,
                Provider: config.Provider,
                TextModel: null,
                LatencyMs: 0,
                UnavailableReason: "No Text LLM model is configured in LLM Settings."
            );
        }

        var probe = await llmClient.ProbeAndListModelsAsync(
            config,
            timeoutSeconds: 4,
            cancellationToken
        );
        return new AiHelperStatusDto(
            IsAvailable: probe.IsReachable,
            Provider: config.Provider,
            TextModel: config.TextModel,
            LatencyMs: probe.LatencyMs,
            UnavailableReason: probe.IsReachable
                ? null
                : (probe.ErrorMessage ?? "LLM host is unreachable.")
        );
    }

    public async Task<IngredientSubstitutionResponseDto> SuggestIngredientSubstitutionsAsync(
        SuggestIngredientSubstitutionsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RecipeId == Guid.Empty)
        {
            throw new DomainValidationException("Recipe ID must be specified.");
        }

        if (request.RecipeIngredientId == Guid.Empty)
        {
            throw new DomainValidationException("Recipe ingredient ID must be specified.");
        }

        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            throw new LlmHostUnavailableException(
                "No Text LLM model is configured in LLM Settings."
            );
        }

        var recipe = await dbContext
            .Recipes.AsNoTracking()
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .Include(r => r.Steps)
                .ThenInclude(s => s.IngredientReferences)
            .FirstOrDefaultAsync(r => r.Id == request.RecipeId, cancellationToken);

        if (recipe is null)
        {
            throw new NotFoundException("Recipe not found.");
        }

        var ingredient = recipe.Ingredients.FirstOrDefault(i => i.Id == request.RecipeIngredientId);
        if (ingredient is null)
        {
            throw new NotFoundException("Recipe ingredient not found.");
        }

        if (ingredient.LinkedRecipeId.HasValue)
        {
            throw new DomainValidationException(
                "Ingredient substitutions are only supported for food ingredients, not linked sub-recipes."
            );
        }

        var effectiveServings =
            request.TargetServings.HasValue && request.TargetServings.Value > 0
                ? request.TargetServings.Value
                : recipe.BaseServings;

        var scaleFactor = recipe.BaseServings > 0 ? effectiveServings / recipe.BaseServings : 1m;
        var formattedQty = CulinaryMath.ScaleAndConvert(
            ingredient.Quantity,
            ingredient.MaxQuantity,
            ingredient.Unit,
            ingredient.CustomUnitName,
            scaleFactor,
            request.TargetSystem
        );

        var scaledAmountDisplay = string.IsNullOrWhiteSpace(formattedQty.FormattedAmount)
            ? string.Empty
            : (
                string.IsNullOrWhiteSpace(formattedQty.UnitLabel)
                    ? formattedQty.FormattedAmount
                    : $"{formattedQty.FormattedAmount} {formattedQty.UnitLabel}"
            );

        var otherIngredients = recipe
            .Ingredients.Where(i => i.Id != ingredient.Id)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.DisplayName)
            .ToList();

        var relevantSteps = recipe
            .Steps.OrderBy(s => s.StepNumber)
            .Where(s => s.IngredientReferences.Any(si => si.RecipeIngredientId == ingredient.Id))
            .Select(s => s.Instruction)
            .ToList();

        if (relevantSteps.Count == 0)
        {
            relevantSteps = recipe
                .Steps.OrderBy(s => s.StepNumber)
                .Select(s => s.Instruction)
                .ToList();
        }

        var context = new LlmSubstitutionContext(
            RecipeTitle: recipe.Title,
            RecipeDescription: recipe.Description,
            TargetServings: effectiveServings,
            TargetIngredientName: ingredient.DisplayName,
            ScaledAmountDisplay: scaledAmountDisplay,
            PreparationNote: ingredient.PreparationNote,
            OtherRecipeIngredients: otherIngredients,
            RelevantStepInstructions: relevantSteps,
            DietaryOrPantryNote: request.DietaryOrPantryNote?.Trim()
        );

        var payload = await llmClient.SuggestIngredientSubstitutionsAsync(
            config,
            context,
            cancellationToken
        );

        var options = (payload.Substitutions ?? [])
            .Where(s =>
                !string.IsNullOrWhiteSpace(s.Label) && !string.IsNullOrWhiteSpace(s.RatioAndAmount)
            )
            .Take(4)
            .Select(s => new IngredientSubstitutionOptionDto(
                Label: s.Label.Trim(),
                RatioAndAmount: s.RatioAndAmount.Trim(),
                CulinaryNotes: s.CulinaryNotes?.Trim() ?? string.Empty,
                FlavorOrTextureImpact: s.FlavorOrTextureImpact?.Trim() ?? string.Empty
            ))
            .ToList();

        if (options.Count == 0)
        {
            throw new DomainValidationException(
                "No valid substitutions were suggested by the model."
            );
        }

        return new IngredientSubstitutionResponseDto(
            RecipeId: recipe.Id,
            RecipeTitle: recipe.Title,
            RecipeIngredientId: ingredient.Id,
            IngredientDisplayName: ingredient.DisplayName,
            ScaledAmountDisplay: scaledAmountDisplay,
            ModelUsed: config.TextModel,
            Substitutions: options
        );
    }

    public async Task<DraftMealPlanResponseDto> DraftWeeklyMealPlanAsync(
        DraftWeeklyMealPlanRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = request.Prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new DomainValidationException("Prompt is required.");
        }

        if (prompt.Length > 600)
        {
            throw new DomainValidationException("Prompt cannot exceed 600 characters.");
        }

        var requestedMealCount = Math.Clamp(request.MealCount, 1, 14);

        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            throw new LlmHostUnavailableException(
                "No Text LLM model is configured in LLM Settings."
            );
        }

        var weekSunday = MealPlanDateHelper.NormalizeToSunday(
            request.WeekStartDate ?? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime)
        );
        var weekSaturday = weekSunday.AddDays(6);

        var publishedRecipes = await dbContext
            .Recipes.AsNoTracking()
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .Where(r => r.Status == RecipeStatus.Published)
            .OrderByDescending(r => r.AverageRating ?? 0m)
            .ThenByDescending(r => r.TimesCooked)
            .ToListAsync(cancellationToken);

        if (publishedRecipes.Count == 0)
        {
            throw new DomainValidationException(
                "Your household cookbook has no published recipes yet. Add or publish recipes before drafting a meal plan."
            );
        }

        var now = _timeProvider.GetUtcNow();

        // Build candidate list (up to 150)
        var catalogList = new List<LlmCookbookRecipeCandidate>(
            Math.Min(150, publishedRecipes.Count)
        );
        for (var i = 0; i < Math.Min(150, publishedRecipes.Count); i++)
        {
            var r = publishedRecipes[i];
            int? daysSinceLastCooked = r.LastCookedAt.HasValue
                ? Math.Max(0, (int)(now - r.LastCookedAt.Value).TotalDays)
                : null;

            var tags = r.RecipeTags.Select(rt => rt.Tag.Name).ToList();
            var keyIngredients = r
                .Ingredients.Where(ing =>
                    !ing.IsOptional && ing.FoodItem != null && !ing.FoodItem.IsPantryStaple
                )
                .Take(8)
                .Select(ing => ing.FoodItem!.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            catalogList.Add(
                new LlmCookbookRecipeCandidate(
                    CatalogIndex: i + 1,
                    RecipeId: r.Id,
                    Title: r.Title,
                    BaseServings: r.BaseServings,
                    TotalTimeMinutes: r.TotalTimeMinutes,
                    Difficulty: r.Difficulty?.ToString(),
                    Tags: tags,
                    KeyIngredients: keyIngredients,
                    AverageRating: r.AverageRating,
                    TimesCooked: r.TimesCooked,
                    DaysSinceLastCooked: daysSinceLastCooked
                )
            );
        }

        var existingWeekEntries = await dbContext
            .MealPlanEntries.AsNoTracking()
            .Include(e => e.Recipe)
            .Where(e =>
                (e.IsOnDeck && e.WeekStartDate >= weekSunday && e.WeekStartDate <= weekSaturday)
                || (!e.IsOnDeck && e.ScheduledDate >= weekSunday && e.ScheduledDate <= weekSaturday)
            )
            .ToListAsync(cancellationToken);

        var existingLines = existingWeekEntries
            .Select(e =>
                e.IsOnDeck
                    ? $"On-Deck: {(e.Recipe?.Title ?? e.CustomTitle ?? "Entry")}"
                    : $"{(e.ScheduledDate.HasValue ? e.ScheduledDate.Value.DayOfWeek.ToString()[..3] : "Day")}: [{(e.Slot.HasValue ? e.Slot.Value.ToString() : "Slot")}] {(e.Recipe?.Title ?? e.CustomTitle ?? "Entry")}"
            )
            .ToList();

        var llmContext = new LlmDraftMealPlanContext(
            WeekStartDate: weekSunday,
            UserPrompt: prompt,
            RequestedMealCount: requestedMealCount,
            DefaultSlotName: request.DefaultSlot.ToString(),
            PlaceOnDeck: request.PlaceOnDeck,
            ExistingWeekScheduleLines: existingLines,
            CookbookCatalog: catalogList
        );

        var payload = await llmClient.DraftWeeklyMealPlanAsync(
            config,
            llmContext,
            cancellationToken
        );

        var candidateMapByIndex = catalogList.ToDictionary(c => c.CatalogIndex);
        var recipeByIdMap = publishedRecipes.ToDictionary(r => r.Id);
        var recipeByTitleMap = publishedRecipes.ToDictionary(
            r => r.Title,
            StringComparer.OrdinalIgnoreCase
        );

        var proposals = payload.Proposals ?? [];
        var resultCandidates = new List<DraftMealPlanCandidateDto>();
        var seenRecipeIds = new HashSet<Guid>();

        // Track open days for default assignment
        var usedDays = existingWeekEntries
            .Where(e => !e.IsOnDeck && e.ScheduledDate.HasValue && e.Slot == request.DefaultSlot)
            .Select(e => e.ScheduledDate!.Value.DayNumber - weekSunday.DayNumber)
            .Where(d => d is >= 0 and <= 6)
            .ToHashSet();

        var nextOpenDay = 0;

        foreach (var p in proposals)
        {
            Recipe? matchedRecipe = null;

            if (candidateMapByIndex.TryGetValue(p.CatalogIndex, out var cand))
            {
                matchedRecipe = recipeByIdMap.GetValueOrDefault(cand.RecipeId);
            }
            else if (
                !string.IsNullOrWhiteSpace(p.RecipeTitle)
                && recipeByTitleMap.TryGetValue(p.RecipeTitle.Trim(), out var byTitle)
            )
            {
                matchedRecipe = byTitle;
            }

            if (matchedRecipe is null)
            {
                // Discard hallucinated / non-existent recipes
                continue;
            }

            if (
                seenRecipeIds.Contains(matchedRecipe.Id)
                && publishedRecipes.Count >= requestedMealCount
            )
            {
                // Skip duplicate recipe in draft if we have enough distinct recipes
                continue;
            }

            seenRecipeIds.Add(matchedRecipe.Id);

            var isOnDeck = request.PlaceOnDeck || (p.IsOnDeck ?? false);
            int? dayOffset = null;
            DateOnly? scheduledDate = null;
            MealSlot? slot = null;

            if (!isOnDeck)
            {
                if (p.DayOffset.HasValue && p.DayOffset.Value is >= 0 and <= 6)
                {
                    dayOffset = p.DayOffset.Value;
                }
                else
                {
                    while (nextOpenDay < 7 && usedDays.Contains(nextOpenDay))
                    {
                        nextOpenDay++;
                    }
                    dayOffset = nextOpenDay < 7 ? nextOpenDay : (nextOpenDay % 7);
                    nextOpenDay++;
                }

                usedDays.Add(dayOffset.Value);
                scheduledDate = weekSunday.AddDays(dayOffset.Value);

                if (
                    !string.IsNullOrWhiteSpace(p.Slot)
                    && Enum.TryParse<MealSlot>(p.Slot.Trim(), true, out var parsedSlot)
                )
                {
                    slot = parsedSlot;
                }
                else
                {
                    slot = request.DefaultSlot;
                }
            }

            var targetServings =
                p.TargetServings.HasValue && p.TargetServings.Value > 0
                    ? p.TargetServings.Value
                    : matchedRecipe.BaseServings;

            var rationale = !string.IsNullOrWhiteSpace(p.Rationale)
                ? p.Rationale.Trim()
                : $"Selected based on your request: {prompt}";

            var badge = new MealPlanRecipeBadgeDto(
                Id: matchedRecipe.Id,
                Title: matchedRecipe.Title,
                Slug: matchedRecipe.Slug,
                BaseServings: matchedRecipe.BaseServings,
                TotalTimeMinutes: matchedRecipe.TotalTimeMinutes,
                Difficulty: matchedRecipe.Difficulty,
                ThumbnailImagePath: matchedRecipe.ThumbnailImagePath
            );

            resultCandidates.Add(
                new DraftMealPlanCandidateDto(
                    RecipeId: matchedRecipe.Id,
                    Recipe: badge,
                    IsOnDeck: isOnDeck,
                    DayOffset: dayOffset,
                    ScheduledDate: scheduledDate,
                    Slot: slot,
                    TargetServings: targetServings,
                    Rationale: rationale
                )
            );

            if (resultCandidates.Count >= requestedMealCount)
            {
                break;
            }
        }

        if (resultCandidates.Count == 0)
        {
            throw new DomainValidationException(
                "The AI model did not return any valid recipes from your household cookbook. Try refining your prompt."
            );
        }

        return new DraftMealPlanResponseDto(
            WeekStartDate: weekSunday,
            WeekEndDate: weekSaturday,
            ModelUsed: config.TextModel,
            ConsideredCookbookRecipeCount: catalogList.Count,
            Candidates: resultCandidates
        );
    }
}
