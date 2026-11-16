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

    public static void RegisterSessionDecision(
        CatalogSnapshot catalog,
        Stage3MatchDecisionDto decision
    )
    {
        if (
            !string.Equals(decision.MatchType, "new_ingredient", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(decision.Name)
        )
        {
            return;
        }

        var cleanName = decision.Name.Trim();
        var norm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(cleanName);
        if (
            catalog.FoodItems.Any(f =>
                string.Equals(f.NormalizedName, norm, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return;
        }

        var category = FoodCategories.Parse(decision.Category) ?? FoodCategory.Other;
        catalog.FoodItems.Add(
            new CatalogFoodRow(
                Guid.NewGuid(),
                cleanName,
                norm,
                category,
                IsPantryStaple: false,
                Aliases: []
            )
        );
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
