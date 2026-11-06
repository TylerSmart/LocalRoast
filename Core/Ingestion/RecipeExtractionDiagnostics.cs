using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public static class RecipeExtractionDiagnostics
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, RegexTimeout);
    private static readonly Regex LeadingMeasurement = new(
        @"^(?<quantity>\d+\s+\d+/\d+|\d+/\d+|\d+(?:\.\d+)?)(?:\s*(?:to|[-\u2013\u2014])\s*(?<max>\d+\s+\d+/\d+|\d+/\d+|\d+(?:\.\d+)?))?\s+(?<unit>[A-Za-z]+)\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex AmountInInstruction = new(
        @"\b\d+(?:[./]\d+)?(?:\s+\d+/\d+)?\s*(?:teaspoons?|tablespoons?|cups?|ounces?|pounds?|tsp|tbsp|lbs?|grams?|kilograms?|milliliters?|liters?)\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex ProcessMeasurementPrefix = new(
        @"\b(?:reduces?|reduced|reduction|obtain|yield|measures?)\b[^.;]{0,55}$",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex CompoundMeasurement = new(
        @"\b(?:plus|and)\b.*\d",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
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
    private static readonly Regex DividedAddition = new(
        @"\b(?:add|heat|lay|stir in|whisk in)\b[^.;]{0,80}\b(?:remaining|half)\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex PreparedMixtureMeasurement = new(
        @"\b\d+(?:[./]\d+)?(?:\s+\d+/\d+)?\s*(?:cups?|tablespoons?|teaspoons?|tbsp|tsp|ounces?|pounds?)\s+(?:(?:of|the|prepared|reserved|hot|warm|cold|remaining)\s+){0,4}(?<mixture>stock|broth|batter|dough|filling|sauce)\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly JsonSerializerOptions NumberOptions = new()
    {
        Converters = { new FlexibleNullableDecimalJsonConverter() },
    };

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

    public static IReadOnlyList<string> GetReviewIssues(
        LlmExtractedRecipePayload recipe,
        string? source = null
    )
    {
        var issues = GetIssues(recipe, source).ToList();
        var instructionHeader = (source ?? "").IndexOf(
            "\nInstructions:",
            StringComparison.OrdinalIgnoreCase
        );
        if (instructionHeader < 0)
        {
            return issues;
        }
        var ingredientLines = GetSourceIngredientLines(source);
        var mixtures = PreparedMixtureMeasurement
            .Matches(source![(instructionHeader + 14)..])
            .Select(match => match.Groups["mixture"].Value.ToLowerInvariant())
            .Distinct();
        foreach (var mixture in mixtures)
        {
            if (
                !ingredientLines.Any(line =>
                    Regex.IsMatch(line, $@"\b{mixture}\b", RegexOptions.IgnoreCase, RegexTimeout)
                )
            )
            {
                issues.Add(
                    $"Source method measures prepared {mixture} that is not a listed ingredient. Manually verify its portions and serving scaling; do not erase the amount or link its original ingredients as fresh additions."
                );
            }
        }
        return issues;
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
            if (
                RecipeDraftNormalizer.NormalizePreparationNote(ingredient, ingredients)?.Length
                > 200
            )
            {
                issues.Add($"Ingredient {i}: preparation note exceeds the 200-character limit.");
            }
            if (
                !FoodNameSanitizer.IsValidFoodName(ingredient.DisplayName)
                || !FoodNameSanitizer.IsValidFoodName(
                    ingredient.CanonicalFoodName ?? ingredient.DisplayName
                )
            )
            {
                issues.Add($"Ingredient {i}: missing a meaningful food name.");
            }
            if (
                new[] { ingredient.DisplayName, ingredient.CanonicalFoodName }.Any(name =>
                    name is not null
                    && (PreparationInName.IsMatch(name) || Alternative.IsMatch(name))
                )
            )
            {
                issues.Add(
                    $"Ingredient {i}: move preparation actions and alternative foods out of food names into preparationNote; name only the chosen food."
                );
            }
            if (
                ingredient.Quantity is > 0
                && steps.Count > 0
                && !steps.Any(s => s.IngredientIndices?.Contains(i) == true)
            )
            {
                issues.Add($"Ingredient {i}: measured ingredient is not linked to any step.");
            }
            if (ingredient.Quantity is <= 0 || ingredient.MaxQuantity is <= 0)
            {
                issues.Add($"Ingredient {i}: non-positive measurement.");
            }
            if (
                string.Equals(ingredient.Unit, "custom", StringComparison.OrdinalIgnoreCase)
                && RecipeDraftNormalizer.IsDisallowedUnitPlaceholder(ingredient.CustomUnitName)
            )
            {
                issues.Add($"Ingredient {i}: custom unit is missing its actual unit name.");
            }
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (
                (step.IngredientIndices ?? []).Any(index => index < 0 || index >= ingredients.Count)
            )
            {
                issues.Add($"Step {i + 1}: ingredient index is out of range.");
            }
            var instruction = step.Instruction ?? "";
            if (
                AmountInInstruction
                    .Matches(instruction)
                    .Any(m => !ProcessMeasurementPrefix.IsMatch(instruction[..m.Index]))
            )
            {
                issues.Add(
                    $"Step {i + 1}: contains fixed ingredient amounts; split measured portions and link them before removing those amounts from prose."
                );
            }
            if (
                DividedAddition.IsMatch(instruction)
                && (step.IngredientIndices ?? []).Any(index =>
                    index >= 0
                    && index < ingredients.Count
                    && ingredients[index].Quantity is > 0
                    && steps
                        .Take(i)
                        .Any(previous => previous.IngredientIndices?.Contains(index) == true)
                )
            )
            {
                issues.Add(
                    $"Step {i + 1}: a divided addition reuses an earlier measured ingredient row; verify separate portions rather than showing the whole amount again."
                );
            }
        }

        var sourceLines = GetSourceIngredientLines(source);
        var seenSourceLines = new HashSet<string>();
        for (var lineIndex = 0; lineIndex < sourceLines.Count; lineIndex++)
        {
            var line = sourceLines[lineIndex];
            var normalizedLine = NormalizeLine(line);
            if (!seenSourceLines.Add(normalizedLine))
            {
                continue;
            }
            var occurrences = sourceLines.Count(candidate =>
                NormalizeLine(candidate) == normalizedLine
            );
            var matches = ingredients
                .Where(i => NormalizeLine(i.OriginalRawText) == NormalizeLine(line))
                .ToList();
            if (matches.Count == 0)
            {
                issues.Add(
                    $"Source ingredient line {lineIndex + 1}: missing or originalRawText is not verbatim."
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
                    $"Source ingredient line {lineIndex + 1}: alternatives must be one food choice, not multiple shopping items."
                );
            }
            var measurement = LeadingMeasurement.Match(line);
            if (!measurement.Success || CompoundMeasurement.IsMatch(line))
            {
                continue;
            }
            var expected = ParseNumber(measurement.Groups["quantity"].Value);
            var expectedMax = measurement.Groups["max"].Success
                ? ParseNumber(measurement.Groups["max"].Value)
                : expected;
            expected *= occurrences;
            expectedMax *= occurrences;
            var (unit, _) = RecipeDraftNormalizer.NormalizeUnit(measurement.Groups["unit"].Value);
            // Unknown count words may be package units or food names; do not guess a conversion.
            if (unit is null or MeasurementUnit.Custom || expected is null || expectedMax is null)
            {
                continue;
            }
            // Separated whole foods have component counts, not additive portion counts.
            if (
                unit == MeasurementUnit.Whole
                && matches.Count > 1
                && Regex.IsMatch(line, @"\bseparated\b", RegexOptions.IgnoreCase, RegexTimeout)
            )
            {
                continue;
            }
            decimal lower = 0,
                upper = 0;
            var compatible = true;
            foreach (var ingredient in matches)
            {
                if (ingredient.Quantity is null && ingredient.IsOptional)
                {
                    continue;
                }
                var (actualUnit, _) = RecipeDraftNormalizer.NormalizeUnit(
                    ingredient.Unit,
                    ingredient.CustomUnitName
                );
                if (
                    ingredient.Quantity is null
                    || !TryConvert(
                        ingredient.Quantity.Value,
                        actualUnit,
                        unit.Value,
                        out var quantity
                    )
                    || !TryConvert(
                        ingredient.MaxQuantity ?? ingredient.Quantity.Value,
                        actualUnit,
                        unit.Value,
                        out var maximum
                    )
                )
                {
                    compatible = false;
                    break;
                }
                lower += ingredient.IsOptional && expected != expectedMax ? 0 : quantity;
                upper += maximum;
            }
            if (
                !compatible
                || Math.Abs(lower - expected.Value) > 0.001m
                || Math.Abs(upper - expectedMax.Value) > 0.001m
            )
            {
                issues.Add(
                    $"Source ingredient line {lineIndex + 1}: portion quantities/units do not conserve the source total ({expected}-{expectedMax} {measurement.Groups["unit"].Value})."
                );
            }
        }

        var instructionHeader = (source ?? "").IndexOf(
            "\nInstructions:",
            StringComparison.OrdinalIgnoreCase
        );
        if (instructionHeader >= 0 && steps.Count > 0)
        {
            var sourceTimers = StepTimerExtractor.DetectTimers(source![(instructionHeader + 14)..]);
            var extractedDurations = steps
                .SelectMany(step =>
                {
                    var instruction = step.Instruction ?? "";
                    var provided = (step.Timers ?? [])
                        .Where(timer =>
                            timer.DurationSeconds > 0 && !string.IsNullOrWhiteSpace(timer.Label)
                        )
                        .ToList();
                    return provided.Count > 0
                        ? provided.Select(timer =>
                            StepTimerExtractor.UseRangeLowerBound(
                                timer.DurationSeconds,
                                instruction
                            )
                        )
                        : StepTimerExtractor
                            .DetectTimers(instruction)
                            .Select(timer => timer.DurationSeconds);
                })
                .ToList();
            foreach (var group in sourceTimers.GroupBy(timer => timer.DurationSeconds))
            {
                if (extractedDurations.Count(duration => duration == group.Key) < group.Count())
                {
                    issues.Add(
                        $"Source timed phases: missing a {group.Key}-second timer; preserve all cooking/waiting durations and use range lower bounds."
                    );
                }
            }
        }
        return issues;
    }

    private static string NormalizeLine(string? line) =>
        Whitespace.Replace((line ?? "").Trim().TrimStart('-', ' '), " ");

    private static decimal? ParseNumber(string value) =>
        JsonSerializer.Deserialize<decimal?>(JsonSerializer.Serialize(value), NumberOptions);

    private static bool TryConvert(
        decimal quantity,
        MeasurementUnit? from,
        MeasurementUnit to,
        out decimal result
    )
    {
        result = quantity;
        if (from == to || (from is null && to == MeasurementUnit.Whole))
        {
            return true;
        }
        var actual = CulinaryMath.ScaleAndConvert(quantity, null, from, null, 1, UnitSystem.Metric);
        var target = CulinaryMath.ScaleAndConvert(1, null, to, null, 1, UnitSystem.Metric);
        var actualUnit =
            actual.Unit is MeasurementUnit.L ? MeasurementUnit.Ml
            : actual.Unit is MeasurementUnit.Kg ? MeasurementUnit.G
            : actual.Unit;
        var targetUnit =
            target.Unit is MeasurementUnit.L ? MeasurementUnit.Ml
            : target.Unit is MeasurementUnit.Kg ? MeasurementUnit.G
            : target.Unit;
        if (actualUnit != targetUnit || actualUnit is not (MeasurementUnit.Ml or MeasurementUnit.G))
        {
            return false;
        }
        var actualFactor = actual.Unit is MeasurementUnit.L or MeasurementUnit.Kg ? 1000m : 1m;
        var targetFactor = target.Unit is MeasurementUnit.L or MeasurementUnit.Kg ? 1000m : 1m;
        result = actual.Quantity!.Value * actualFactor / (target.Quantity!.Value * targetFactor);
        return true;
    }
}
