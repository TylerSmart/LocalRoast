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
    TimeProvider? timeProvider = null,
    IIngestionQueueSignal? queueSignal = null,
    IRecipeEmbeddingService? recipeEmbeddingService = null,
    IIngestionLiveTracker? liveTracker = null
) : IAiKitchenHelperService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IRecipeEmbeddingService _recipeEmbeddingService =
        recipeEmbeddingService ?? new RecipeEmbeddingService(dbContext, llmClient, queueSignal);

    public async Task<AiHelperStatusDto> GetStatusAsync(
        CancellationToken cancellationToken = default
    )
    {
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        var refineModel = config.HasRefinementModelConfigured
            ? (config.ForRefinement().TextModel ?? config.RefineModel)?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            return new AiHelperStatusDto(
                IsAvailable: false,
                Provider: config.Provider,
                TextModel: null,
                LatencyMs: 0,
                UnavailableReason: "No Text LLM model is configured in LLM Settings.",
                RefineModel: refineModel
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
                : (probe.ErrorMessage ?? "LLM host is unreachable."),
            RefineModel: refineModel
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
            .Where(s => StepMentionsIngredient(s.Instruction, ingredient))
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

        queueSignal?.PreemptBackgroundWork();

        using var workload = liveTracker?.BeginWorkload(
            kind: LlmWorkloadKind.KitchenHelper,
            title: $"Substitutions: {ingredient.DisplayName}",
            segments:
            [
                new LlmWorkflowSegmentDto(
                    "kitchen_helper",
                    "Suggest Substitutions",
                    LlmWorkflowSegmentState.Active,
                    SubStatus: recipe.Title
                ),
            ],
            recipeId: recipe.Id,
            subtitle: recipe.Title,
            thumbnailUrl: recipe.ThumbnailImagePath,
            activeModel: config.TextModel,
            statusDetail: $"Finding substitutions for {ingredient.DisplayName}..."
        );

        LlmSubstitutionPayload payload;
        try
        {
            payload = await llmClient.SuggestIngredientSubstitutionsAsync(
                config,
                context,
                cancellationToken
            );
            workload?.CompleteWorkload("Substitutions generated.");
        }
        catch (OperationCanceledException)
        {
            workload?.CancelWorkload("Substitution request canceled.");
            throw;
        }
        catch (Exception ex)
        {
            workload?.FailWorkload(ex.Message);
            throw;
        }

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

        queueSignal?.PreemptBackgroundWork();

        var questionPreview =
            context.Question.Length > 72 ? context.Question[..72] + "…" : context.Question;
        using var workload = liveTracker?.BeginWorkload(
            kind: LlmWorkloadKind.KitchenHelper,
            title: recipe is not null ? $"Kitchen Q&A: {recipe.Title}" : "Kitchen AI Question",
            segments:
            [
                new LlmWorkflowSegmentDto(
                    "kitchen_helper",
                    "Answer Question",
                    LlmWorkflowSegmentState.Active
                ),
            ],
            recipeId: recipe?.Id,
            subtitle: questionPreview,
            thumbnailUrl: recipe?.ThumbnailImagePath,
            activeModel: config.TextModel,
            statusDetail: "Answering cooking question..."
        );

        string answer;
        try
        {
            answer = await llmClient.AnswerKitchenQuestionAsync(config, context, cancellationToken);
            workload?.CompleteWorkload("Answer ready.");
        }
        catch (OperationCanceledException)
        {
            workload?.CancelWorkload("Question canceled.");
            throw;
        }
        catch (Exception ex)
        {
            workload?.FailWorkload(ex.Message);
            throw;
        }
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
        var isSurpriseDraft = string.IsNullOrWhiteSpace(prompt);

        if (prompt.Length > 600)
        {
            throw new DomainValidationException("Prompt cannot exceed 600 characters.");
        }

        if (
            !Enum.IsDefined(request.DefaultSlot)
            || request.ServingsPerMeal is < 0.25m or > 999m
            || request.ExcludedRecipeIds?.Count > 150
        )
        {
            throw new DomainValidationException(
                "Invalid meal count, servings, slot, or recipe exclusions."
            );
        }

        var hasStructuredSlots = request.RequestedSlots is { Count: > 0 };
        if (hasStructuredSlots)
        {
            if (request.RequestedSlots!.Count is < 1 or > 35)
            {
                throw new DomainValidationException(
                    "Requested cooking slots must be between 1 and 35."
                );
            }
        }
        else if (request.MealCount is < 1 or > 14)
        {
            throw new DomainValidationException(
                "Invalid meal count, servings, slot, or recipe exclusions."
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

        if (hasStructuredSlots)
        {
            ValidateStructuredSlotRequests(request.RequestedSlots!, today, rangeEnd);
        }

        var requestedMealCount = hasStructuredSlots
            ? request.RequestedSlots!.Count
            : request.MealCount;
        var slotCounts = BuildRequestedSlotCounts(request, requestedMealCount);

        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            throw new LlmHostUnavailableException(
                "No Text LLM model is configured in LLM Settings."
            );
        }

        var excludedIds = request.ExcludedRecipeIds?.ToHashSet() ?? [];

        var hasEligiblePublished = await dbContext
            .Recipes.AsNoTracking()
            .AnyAsync(
                r => r.Status == RecipeStatus.Published && !excludedIds.Contains(r.Id),
                cancellationToken
            );

        if (!hasEligiblePublished)
        {
            throw new DomainValidationException(
                "No eligible published recipes remain in your household cookbook. Add recipes or remove exclusions before drafting."
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

        List<DateOnly> openDates = [];
        if (!hasStructuredSlots)
        {
            var occupiedDates = existingWeekEntries
                .Where(e =>
                    !e.IsOnDeck && e.Slot == request.DefaultSlot && e.ScheduledDate.HasValue
                )
                .Select(e => e.ScheduledDate!.Value)
                .ToHashSet();
            openDates = Enumerable
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
        }

        var now = _timeProvider.GetUtcNow();
        var recentlyScheduledRecipeIds = existingWeekEntries
            .Where(e => e.RecipeId.HasValue)
            .Select(e => e.RecipeId!.Value)
            .Distinct()
            .ToList();

        queueSignal?.PreemptBackgroundWork();

        var workloadSlotSummary =
            slotCounts.Count == 1 ? slotCounts.Keys.First().ToString() : $"{slotCounts.Count}-Slot";

        using var workload = liveTracker?.BeginWorkload(
            kind: LlmWorkloadKind.MealPlanDraft,
            title: isSurpriseDraft
                ? $"Draft {requestedMealCount} Surprise {workloadSlotSummary} Meals"
                : $"Draft {requestedMealCount} {workloadSlotSummary} Meals",
            segments:
            [
                new LlmWorkflowSegmentDto(
                    "analyze_prompt",
                    "Analyze Cravings",
                    isSurpriseDraft
                        ? LlmWorkflowSegmentState.Skipped
                        : LlmWorkflowSegmentState.Pending
                ),
                new LlmWorkflowSegmentDto(
                    "retrieve",
                    "Search Cookbook",
                    LlmWorkflowSegmentState.Pending
                ),
                new LlmWorkflowSegmentDto(
                    "draft_schedule",
                    "Assemble Plan",
                    LlmWorkflowSegmentState.Pending
                ),
            ],
            subtitle: isSurpriseDraft
                ? $"Week of {rangeStart:MMM d}"
                : (prompt.Length > 80 ? prompt[..80] + "…" : prompt),
            activeModel: config.TextModel,
            statusDetail: "Drafting weekly meal plan..."
        );

        try
        {
            List<string> excludedTerms = [];
            List<string> prioritizeIngredients = [];
            int? globalMaxTime = null;
            List<MealPlanSearchBucketInput> searchBuckets = [];

            if (!isSurpriseDraft)
            {
                workload?.ActivateSegment(
                    "analyze_prompt",
                    "Decomposing prompt",
                    "Analyzing meal-plan prompt..."
                );
                var analysisSlotName =
                    slotCounts.Count == 1
                        ? slotCounts.Keys.First().ToString()
                        : string.Join(
                            ", ",
                            slotCounts.Keys.OrderBy(k => (int)k).Select(k => k.ToString())
                        );
                // Stage 1: Decompose natural-language mood prompt into exclusions, priorities, and search buckets
                var analysis = await llmClient.DecomposeMealPlanPromptAsync(
                    config,
                    new LlmMealPlanQueryAnalysisContext(
                        UserPrompt: prompt,
                        RequestedMealCount: requestedMealCount,
                        DefaultSlotName: analysisSlotName
                    ),
                    cancellationToken
                );

                excludedTerms = (analysis.ExcludedTerms ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToList();

                prioritizeIngredients = (analysis.PrioritizeIngredients ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToList();

                globalMaxTime = analysis.MaxTotalTimeMinutes is int gMax and > 0 and <= 1440
                    ? gMax
                    : null;

                searchBuckets = (analysis.SearchBuckets ?? [])
                    .Where(b =>
                        b is not null
                        && (
                            !string.IsNullOrWhiteSpace(b.SemanticQuery)
                            || !string.IsNullOrWhiteSpace(b.BucketName)
                            || (b.SuggestedTags?.Count ?? 0) > 0
                        )
                    )
                    .Take(4)
                    .Select(b => new MealPlanSearchBucketInput(
                        BucketName: !string.IsNullOrWhiteSpace(b.BucketName)
                            ? b.BucketName.Trim()
                            : (b.SemanticQuery?.Trim() ?? "Requested meals"),
                        SemanticQuery: !string.IsNullOrWhiteSpace(b.SemanticQuery)
                            ? b.SemanticQuery.Trim()
                            : (b.BucketName?.Trim() ?? prompt),
                        SuggestedTags: (b.SuggestedTags ?? [])
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Select(t => t.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(6)
                            .ToList(),
                        MaxTotalTimeMinutes: b.MaxTotalTimeMinutes is int bMax and > 0 and <= 1440
                            ? bMax
                            : null,
                        DesiredCount: Math.Clamp(b.DesiredCount ?? 1, 1, requestedMealCount)
                    ))
                    .ToList();

                if (searchBuckets.Count == 0)
                {
                    searchBuckets.Add(
                        new MealPlanSearchBucketInput(
                            BucketName: "Requested meals",
                            SemanticQuery: prompt,
                            SuggestedTags: [],
                            MaxTotalTimeMinutes: globalMaxTime,
                            DesiredCount: requestedMealCount
                        )
                    );
                }
                workload?.CompleteSegment("analyze_prompt");
            }

            workload?.ActivateSegment(
                "retrieve",
                "Searching recipes",
                "Retrieving cookbook candidates..."
            );
            // Stage 2: Deterministic exclusions + hybrid RecipeEmbedding search (or slot-aware randomized sample when prompt is blank) + perishable synergy
            var retrieval = await _recipeEmbeddingService.RetrieveMealPlanCandidatesAsync(
                config,
                new MealPlanRetrievalRequest(
                    RawUserPrompt: prompt,
                    RequestedMealCount: requestedMealCount,
                    ExcludedRecipeIds: excludedIds.ToList(),
                    RecentlyScheduledRecipeIds: recentlyScheduledRecipeIds,
                    ExcludedTerms: excludedTerms,
                    PrioritizeIngredients: prioritizeIngredients,
                    GlobalMaxTotalTimeMinutes: globalMaxTime,
                    SearchBuckets: searchBuckets,
                    UtcNow: now,
                    DefaultSlot: request.DefaultSlot,
                    RandomizeSelection: isSurpriseDraft,
                    SlotCounts: slotCounts
                ),
                cancellationToken
            );

            if (retrieval.Candidates.Count == 0)
            {
                throw new DomainValidationException(
                    excludedTerms.Count > 0
                        ? "No eligible published recipes matched your prompt's exclusions. Relax your exclusions or add matching recipes before drafting."
                        : "No eligible published recipes remain in your household cookbook. Add recipes or remove exclusions before drafting."
                );
            }
            workload?.CompleteSegment("retrieve");

            var catalogList = retrieval.Candidates;
            var bucketSummaries = searchBuckets
                .Select(b =>
                {
                    var details = new List<string> { $"target ~{b.DesiredCount}" };
                    if (b.MaxTotalTimeMinutes.HasValue)
                    {
                        details.Add($"<= {b.MaxTotalTimeMinutes.Value}m");
                    }
                    if (b.SuggestedTags.Count > 0)
                    {
                        details.Add($"tags: {string.Join(", ", b.SuggestedTags)}");
                    }
                    return $"{b.BucketName} ({string.Join("; ", details)})";
                })
                .ToList();

            var requestedSlotLines = hasStructuredSlots
                ? BuildRequestedSlotLines(request.RequestedSlots!, request.DefaultSlot)
                : null;

            workload?.ActivateSegment(
                "draft_schedule",
                $"{catalogList.Count} candidates",
                "Selecting & scheduling meals..."
            );
            // Stage 3: Constrained schedule assembly from compact candidate cards
            var llmContext = new LlmDraftMealPlanContext(
                WeekStartDate: rangeStart,
                UserPrompt: prompt,
                RequestedMealCount: requestedMealCount,
                DefaultSlotName: slotCounts.Count == 1
                    ? slotCounts.Keys.First().ToString()
                    : request.DefaultSlot.ToString(),
                PlaceOnDeck: hasStructuredSlots
                    ? request.RequestedSlots!.All(s => s.IsOnDeck)
                    : request.PlaceOnDeck,
                ExistingWeekScheduleLines: existingLines,
                CookbookCatalog: catalogList,
                BucketSummaries: bucketSummaries.Count > 0 ? bucketSummaries : null,
                PrioritizeIngredients: prioritizeIngredients.Count > 0
                    ? prioritizeIngredients
                    : null,
                UnmatchedBuckets: retrieval.UnmatchedBuckets.Count > 0
                    ? retrieval.UnmatchedBuckets
                    : null,
                RequestedSlotLines: requestedSlotLines
            );

            var payload = await llmClient.DraftWeeklyMealPlanAsync(
                config,
                llmContext,
                cancellationToken
            );
            workload?.CompleteSegment("draft_schedule");
            workload?.CompleteWorkload($"Drafted {requestedMealCount} meal suggestion(s).");

            var candidateRecipeIds = catalogList.Select(c => c.RecipeId).Distinct().ToList();
            var candidateRecipes = await dbContext
                .Recipes.AsNoTracking()
                .Where(r => candidateRecipeIds.Contains(r.Id))
                .Select(r => new MealPlanRecipeBadgeDto(
                    r.Id,
                    r.Title,
                    r.Slug,
                    r.BaseServings,
                    r.TotalTimeMinutes,
                    r.Difficulty,
                    r.ThumbnailImagePath
                ))
                .ToListAsync(cancellationToken);

            var candidateMapByIndex = catalogList.ToDictionary(c => c.CatalogIndex);
            var candidateMapByRecipeId = catalogList.ToDictionary(c => c.RecipeId);
            var badgeByIdMap = candidateRecipes.ToDictionary(r => r.Id);
            var badgeByTitleMap = catalogList
                .Where(candidate => badgeByIdMap.ContainsKey(candidate.RecipeId))
                .Select(candidate => badgeByIdMap[candidate.RecipeId])
                .GroupBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var validProposals = ResolveValidProposals(
                payload.Proposals,
                candidateMapByIndex,
                candidateMapByRecipeId,
                badgeByIdMap,
                badgeByTitleMap,
                catalogList.Count,
                requestedMealCount
            );

            if (validProposals.Count != requestedMealCount)
            {
                throw new DomainValidationException(
                    $"The AI model returned {validProposals.Count} valid cookbook suggestions instead of {requestedMealCount}. Try again or reduce the cooking count."
                );
            }

            var resultCandidates = hasStructuredSlots
                ? BuildStructuredCandidates(
                    request.RequestedSlots!,
                    request.DefaultSlot,
                    rangeStart,
                    prompt,
                    isSurpriseDraft,
                    validProposals
                )
                : BuildLegacyCandidates(
                    request,
                    rangeStart,
                    openDates,
                    prompt,
                    isSurpriseDraft,
                    validProposals
                );

            var unmetCravingNotes = isSurpriseDraft
                ? []
                : (payload.UnmetCravingNotes ?? [])
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (!isSurpriseDraft)
            {
                foreach (var bucket in retrieval.UnmatchedBuckets)
                {
                    if (
                        !string.IsNullOrWhiteSpace(bucket)
                        && !unmetCravingNotes.Any(n =>
                            n.Contains(bucket, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        unmetCravingNotes.Add(
                            $"Limited cookbook matches found for \"{bucket}\"; substituted the closest available recipes."
                        );
                    }
                }
            }

            return new DraftMealPlanResponseDto(
                WeekStartDate: rangeStart,
                WeekEndDate: rangeEnd,
                ModelUsed: config.TextModel,
                ConsideredCookbookRecipeCount: retrieval.TotalEligiblePublishedCount,
                Candidates: resultCandidates,
                UnmetCravingNotes: unmetCravingNotes.Count > 0 ? unmetCravingNotes : null,
                UsedEmbeddings: retrieval.UsedEmbeddings
            );
        }
        catch (OperationCanceledException)
        {
            workload?.CancelWorkload("Meal plan draft canceled.");
            throw;
        }
        catch (Exception ex)
        {
            workload?.FailWorkload(ex.Message);
            throw;
        }
    }

    private static void ValidateStructuredSlotRequests(
        IReadOnlyList<DraftMealCookSlotRequest> requestedSlots,
        DateOnly today,
        DateOnly rangeEnd
    )
    {
        var usedScheduledCells = new HashSet<(DateOnly Date, MealSlot Slot)>();

        foreach (var cookSlot in requestedSlots)
        {
            if (cookSlot.Servings is < 0.25m or > 999m)
            {
                throw new DomainValidationException(
                    "Servings per slot must be between 0.25 and 999."
                );
            }

            if (cookSlot.IsOnDeck)
            {
                if (cookSlot.Leftovers is { Count: > 0 })
                {
                    throw new DomainValidationException(
                        "On-deck meal suggestions cannot directly cover scheduled leftover slots."
                    );
                }

                if (cookSlot.Slot.HasValue && !Enum.IsDefined(cookSlot.Slot.Value))
                {
                    throw new DomainValidationException("Invalid meal slot specified.");
                }

                continue;
            }

            if (
                !cookSlot.ScheduledDate.HasValue
                || !cookSlot.Slot.HasValue
                || !Enum.IsDefined(cookSlot.Slot.Value)
            )
            {
                throw new DomainValidationException(
                    "Scheduled cooking slots require a valid date and meal slot."
                );
            }

            var cookDate = cookSlot.ScheduledDate.Value;
            var cookMealSlot = cookSlot.Slot.Value;
            if (cookDate < today || cookDate > rangeEnd)
            {
                throw new DomainValidationException(
                    "Requested cooking dates must fall between today and the end of the seven-day period."
                );
            }

            if (!usedScheduledCells.Add((cookDate, cookMealSlot)))
            {
                throw new DomainValidationException(
                    "Each day and meal slot can only appear once in a draft request."
                );
            }

            var totalServings = cookSlot.Servings;
            foreach (var leftover in cookSlot.Leftovers ?? [])
            {
                if (leftover.Servings is < 0.25m or > 999m)
                {
                    throw new DomainValidationException(
                        "Servings per leftover slot must be between 0.25 and 999."
                    );
                }

                if (!Enum.IsDefined(leftover.Slot))
                {
                    throw new DomainValidationException("Invalid leftover meal slot specified.");
                }

                if (leftover.ScheduledDate < today || leftover.ScheduledDate > rangeEnd)
                {
                    throw new DomainValidationException(
                        "Leftover dates must fall between today and the end of the seven-day period."
                    );
                }

                var isAfterCook =
                    leftover.ScheduledDate > cookDate
                    || (leftover.ScheduledDate == cookDate && leftover.Slot > cookMealSlot);
                if (!isAfterCook)
                {
                    throw new DomainValidationException(
                        "Leftover slots must occur chronologically after their cooking slot."
                    );
                }

                if (!usedScheduledCells.Add((leftover.ScheduledDate, leftover.Slot)))
                {
                    throw new DomainValidationException(
                        "Each day and meal slot can only appear once in a draft request."
                    );
                }

                totalServings += leftover.Servings;
            }

            if (totalServings > 999m)
            {
                throw new DomainValidationException(
                    "Cooking servings including leftovers cannot exceed 999. Reduce servings per meal."
                );
            }
        }
    }

    private static Dictionary<MealSlot, int> BuildRequestedSlotCounts(
        DraftWeeklyMealPlanRequest request,
        int requestedMealCount
    )
    {
        if (request.RequestedSlots is not { Count: > 0 })
        {
            return new Dictionary<MealSlot, int> { [request.DefaultSlot] = requestedMealCount };
        }

        return request
            .RequestedSlots.GroupBy(s =>
                s.IsOnDeck ? (s.Slot ?? request.DefaultSlot) : s.Slot!.Value
            )
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static List<string> BuildRequestedSlotLines(
        IReadOnlyList<DraftMealCookSlotRequest> requestedSlots,
        MealSlot defaultSlot
    )
    {
        var lines = new List<string>(requestedSlots.Count);
        for (var i = 0; i < requestedSlots.Count; i++)
        {
            var s = requestedSlots[i];
            var effectiveSlot = s.IsOnDeck ? (s.Slot ?? defaultSlot) : s.Slot!.Value;
            if (s.IsOnDeck)
            {
                lines.Add(
                    $"{i + 1}. On-Deck [{effectiveSlot}] — Flexible weekly meal ({s.Servings} servings)"
                );
                continue;
            }

            var leftoverDesc = s.Leftovers is { Count: > 0 }
                ? $" (covers {string.Join(", ", s.Leftovers.Select(l => $"{l.ScheduledDate:ddd} {l.Slot} {l.Servings}srv"))} leftovers)"
                : string.Empty;
            lines.Add(
                $"{i + 1}. {s.ScheduledDate!.Value:ddd yyyy-MM-dd} [{effectiveSlot}] — Cook {s.Servings} servings{leftoverDesc}"
            );
        }

        return lines;
    }

    private sealed record ValidProposalMatch(
        LlmDraftMealProposalItem Proposal,
        MealPlanRecipeBadgeDto Recipe,
        LlmCookbookRecipeCandidate? Candidate
    );

    private static List<ValidProposalMatch> ResolveValidProposals(
        IReadOnlyList<LlmDraftMealProposalItem>? proposals,
        Dictionary<int, LlmCookbookRecipeCandidate> candidateMapByIndex,
        Dictionary<Guid, LlmCookbookRecipeCandidate> candidateMapByRecipeId,
        Dictionary<Guid, MealPlanRecipeBadgeDto> badgeByIdMap,
        Dictionary<string, MealPlanRecipeBadgeDto> badgeByTitleMap,
        int catalogCount,
        int requestedMealCount
    )
    {
        var valid = new List<ValidProposalMatch>(requestedMealCount);
        var seenRecipeIds = new HashSet<Guid>();

        foreach (var p in proposals ?? [])
        {
            MealPlanRecipeBadgeDto? matchedRecipe = null;
            LlmCookbookRecipeCandidate? matchedCandidate = null;

            if (candidateMapByIndex.TryGetValue(p.CatalogIndex, out var cand))
            {
                matchedRecipe = badgeByIdMap.GetValueOrDefault(cand.RecipeId);
                matchedCandidate = cand;
            }
            else if (
                !string.IsNullOrWhiteSpace(p.RecipeTitle)
                && badgeByTitleMap.TryGetValue(p.RecipeTitle.Trim(), out var byTitle)
            )
            {
                matchedRecipe = byTitle;
                matchedCandidate = candidateMapByRecipeId.GetValueOrDefault(byTitle.Id);
            }

            if (matchedRecipe is null)
            {
                continue;
            }

            if (seenRecipeIds.Contains(matchedRecipe.Id) && catalogCount >= requestedMealCount)
            {
                continue;
            }

            seenRecipeIds.Add(matchedRecipe.Id);
            valid.Add(new ValidProposalMatch(p, matchedRecipe, matchedCandidate));

            if (valid.Count >= requestedMealCount)
            {
                break;
            }
        }

        return valid;
    }

    private static List<DraftMealPlanCandidateDto> BuildStructuredCandidates(
        IReadOnlyList<DraftMealCookSlotRequest> requestedSlots,
        MealSlot defaultSlot,
        DateOnly rangeStart,
        string prompt,
        bool isSurpriseDraft,
        List<ValidProposalMatch> validProposals
    )
    {
        var usedIndices = new HashSet<int>();
        var results = new List<DraftMealPlanCandidateDto>(requestedSlots.Count);

        foreach (var cookSlot in requestedSlots)
        {
            var effectiveSlot = cookSlot.IsOnDeck
                ? (cookSlot.Slot ?? defaultSlot)
                : cookSlot.Slot!.Value;
            var slotName = effectiveSlot.ToString();

            var chosenIdx = -1;
            for (var i = 0; i < validProposals.Count; i++)
            {
                if (usedIndices.Contains(i))
                {
                    continue;
                }

                var eligibleSlots = validProposals[i].Candidate?.EligibleSlots;
                if (
                    eligibleSlots is { Count: > 0 }
                    && eligibleSlots.Contains(slotName, StringComparer.OrdinalIgnoreCase)
                )
                {
                    chosenIdx = i;
                    break;
                }
            }

            if (chosenIdx < 0)
            {
                for (var i = 0; i < validProposals.Count; i++)
                {
                    if (!usedIndices.Contains(i))
                    {
                        chosenIdx = i;
                        break;
                    }
                }
            }

            usedIndices.Add(chosenIdx);
            var match = validProposals[chosenIdx];

            var leftoverSlots = (cookSlot.Leftovers ?? [])
                .OrderBy(l => l.ScheduledDate)
                .ThenBy(l => (int)l.Slot)
                .Select(l => new DraftMealLeftoverSlotDto(l.ScheduledDate, l.Slot, l.Servings))
                .ToList();
            var leftoverDates = leftoverSlots.Select(l => l.ScheduledDate).ToList();
            var targetServings = cookSlot.Servings + leftoverSlots.Sum(l => l.Servings);

            DateOnly? scheduledDate = cookSlot.IsOnDeck ? null : cookSlot.ScheduledDate;
            int? dayOffset = scheduledDate.HasValue
                ? scheduledDate.Value.DayNumber - rangeStart.DayNumber
                : null;
            MealSlot? dtoSlot = cookSlot.IsOnDeck ? cookSlot.Slot : effectiveSlot;

            var rationale = !string.IsNullOrWhiteSpace(match.Proposal.Rationale)
                ? match.Proposal.Rationale.Trim()
                : (
                    isSurpriseDraft
                        ? $"Selected as a varied {effectiveSlot} pick from your cookbook."
                        : $"Selected based on your request: {prompt}"
                );

            results.Add(
                new DraftMealPlanCandidateDto(
                    RecipeId: match.Recipe.Id,
                    Recipe: match.Recipe,
                    IsOnDeck: cookSlot.IsOnDeck,
                    DayOffset: dayOffset,
                    ScheduledDate: scheduledDate,
                    Slot: dtoSlot,
                    TargetServings: targetServings,
                    Rationale: rationale,
                    LeftoverDates: leftoverDates,
                    CookServings: cookSlot.Servings,
                    LeftoverSlots: leftoverSlots
                )
            );
        }

        return results;
    }

    private static List<DraftMealPlanCandidateDto> BuildLegacyCandidates(
        DraftWeeklyMealPlanRequest request,
        DateOnly rangeStart,
        List<DateOnly> openDates,
        string prompt,
        bool isSurpriseDraft,
        List<ValidProposalMatch> validProposals
    )
    {
        var requestedMealCount = request.MealCount;
        var results = new List<DraftMealPlanCandidateDto>(validProposals.Count);
        var nextOpenDay = 0;

        foreach (var match in validProposals)
        {
            var isOnDeck = request.PlaceOnDeck;
            int? dayOffset = null;
            DateOnly? scheduledDate = null;
            MealSlot? slot = null;

            var leftoverDates = new List<DateOnly>();
            if (!isOnDeck)
            {
                var coverage =
                    openDates.Count / requestedMealCount
                    + (results.Count < openDates.Count % requestedMealCount ? 1 : 0);
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

            var leftoverSlots = leftoverDates
                .Select(d => new DraftMealLeftoverSlotDto(
                    d,
                    request.DefaultSlot,
                    request.ServingsPerMeal
                ))
                .ToList();

            var rationale = !string.IsNullOrWhiteSpace(match.Proposal.Rationale)
                ? match.Proposal.Rationale.Trim()
                : (
                    isSurpriseDraft
                        ? $"Selected as a varied {request.DefaultSlot} pick from your cookbook."
                        : $"Selected based on your request: {prompt}"
                );

            results.Add(
                new DraftMealPlanCandidateDto(
                    RecipeId: match.Recipe.Id,
                    Recipe: match.Recipe,
                    IsOnDeck: isOnDeck,
                    DayOffset: dayOffset,
                    ScheduledDate: scheduledDate,
                    Slot: slot,
                    TargetServings: targetServings,
                    Rationale: rationale,
                    LeftoverDates: leftoverDates,
                    CookServings: request.ServingsPerMeal,
                    LeftoverSlots: leftoverSlots
                )
            );
        }

        return results;
    }

    private static bool StepMentionsIngredient(string? instruction, RecipeIngredient ingredient)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return false;
        }

        var displayName = ingredient.DisplayName?.Trim() ?? string.Empty;
        var tokens = StepInstructionParser.ExtractTokens(instruction);
        foreach (var token in tokens.OfType<StepIngredientToken>())
        {
            if (token.IngredientId.HasValue && token.IngredientId.Value == ingredient.Id)
            {
                return true;
            }

            if (displayName.Length > 0 && !string.IsNullOrWhiteSpace(token.Ingredient))
            {
                var tokenName = token.Ingredient.Trim();
                if (
                    displayName.Equals(tokenName, StringComparison.OrdinalIgnoreCase)
                    || displayName.Contains(tokenName, StringComparison.OrdinalIgnoreCase)
                    || tokenName.Contains(displayName, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return true;
                }
            }
        }

        if (displayName.Length > 0)
        {
            var plainText = StepInstructionParser.ToPlainText(instruction);
            if (plainText.Contains(displayName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
