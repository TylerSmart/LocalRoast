using System.Text.Json;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;
using App.Data;
using IngestionPoc.Models;
using Microsoft.EntityFrameworkCore;

namespace IngestionPoc.Services;

public sealed class CatalogSnapshot
{
    public List<CatalogFoodRow> FoodItems { get; } = [];
    public List<CatalogRecipeRow> Recipes { get; } = [];
    public bool LoadedFromDatabase { get; init; }
    public Dictionary<Guid, List<float[]>> FoodVectors { get; } = [];
    public Dictionary<Guid, float[]> RecipeVectors { get; } = [];
    public Dictionary<string, float[]> VectorCache { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record CatalogFoodRow(
    Guid Id,
    string Name,
    string NormalizedName,
    FoodCategory Category,
    bool IsPantryStaple,
    List<string> Aliases
);

public sealed record CatalogRecipeRow(Guid Id, string Title, string Slug);

public sealed class CatalogSearchService
{
    public static AppDbContext CreateReadOnlyDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
        return new AppDbContext(options);
    }

    public static async Task<PocLlmConfig?> TryLoadDefaultLlmConfigAsync(
        string connectionString,
        string? modelFilter = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var db = CreateReadOnlyDbContext(connectionString);

        if (!string.IsNullOrWhiteSpace(modelFilter))
        {
            var term = modelFilter.Trim();
            var matchedModel = await db
                .LlmModelConfigurations.Include(m => m.Connection)
                .Where(m => m.Model == term || m.Name == term || m.Model.Contains(term))
                .OrderByDescending(m => m.Model == term || m.Name == term)
                .FirstOrDefaultAsync(cancellationToken);

            if (matchedModel?.Connection is not null)
            {
                return new PocLlmConfig(
                    Provider: matchedModel.Connection.Provider,
                    BaseUrl: matchedModel.Connection.BaseUrl,
                    ApiKey: matchedModel.Connection.ApiKey,
                    TextModel: matchedModel.Model,
                    VisionModel: null,
                    EnableThinking: matchedModel.EnableThinking,
                    AuxThinkingOverride: null,
                    Temperature: matchedModel.Temperature,
                    TopP: matchedModel.TopP,
                    TopK: matchedModel.TopK,
                    ContextWindowTokens: matchedModel.ContextWindowTokens,
                    RequestTimeoutSeconds: matchedModel.RequestTimeoutSeconds,
                    OllamaGpuLayers: matchedModel.OllamaGpuLayers,
                    OllamaCpuOnly: matchedModel.Connection.OllamaCpuOnly,
                    OllamaKeepAlive: matchedModel.Connection.OllamaKeepAlive
                );
            }
        }

        var settings = await db
            .HouseholdLlmSettings.Include(s => s.TextModelConfiguration!)
                .ThenInclude(m => m.Connection)
            .Include(s => s.VisionModelConfiguration!)
                .ThenInclude(m => m.Connection)
            .FirstOrDefaultAsync(cancellationToken);

        if (settings is null)
        {
            return null;
        }

        var textCfg = settings.TextModelConfiguration;
        var conn = textCfg?.Connection;

        return new PocLlmConfig(
            Provider: conn?.Provider ?? settings.Provider,
            BaseUrl: conn?.BaseUrl ?? settings.BaseUrl,
            ApiKey: conn?.ApiKey ?? settings.ApiKey,
            TextModel: modelFilter ?? textCfg?.Model ?? settings.TextModel ?? "gemma4:e4b",
            VisionModel: settings.VisionModelConfiguration?.Model ?? settings.VisionModel,
            EnableThinking: textCfg?.EnableThinking ?? settings.EnableThinking,
            AuxThinkingOverride: null,
            Temperature: textCfg?.Temperature ?? settings.Temperature,
            TopP: textCfg?.TopP ?? settings.TopP,
            TopK: textCfg?.TopK ?? settings.TopK,
            ContextWindowTokens: textCfg?.ContextWindowTokens ?? settings.ContextWindowTokens,
            RequestTimeoutSeconds: textCfg?.RequestTimeoutSeconds ?? settings.RequestTimeoutSeconds,
            OllamaGpuLayers: textCfg?.OllamaGpuLayers,
            OllamaCpuOnly: conn?.OllamaCpuOnly ?? settings.OllamaCpuOnly,
            OllamaKeepAlive: conn?.OllamaKeepAlive ?? settings.OllamaKeepAlive
        );
    }

    public static async Task<CatalogSnapshot> LoadSnapshotAsync(
        string? connectionString,
        CancellationToken cancellationToken = default
    )
    {
        var snapshot = new CatalogSnapshot { LoadedFromDatabase = false };
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return snapshot;
        }

        await using var db = CreateReadOnlyDbContext(connectionString);

        var foods = await db
            .FoodItems.Include(f => f.Aliases)
            .Select(f => new CatalogFoodRow(
                f.Id,
                f.Name,
                f.NormalizedName,
                f.Category ?? FoodCategory.Other,
                f.IsPantryStaple,
                f.Aliases.Select(a => a.Alias).ToList()
            ))
            .ToListAsync(cancellationToken);

        var recipes = await db
            .Recipes.Where(r => r.Status != RecipeStatus.Archived)
            .Select(r => new CatalogRecipeRow(r.Id, r.Title, r.Slug))
            .ToListAsync(cancellationToken);

        var dbSnapshot = new CatalogSnapshot { LoadedFromDatabase = true };
        dbSnapshot.FoodItems.AddRange(foods);
        dbSnapshot.Recipes.AddRange(recipes);
        return dbSnapshot;
    }

    public static DbSearchCandidates Search(
        CatalogSnapshot catalog,
        IReadOnlyList<string> searchTerms,
        string currentRecipeTitle,
        int maxFoodCandidates = 12,
        int maxRecipeCandidates = 5
    )
    {
        var expandedTerms = BuildExpandedTerms(searchTerms);
        var currentTitleKey = RecipeUnitAndSectionHelpers.NormalizeRecipeLookupKey(
            currentRecipeTitle
        );

        var scoredFoods = new List<FoodItemCandidate>();
        foreach (var food in catalog.FoodItems)
        {
            var (score, reason) = ScoreFoodItem(food, expandedTerms);
            if (score >= 0.30)
            {
                scoredFoods.Add(
                    new FoodItemCandidate(
                        food.Id,
                        food.Name,
                        food.NormalizedName,
                        food.Category,
                        food.IsPantryStaple,
                        food.Aliases,
                        Math.Round(score, 4),
                        reason
                    )
                );
            }
        }

        var topFoods = scoredFoods
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Name.Length)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxFoodCandidates)
            .ToList();

        var scoredRecipes = new List<RecipeCandidate>();
        foreach (var recipe in catalog.Recipes)
        {
            var recipeKey = RecipeUnitAndSectionHelpers.NormalizeRecipeLookupKey(recipe.Title);
            if (
                !string.IsNullOrEmpty(currentTitleKey)
                && string.Equals(recipeKey, currentTitleKey, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var (score, reason) = ScoreRecipe(recipe, expandedTerms);
            if (score >= 0.45)
            {
                scoredRecipes.Add(
                    new RecipeCandidate(
                        recipe.Id,
                        recipe.Title,
                        recipe.Slug,
                        Math.Round(score, 4),
                        reason
                    )
                );
            }
        }

        var topRecipes = scoredRecipes
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title.Length)
            .Take(maxRecipeCandidates)
            .ToList();

        return new DbSearchCandidates(expandedTerms, topFoods, topRecipes);
    }

    public static async Task<(
        int EmbeddedCount,
        long ElapsedMs,
        string? Error
    )> EnsureCatalogEmbeddingsAsync(
        CatalogSnapshot catalog,
        LlmRunnerClient llmClient,
        PocLlmConfig config,
        string? cacheDirectory,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(config.EmbedModel))
        {
            return (0, 0, null);
        }

        string? cachePath = null;
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            Directory.CreateDirectory(cacheDirectory);
            var safeModel = string.Concat(
                config
                    .EmbedModel.Trim()
                    .Select(c =>
                        Path.GetInvalidFileNameChars().Contains(c) || c == ':' || c == '/' ? '-' : c
                    )
            );
            cachePath = Path.Combine(cacheDirectory, $"catalog-embeddings_{safeModel}.json");
            if (File.Exists(cachePath))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<Dictionary<string, float[]>>(
                        await File.ReadAllTextAsync(cachePath, cancellationToken)
                    );
                    if (cached is not null)
                    {
                        foreach (var (k, v) in cached)
                        {
                            catalog.VectorCache[k] = v;
                        }
                    }
                }
                catch
                {
                    // Ignore corrupt cache file and rebuild cleanly
                }
            }
        }

        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var food in catalog.FoodItems)
        {
            if (!catalog.VectorCache.ContainsKey(food.Name))
            {
                missing.Add(food.Name);
            }
            foreach (var alias in food.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias) && !catalog.VectorCache.ContainsKey(alias))
                {
                    missing.Add(alias);
                }
            }
        }

        foreach (var recipe in catalog.Recipes)
        {
            if (!catalog.VectorCache.ContainsKey(recipe.Title))
            {
                missing.Add(recipe.Title);
            }
        }

        long totalElapsedMs = 0;
        var embeddedCount = 0;
        string? lastError = null;
        var missingList = missing.ToList();

        const int batchSize = 64;
        for (var i = 0; i < missingList.Count; i += batchSize)
        {
            var chunk = missingList.Skip(i).Take(batchSize).ToList();
            var res = await llmClient.GenerateEmbeddingsAsync(config, chunk, cancellationToken);
            totalElapsedMs += res.ElapsedMs;
            if (res.Error is not null)
            {
                lastError = res.Error;
                break;
            }

            for (var j = 0; j < chunk.Count && j < res.Vectors.Count; j++)
            {
                catalog.VectorCache[chunk[j]] = res.Vectors[j];
                embeddedCount++;
            }
        }

        PopulateCatalogVectorMaps(catalog);

        if (cachePath is not null && embeddedCount > 0)
        {
            try
            {
                await File.WriteAllTextAsync(
                    cachePath,
                    JsonSerializer.Serialize(catalog.VectorCache),
                    cancellationToken
                );
            }
            catch
            {
                // Ignore non-fatal cache persistence errors
            }
        }

        return (embeddedCount, totalElapsedMs, lastError);
    }

    private static void PopulateCatalogVectorMaps(CatalogSnapshot catalog)
    {
        foreach (var food in catalog.FoodItems)
        {
            var list = new List<float[]>();
            if (catalog.VectorCache.TryGetValue(food.Name, out var mainVec))
            {
                list.Add(mainVec);
            }
            foreach (var alias in food.Aliases)
            {
                if (catalog.VectorCache.TryGetValue(alias, out var aliasVec))
                {
                    list.Add(aliasVec);
                }
            }
            if (list.Count > 0)
            {
                catalog.FoodVectors[food.Id] = list;
            }
        }

        foreach (var recipe in catalog.Recipes)
        {
            if (catalog.VectorCache.TryGetValue(recipe.Title, out var titleVec))
            {
                catalog.RecipeVectors[recipe.Id] = titleVec;
            }
        }
    }

    public static DbSearchCandidates SearchHybrid(
        CatalogSnapshot catalog,
        string ingredientName,
        float[]? queryVector,
        string currentRecipeTitle,
        string? ingredientUnit = null,
        string? ingredientPrepNote = null,
        int maxFoodCandidates = 10,
        int maxRecipeCandidates = 5
    )
    {
        var expandedTerms = BuildExpandedTerms([ingredientName]);
        var currentTitleKey = RecipeUnitAndSectionHelpers.NormalizeRecipeLookupKey(
            currentRecipeTitle
        );

        var scoredFoods = new List<FoodItemCandidate>();
        foreach (var food in catalog.FoodItems)
        {
            var (lexScore, lexReason) = ScoreFoodItem(food, expandedTerms);
            var vecScore = 0.0;
            if (
                queryVector is not null
                && catalog.FoodVectors.TryGetValue(food.Id, out var candVecs)
            )
            {
                vecScore = candVecs.Max(v => CosineDot(queryVector, v));
            }

            double finalScore;
            string reason;
            if (lexScore >= 0.98)
            {
                finalScore = lexScore;
                reason = vecScore > 0 ? $"{lexReason},vec={vecScore:F2}" : lexReason;
            }
            else if (queryVector is not null && vecScore > 0)
            {
                finalScore = 0.68 * vecScore + 0.32 * lexScore;
                reason =
                    lexScore > 0
                        ? $"hybrid(v={vecScore:F2},{lexReason}={lexScore:F2})"
                        : $"vec={vecScore:F2}";
            }
            else
            {
                finalScore = lexScore;
                reason = lexReason;
            }

            if (finalScore >= 0.42 || lexScore >= 0.35)
            {
                scoredFoods.Add(
                    new FoodItemCandidate(
                        food.Id,
                        food.Name,
                        food.NormalizedName,
                        food.Category,
                        food.IsPantryStaple,
                        food.Aliases,
                        Math.Round(finalScore, 4),
                        reason
                    )
                );
            }
        }

        var topFoods = scoredFoods
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Name.Length)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxFoodCandidates)
            .ToList();

        var looksLikeSubRecipe =
            string.Equals(ingredientUnit, "recipe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ingredientUnit, "batch", StringComparison.OrdinalIgnoreCase)
            || (ingredientPrepNote?.Contains("recipe", StringComparison.OrdinalIgnoreCase) ?? false)
            || ingredientName.Contains("recipe", StringComparison.OrdinalIgnoreCase);

        var scoredRecipes = new List<RecipeCandidate>();
        foreach (var recipe in catalog.Recipes)
        {
            var recipeKey = RecipeUnitAndSectionHelpers.NormalizeRecipeLookupKey(recipe.Title);
            if (
                !string.IsNullOrEmpty(currentTitleKey)
                && string.Equals(recipeKey, currentTitleKey, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var (lexScore, lexReason) = ScoreRecipe(recipe, expandedTerms);
            var vecScore =
                queryVector is not null
                && catalog.RecipeVectors.TryGetValue(recipe.Id, out var rVec)
                    ? CosineDot(queryVector, rVec)
                    : 0.0;

            double finalScore;
            string reason;
            if (lexScore >= 0.95)
            {
                finalScore = lexScore;
                reason = vecScore > 0 ? $"{lexReason},vec={vecScore:F2}" : lexReason;
            }
            else if (queryVector is not null && vecScore > 0)
            {
                finalScore = 0.65 * vecScore + 0.35 * lexScore;
                reason =
                    lexScore > 0
                        ? $"hybrid-rec(v={vecScore:F2},{lexReason}={lexScore:F2})"
                        : $"vec-rec={vecScore:F2}";
            }
            else
            {
                finalScore = lexScore;
                reason = lexReason;
            }

            var includeRecipe = looksLikeSubRecipe
                ? (finalScore >= 0.48 || lexScore >= 0.45)
                : (
                    lexScore >= 0.85
                    || (
                        ingredientName.Trim().Length >= 10 && finalScore >= 0.68 && lexScore >= 0.55
                    )
                );

            if (includeRecipe)
            {
                scoredRecipes.Add(
                    new RecipeCandidate(
                        recipe.Id,
                        recipe.Title,
                        recipe.Slug,
                        Math.Round(finalScore, 4),
                        reason
                    )
                );
            }
        }

        var topRecipes = scoredRecipes
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title.Length)
            .Take(maxRecipeCandidates)
            .ToList();

        return new DbSearchCandidates(expandedTerms, topFoods, topRecipes);
    }

    private static double CosineDot(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < len; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    public static CatalogFoodRow? RegisterSessionDecision(
        CatalogSnapshot catalog,
        Stage3MatchDecisionDto decision,
        float[]? vector = null
    )
    {
        if (
            !string.Equals(decision.MatchType, "new_ingredient", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(decision.Name)
        )
        {
            return null;
        }

        var cleanName = decision.Name.Trim();
        var norm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(cleanName);
        if (
            catalog.FoodItems.Any(f =>
                string.Equals(f.NormalizedName, norm, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return null;
        }

        var category = FoodCategories.Parse(decision.Category) ?? FoodCategory.Other;
        var row = new CatalogFoodRow(
            Guid.NewGuid(),
            cleanName,
            norm,
            category,
            IsPantryStaple: false,
            Aliases: []
        );
        catalog.FoodItems.Add(row);
        if (vector is not null)
        {
            catalog.VectorCache[cleanName] = vector;
            catalog.FoodVectors[row.Id] = [vector];
        }
        return row;
    }

    public static CatalogRecipeRow? RegisterSessionRecipe(
        CatalogSnapshot catalog,
        string title,
        float[]? vector = null
    )
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var cleanTitle = title.Trim();
        var key = RecipeUnitAndSectionHelpers.NormalizeRecipeLookupKey(cleanTitle);
        if (
            catalog.Recipes.Any(r =>
                string.Equals(
                    RecipeUnitAndSectionHelpers.NormalizeRecipeLookupKey(r.Title),
                    key,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            return null;
        }

        var slug = RecipeUnitAndSectionHelpers
            .NormalizeRecipeLookupKey(cleanTitle)
            .Replace(' ', '-');
        var row = new CatalogRecipeRow(Guid.NewGuid(), cleanTitle, slug);
        catalog.Recipes.Add(row);
        if (vector is not null)
        {
            catalog.VectorCache[cleanTitle] = vector;
            catalog.RecipeVectors[row.Id] = vector;
        }
        return row;
    }

    private static List<string> BuildExpandedTerms(IReadOnlyList<string> rawTerms)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void Add(string? term)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return;
            }
            var t = term.Trim();
            if (t.Length > 0 && seen.Add(t))
            {
                list.Add(t);
            }
        }

        foreach (var raw in rawTerms)
        {
            Add(raw);
            var norm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(raw);
            Add(norm);
            var sanitized = FoodNameSanitizer.NormalizeCanonicalFoodName(raw);
            Add(sanitized);
            if (!string.IsNullOrWhiteSpace(norm))
            {
                var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0)
                {
                    var singularLast = string.Join(
                        ' ',
                        words
                            .Take(words.Length - 1)
                            .Append(RecipeUnitAndSectionHelpers.Singularize(words[^1]))
                    );
                    Add(singularLast);
                }
            }
        }

        return list;
    }

    private static (double Score, string Reason) ScoreFoodItem(
        CatalogFoodRow food,
        IReadOnlyList<string> terms
    )
    {
        var foodNorm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(food.Name);
        var foodTokens = Tokenize(foodNorm);
        var bestScore = 0.0;
        var bestReason = string.Empty;

        foreach (var term in terms)
        {
            var termNorm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(term);
            if (string.IsNullOrEmpty(termNorm))
            {
                continue;
            }

            if (string.Equals(foodNorm, termNorm, StringComparison.OrdinalIgnoreCase))
            {
                return (1.0, $"exact:{termNorm}");
            }

            foreach (var alias in food.Aliases)
            {
                var aliasNorm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(alias);
                if (string.Equals(aliasNorm, termNorm, StringComparison.OrdinalIgnoreCase))
                {
                    return (0.98, $"alias:{aliasNorm}");
                }
            }

            if (
                foodNorm.Contains(termNorm, StringComparison.OrdinalIgnoreCase)
                || termNorm.Contains(foodNorm, StringComparison.OrdinalIgnoreCase)
            )
            {
                var minLen = Math.Min(foodNorm.Length, termNorm.Length);
                var maxLen = Math.Max(foodNorm.Length, termNorm.Length);
                var s = 0.65 + 0.28 * ((double)minLen / maxLen);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestReason = $"substring:{termNorm}";
                }
            }

            var termTokens = Tokenize(termNorm);
            if (termTokens.Count > 0 && foodTokens.Count > 0)
            {
                var intersection = termTokens
                    .Intersect(foodTokens, StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (intersection > 0)
                {
                    var union = termTokens
                        .Union(foodTokens, StringComparer.OrdinalIgnoreCase)
                        .Count();
                    var jaccard = (double)intersection / union;
                    var s = 0.40 + 0.50 * jaccard;
                    if (s > bestScore)
                    {
                        bestScore = s;
                        bestReason = $"tokens:{termNorm}";
                    }
                }
            }
        }

        return (bestScore, bestReason);
    }

    private static (double Score, string Reason) ScoreRecipe(
        CatalogRecipeRow recipe,
        IReadOnlyList<string> terms
    )
    {
        var titleNorm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(recipe.Title);
        var titleTokens = Tokenize(titleNorm);
        var bestScore = 0.0;
        var bestReason = string.Empty;

        foreach (var term in terms)
        {
            var termNorm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(term);
            if (string.IsNullOrEmpty(termNorm))
            {
                continue;
            }

            if (string.Equals(titleNorm, termNorm, StringComparison.OrdinalIgnoreCase))
            {
                return (1.0, $"exact-title:{termNorm}");
            }

            if (
                termNorm.Length >= 6
                && (
                    titleNorm.Contains(termNorm, StringComparison.OrdinalIgnoreCase)
                    || termNorm.Contains(titleNorm, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                var minLen = Math.Min(titleNorm.Length, termNorm.Length);
                var maxLen = Math.Max(titleNorm.Length, termNorm.Length);
                var s = 0.60 + 0.35 * ((double)minLen / maxLen);
                if (s > bestScore)
                {
                    bestScore = s;
                    bestReason = $"substring-title:{termNorm}";
                }
            }

            var termTokens = Tokenize(termNorm);
            if (termTokens.Count > 0 && titleTokens.Count > 0)
            {
                var intersection = termTokens
                    .Intersect(titleTokens, StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (intersection > 0)
                {
                    var union = termTokens
                        .Union(titleTokens, StringComparer.OrdinalIgnoreCase)
                        .Count();
                    var jaccard = (double)intersection / union;
                    var s = 0.35 + 0.55 * jaccard;
                    if (s > bestScore)
                    {
                        bestScore = s;
                        bestReason = $"tokens-title:{termNorm}";
                    }
                }
            }
        }

        return (bestScore, bestReason);
    }

    private static HashSet<string> Tokenize(string normalized)
    {
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(RecipeUnitAndSectionHelpers.Singularize)
            .Where(t => t.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
