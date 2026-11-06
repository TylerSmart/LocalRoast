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
        var measurement = ingredient.GetPreferredMeasurement();
        var formattedQty = CulinaryMath.ScaleAndConvert(
            measurement.Quantity,
            measurement.MaxQuantity,
            measurement.Unit,
            measurement.CustomUnitName,
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

    public async Task<KitchenAnswerDto> AskKitchenQuestionAsync(
        AskKitchenQuestionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 600)
        {
            throw new DomainValidationException(
                "A cooking question of at most 600 characters is required."
            );
        }

        if (
            request.RecipeId == Guid.Empty
            || request.RecipeIngredientId == Guid.Empty
            || request.RecipeStepId == Guid.Empty
        )
        {
            throw new DomainValidationException("Recipe context IDs must not be empty.");
        }

        if (
            request.RecipeId is null
            && (request.RecipeIngredientId is not null || request.RecipeStepId is not null)
        )
        {
            throw new DomainValidationException(
                "A recipe ID is required when specifying an ingredient or step."
            );
        }

        Recipe? recipe = null;
        RecipeIngredient? ingredient = null;
        RecipeStep? step = null;
        if (request.RecipeId is not null)
        {
            recipe = await dbContext
                .Recipes.AsNoTracking()
                .Include(r => r.Ingredients)
                .Include(r => r.Steps)
                .FirstOrDefaultAsync(r => r.Id == request.RecipeId, cancellationToken);

            if (recipe is null)
            {
                throw new NotFoundException("Recipe not found.");
            }

            if (request.RecipeIngredientId is not null)
            {
                ingredient =
                    recipe.Ingredients.FirstOrDefault(i => i.Id == request.RecipeIngredientId)
                    ?? throw new NotFoundException("Recipe ingredient not found.");
            }

            if (request.RecipeStepId is not null)
            {
                step =
                    recipe.Steps.FirstOrDefault(s => s.Id == request.RecipeStepId)
                    ?? throw new NotFoundException("Recipe step not found.");
            }
        }

        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            throw new LlmHostUnavailableException(
                "No Text LLM model is configured in LLM Settings."
            );
        }

        var context = new LlmKitchenQuestionContext(
            Question: request.Question.Trim(),
            RecipeTitle: recipe?.Title,
            RecipeDescription: recipe?.Description,
            RecipeIngredients: recipe
                ?.Ingredients.OrderBy(i => i.SortOrder)
                .Take(30)
                .Select(i => i.DisplayName)
                .ToList()
                ?? [],
            IngredientName: ingredient?.DisplayName,
            IngredientPreparationNote: ingredient?.PreparationNote,
            StepNumber: step?.StepNumber,
            StepInstruction: step?.Instruction
        );

        var answer = await llmClient.AnswerKitchenQuestionAsync(config, context, cancellationToken);
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new DomainValidationException("The AI returned an empty answer. Please retry.");
        }

        return new KitchenAnswerDto(answer.Trim(), config.TextModel.Trim());
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

        if (
            request.MealCount is < 1 or > 14
            || request.ServingsPerMeal is < 0.25m or > 999m
            || !Enum.IsDefined(request.DefaultSlot)
            || request.ExcludedRecipeIds?.Count > 150
        )
        {
            throw new DomainValidationException(
                "Invalid meal count, servings, slot, or recipe exclusions."
            );
        }
        var requestedMealCount = request.MealCount;

        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            throw new LlmHostUnavailableException(
                "No Text LLM model is configured in LLM Settings."
            );
        }

        var utcToday = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var today = request.TodayDate ?? utcToday;
        if (Math.Abs(today.DayNumber - utcToday.DayNumber) > 1)
        {
            throw new DomainValidationException("TodayDate must match the current local date.");
        }
        var rangeStart = request.WeekStartDate is { } start && start > today ? start : today;
        var rangeEnd = rangeStart.AddDays(6);
        var excludedIds = request.ExcludedRecipeIds?.ToHashSet() ?? [];

        var publishedRecipes = await dbContext
            .Recipes.AsNoTracking()
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .Where(r => r.Status == RecipeStatus.Published && !excludedIds.Contains(r.Id))
            .OrderByDescending(r => r.AverageRating ?? 0m)
            .ThenByDescending(r => r.TimesCooked)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        if (publishedRecipes.Count == 0)
        {
            throw new DomainValidationException(
                "No eligible published recipes remain in your household cookbook. Add recipes or remove exclusions before drafting."
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
                (
                    e.IsOnDeck
                    && e.WeekStartDate >= MealPlanDateHelper.NormalizeToSunday(rangeStart)
                    && e.WeekStartDate <= rangeEnd
                ) || (!e.IsOnDeck && e.ScheduledDate >= rangeStart && e.ScheduledDate <= rangeEnd)
            )
            .ToListAsync(cancellationToken);

        var existingLines = existingWeekEntries
            .Select(e =>
                e.IsOnDeck
                    ? $"On-Deck: {(e.Recipe?.Title ?? e.CustomTitle ?? "Entry")}"
                    : $"{(e.ScheduledDate.HasValue ? e.ScheduledDate.Value.DayOfWeek.ToString()[..3] : "Day")}: [{(e.Slot.HasValue ? e.Slot.Value.ToString() : "Slot")}] {(e.Recipe?.Title ?? e.CustomTitle ?? "Entry")}"
            )
            .ToList();

        var occupiedDates = existingWeekEntries
            .Where(e => !e.IsOnDeck && e.Slot == request.DefaultSlot && e.ScheduledDate.HasValue)
            .Select(e => e.ScheduledDate!.Value)
            .ToHashSet();
        var openDates = Enumerable
            .Range(0, 7)
            .Select(offset => rangeStart.AddDays(offset))
            .Where(date => !occupiedDates.Contains(date))
            .ToList();
        if (!request.PlaceOnDeck && requestedMealCount > openDates.Count)
        {
            throw new DomainValidationException(
                $"Only {openDates.Count} unplanned {request.DefaultSlot} dates remain in this seven-day period. Reduce the cooking count."
            );
        }

        var llmContext = new LlmDraftMealPlanContext(
            WeekStartDate: rangeStart,
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
        var recipeByTitleMap = catalogList
            .Select(candidate => recipeByIdMap[candidate.RecipeId])
            .GroupBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var proposals = payload.Proposals ?? [];
        var resultCandidates = new List<DraftMealPlanCandidateDto>();
        var seenRecipeIds = new HashSet<Guid>();

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

            if (seenRecipeIds.Contains(matchedRecipe.Id) && catalogList.Count >= requestedMealCount)
            {
                // Skip duplicate recipe in draft if we have enough distinct recipes
                continue;
            }

            seenRecipeIds.Add(matchedRecipe.Id);

            var isOnDeck = request.PlaceOnDeck;
            int? dayOffset = null;
            DateOnly? scheduledDate = null;
            MealSlot? slot = null;

            var leftoverDates = new List<DateOnly>();
            if (!isOnDeck)
            {
                // Meal count is cooking occasions; each batch covers its share of open dates.
                var coverage =
                    openDates.Count / requestedMealCount
                    + (resultCandidates.Count < openDates.Count % requestedMealCount ? 1 : 0);
                scheduledDate = openDates[nextOpenDay++];
                dayOffset = scheduledDate.Value.DayNumber - rangeStart.DayNumber;
                slot = request.DefaultSlot;
                for (var i = 1; i < coverage; i++)
                {
                    leftoverDates.Add(openDates[nextOpenDay++]);
                }
            }

            var targetServings = request.ServingsPerMeal * (1 + leftoverDates.Count);
            if (targetServings > 999m)
            {
                throw new DomainValidationException(
                    "Cooking servings including leftovers cannot exceed 999. Reduce servings per meal."
                );
            }

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
                    Rationale: rationale,
                    LeftoverDates: leftoverDates
                )
            );

            if (resultCandidates.Count >= requestedMealCount)
            {
                break;
            }
        }

        if (resultCandidates.Count != requestedMealCount)
        {
            throw new DomainValidationException(
                $"The AI model returned {resultCandidates.Count} valid cookbook suggestions instead of {requestedMealCount}. Try again or reduce the cooking count."
            );
        }

        return new DraftMealPlanResponseDto(
            WeekStartDate: rangeStart,
            WeekEndDate: rangeEnd,
            ModelUsed: config.TextModel,
            ConsideredCookbookRecipeCount: catalogList.Count,
            Candidates: resultCandidates
        );
    }
}
