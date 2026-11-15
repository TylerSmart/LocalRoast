using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Ingestion;

/// <summary>
/// Stage 3 deterministic 3-tier catalog matcher and disambiguation applier for extracted food
/// items and proposed tags.
/// </summary>
public static class EntityCandidateMatcher
{
    public const double AmbiguousMinScore = 0.55;
    public const double AutoMatchMinScore = 0.92;
    public const double AutoMatchLeadMargin = 0.08;
    public const int MaxCandidatesPerEntity = 5;

    public static EntityMatchResult Match(
        ExtractedRecipeDto recipe,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogTagLookupItem> catalogTags
    )
    {
        var foodIndex = BuildFoodLookupIndex(catalogFoods);
        var tagIndex = BuildTagLookupIndex(catalogTags);

        var ingredientMatches = new Dictionary<string, ResolvedIngredientMatch>(
            StringComparer.OrdinalIgnoreCase
        );
        var ambiguousIngredients = new List<AmbiguousIngredientCandidate>();

        foreach (var ing in recipe.Ingredients ?? [])
        {
            var refId = ing.RefId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(refId))
            {
                continue;
            }

            if (ing.IsRecipeReference)
            {
                ingredientMatches[refId] = new ResolvedIngredientMatch(
                    RefId: refId,
                    MatchedFoodItemId: null,
                    MatchedFoodItemName: null,
                    MatchedCategory: null,
                    IsPantryStaple: false,
                    IsNewFoodItem: false
                );
                continue;
            }

            var rawQuery = !string.IsNullOrWhiteSpace(ing.CanonicalFoodItem)
                ? ing.CanonicalFoodItem
                : (!string.IsNullOrWhiteSpace(ing.DisplayName) ? ing.DisplayName : ing.RawText);

            var resolvedFoodName = RecipeDraftCompiler.ResolveNormalizedCanonicalFoodName(ing);
            if (
                string.Equals(resolvedFoodName, "black pepper", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    FoodNameSanitizer.NormalizeCanonicalFoodName(rawQuery),
                    "pepper",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                rawQuery = "black pepper";
            }

            var exactFood = TryFindExactFood(rawQuery, foodIndex);
            if (exactFood is not null)
            {
                ingredientMatches[refId] = new ResolvedIngredientMatch(
                    RefId: refId,
                    MatchedFoodItemId: exactFood.Id,
                    MatchedFoodItemName: exactFood.Name,
                    MatchedCategory: exactFood.Category,
                    IsPantryStaple: exactFood.IsPantryStaple,
                    IsNewFoodItem: false
                );
                continue;
            }

            var candidates = FindTopFoodCandidates(rawQuery, foodIndex);
            if (ShouldAutoSelectTopCandidate(candidates))
            {
                var best = foodIndex.ById[candidates[0].Id];
                ingredientMatches[refId] = new ResolvedIngredientMatch(
                    RefId: refId,
                    MatchedFoodItemId: best.Id,
                    MatchedFoodItemName: best.Name,
                    MatchedCategory: best.Category,
                    IsPantryStaple: best.IsPantryStaple,
                    IsNewFoodItem: false
                );
                continue;
            }

            ingredientMatches[refId] = new ResolvedIngredientMatch(
                RefId: refId,
                MatchedFoodItemId: null,
                MatchedFoodItemName: null,
                MatchedCategory: null,
                IsPantryStaple: false,
                IsNewFoodItem: true
            );

            if (candidates.Count > 0)
            {
                ambiguousIngredients.Add(
                    new AmbiguousIngredientCandidate(
                        RefId: refId,
                        CanonicalFoodItem: rawQuery?.Trim() ?? string.Empty,
                        RawText: ing.RawText ?? string.Empty,
                        Candidates: candidates
                    )
                );
            }
        }

        var tagMatches = new Dictionary<string, ResolvedTagMatch>(StringComparer.OrdinalIgnoreCase);
        var ambiguousTags = new List<AmbiguousTagCandidate>();

        foreach (var rawTag in recipe.ProposedTags ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawTag))
            {
                continue;
            }

            var tagKey = rawTag.Trim();
            if (
                tagMatches.ContainsKey(tagKey)
                || BuiltInTags.DisallowedGrocerySectionNames.Contains(tagKey)
                || BuiltInTags.DisallowedGenericStaples.Contains(tagKey)
            )
            {
                continue;
            }

            var lookupQuery = ResolveCanonicalTagAlias(tagKey);

            var exactTag = TryFindExactTag(lookupQuery, tagIndex);
            if (exactTag is not null)
            {
                tagMatches[tagKey] = new ResolvedTagMatch(
                    ProposedTag: tagKey,
                    MatchedTagId: exactTag.Id,
                    MatchedTagName: exactTag.Name
                );
                continue;
            }

            var candidates = FindTopTagCandidates(lookupQuery, tagIndex);
            if (ShouldAutoSelectTopCandidate(candidates))
            {
                var best = tagIndex.ById[candidates[0].Id];
                tagMatches[tagKey] = new ResolvedTagMatch(
                    ProposedTag: tagKey,
                    MatchedTagId: best.Id,
                    MatchedTagName: best.Name
                );
                continue;
            }

            tagMatches[tagKey] = new ResolvedTagMatch(
                ProposedTag: tagKey,
                MatchedTagId: null,
                MatchedTagName: null
            );

            if (candidates.Count > 0)
            {
                ambiguousTags.Add(
                    new AmbiguousTagCandidate(ProposedTag: tagKey, Candidates: candidates)
                );
            }
        }

        var profile = RecipeDraftCompiler.AnalyzeDietaryAndCookingProfile(recipe);
        var hasValidVegan =
            !profile.HasMeatOrSeafood
            && !profile.HasDairy
            && !profile.HasEggOrHoney
            && tagMatches.Values.Any(m =>
                string.Equals(m.MatchedTagName, "Vegan", StringComparison.OrdinalIgnoreCase)
            );

        void TryAttachBuiltInTag(string tagName)
        {
            if (
                tagMatches.ContainsKey(tagName)
                || tagMatches.Values.Any(m =>
                    string.Equals(m.MatchedTagName, tagName, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return;
            }

            var tag = TryFindExactTag(tagName, tagIndex);
            if (tag is not null)
            {
                tagMatches[tagName] = new ResolvedTagMatch(
                    ProposedTag: tagName,
                    MatchedTagId: tag.Id,
                    MatchedTagName: tag.Name
                );
            }
        }

        // Every valid Vegan recipe is implicitly Vegetarian and Dairy-Free; attach seeded tags when available.
        if (hasValidVegan)
        {
            TryAttachBuiltInTag("Vegetarian");
            TryAttachBuiltInTag("Dairy-Free");
        }

        // Attach seeded No-Cook tag when the extracted recipe performs no thermal cooking.
        if (profile.IsNoCookCandidate)
        {
            TryAttachBuiltInTag("No-Cook");
        }

        return new EntityMatchResult(
            IngredientMatches: ingredientMatches,
            TagMatches: tagMatches,
            AmbiguousIngredients: ambiguousIngredients,
            AmbiguousTags: ambiguousTags
        );
    }

    public static EntityMatchResult ApplyDisambiguation(
        EntityMatchResult matchResult,
        LlmEntityDisambiguationResult? disambiguation,
        IReadOnlyList<CatalogFoodLookupItem> catalogFoods,
        IReadOnlyList<CatalogTagLookupItem> catalogTags
    )
    {
        if (disambiguation is null)
        {
            return matchResult;
        }

        var updatedIngredients = new Dictionary<string, ResolvedIngredientMatch>(
            matchResult.IngredientMatches,
            StringComparer.OrdinalIgnoreCase
        );
        var updatedTags = new Dictionary<string, ResolvedTagMatch>(
            matchResult.TagMatches,
            StringComparer.OrdinalIgnoreCase
        );

        var foodsById = catalogFoods.ToDictionary(f => f.Id);
        var tagsById = catalogTags.ToDictionary(t => t.Id);

        var allowedFoodIdsByRef = matchResult.AmbiguousIngredients.ToDictionary(
            a => a.RefId,
            a => a.Candidates.Select(c => c.Id).ToHashSet(),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var decision in disambiguation.IngredientDecisions ?? [])
        {
            var refId = decision.RefId?.Trim() ?? string.Empty;
            if (
                string.IsNullOrWhiteSpace(refId)
                || !updatedIngredients.ContainsKey(refId)
                || !allowedFoodIdsByRef.TryGetValue(refId, out var allowedIds)
            )
            {
                continue;
            }

            if (
                decision.MatchedFoodItemId is { } matchedId
                && allowedIds.Contains(matchedId)
                && foodsById.TryGetValue(matchedId, out var food)
            )
            {
                updatedIngredients[refId] = new ResolvedIngredientMatch(
                    RefId: refId,
                    MatchedFoodItemId: food.Id,
                    MatchedFoodItemName: food.Name,
                    MatchedCategory: food.Category,
                    IsPantryStaple: food.IsPantryStaple,
                    IsNewFoodItem: false
                );
            }
        }

        var allowedTagIdsByName = matchResult.AmbiguousTags.ToDictionary(
            a => a.ProposedTag,
            a => a.Candidates.Select(c => c.Id).ToHashSet(),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var decision in disambiguation.TagDecisions ?? [])
        {
            var proposedTag = decision.ProposedTag?.Trim() ?? string.Empty;
            if (
                string.IsNullOrWhiteSpace(proposedTag)
                || !updatedTags.ContainsKey(proposedTag)
                || !allowedTagIdsByName.TryGetValue(proposedTag, out var allowedIds)
            )
            {
                continue;
            }

            if (
                decision.MatchedTagId is { } matchedId
                && allowedIds.Contains(matchedId)
                && tagsById.TryGetValue(matchedId, out var tag)
            )
            {
                updatedTags[proposedTag] = new ResolvedTagMatch(
                    ProposedTag: proposedTag,
                    MatchedTagId: tag.Id,
                    MatchedTagName: tag.Name
                );
            }
        }

        return matchResult with
        {
            IngredientMatches = updatedIngredients,
            TagMatches = updatedTags,
        };
    }

    private static bool ShouldAutoSelectTopCandidate(
        IReadOnlyList<EntityCandidateOption> candidates
    )
    {
        if (candidates.Count == 0 || candidates[0].Score < AutoMatchMinScore)
        {
            return false;
        }

        return candidates.Count == 1
            || (candidates[0].Score - candidates[1].Score) >= AutoMatchLeadMargin;
    }

    private sealed record FoodLookupIndex(
        Dictionary<Guid, CatalogFoodLookupItem> ById,
        Dictionary<string, CatalogFoodLookupItem> ByNormalizedKey,
        IReadOnlyList<(CatalogFoodLookupItem Item, IReadOnlyList<string> SearchTerms)> Entries
    );

    private sealed record TagLookupIndex(
        Dictionary<Guid, CatalogTagLookupItem> ById,
        Dictionary<string, CatalogTagLookupItem> ByNormalizedKey,
        IReadOnlyList<(CatalogTagLookupItem Item, IReadOnlyList<string> SearchTerms)> Entries
    );

    private static FoodLookupIndex BuildFoodLookupIndex(IReadOnlyList<CatalogFoodLookupItem> foods)
    {
        var byId = new Dictionary<Guid, CatalogFoodLookupItem>(foods.Count);
        var byKey = new Dictionary<string, CatalogFoodLookupItem>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(CatalogFoodLookupItem, IReadOnlyList<string>)>(foods.Count);

        foreach (var food in foods)
        {
            byId[food.Id] = food;
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (
                var raw in new[] { food.NormalizedName, food.Name }.Concat(food.NormalizedAliases)
            )
            {
                foreach (var key in ExpandNormalizedKeys(raw))
                {
                    byKey.TryAdd(key, food);
                    terms.Add(key);
                }
            }

            entries.Add((food, terms.ToList()));
        }

        return new FoodLookupIndex(byId, byKey, entries);
    }

    private static TagLookupIndex BuildTagLookupIndex(IReadOnlyList<CatalogTagLookupItem> tags)
    {
        var byId = new Dictionary<Guid, CatalogTagLookupItem>(tags.Count);
        var byKey = new Dictionary<string, CatalogTagLookupItem>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(CatalogTagLookupItem, IReadOnlyList<string>)>(tags.Count);

        foreach (var tag in tags)
        {
            byId[tag.Id] = tag;
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in new[] { tag.NormalizedName, tag.Name, tag.Slug.Replace('-', ' ') })
            {
                foreach (var key in ExpandNormalizedKeys(raw))
                {
                    byKey.TryAdd(key, tag);
                    terms.Add(key);
                }
            }

            entries.Add((tag, terms.ToList()));
        }

        return new TagLookupIndex(byId, byKey, entries);
    }

    private static CatalogFoodLookupItem? TryFindExactFood(string? query, FoodLookupIndex index)
    {
        foreach (var key in ExpandNormalizedKeys(query))
        {
            if (index.ByNormalizedKey.TryGetValue(key, out var item))
            {
                return item;
            }
        }

        return null;
    }

    private static CatalogTagLookupItem? TryFindExactTag(string? query, TagLookupIndex index)
    {
        foreach (var key in ExpandNormalizedKeys(query))
        {
            if (index.ByNormalizedKey.TryGetValue(key, out var item))
            {
                return item;
            }
        }

        return null;
    }

    private static List<EntityCandidateOption> FindTopFoodCandidates(
        string? query,
        FoodLookupIndex index
    )
    {
        var queryForms = ExpandNormalizedKeys(query);
        if (queryForms.Count == 0)
        {
            return [];
        }

        return index
            .Entries.Select(entry => new EntityCandidateOption(
                Id: entry.Item.Id,
                Name: entry.Item.Name,
                Score: ScoreBestTermPair(queryForms, entry.SearchTerms)
            ))
            .Where(opt => opt.Score >= AmbiguousMinScore)
            .OrderByDescending(opt => opt.Score)
            .ThenBy(opt => opt.Name.Length)
            .Take(MaxCandidatesPerEntity)
            .ToList();
    }

    private static List<EntityCandidateOption> FindTopTagCandidates(
        string? query,
        TagLookupIndex index
    )
    {
        var queryForms = ExpandNormalizedKeys(query);
        if (queryForms.Count == 0)
        {
            return [];
        }

        return index
            .Entries.Select(entry => new EntityCandidateOption(
                Id: entry.Item.Id,
                Name: entry.Item.Name,
                Score: ScoreBestTermPair(queryForms, entry.SearchTerms)
            ))
            .Where(opt => opt.Score >= AmbiguousMinScore)
            .OrderByDescending(opt => opt.Score)
            .ThenBy(opt => opt.Name.Length)
            .Take(MaxCandidatesPerEntity)
            .ToList();
    }

    private static string ResolveCanonicalTagAlias(string tagKey)
    {
        if (BuiltInTags.CanonicalAliases.TryGetValue(tagKey, out var alias))
        {
            return alias;
        }

        var display = RecipeUnitAndSectionHelpers.ToTagDisplayCase(tagKey);
        return BuiltInTags.CanonicalAliases.TryGetValue(display, out var displayAlias)
            ? displayAlias
            : tagKey;
    }

    private static List<string> ExpandNormalizedKeys(string? raw)
    {
        var norm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(raw);
        if (string.IsNullOrWhiteSpace(norm))
        {
            return [];
        }

        var keys = new List<string> { norm };
        var canonical = RecipeUnitAndSectionHelpers.NormalizeLookupKey(
            FoodNameSanitizer.NormalizeCanonicalFoodName(raw)
        );
        if (
            !string.IsNullOrWhiteSpace(canonical)
            && !keys.Contains(canonical, StringComparer.Ordinal)
        )
        {
            keys.Add(canonical);
        }

        foreach (var seed in keys.ToArray())
        {
            var singularizedWords = seed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(RecipeUnitAndSectionHelpers.Singularize)
                .ToArray();
            var singularPhrase = string.Join(' ', singularizedWords);
            if (
                !string.IsNullOrWhiteSpace(singularPhrase)
                && !keys.Contains(singularPhrase, StringComparer.Ordinal)
            )
            {
                keys.Add(singularPhrase);
            }
        }

        return keys;
    }

    private static double ScoreBestTermPair(
        IReadOnlyList<string> queryForms,
        IReadOnlyList<string> candidateForms
    )
    {
        var best = 0.0;
        foreach (var q in queryForms)
        {
            foreach (var c in candidateForms)
            {
                var score = ComputeCompositeSimilarity(q, c);
                if (score > best)
                {
                    best = score;
                }
            }
        }
        return Math.Round(best, 4);
    }

    internal static double ComputeCompositeSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return 0.0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var setA = new HashSet<string>(tokensA, StringComparer.Ordinal);
        var setB = new HashSet<string>(tokensB, StringComparer.Ordinal);

        var intersection = setA.Count(t => setB.Contains(t));
        var union = setA.Count + setB.Count - intersection;
        var jaccard = union > 0 ? (double)intersection / union : 0.0;
        var minCount = Math.Min(setA.Count, setB.Count);
        var overlap = minCount > 0 ? (double)intersection / minCount : 0.0;

        var maxLen = Math.Max(a.Length, b.Length);
        var levSimilarity = maxLen > 0 ? 1.0 - ((double)LevenshteinDistance(a, b) / maxLen) : 0.0;

        if (intersection == 0 && levSimilarity < 0.65)
        {
            // No shared words and low character similarity -> clearly distinct foods.
            return Math.Min(0.45, levSimilarity * 0.5);
        }

        return Math.Clamp((0.35 * jaccard) + (0.30 * overlap) + (0.35 * levSimilarity), 0.0, 1.0);
    }

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
}
