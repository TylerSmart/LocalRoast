namespace App.Core.Recipes;

public sealed record MatchableOnHandItem(
    Guid FoodItemId,
    string Name,
    string NormalizedName,
    IReadOnlyList<string> NormalizedAliases
);

public sealed record MatchableRecipeIngredient(
    Guid? FoodItemId,
    string DisplayName,
    bool IsOptional,
    bool IsPantryStaple
);

public sealed record MatchableRecipeCandidate(
    RecipeSummaryDto Summary,
    IReadOnlyList<MatchableRecipeIngredient> Ingredients
);

public static class IngredientMatcher
{
    public static IReadOnlyList<IngredientMatchResultDto> RankRecipes(
        IReadOnlyList<MatchableRecipeCandidate> candidates,
        IReadOnlyList<MatchableOnHandItem> onHandItems
    )
    {
        if (candidates.Count == 0 || onHandItems.Count == 0)
        {
            return [];
        }

        // Deduplicate on-hand items by FoodItemId
        var distinctOnHand = onHandItems.GroupBy(i => i.FoodItemId).Select(g => g.First()).ToList();

        var onHandIdMap = distinctOnHand.ToDictionary(i => i.FoodItemId);
        var results = new List<IngredientMatchResultDto>();

        foreach (var candidate in candidates)
        {
            var matchedOnHandIds = new HashSet<Guid>();
            var matchedIngredientNames = new List<string>();

            // 1. Identify which on-hand items match this recipe
            foreach (var onHand in distinctOnHand)
            {
                var matches = candidate.Ingredients.Any(ing =>
                    (ing.FoodItemId.HasValue && ing.FoodItemId.Value == onHand.FoodItemId)
                    || (!ing.FoodItemId.HasValue && MatchesNameOrAlias(ing.DisplayName, onHand))
                );

                if (matches)
                {
                    matchedOnHandIds.Add(onHand.FoodItemId);
                    matchedIngredientNames.Add(onHand.Name);
                }
            }

            if (matchedOnHandIds.Count == 0)
            {
                continue;
            }

            // 2. Additional required non-staple ingredients needed
            var missingNonStaples = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase
            );
            var pantryStaplesCount = 0;
            var seenPantryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ing in candidate.Ingredients)
            {
                var isMatched =
                    (ing.FoodItemId.HasValue && matchedOnHandIds.Contains(ing.FoodItemId.Value))
                    || (
                        !ing.FoodItemId.HasValue
                        && distinctOnHand.Any(oh =>
                            matchedOnHandIds.Contains(oh.FoodItemId)
                            && MatchesNameOrAlias(ing.DisplayName, oh)
                        )
                    );

                if (isMatched)
                {
                    continue;
                }

                var key = ing.FoodItemId.HasValue
                    ? ing.FoodItemId.Value.ToString()
                    : ing.DisplayName.Trim().ToLowerInvariant();

                if (ing.IsPantryStaple)
                {
                    if (seenPantryKeys.Add(key))
                    {
                        pantryStaplesCount++;
                    }
                    continue;
                }

                // If it is required (!IsOptional) and non-staple, track it
                if (!ing.IsOptional && !missingNonStaples.ContainsKey(key))
                {
                    missingNonStaples[key] = ing.DisplayName.Trim();
                }
            }

            results.Add(
                new IngredientMatchResultDto(
                    Recipe: candidate.Summary,
                    MatchedCount: matchedOnHandIds.Count,
                    SelectedOnHandCount: distinctOnHand.Count,
                    AdditionalNonStapleCount: missingNonStaples.Count,
                    PantryStaplesUsedCount: pantryStaplesCount,
                    MatchedIngredientNames: matchedIngredientNames
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    MissingNonStapleNames: missingNonStaples
                        .Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                )
            );
        }

        return results
            .OrderByDescending(r => r.MatchedCount)
            .ThenBy(r => r.AdditionalNonStapleCount)
            .ThenBy(r => r.Recipe.TotalTimeMinutes ?? int.MaxValue)
            .ThenBy(r => r.Recipe.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MatchesNameOrAlias(string displayName, MatchableOnHandItem onHand)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        var normalized = displayName.Trim().ToLowerInvariant();
        if (normalized.Equals(onHand.NormalizedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var alias in onHand.NormalizedAliases)
        {
            if (normalized.Equals(alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
