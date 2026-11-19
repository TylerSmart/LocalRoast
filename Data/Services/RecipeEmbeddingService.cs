using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.Data.Services;

/// <summary>
/// Persists L2-normalized semantic recipe embeddings in <see cref="AppDbContext.RecipeEmbeddings" />
/// and executes Stage 2 hybrid retrieval (hard exclusions, per-bucket vector/lexical search,
/// prioritized ingredients, and perishable ingredient synergy) for AI meal planning.
/// </summary>
public sealed partial class RecipeEmbeddingService(
    AppDbContext dbContext,
    ILlmExtractionClient llmClient,
    IIngestionQueueSignal? queueSignal = null,
    ILogger<RecipeEmbeddingService>? logger = null
) : IRecipeEmbeddingService
{
    private const int MaxModelTagLength = 200;
    private const int MaxInlineMissingEmbeddings = 128;
    private const int DefaultCandidatePoolCap = 36;
    private const int MaxCandidatePoolCap = 40;

    private static readonly HashSet<FoodCategory> PerishableCategories =
    [
        FoodCategory.Produce,
        FoodCategory.MeatAndSeafood,
        FoodCategory.DairyAndEggs,
        FoodCategory.Refrigerated,
        FoodCategory.Bakery,
    ];

    private static readonly HashSet<string> GenericQueryStopWords = new(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "any",
        "be",
        "breakfast",
        "cook",
        "cooking",
        "day",
        "days",
        "dinner",
        "dinners",
        "dish",
        "dishes",
        "easy",
        "fast",
        "family",
        "food",
        "for",
        "from",
        "good",
        "great",
        "i",
        "in",
        "lunch",
        "lunches",
        "make",
        "meal",
        "meals",
        "min",
        "minute",
        "minutes",
        "my",
        "night",
        "nights",
        "of",
        "on",
        "or",
        "plan",
        "quick",
        "recipe",
        "recipes",
        "requested",
        "simple",
        "snack",
        "some",
        "something",
        "that",
        "the",
        "this",
        "to",
        "under",
        "use",
        "using",
        "want",
        "we",
        "week",
        "weekday",
        "weekend",
        "weeknight",
        "weeknights",
        "with",
    };

    private static readonly Dictionary<string, string[]> BroadExclusionExpansions = new(
        StringComparer.Ordinal
    )
    {
        ["seafood"] =
        [
            "seafood",
            "fish",
            "shellfish",
            "shrimp",
            "prawn",
            "salmon",
            "tuna",
            "cod",
            "halibut",
            "tilapia",
            "trout",
            "snapper",
            "mahi",
            "anchovy",
            "sardine",
            "mackerel",
            "crab",
            "lobster",
            "scallop",
            "clam",
            "mussel",
            "oyster",
            "squid",
            "calamari",
            "octopus",
        ],
        ["shellfish"] =
        [
            "shellfish",
            "shrimp",
            "prawn",
            "crab",
            "lobster",
            "scallop",
            "clam",
            "mussel",
            "oyster",
        ],
        ["fish"] =
        [
            "fish",
            "salmon",
            "tuna",
            "cod",
            "halibut",
            "tilapia",
            "trout",
            "snapper",
            "mahi",
            "anchovy",
            "sardine",
            "mackerel",
            "haddock",
            "sea bass",
        ],
        ["dairy"] =
        [
            "dairy",
            "milk",
            "butter",
            "cream",
            "heavy cream",
            "half and half",
            "sour cream",
            "cream cheese",
            "cheese",
            "parmesan",
            "cheddar",
            "mozzarella",
            "ricotta",
            "feta",
            "gruyere",
            "yogurt",
            "buttermilk",
            "ghee",
            "whey",
        ],
        ["pork"] =
        [
            "pork",
            "bacon",
            "ham",
            "prosciutto",
            "pancetta",
            "chorizo",
            "sausage",
            "salami",
            "pepperoni",
            "carnitas",
        ],
        ["meat"] =
        [
            "meat",
            "beef",
            "steak",
            "pork",
            "bacon",
            "ham",
            "sausage",
            "chicken",
            "turkey",
            "duck",
            "lamb",
            "veal",
            "venison",
            "bison",
        ],
        ["nut"] =
        [
            "nut",
            "almond",
            "walnut",
            "pecan",
            "cashew",
            "pistachio",
            "hazelnut",
            "macadamia",
            "pine nut",
            "peanut",
            "brazil nut",
        ],
        ["peanut"] = ["peanut", "peanut butter"],
        ["tree nut"] =
        [
            "almond",
            "walnut",
            "pecan",
            "cashew",
            "pistachio",
            "hazelnut",
            "macadamia",
            "pine nut",
            "brazil nut",
        ],
    };

    public async Task<int> UpsertRecipeEmbeddingsAsync(
        EffectiveLlmConnectionConfig embeddingConfig,
        IReadOnlyCollection<Guid> recipeIds,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(embeddingConfig);
        if (
            string.IsNullOrWhiteSpace(embeddingConfig.TextModel)
            || recipeIds is null
            || recipeIds.Count == 0
        )
        {
            return 0;
        }

        var distinctIds = recipeIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return 0;
        }

        var modelTag = Truncate(embeddingConfig.TextModel.Trim(), MaxModelTagLength);

        var recipes = await LoadRecipesForEmbeddingQuery()
            .Where(r => distinctIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        var publishedIds = recipes
            .Where(r => r.Status == RecipeStatus.Published)
            .Select(r => r.Id)
            .ToHashSet();

        var nonPublishedIds = distinctIds.Where(id => !publishedIds.Contains(id)).ToList();
        if (nonPublishedIds.Count > 0)
        {
            var staleForUnpublished = await dbContext
                .RecipeEmbeddings.Where(e => nonPublishedIds.Contains(e.RecipeId))
                .ToListAsync(cancellationToken);
            if (staleForUnpublished.Count > 0)
            {
                dbContext.RecipeEmbeddings.RemoveRange(staleForUnpublished);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        var publishedRecipes = recipes.Where(r => r.Status == RecipeStatus.Published).ToList();
        if (publishedRecipes.Count == 0)
        {
            return 0;
        }

        return await UpsertForLoadedPublishedRecipesAsync(
            embeddingConfig,
            modelTag,
            publishedRecipes,
            cancellationToken
        );
    }

    public async Task<int> SyncPendingEmbeddingsAsync(
        EffectiveLlmConnectionConfig config,
        int maxBatchCount = 64,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(config);
        var embConfig = config.ForEmbedding();
        if (embConfig is null || string.IsNullOrWhiteSpace(embConfig.TextModel))
        {
            return 0;
        }

        var batchLimit = Math.Clamp(maxBatchCount, 1, 256);
        var modelTag = Truncate(embConfig.TextModel.Trim(), MaxModelTagLength);

        var publishedRecipes = await LoadRecipesForEmbeddingQuery()
            .Where(r => r.Status == RecipeStatus.Published)
            .OrderByDescending(r => r.ModifiedAt)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        if (publishedRecipes.Count == 0)
        {
            return 0;
        }

        var existingHashes = await dbContext
            .RecipeEmbeddings.AsNoTracking()
            .Where(e => e.ModelTag == modelTag)
            .ToDictionaryAsync(e => e.RecipeId, e => e.ContentHash, cancellationToken);

        var pendingRecipes = new List<(Recipe Recipe, BuiltRecipeEmbeddingInput Built)>();
        foreach (var recipe in publishedRecipes)
        {
            var built = BuildEmbeddingInput(recipe);
            if (
                string.IsNullOrWhiteSpace(built.EmbeddingText)
                || (
                    existingHashes.TryGetValue(recipe.Id, out var existingHash)
                    && string.Equals(existingHash, built.ContentHash, StringComparison.Ordinal)
                )
            )
            {
                continue;
            }

            pendingRecipes.Add((recipe, built));
            if (pendingRecipes.Count >= batchLimit)
            {
                break;
            }
        }

        if (pendingRecipes.Count == 0)
        {
            return 0;
        }

        return await PersistComputedEmbeddingsAsync(
            embConfig,
            modelTag,
            pendingRecipes,
            cancellationToken
        );
    }

    public async Task<MealPlanRetrievalResult> RetrieveMealPlanCandidatesAsync(
        EffectiveLlmConnectionConfig config,
        MealPlanRetrievalRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        var excludedRecipeIds = request.ExcludedRecipeIds?.ToHashSet() ?? [];
        var recentlyScheduledIds = request.RecentlyScheduledRecipeIds?.ToHashSet() ?? [];

        var allPublished = await LoadRecipesForMealPlanningQuery()
            .Where(r => r.Status == RecipeStatus.Published && !excludedRecipeIds.Contains(r.Id))
            .OrderByDescending(r => r.AverageRating ?? 0m)
            .ThenByDescending(r => r.TimesCooked)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        if (allPublished.Count == 0)
        {
            return new MealPlanRetrievalResult(0, 0, false, [], []);
        }

        var subRecipeIds = allPublished
            .SelectMany(r => r.Ingredients)
            .Where(i => i.LinkedRecipeId.HasValue)
            .Select(i => i.LinkedRecipeId!.Value)
            .Distinct()
            .ToList();

        var subRecipesById =
            subRecipeIds.Count > 0
                ? await dbContext
                    .Recipes.AsNoTracking()
                    .Where(r => subRecipeIds.Contains(r.Id))
                    .Include(r => r.Ingredients)
                        .ThenInclude(i => i.FoodItem)
                            .ThenInclude(f => f!.Aliases)
                    .ToDictionaryAsync(r => r.Id, cancellationToken)
                : new Dictionary<Guid, Recipe>();

        var expandedExclusions = ExpandExclusionTerms(request.ExcludedTerms);
        var prioritizedTerms = NormalizeTermList(request.PrioritizeIngredients);

        var indexedRecipes = allPublished
            .Select(r => BuildSearchableRecipe(r, subRecipesById))
            .ToList();

        var eligibleRecipes =
            expandedExclusions.Count > 0
                ? indexedRecipes.Where(r => !MatchesAnyExclusion(r, expandedExclusions)).ToList()
                : indexedRecipes;

        if (eligibleRecipes.Count == 0)
        {
            return new MealPlanRetrievalResult(allPublished.Count, 0, false, [], []);
        }

        var buckets = NormalizeBuckets(request);

        // Try vector retrieval when an Embedding workflow is configured
        var usedEmbeddings = false;
        var recipeVectors = new Dictionary<Guid, float[]>();
        var bucketVectors = new Dictionary<int, float[]>();
        float[]? prioritizeVector = null;

        var embConfig = config.ForEmbedding();
        if (embConfig is not null && !string.IsNullOrWhiteSpace(embConfig.TextModel))
        {
            try
            {
                var modelTag = Truncate(embConfig.TextModel.Trim(), MaxModelTagLength);
                recipeVectors = await LoadAndSyncVectorsForEligibleAsync(
                    embConfig,
                    modelTag,
                    eligibleRecipes,
                    cancellationToken
                );

                if (recipeVectors.Count > 0)
                {
                    var queryTexts = new List<string>();
                    var bucketQueryIndex = new Dictionary<int, int>();

                    for (var i = 0; i < buckets.Count; i++)
                    {
                        var qText = BuildBucketEmbeddingQueryText(buckets[i]);
                        if (!string.IsNullOrWhiteSpace(qText))
                        {
                            bucketQueryIndex[i] = queryTexts.Count;
                            queryTexts.Add(qText);
                        }
                    }

                    var prioritizeQueryIndex = -1;
                    if (prioritizedTerms.Count > 0)
                    {
                        prioritizeQueryIndex = queryTexts.Count;
                        queryTexts.Add($"Ingredients: {string.Join(", ", prioritizedTerms)}");
                    }

                    if (queryTexts.Count > 0)
                    {
                        var qVectors = await llmClient.GenerateEmbeddingsAsync(
                            embConfig,
                            queryTexts,
                            cancellationToken
                        );

                        if (qVectors.Length == queryTexts.Count)
                        {
                            foreach (var (bucketIdx, qIdx) in bucketQueryIndex)
                            {
                                bucketVectors[bucketIdx] = qVectors[qIdx];
                            }

                            if (prioritizeQueryIndex >= 0)
                            {
                                prioritizeVector = qVectors[prioritizeQueryIndex];
                            }

                            usedEmbeddings = true;
                        }
                    }
                }
            }
            catch (Exception ex)
                when (!(
                        ex is OperationCanceledException
                        && cancellationToken.IsCancellationRequested
                    )
                )
            {
                logger?.LogWarning(
                    ex,
                    "Recipe embedding retrieval failed on model '{ModelTag}'; falling back to lexical meal-plan candidate search.",
                    embConfig.TextModel
                );
                usedEmbeddings = false;
                bucketVectors.Clear();
                prioritizeVector = null;
            }
        }

        // Per-bucket scoring & pool collection
        var poolCap = Math.Clamp(
            Math.Max(DefaultCandidatePoolCap, request.RequestedMealCount * 3),
            DefaultCandidatePoolCap,
            MaxCandidatePoolCap
        );

        var bestScoreByRecipeId = new Dictionary<Guid, double>(eligibleRecipes.Count);
        var pooledIds = new HashSet<Guid>();
        var unmatchedBuckets = new List<string>();

        for (var bIdx = 0; bIdx < buckets.Count; bIdx++)
        {
            var bucket = buckets[bIdx];
            bucketVectors.TryGetValue(bIdx, out var bVec);
            var effectiveMaxTime = bucket.MaxTotalTimeMinutes ?? request.GlobalMaxTotalTimeMinutes;
            var bucketQueryTokens = ExtractMeaningfulTokens(bucket.SemanticQuery);
            var bucketTagNorms = NormalizeTermList(bucket.SuggestedTags);
            var hasSpecificCraving = bucketQueryTokens.Count > 0 || bucketTagNorms.Count > 0;

            var scoredForBucket = new List<(SearchableRecipe Item, double Relevance, double Total)>(
                eligibleRecipes.Count
            );

            foreach (var item in eligibleRecipes)
            {
                var lexical = ComputeBucketLexicalScore(item, bucketQueryTokens, bucketTagNorms);
                double relevance;
                if (
                    usedEmbeddings
                    && bVec is not null
                    && recipeVectors.TryGetValue(item.Recipe.Id, out var rVec)
                )
                {
                    var cosine = Math.Max(0.0, DotProduct(bVec, rVec));
                    relevance = (0.62 * cosine) + (0.38 * lexical);
                }
                else
                {
                    relevance = lexical;
                }

                var total =
                    relevance
                    + ComputeTimeAdjustment(item.Recipe.TotalTimeMinutes, effectiveMaxTime)
                    + ComputePrioritizedIngredientBoost(item, prioritizedTerms)
                    + ComputeRecencyAndRatingAdjustment(
                        item.Recipe,
                        recentlyScheduledIds,
                        request.UtcNow
                    );

                scoredForBucket.Add((item, relevance, total));

                if (
                    !bestScoreByRecipeId.TryGetValue(item.Recipe.Id, out var existingScore)
                    || total > existingScore
                )
                {
                    bestScoreByRecipeId[item.Recipe.Id] = total;
                }
            }

            scoredForBucket.Sort(
                (a, b) =>
                {
                    var cmp = b.Total.CompareTo(a.Total);
                    if (cmp != 0)
                        return cmp;
                    var rateCmp = (b.Item.Recipe.AverageRating ?? 0m).CompareTo(
                        a.Item.Recipe.AverageRating ?? 0m
                    );
                    if (rateCmp != 0)
                        return rateCmp;
                    var cookCmp = b.Item.Recipe.TimesCooked.CompareTo(a.Item.Recipe.TimesCooked);
                    return cookCmp != 0 ? cookCmp : a.Item.Recipe.Id.CompareTo(b.Item.Recipe.Id);
                }
            );

            if (hasSpecificCraving && scoredForBucket.Count > 0)
            {
                var topRelevance = scoredForBucket[0].Relevance;
                var minThreshold = usedEmbeddings ? 0.24 : 0.12;
                if (topRelevance < minThreshold)
                {
                    unmatchedBuckets.Add(bucket.BucketName);
                }
            }

            var takePerBucket = Math.Max(8, bucket.DesiredCount * 4);
            foreach (var entry in scoredForBucket.Take(takePerBucket))
            {
                pooledIds.Add(entry.Item.Recipe.Id);
            }
        }

        // Add dedicated "pantry clearer" candidates for PrioritizeIngredients
        if (prioritizedTerms.Count > 0)
        {
            var pantryScored = eligibleRecipes
                .Select(item =>
                {
                    var directMatch = ComputePrioritizedIngredientBoost(item, prioritizedTerms);
                    var vecSim =
                        usedEmbeddings
                        && prioritizeVector is not null
                        && recipeVectors.TryGetValue(item.Recipe.Id, out var rVec)
                            ? Math.Max(0.0, DotProduct(prioritizeVector, rVec)) * 0.20
                            : 0.0;
                    return (Item: item, Score: directMatch + vecSim);
                })
                .Where(x => x.Score > 0.05)
                .OrderByDescending(x => x.Score)
                .Take(6);

            foreach (var entry in pantryScored)
            {
                pooledIds.Add(entry.Item.Recipe.Id);
                if (bestScoreByRecipeId.TryGetValue(entry.Item.Recipe.Id, out var current))
                {
                    bestScoreByRecipeId[entry.Item.Recipe.Id] = current + (entry.Score * 0.5);
                }
            }
        }

        // Perishable ingredient synergy boost anchored on the top candidates
        var anchorCount = Math.Clamp(Math.Max(request.RequestedMealCount, 3), 3, 6);
        var anchorItems = eligibleRecipes
            .Where(item => pooledIds.Contains(item.Recipe.Id))
            .OrderByDescending(item => bestScoreByRecipeId.GetValueOrDefault(item.Recipe.Id))
            .Take(anchorCount)
            .ToList();

        var anchorPerishableIds = anchorItems.SelectMany(a => a.PerishableFoodItemIds).ToHashSet();

        if (anchorPerishableIds.Count > 0)
        {
            var anchorIdSet = anchorItems.Select(a => a.Recipe.Id).ToHashSet();
            foreach (var item in eligibleRecipes)
            {
                if (anchorIdSet.Contains(item.Recipe.Id) || item.PerishableFoodItemIds.Count == 0)
                {
                    continue;
                }

                var shared = item.PerishableFoodItemIds.Count(id =>
                    anchorPerishableIds.Contains(id)
                );
                if (shared > 0)
                {
                    var bonus = 0.06 * Math.Min(3, shared);
                    bestScoreByRecipeId[item.Recipe.Id] =
                        bestScoreByRecipeId.GetValueOrDefault(item.Recipe.Id) + bonus;
                    if (pooledIds.Count < poolCap * 2)
                    {
                        pooledIds.Add(item.Recipe.Id);
                    }
                }
            }
        }

        // Deduplicate & select final compact planning card list
        var finalSelection = eligibleRecipes
            .Where(item => pooledIds.Count == 0 || pooledIds.Contains(item.Recipe.Id))
            .OrderByDescending(item => bestScoreByRecipeId.GetValueOrDefault(item.Recipe.Id))
            .ThenByDescending(item => item.Recipe.AverageRating ?? 0m)
            .ThenByDescending(item => item.Recipe.TimesCooked)
            .ThenBy(item => item.Recipe.Id)
            .Take(poolCap)
            .ToList();

        var candidates = new List<LlmCookbookRecipeCandidate>(finalSelection.Count);
        for (var i = 0; i < finalSelection.Count; i++)
        {
            var r = finalSelection[i].Recipe;
            int? daysSinceLastCooked = r.LastCookedAt.HasValue
                ? Math.Max(0, (int)(request.UtcNow - r.LastCookedAt.Value).TotalDays)
                : null;

            var tags = r.RecipeTags.Select(rt => rt.Tag.Name).OrderBy(t => t).ToList();
            var keyIngredients = r
                .Ingredients.OrderBy(ing => ing.IsOptional ? 1 : 0)
                .ThenBy(ing => ing.SortOrder)
                .Where(ing =>
                    !ing.IsOptional
                    && (
                        (ing.FoodItem != null && !ing.FoodItem.IsPantryStaple)
                        || (ing.FoodItem == null && !string.IsNullOrWhiteSpace(ing.DisplayName))
                    )
                )
                .Select(ing => ing.FoodItem?.Name ?? ing.DisplayName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            candidates.Add(
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

        return new MealPlanRetrievalResult(
            TotalEligiblePublishedCount: allPublished.Count,
            PostExclusionEligibleCount: eligibleRecipes.Count,
            UsedEmbeddings: usedEmbeddings,
            Candidates: candidates,
            UnmatchedBuckets: unmatchedBuckets
        );
    }

    private async Task<Dictionary<Guid, float[]>> LoadAndSyncVectorsForEligibleAsync(
        EffectiveLlmConnectionConfig embConfig,
        string modelTag,
        List<SearchableRecipe> eligibleRecipes,
        CancellationToken cancellationToken
    )
    {
        var eligibleIds = eligibleRecipes.Select(e => e.Recipe.Id).ToHashSet();

        var existingRows = await dbContext
            .RecipeEmbeddings.AsNoTracking()
            .Where(e => e.ModelTag == modelTag)
            .ToListAsync(cancellationToken);

        var vectorsByRecipeId = new Dictionary<Guid, float[]>(eligibleRecipes.Count);
        var existingByRecipeId = new Dictionary<Guid, RecipeEmbedding>(existingRows.Count);

        foreach (var row in existingRows)
        {
            if (!eligibleIds.Contains(row.RecipeId))
            {
                continue;
            }

            existingByRecipeId[row.RecipeId] = row;
            if (row.Dimensions > 0 && row.VectorBytes.Length == row.Dimensions * sizeof(float))
            {
                vectorsByRecipeId[row.RecipeId] = MemoryMarshal
                    .Cast<byte, float>(row.VectorBytes.AsSpan())
                    .ToArray();
            }
        }

        var missingOrStale = new List<(Recipe Recipe, BuiltRecipeEmbeddingInput Built)>();
        foreach (var item in eligibleRecipes)
        {
            var built = BuildEmbeddingInput(item.Recipe);
            if (string.IsNullOrWhiteSpace(built.EmbeddingText))
            {
                continue;
            }

            if (
                !existingByRecipeId.TryGetValue(item.Recipe.Id, out var existing)
                || !string.Equals(existing.ContentHash, built.ContentHash, StringComparison.Ordinal)
                || !vectorsByRecipeId.ContainsKey(item.Recipe.Id)
            )
            {
                missingOrStale.Add((item.Recipe, built));
            }
        }

        if (missingOrStale.Count == 0)
        {
            return vectorsByRecipeId;
        }

        if (missingOrStale.Count > MaxInlineMissingEmbeddings)
        {
            // Avoid stalling an interactive dialog on huge backfills; wake background worker and
            // use already-cached vectors alongside lexical scoring for the un-embedded remainder.
            queueSignal?.NotifyBackgroundWorkAvailable();
            return vectorsByRecipeId;
        }

        var texts = missingOrStale.Select(x => x.Built.EmbeddingText).ToList();
        var generated = await llmClient.GenerateEmbeddingsAsync(
            embConfig,
            texts,
            cancellationToken
        );

        if (generated.Length != missingOrStale.Count)
        {
            return vectorsByRecipeId;
        }

        for (var i = 0; i < missingOrStale.Count; i++)
        {
            vectorsByRecipeId[missingOrStale[i].Recipe.Id] = generated[i];
        }

        await SaveGeneratedVectorsAsync(modelTag, missingOrStale, generated, cancellationToken);
        return vectorsByRecipeId;
    }

    private async Task<int> UpsertForLoadedPublishedRecipesAsync(
        EffectiveLlmConnectionConfig embConfig,
        string modelTag,
        List<Recipe> publishedRecipes,
        CancellationToken cancellationToken
    )
    {
        var ids = publishedRecipes.Select(r => r.Id).ToList();
        var existingHashes = await dbContext
            .RecipeEmbeddings.AsNoTracking()
            .Where(e => e.ModelTag == modelTag && ids.Contains(e.RecipeId))
            .ToDictionaryAsync(e => e.RecipeId, e => e.ContentHash, cancellationToken);

        var toEmbed = new List<(Recipe Recipe, BuiltRecipeEmbeddingInput Built)>();
        foreach (var recipe in publishedRecipes)
        {
            var built = BuildEmbeddingInput(recipe);
            if (string.IsNullOrWhiteSpace(built.EmbeddingText))
            {
                continue;
            }

            if (
                existingHashes.TryGetValue(recipe.Id, out var existingHash)
                && string.Equals(existingHash, built.ContentHash, StringComparison.Ordinal)
            )
            {
                continue;
            }

            toEmbed.Add((recipe, built));
        }

        if (toEmbed.Count == 0)
        {
            return 0;
        }

        return await PersistComputedEmbeddingsAsync(
            embConfig,
            modelTag,
            toEmbed,
            cancellationToken
        );
    }

    private async Task<int> PersistComputedEmbeddingsAsync(
        EffectiveLlmConnectionConfig embConfig,
        string modelTag,
        List<(Recipe Recipe, BuiltRecipeEmbeddingInput Built)> pending,
        CancellationToken cancellationToken
    )
    {
        var texts = pending.Select(p => p.Built.EmbeddingText).ToList();
        var vectors = await llmClient.GenerateEmbeddingsAsync(embConfig, texts, cancellationToken);

        if (vectors.Length != pending.Count)
        {
            logger?.LogWarning(
                "Embedding model '{ModelTag}' returned {Returned} vectors for {Expected} recipes; skipping persistence.",
                modelTag,
                vectors.Length,
                pending.Count
            );
            return 0;
        }

        await SaveGeneratedVectorsAsync(modelTag, pending, vectors, cancellationToken);
        return pending.Count;
    }

    private async Task SaveGeneratedVectorsAsync(
        string modelTag,
        List<(Recipe Recipe, BuiltRecipeEmbeddingInput Built)> items,
        float[][] vectors,
        CancellationToken cancellationToken
    )
    {
        var ids = items.Select(x => x.Recipe.Id).Distinct().ToList();
        var existingEntities = await dbContext
            .RecipeEmbeddings.Where(e => e.ModelTag == modelTag && ids.Contains(e.RecipeId))
            .ToDictionaryAsync(e => e.RecipeId, cancellationToken);

        var addedEntities = new List<RecipeEmbedding>();

        for (var i = 0; i < items.Count; i++)
        {
            var (recipe, built) = items[i];
            var vec = vectors[i];
            var vectorBytes = MemoryMarshal.AsBytes(vec.AsSpan()).ToArray();

            if (existingEntities.TryGetValue(recipe.Id, out var existing))
            {
                existing.ContentHash = built.ContentHash;
                existing.EmbeddingText = built.EmbeddingText;
                existing.Dimensions = vec.Length;
                existing.VectorBytes = vectorBytes;
            }
            else
            {
                var entity = new RecipeEmbedding
                {
                    RecipeId = recipe.Id,
                    ModelTag = modelTag,
                    ContentHash = built.ContentHash,
                    EmbeddingText = built.EmbeddingText,
                    Dimensions = vec.Length,
                    VectorBytes = vectorBytes,
                };
                dbContext.RecipeEmbeddings.Add(entity);
                existingEntities[recipe.Id] = entity;
                addedEntities.Add(entity);
            }
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger?.LogDebug(
                ex,
                "Concurrent upsert on RecipeEmbeddings for model '{ModelTag}'; detaching added entries.",
                modelTag
            );
            foreach (var added in addedEntities)
            {
                dbContext.Entry(added).State = EntityState.Detached;
            }
        }
    }

    private IQueryable<Recipe> LoadRecipesForEmbeddingQuery() =>
        dbContext
            .Recipes.AsNoTracking()
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
            .Include(r => r.RecipeCollections)
                .ThenInclude(rc => rc.Collection)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.LinkedRecipe);

    private IQueryable<Recipe> LoadRecipesForMealPlanningQuery() =>
        dbContext
            .Recipes.AsNoTracking()
            .Include(r => r.RecipeTags)
                .ThenInclude(rt => rt.Tag)
            .Include(r => r.RecipeCollections)
                .ThenInclude(rc => rc.Collection)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.FoodItem)
                    .ThenInclude(f => f!.Aliases)
            .Include(r => r.Ingredients)
                .ThenInclude(i => i.LinkedRecipe);

    internal static BuiltRecipeEmbeddingInput BuildEmbeddingInput(Recipe recipe)
    {
        var tags = recipe.RecipeTags.Select(rt => rt.Tag.Name).ToList();
        var collections = recipe.RecipeCollections.Select(rc => rc.Collection.Name).ToList();
        var ingredients = recipe
            .Ingredients.OrderBy(i => i.SortOrder)
            .Select(i => new RecipeEmbeddingIngredientInput(
                CanonicalFoodName: i.FoodItem?.Name,
                DisplayName: i.DisplayName,
                IsPantryStaple: i.FoodItem?.IsPantryStaple ?? false,
                IsOptional: i.IsOptional,
                LinkedRecipeTitle: i.LinkedRecipe?.Title
            ))
            .ToList();

        return RecipeEmbeddingTextBuilder.Build(
            new RecipeEmbeddingDocument(
                Title: recipe.Title,
                Description: recipe.Description,
                TotalTimeMinutes: recipe.TotalTimeMinutes,
                Difficulty: recipe.Difficulty,
                Tags: tags,
                Collections: collections,
                Ingredients: ingredients
            )
        );
    }

    private static SearchableRecipe BuildSearchableRecipe(
        Recipe recipe,
        Dictionary<Guid, Recipe> subRecipesById
    )
    {
        var tagTokens = new HashSet<string>(StringComparer.Ordinal);
        var normalizedTagNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rt in recipe.RecipeTags)
        {
            var norm = NormalizePhrase(rt.Tag.Name);
            if (!string.IsNullOrEmpty(norm))
            {
                normalizedTagNames.Add(norm);
                foreach (var t in TokenizeNormalized(norm))
                {
                    tagTokens.Add(t);
                }
            }
        }

        var collectionTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rc in recipe.RecipeCollections)
        {
            foreach (var t in TokenizeNormalized(NormalizePhrase(rc.Collection.Name)))
            {
                collectionTokens.Add(t);
            }
        }

        var titleTokens = TokenizeNormalized(NormalizePhrase(recipe.Title));
        var descTokens = TokenizeNormalized(NormalizePhrase(recipe.Description));

        var ingredientPhrases = new HashSet<string>(StringComparer.Ordinal);
        var ingredientTokens = new HashSet<string>(StringComparer.Ordinal);
        var perishableFoodItemIds = new HashSet<Guid>();

        void AddIngredientSource(RecipeIngredient ing)
        {
            if (ing.FoodItem is not null)
            {
                AddPhraseAndTokens(ing.FoodItem.Name, ingredientPhrases, ingredientTokens);
                foreach (var alias in ing.FoodItem.Aliases)
                {
                    AddPhraseAndTokens(alias.Alias, ingredientPhrases, ingredientTokens);
                }

                if (
                    !ing.FoodItem.IsPantryStaple
                    && ing.FoodItem.Category.HasValue
                    && PerishableCategories.Contains(ing.FoodItem.Category.Value)
                )
                {
                    perishableFoodItemIds.Add(ing.FoodItem.Id);
                }
            }

            AddPhraseAndTokens(ing.DisplayName, ingredientPhrases, ingredientTokens);

            if (ing.LinkedRecipe is not null)
            {
                AddPhraseAndTokens(ing.LinkedRecipe.Title, ingredientPhrases, ingredientTokens);
            }
        }

        foreach (var ing in recipe.Ingredients)
        {
            AddIngredientSource(ing);
            if (
                ing.LinkedRecipeId.HasValue
                && subRecipesById.TryGetValue(ing.LinkedRecipeId.Value, out var sub)
            )
            {
                foreach (var subIng in sub.Ingredients)
                {
                    AddIngredientSource(subIng);
                }
            }
        }

        var allTokens = new HashSet<string>(StringComparer.Ordinal);
        allTokens.UnionWith(titleTokens);
        allTokens.UnionWith(descTokens);
        allTokens.UnionWith(tagTokens);
        allTokens.UnionWith(collectionTokens);
        allTokens.UnionWith(ingredientTokens);

        var allPhrases = new HashSet<string>(StringComparer.Ordinal);
        var normTitle = NormalizePhrase(recipe.Title);
        if (!string.IsNullOrEmpty(normTitle))
        {
            allPhrases.Add(normTitle);
        }
        allPhrases.UnionWith(normalizedTagNames);
        allPhrases.UnionWith(ingredientPhrases);

        return new SearchableRecipe(
            Recipe: recipe,
            TitleTokens: titleTokens,
            TagNames: normalizedTagNames,
            TagTokens: tagTokens,
            IngredientPhrases: ingredientPhrases,
            IngredientTokens: ingredientTokens,
            AllTokens: allTokens,
            AllPhrases: allPhrases,
            PerishableFoodItemIds: perishableFoodItemIds
        );
    }

    private static void AddPhraseAndTokens(
        string? raw,
        HashSet<string> phrases,
        HashSet<string> tokens
    )
    {
        var norm = NormalizePhrase(raw);
        if (string.IsNullOrEmpty(norm))
        {
            return;
        }

        phrases.Add(norm);
        foreach (var token in TokenizeNormalized(norm))
        {
            tokens.Add(token);
        }
    }

    private static List<string[]> ExpandExclusionTerms(IReadOnlyList<string>? terms)
    {
        if (terms is null || terms.Count == 0)
        {
            return [];
        }

        var expandedPhrases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in terms)
        {
            var norm = NormalizePhrase(raw);
            if (string.IsNullOrEmpty(norm))
            {
                continue;
            }

            expandedPhrases.Add(norm);
            var singNorm = SingularizePhrase(norm);
            if (!string.IsNullOrEmpty(singNorm))
            {
                expandedPhrases.Add(singNorm);
            }

            if (BroadExclusionExpansions.TryGetValue(norm, out var group))
            {
                foreach (var item in group)
                {
                    expandedPhrases.Add(NormalizePhrase(item));
                }
            }
            else if (BroadExclusionExpansions.TryGetValue(singNorm, out var singGroup))
            {
                foreach (var item in singGroup)
                {
                    expandedPhrases.Add(NormalizePhrase(item));
                }
            }
        }

        return expandedPhrases.Select(TokenizeOrdered).Where(arr => arr.Length > 0).ToList();
    }

    private static bool MatchesAnyExclusion(
        SearchableRecipe recipe,
        List<string[]> expandedExclusionTokenSequences
    )
    {
        foreach (var seq in expandedExclusionTokenSequences)
        {
            if (seq.Length == 1)
            {
                if (
                    recipe.IngredientTokens.Contains(seq[0])
                    || recipe.TagTokens.Contains(seq[0])
                    || recipe.TitleTokens.Contains(seq[0])
                )
                {
                    return true;
                }
            }
            else
            {
                var target = string.Join(' ', seq);
                foreach (var phrase in recipe.AllPhrases)
                {
                    if (ContainsTokenSequence(phrase, target))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ContainsTokenSequence(string phrase, string targetSequence)
    {
        var phraseTokens = string.Join(' ', TokenizeOrdered(phrase));
        if (phraseTokens.Length == 0 || targetSequence.Length == 0)
        {
            return false;
        }

        var idx = phraseTokens.IndexOf(targetSequence, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var beforeOk = idx == 0 || phraseTokens[idx - 1] == ' ';
            var afterIdx = idx + targetSequence.Length;
            var afterOk = afterIdx == phraseTokens.Length || phraseTokens[afterIdx] == ' ';
            if (beforeOk && afterOk)
            {
                return true;
            }

            idx = phraseTokens.IndexOf(targetSequence, idx + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static List<MealPlanSearchBucketInput> NormalizeBuckets(
        MealPlanRetrievalRequest request
    )
    {
        var valid = (request.SearchBuckets ?? [])
            .Where(b =>
                b is not null
                && (
                    !string.IsNullOrWhiteSpace(b.SemanticQuery)
                    || !string.IsNullOrWhiteSpace(b.BucketName)
                    || (b.SuggestedTags?.Count ?? 0) > 0
                )
            )
            .Take(4)
            .Select(b =>
                b with
                {
                    BucketName = string.IsNullOrWhiteSpace(b.BucketName)
                        ? b.SemanticQuery.Trim()
                        : b.BucketName.Trim(),
                    SemanticQuery = string.IsNullOrWhiteSpace(b.SemanticQuery)
                        ? b.BucketName.Trim()
                        : b.SemanticQuery.Trim(),
                    DesiredCount = Math.Clamp(
                        b.DesiredCount,
                        1,
                        Math.Max(1, request.RequestedMealCount)
                    ),
                }
            )
            .ToList();

        if (valid.Count == 0)
        {
            valid.Add(
                new MealPlanSearchBucketInput(
                    BucketName: "Requested meals",
                    SemanticQuery: request.RawUserPrompt.Trim(),
                    SuggestedTags: [],
                    MaxTotalTimeMinutes: request.GlobalMaxTotalTimeMinutes,
                    DesiredCount: Math.Max(1, request.RequestedMealCount)
                )
            );
        }

        return valid;
    }

    private static string BuildBucketEmbeddingQueryText(MealPlanSearchBucketInput bucket)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(bucket.SemanticQuery))
        {
            parts.Add(bucket.SemanticQuery.Trim());
        }
        if (bucket.SuggestedTags is { Count: > 0 })
        {
            var cleanTags = bucket
                .SuggestedTags.Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cleanTags.Count > 0)
            {
                parts.Add($"Tags: {string.Join(", ", cleanTags)}");
            }
        }

        return string.Join(". ", parts);
    }

    private static double ComputeBucketLexicalScore(
        SearchableRecipe recipe,
        HashSet<string> queryTokens,
        List<string> tagNorms
    )
    {
        double tagScore = 0.0;
        if (tagNorms.Count > 0)
        {
            var matchedTags = 0;
            foreach (var tagNorm in tagNorms)
            {
                var tokens = TokenizeNormalized(tagNorm);
                if (
                    recipe.TagNames.Contains(tagNorm)
                    || (tokens.Count > 0 && tokens.All(t => recipe.TagTokens.Contains(t)))
                    || (tokens.Count > 0 && tokens.All(t => recipe.AllTokens.Contains(t)))
                )
                {
                    matchedTags++;
                }
            }

            tagScore = (double)matchedTags / tagNorms.Count;
        }

        double tokenScore = 0.0;
        if (queryTokens.Count > 0)
        {
            var weightedMatches = 0.0;
            foreach (var token in queryTokens)
            {
                if (recipe.TitleTokens.Contains(token) || recipe.TagTokens.Contains(token))
                {
                    weightedMatches += 1.0;
                }
                else if (recipe.IngredientTokens.Contains(token))
                {
                    weightedMatches += 0.85;
                }
                else if (recipe.AllTokens.Contains(token))
                {
                    weightedMatches += 0.55;
                }
            }

            tokenScore = Math.Min(1.0, weightedMatches / queryTokens.Count);
        }

        if (tagNorms.Count > 0 && queryTokens.Count > 0)
        {
            return (0.45 * tagScore) + (0.55 * tokenScore);
        }

        return tagNorms.Count > 0 ? tagScore : tokenScore;
    }

    private static double ComputeTimeAdjustment(int? totalTimeMinutes, int? maxTimeMinutes)
    {
        if (!maxTimeMinutes.HasValue || maxTimeMinutes.Value <= 0 || !totalTimeMinutes.HasValue)
        {
            return 0.0;
        }

        return totalTimeMinutes.Value <= maxTimeMinutes.Value ? 0.08 : -0.20;
    }

    private static double ComputePrioritizedIngredientBoost(
        SearchableRecipe recipe,
        List<string> prioritizedTerms
    )
    {
        if (prioritizedTerms.Count == 0)
        {
            return 0.0;
        }

        var matched = 0;
        foreach (var term in prioritizedTerms)
        {
            var tokens = TokenizeOrdered(term);
            if (tokens.Length == 1 && recipe.IngredientTokens.Contains(tokens[0]))
            {
                matched++;
            }
            else if (tokens.Length > 1)
            {
                var target = string.Join(' ', tokens);
                if (recipe.IngredientPhrases.Any(p => ContainsTokenSequence(p, target)))
                {
                    matched++;
                }
            }
        }

        return Math.Min(0.30, matched * 0.14);
    }

    private static double ComputeRecencyAndRatingAdjustment(
        Recipe recipe,
        HashSet<Guid> recentlyScheduledIds,
        DateTimeOffset utcNow
    )
    {
        double adj = 0.0;

        if (recentlyScheduledIds.Contains(recipe.Id))
        {
            adj -= 0.18;
        }
        else if (!recipe.LastCookedAt.HasValue)
        {
            adj += 0.05;
        }
        else
        {
            var days = (utcNow - recipe.LastCookedAt.Value).TotalDays;
            if (days >= 21)
            {
                adj += 0.06;
            }
            else if (days < 7)
            {
                adj -= 0.10;
            }
        }

        if (recipe.AverageRating is >= 4.0m)
        {
            adj += 0.02;
        }

        return adj;
    }

    private static List<string> NormalizeTermList(IReadOnlyList<string>? terms)
    {
        if (terms is null || terms.Count == 0)
        {
            return [];
        }

        return terms
            .Select(NormalizePhrase)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<string> ExtractMeaningfulTokens(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in TokenizeNormalized(NormalizePhrase(text)))
        {
            if (token.Length >= 2 && !GenericQueryStopWords.Contains(token))
            {
                tokens.Add(token);
            }
        }
        return tokens;
    }

    private static string NormalizePhrase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = NonAlphaNumericRegex().Replace(value.Trim().ToLowerInvariant(), " ");
        return WhitespaceCollapseRegex().Replace(cleaned.Trim(), " ");
    }

    private static string SingularizePhrase(string normalizedPhrase)
    {
        if (string.IsNullOrEmpty(normalizedPhrase))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            normalizedPhrase
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(RecipeUnitAndSectionHelpers.Singularize)
        );
    }

    private static string[] TokenizeOrdered(string normalizedPhrase)
    {
        if (string.IsNullOrEmpty(normalizedPhrase))
        {
            return [];
        }

        return normalizedPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(RecipeUnitAndSectionHelpers.Singularize)
            .ToArray();
    }

    private static HashSet<string> TokenizeNormalized(string normalizedPhrase)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in TokenizeOrdered(normalizedPhrase))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static double DotProduct(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        double sum = 0.0;
        for (var i = 0; i < len; i++)
        {
            sum += a[i] * (double)b[i];
        }
        return sum;
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length > maxLen ? value[..maxLen] : value;

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceCollapseRegex();

    private sealed record SearchableRecipe(
        Recipe Recipe,
        HashSet<string> TitleTokens,
        HashSet<string> TagNames,
        HashSet<string> TagTokens,
        HashSet<string> IngredientPhrases,
        HashSet<string> IngredientTokens,
        HashSet<string> AllTokens,
        HashSet<string> AllPhrases,
        HashSet<Guid> PerishableFoodItemIds
    );
}
