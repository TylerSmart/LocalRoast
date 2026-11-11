using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public static class RecipeExtractionDiagnostics
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, RegexTimeout);
    private static readonly Regex Alternative = new(
        @"\bor\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex PreparationInName = new(
        @",\s*(?:(?:finely|coarsely|roughly|thinly)\s+)?(?:chopped|minced|diced|sliced|grated|peeled|softened|melted|drained|toasted|cut)\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex VagueTimerLabel = new(
        @"^(?:step\s+\d+\s+)?(?:timer|cook|cooking|bake|baking|boil|fry|heat|rest|simmer|wait)(?:\s+timer)?$",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );

    public static IReadOnlyList<string> GetSourceIngredientLines(string? source)
    {
        var result = new List<string>();
        var inIngredients = false;
        foreach (var line in (source ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("Ingredients:", StringComparison.OrdinalIgnoreCase))
            {
                inIngredients = true;
            }
            else if (
                inIngredients && trimmed.Equals("Instructions:", StringComparison.OrdinalIgnoreCase)
            )
            {
                break;
            }
            else if (inIngredients && trimmed.StartsWith("- "))
            {
                result.Add(trimmed[2..].Trim());
            }
        }
        return result;
    }

    /// <summary>
    /// Ingredient-row checks plus the structural and reconciliation checks of the tagged steps.
    /// </summary>
    public static IReadOnlyList<string> GetReviewIssues(
        LlmExtractedRecipePayload recipe,
        string? source = null
    ) => [.. GetIssues(recipe, source), .. TaggedStepDiagnostics.GetIssues(recipe)];

    public static IReadOnlyList<string> GetQualityWarnings(LlmExtractedRecipePayload recipe)
    {
        var warnings = new List<string>();
        var ingredients = recipe.Ingredients ?? [];
        var steps = recipe.Steps ?? [];
        var reportedFoods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var stepIndex = 0; stepIndex < steps.Count; stepIndex++)
        {
            var step = steps[stepIndex];
            var instruction = step.Instruction ?? "";
            foreach (var timer in step.Timers ?? [])
            {
                var label = timer.Label?.Trim() ?? "";
                if (string.IsNullOrEmpty(label) || VagueTimerLabel.IsMatch(label))
                {
                    warnings.Add(
                        $"Step {stepIndex + 1}: timer {Quote(label)} has a vague label; name the food or action being timed."
                    );
                }
            }

            for (var ingredientIndex = 0; ingredientIndex < ingredients.Count; ingredientIndex++)
            {
                var ingredient = ingredients[ingredientIndex];
                var foodKey = ingredient.CanonicalFoodName?.Trim();
                if (string.IsNullOrWhiteSpace(foodKey))
                {
                    foodKey = ingredient.DisplayName?.Trim();
                }
                if (
                    string.IsNullOrWhiteSpace(foodKey)
                    || !reportedFoods.Add(foodKey)
                    || !MentionsIngredient(instruction, ingredientIndex, ingredients)
                )
                {
                    continue;
                }

                var linked =
                    (step.IngredientIndices ?? [])
                        .Concat(
                            (step.IngredientAmounts ?? [])
                                .Where(amount => amount.IngredientIndex.HasValue)
                                .Select(amount => amount.IngredientIndex!.Value)
                        )
                        .Any(index =>
                            index >= 0
                            && index < ingredients.Count
                            && SameFood(ingredient, ingredients[index])
                        )
                    || StepInstructionParser
                        .ExtractTokens(instruction)
                        .OfType<StepIngredientToken>()
                        .Any(t => TokenMatchesIngredient(t, ingredient));
                if (!linked)
                {
                    var linkedInEarlierStep = steps
                        .Take(stepIndex)
                        .Any(prevStep =>
                            (prevStep.IngredientIndices ?? [])
                                .Concat(
                                    (prevStep.IngredientAmounts ?? [])
                                        .Where(a => a.IngredientIndex.HasValue)
                                        .Select(a => a.IngredientIndex!.Value)
                                )
                                .Any(idx =>
                                    idx >= 0
                                    && idx < ingredients.Count
                                    && SameFood(ingredient, ingredients[idx])
                                )
                            || StepInstructionParser
                                .ExtractTokens(prevStep.Instruction)
                                .OfType<StepIngredientToken>()
                                .Any(t => TokenMatchesIngredient(t, ingredient))
                        );
                    if (!linkedInEarlierStep)
                    {
                        warnings.Add(
                            $"Step {stepIndex + 1}: {DescribeIngredient(ingredientIndex, ingredient)} is mentioned but not linked; verify whether this step uses it."
                        );
                    }
                }
            }
        }

        return warnings;
    }

    public static IReadOnlyList<string> GetIssues(
        LlmExtractedRecipePayload recipe,
        string? source = null
    )
    {
        var issues = new List<string>();
        var ingredients = recipe.Ingredients ?? [];
        var steps = recipe.Steps ?? [];
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ingredient = ingredients[i];
            var name = DescribeIngredient(i, ingredient);
            if (
                RecipeDraftNormalizer.NormalizePreparationNote(ingredient, ingredients)?.Length
                > 200
            )
            {
                issues.Add($"{name}: preparation note exceeds the 200-character limit.");
            }
            if (
                !FoodNameSanitizer.IsValidFoodName(ingredient.DisplayName)
                || (
                    !ingredient.IsRecipeReference
                    && !FoodNameSanitizer.IsValidFoodName(
                        string.IsNullOrWhiteSpace(ingredient.CanonicalFoodName)
                            ? ingredient.DisplayName
                            : ingredient.CanonicalFoodName
                    )
                )
            )
            {
                issues.Add($"{name}: missing a meaningful food name.");
            }
            if (
                new[] { ingredient.DisplayName, ingredient.CanonicalFoodName }.Any(name =>
                    name is not null
                    && (PreparationInName.IsMatch(name) || Alternative.IsMatch(name))
                )
            )
            {
                issues.Add(
                    $"{name}: move preparation actions and alternative foods out of food names into preparationNote; name only the chosen food."
                );
            }
            if (ingredient.Quantity is <= 0 || ingredient.MaxQuantity is <= 0)
            {
                issues.Add($"{name}: non-positive measurement.");
            }
            if (
                string.Equals(ingredient.Unit, "custom", StringComparison.OrdinalIgnoreCase)
                && RecipeDraftNormalizer.IsDisallowedUnitPlaceholder(ingredient.CustomUnitName)
            )
            {
                issues.Add($"{name}: custom unit is missing its actual unit name.");
            }
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (
                (step.IngredientIndices ?? []).Any(index => index < 0 || index >= ingredients.Count)
                || (step.IngredientAmounts ?? []).Any(a =>
                    a.IngredientIndex is null or < 0 || a.IngredientIndex >= ingredients.Count
                )
            )
            {
                issues.Add($"Step {i + 1}: ingredient index is out of range.");
            }
        }

        var sourceLines = GetSourceIngredientLines(source);
        var normalizedSourceLines = sourceLines.Select(NormalizeLine).ToHashSet();
        var allRowsVerbatim = ingredients.All(ingredient =>
            normalizedSourceLines.Contains(NormalizeLine(ingredient.OriginalRawText))
        );
        var seenSourceLines = new HashSet<string>();
        for (var lineIndex = 0; lineIndex < sourceLines.Count; lineIndex++)
        {
            var line = sourceLines[lineIndex];
            var normalizedLine = NormalizeLine(line);
            if (!seenSourceLines.Add(normalizedLine))
            {
                continue;
            }
            var matches = ingredients
                .Where(i => NormalizeLine(i.OriginalRawText) == NormalizeLine(line))
                .ToList();
            if (matches.Count == 0)
            {
                // The model has to copy this text, so it is quoted in full.
                var exact = Quote(line, maxLength: 400);
                issues.Add(
                    allRowsVerbatim
                        ? $"Source ingredient line {lineIndex + 1}: {exact} is missing; add an ingredient row with this exact originalRawText."
                        : $"Source ingredient line {lineIndex + 1}: {exact} is missing or its originalRawText is not verbatim; copy this exact text into originalRawText."
                );
                continue;
            }
            if (
                Alternative.IsMatch(line)
                && matches
                    .Select(i => i.CanonicalFoodName?.Trim().ToLowerInvariant())
                    .Distinct()
                    .Count() > 1
            )
            {
                issues.Add(
                    $"Source ingredient line {lineIndex + 1}: {Quote(line)} alternatives must be one food choice, not multiple shopping items."
                );
            }
        }

        return issues;
    }

    private const int QuoteLength = 120;

    private static string DescribeIngredient(int index, LlmExtractedIngredientItem ingredient)
    {
        var text = FoodNameSanitizer.IsValidFoodName(ingredient.DisplayName)
            ? ingredient.DisplayName
            : ingredient.OriginalRawText;
        return string.IsNullOrWhiteSpace(text)
            ? $"Ingredient {index}"
            : $"Ingredient {index} {Quote(text)}";
    }

    // Issues quote the text they refer to, so a correction request (or a reviewer) can find it
    // without counting lines.
    private static string Quote(string? text, int maxLength = QuoteLength)
    {
        var value = Whitespace.Replace(text ?? "", " ").Trim();
        return value.Length <= maxLength
            ? $"\"{value}\""
            : $"\"{value[..(maxLength - 3)].TrimEnd()}...\"";
    }

    public static bool MentionsIngredient(
        string instruction,
        int index,
        IReadOnlyList<LlmExtractedIngredientItem> ingredients
    )
    {
        var names = IngredientNames(ingredients[index]);
        if (names.Any(name => ContainsIngredientTerm(instruction, name)))
        {
            return true;
        }

        foreach (var name in names)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var term in new[] { words[0], words[^1] }.Distinct())
            {
                if (
                    term.Length < 3
                    || !ContainsIngredientTerm(instruction, term)
                    || ingredients
                        .Where(
                            (other, otherIndex) =>
                                otherIndex != index && !SameFood(ingredients[index], other)
                        )
                        .SelectMany(IngredientNames)
                        .Any(other =>
                            other.Split(' ').Contains(term, StringComparer.OrdinalIgnoreCase)
                        )
                )
                {
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    private static List<string> IngredientNames(LlmExtractedIngredientItem ingredient) =>
        new[] { ingredient.DisplayName, ingredient.CanonicalFoodName }
            .Select(name => Whitespace.Replace(name ?? "", " ").Trim())
            .Where(name => name.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool ContainsIngredientTerm(string instruction, string term) =>
        Regex.IsMatch(
            instruction,
            $@"\b{Regex.Escape(term)}(?:e?s)?\b",
            RegexOptions.IgnoreCase,
            RegexTimeout
        );

    private static bool SameFood(
        LlmExtractedIngredientItem first,
        LlmExtractedIngredientItem second
    ) =>
        IngredientNames(first)
            .Any(name => IngredientNames(second).Contains(name, StringComparer.OrdinalIgnoreCase));

    private static string NormalizeLine(string? line) =>
        Whitespace.Replace((line ?? "").Trim().TrimStart('-', ' '), " ");

    private static bool TokenMatchesIngredient(
        StepIngredientToken token,
        LlmExtractedIngredientItem ingredient
    )
    {
        if (string.IsNullOrWhiteSpace(token.Ingredient))
        {
            return false;
        }

        var names = IngredientNames(ingredient);
        var tokenIng = token.Ingredient.Trim();
        return names.Any(name =>
            string.Equals(tokenIng, name.Trim(), StringComparison.OrdinalIgnoreCase)
            || tokenIng.Contains(name, StringComparison.OrdinalIgnoreCase)
            || name.Contains(tokenIng, StringComparison.OrdinalIgnoreCase)
        );
    }
}
