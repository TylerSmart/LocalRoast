using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.Data.Services;

/// <summary>
/// Hybrid semantic vector + lexical catalog search for Stage 3 ingredient and sub-recipe matching.
/// Persists L2-normalized catalog embeddings in <see cref="AppDbContext.CatalogEmbeddings"/> when an
/// Embedding workflow is configured, and gracefully falls back to Stage 03a query expansion +
/// lexical similarity when embeddings are unconfigured or unavailable.
/// </summary>
public sealed partial class CatalogVectorSearchService(
    AppDbContext dbContext,
    ILlmExtractionClient llmClient,
    ILogger<CatalogVectorSearchService>? logger = null
) : ICatalogVectorSearchService
{
    private const int MaxNormalizedTextLength = 400;
    private const int MaxModelTagLength = 200;
    private const int MaxFoodCandidates = 8;
    private const int MaxRecipeCandidates = 3;

    public async Task<Stage3CatalogSearchResult> SearchAsync(
        EffectiveLlmConnectionConfig config,
        IReadOnlyList<Stage3SearchQueryItem> queryItems,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogRecipeLookupItem> catalogRecipes,
        CancellationToken cancellationToken = default
    )
    {
        if (queryItems.Count == 0)
        {
            return new Stage3CatalogSearchResult(
                new Dictionary<int, Stage3ItemCandidatesDto>(),
                UsedEmbeddings: false
            );
        }

        var embConfig = config.ForEmbedding();
        if (embConfig is not null && !string.IsNullOrWhiteSpace(embConfig.TextModel))
        {
            try
            {
                var vectorResults = await SearchWithEmbeddingsAsync(
                    embConfig,
                    queryItems,
                    catalogFoods,
                    catalogRecipes,
                    cancellationToken
                );
                return new Stage3CatalogSearchResult(vectorResults, UsedEmbeddings: true);
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
                    "Embedding search failed on model '{ModelTag}'; falling back to Stage 03a search terms + lexical catalog search.",
                    embConfig.TextModel
                );
            }
        }

        var fallbackResults = await SearchWithLexicalFallbackAsync(
            config,
            queryItems,
            catalogFoods,
            catalogRecipes,
            cancellationToken
        );
        return new Stage3CatalogSearchResult(fallbackResults, UsedEmbeddings: false);
    }

    private async Task<Dictionary<int, Stage3ItemCandidatesDto>> SearchWithEmbeddingsAsync(
        EffectiveLlmConnectionConfig embConfig,
        IReadOnlyList<Stage3SearchQueryItem> queryItems,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogRecipeLookupItem> catalogRecipes,
        CancellationToken cancellationToken
    )
    {
        var modelTag = Truncate(embConfig.TextModel!.Trim(), MaxModelTagLength);

        var foodSearchTerms = new List<(CatalogFoodLookupItem Food, List<string> Terms)>(
            catalogFoods.Count
        );
        var recipeSearchTerms = new List<(CatalogRecipeLookupItem Recipe, string Term)>(
            catalogRecipes.Count
        );
        var neededCatalogTexts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var food in catalogFoods)
        {
            var terms = new List<string>();
            var normName = NormalizeForEmbedding(food.Name);
            if (!string.IsNullOrWhiteSpace(normName))
            {
                terms.Add(normName);
                neededCatalogTexts.Add(normName);
            }

            foreach (var alias in food.NormalizedAliases)
            {
                var normAlias = NormalizeForEmbedding(alias);
                if (
                    !string.IsNullOrWhiteSpace(normAlias)
                    && !terms.Contains(normAlias, StringComparer.Ordinal)
                )
                {
                    terms.Add(normAlias);
                    neededCatalogTexts.Add(normAlias);
                }
            }

            if (terms.Count > 0)
            {
                foodSearchTerms.Add((food, terms));
            }
        }

        foreach (var recipe in catalogRecipes)
        {
            var normTitle = NormalizeForEmbedding(recipe.Title);
            if (!string.IsNullOrWhiteSpace(normTitle))
            {
                recipeSearchTerms.Add((recipe, normTitle));
                neededCatalogTexts.Add(normTitle);
            }
        }

        var existingRows = await dbContext
            .CatalogEmbeddings.AsNoTracking()
            .Where(e => e.ModelTag == modelTag)
            .ToListAsync(cancellationToken);

        var vectorsByText = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var row in existingRows)
        {
            if (row.Dimensions > 0 && row.VectorBytes.Length == row.Dimensions * sizeof(float))
            {
                var vec = MemoryMarshal.Cast<byte, float>(row.VectorBytes.AsSpan()).ToArray();
                vectorsByText[row.NormalizedText] = vec;
            }
        }

        var missingTexts = neededCatalogTexts.Where(t => !vectorsByText.ContainsKey(t)).ToList();

        if (missingTexts.Count > 0)
        {
            var newVectors = await llmClient.GenerateEmbeddingsAsync(
                embConfig,
                missingTexts,
                cancellationToken
            );

            var addedEntities = new List<CatalogEmbedding>();
            var trackedLocalTexts = dbContext
                .CatalogEmbeddings.Local.Where(e =>
                    string.Equals(e.ModelTag, modelTag, StringComparison.Ordinal)
                )
                .Select(e => e.NormalizedText)
                .ToHashSet(StringComparer.Ordinal);

            for (var i = 0; i < missingTexts.Count; i++)
            {
                var text = missingTexts[i];
                var vec = newVectors[i];
                vectorsByText[text] = vec;

                if (trackedLocalTexts.Add(text))
                {
                    var entity = new CatalogEmbedding
                    {
                        ModelTag = modelTag,
                        NormalizedText = text,
                        Dimensions = vec.Length,
                        VectorBytes = MemoryMarshal.AsBytes(vec.AsSpan()).ToArray(),
                    };
                    addedEntities.Add(entity);
                    dbContext.CatalogEmbeddings.Add(entity);
                }
            }

            if (addedEntities.Count > 0)
            {
                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex)
                {
                    logger?.LogDebug(
                        ex,
                        "Concurrent insert on CatalogEmbeddings for model '{ModelTag}'; detaching duplicate entries.",
                        modelTag
                    );
                    foreach (var entry in addedEntities)
                    {
                        dbContext.Entry(entry).State = EntityState.Detached;
                    }
                }
            }
        }

        var queryFormsByKey = new Dictionary<int, List<string>>(queryItems.Count);
        var uniqueQueryTexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in queryItems)
        {
            var forms = BuildDeterministicSearchTerms(q)
                .Select(NormalizeForEmbedding)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            queryFormsByKey[q.Key] = forms;
            foreach (var f in forms)
            {
                uniqueQueryTexts.Add(f);
            }
        }

        var queryTextList = uniqueQueryTexts.ToList();
        var queryVectorsByText = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (queryTextList.Count > 0)
        {
            var qVectors = await llmClient.GenerateEmbeddingsAsync(
                embConfig,
                queryTextList,
                cancellationToken
            );
            for (var i = 0; i < queryTextList.Count; i++)
            {
                queryVectorsByText[queryTextList[i]] = qVectors[i];
            }
        }

        var results = new Dictionary<int, Stage3ItemCandidatesDto>(queryItems.Count);
        foreach (var q in queryItems)
        {
            var qForms = queryFormsByKey[q.Key];

            var scoredFoods = new List<Stage3IngredientCandidateDto>(foodSearchTerms.Count);
            foreach (var (food, candTerms) in foodSearchTerms)
            {
                double bestCosine = 0.0;
                double bestLexical = 0.0;
                foreach (var qForm in qForms)
                {
                    queryVectorsByText.TryGetValue(qForm, out var qVec);
                    foreach (var candTerm in candTerms)
                    {
                        var lex = ComputeLexicalSimilarity(qForm, candTerm);
                        if (lex > bestLexical)
                        {
                            bestLexical = lex;
                        }

                        if (qVec is not null && vectorsByText.TryGetValue(candTerm, out var cVec))
                        {
                            var cos = Math.Max(0.0, DotProduct(qVec, cVec));
                            if (cos > bestCosine)
                            {
                                bestCosine = cos;
                            }
                        }
                    }
                }

                var hybrid =
                    bestLexical >= 0.99
                        ? Math.Max(0.99, (0.68 * bestCosine) + (0.32 * bestLexical))
                        : (0.68 * bestCosine) + (0.32 * bestLexical);
                if (hybrid >= 0.25)
                {
                    scoredFoods.Add(
                        new Stage3IngredientCandidateDto(
                            food.Id,
                            food.Name,
                            food.Category ?? FoodCategory.Other,
                            food.IsPantryStaple,
                            Math.Round(hybrid, 4),
                            "hybrid_vector"
                        )
                    );
                }
            }

            var topFoods = scoredFoods
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Name.Length)
                .Take(MaxFoodCandidates)
                .ToList();

            var scoredRecipes = new List<Stage3RecipeCandidateDto>(recipeSearchTerms.Count);
            foreach (var (recipe, normTitle) in recipeSearchTerms)
            {
                double bestCosine = 0.0;
                double bestLexical = 0.0;
                foreach (var qForm in qForms)
                {
                    var lex = ComputeLexicalSimilarity(qForm, normTitle);
                    if (lex > bestLexical)
                    {
                        bestLexical = lex;
                    }

                    if (
                        queryVectorsByText.TryGetValue(qForm, out var qVec)
                        && vectorsByText.TryGetValue(normTitle, out var rVec)
                    )
                    {
                        var cos = Math.Max(0.0, DotProduct(qVec, rVec));
                        if (cos > bestCosine)
                        {
                            bestCosine = cos;
                        }
                    }
                }

                var hybrid =
                    bestLexical >= 0.99
                        ? Math.Max(0.99, (0.65 * bestCosine) + (0.35 * bestLexical))
                        : (0.65 * bestCosine) + (0.35 * bestLexical);
                if (hybrid >= 0.45)
                {
                    scoredRecipes.Add(
                        new Stage3RecipeCandidateDto(
                            recipe.Id,
                            recipe.Title,
                            Math.Round(hybrid, 4),
                            "hybrid_vector"
                        )
                    );
                }
            }

            var topRecipes = scoredRecipes
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Title.Length)
                .Take(MaxRecipeCandidates)
                .ToList();

            results[q.Key] = new Stage3ItemCandidatesDto(topFoods, topRecipes);
        }

        return results;
    }

    private async Task<Dictionary<int, Stage3ItemCandidatesDto>> SearchWithLexicalFallbackAsync(
        EffectiveLlmConnectionConfig config,
        IReadOnlyList<Stage3SearchQueryItem> queryItems,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogRecipeLookupItem> catalogRecipes,
        CancellationToken cancellationToken
    )
    {
        var searchPrompt = IngestionPromptLoader.LoadPrompt(
            IngestionPromptLoader.Stage3SearchTerms
        );
        var hasTextModel = !string.IsNullOrWhiteSpace(config.TextModel);
        var results = new Dictionary<int, Stage3ItemCandidatesDto>(queryItems.Count);

        var foodSearchTerms = catalogFoods
            .Select(food =>
            {
                var terms = new List<string>();
                var baseNorm = NormalizeForEmbedding(food.Name);
                if (!string.IsNullOrWhiteSpace(baseNorm))
                {
                    terms.Add(baseNorm);
                }
                foreach (var alias in food.NormalizedAliases)
                {
                    var normAlias = NormalizeForEmbedding(alias);
                    if (
                        !string.IsNullOrWhiteSpace(normAlias)
                        && !terms.Contains(normAlias, StringComparer.Ordinal)
                    )
                    {
                        terms.Add(normAlias);
                    }
                }
                return (Food: food, Terms: terms);
            })
            .ToList();

        var recipeSearchTerms = catalogRecipes
            .Select(r => (Recipe: r, NormTitle: NormalizeForEmbedding(r.Title)))
            .Where(x => !string.IsNullOrWhiteSpace(x.NormTitle))
            .ToList();

        foreach (var q in queryItems)
        {
            var searchTerms = BuildDeterministicSearchTerms(q).ToList();

            if (hasTextModel)
            {
                var payloadJson = JsonSerializer.Serialize(
                    new
                    {
                        name = q.Name,
                        unit = q.Unit,
                        prepNote = q.PrepNote,
                    }
                );
                var stageRes = await llmClient.ExecuteStructuredStageAsync<Stage3SearchTermsDto>(
                    config,
                    stageName: "stage3a_ingredient_search_terms",
                    messages: [new("system", searchPrompt), new("user", payloadJson)],
                    jsonSchema: MultiStageSchemas.Stage3SearchTermsSchema,
                    overrideThinking: false,
                    maxTokens: 1024,
                    cancellationToken: cancellationToken
                );

                if (stageRes.Parsed?.SearchTerms is { Count: > 0 } llmTerms)
                {
                    foreach (var term in llmTerms)
                    {
                        if (
                            !string.IsNullOrWhiteSpace(term)
                            && !searchTerms.Contains(term.Trim(), StringComparer.OrdinalIgnoreCase)
                        )
                        {
                            searchTerms.Add(term.Trim());
                        }
                    }
                }
            }

            var normSearchTerms = searchTerms
                .Select(NormalizeForEmbedding)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var scoredFoods = new List<Stage3IngredientCandidateDto>();
            foreach (var (food, candTerms) in foodSearchTerms)
            {
                double best = 0.0;
                foreach (var qTerm in normSearchTerms)
                {
                    foreach (var cTerm in candTerms)
                    {
                        var sim = ComputeLexicalSimilarity(qTerm, cTerm);
                        if (sim > best)
                        {
                            best = sim;
                        }
                    }
                }

                if (best >= 0.25)
                {
                    scoredFoods.Add(
                        new Stage3IngredientCandidateDto(
                            food.Id,
                            food.Name,
                            food.Category ?? FoodCategory.Other,
                            food.IsPantryStaple,
                            Math.Round(best, 4),
                            "lexical_03a"
                        )
                    );
                }
            }

            var topFoods = scoredFoods
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Name.Length)
                .Take(MaxFoodCandidates)
                .ToList();

            var scoredRecipes = new List<Stage3RecipeCandidateDto>();
            foreach (var (recipe, normTitle) in recipeSearchTerms)
            {
                double best = 0.0;
                foreach (var qTerm in normSearchTerms)
                {
                    var sim = ComputeLexicalSimilarity(qTerm, normTitle);
                    if (sim > best)
                    {
                        best = sim;
                    }
                }

                if (best >= 0.40)
                {
                    scoredRecipes.Add(
                        new Stage3RecipeCandidateDto(
                            recipe.Id,
                            recipe.Title,
                            Math.Round(best, 4),
                            "lexical_03a"
                        )
                    );
                }
            }

            var topRecipes = scoredRecipes
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.Title.Length)
                .Take(MaxRecipeCandidates)
                .ToList();

            results[q.Key] = new Stage3ItemCandidatesDto(topFoods, topRecipes);
        }

        return results;
    }

    internal static IReadOnlyList<string> BuildDeterministicSearchTerms(Stage3SearchQueryItem item)
    {
        var results = new List<string>();
        void AddIfNew(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return;
            }
            var trimmed = s.Trim();
            if (!results.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(trimmed);
            }
        }

        var cleanRaw =
            RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(item.Name) ?? item.Name;
        AddIfNew(item.Name);
        AddIfNew(cleanRaw);
        AddIfNew(FoodNameSanitizer.NormalizeCanonicalFoodName(cleanRaw));

        var singular = string.Join(
            ' ',
            RecipeUnitAndSectionHelpers
                .NormalizeLookupKey(cleanRaw)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(RecipeUnitAndSectionHelpers.Singularize)
        );
        AddIfNew(singular);
        return results;
    }

    internal static double ComputeLexicalSimilarity(string aRaw, string bRaw)
    {
        var a = RecipeUnitAndSectionHelpers.NormalizeLookupKey(aRaw);
        var b = RecipeUnitAndSectionHelpers.NormalizeLookupKey(bRaw);
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0.0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        var singA = string.Join(
            ' ',
            a.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(RecipeUnitAndSectionHelpers.Singularize)
        );
        var singB = string.Join(
            ' ',
            b.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(RecipeUnitAndSectionHelpers.Singularize)
        );
        if (string.Equals(singA, singB, StringComparison.Ordinal))
        {
            return 0.98;
        }

        var setA = new HashSet<string>(
            singA.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal
        );
        var setB = new HashSet<string>(
            singB.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal
        );

        var intersection = setA.Count(t => setB.Contains(t));
        var union = setA.Count + setB.Count - intersection;
        var jaccard = union > 0 ? (double)intersection / union : 0.0;
        var minCount = Math.Min(setA.Count, setB.Count);
        var overlap = minCount > 0 ? (double)intersection / minCount : 0.0;

        var maxLen = Math.Max(singA.Length, singB.Length);
        var levSimilarity =
            maxLen > 0 ? 1.0 - ((double)LevenshteinDistance(singA, singB) / maxLen) : 0.0;

        if (intersection == 0 && levSimilarity < 0.65)
        {
            return Math.Min(0.45, levSimilarity * 0.5);
        }

        return Math.Clamp((0.35 * jaccard) + (0.35 * overlap) + (0.30 * levSimilarity), 0.0, 0.97);
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

    private static string NormalizeForEmbedding(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var collapsed = WhitespaceCollapseRegex().Replace(value.Trim(), " ").ToLowerInvariant();
        return Truncate(collapsed, MaxNormalizedTextLength);
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length > maxLen ? value[..maxLen] : value;

    private static int LevenshteinDistance(string s, string t)
    {
        if (s.Length == 0)
        {
            return t.Length;
        }
        if (t.Length == 0)
        {
            return s.Length;
        }

        var prev = new int[t.Length + 1];
        var curr = new int[t.Length + 1];
        for (var j = 0; j <= t.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= s.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[t.Length];
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceCollapseRegex();
}
