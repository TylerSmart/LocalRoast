using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public static class RecipeExtractionDiagnostics
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, RegexTimeout);
    private static readonly Regex SentenceBreak = new(
        @"(?<=[.!?][)\]""'\u201D]?)\s+|\n",
        RegexOptions.None,
        RegexTimeout
    );
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
            if (
                ingredient.Quantity is > 0
                && steps.Count > 0
                && !steps.Any(s =>
                    s.IngredientIndices?.Contains(i) == true
                    || s.IngredientAmounts?.Any(a => a.IngredientIndex == i) == true
                )
            )
            {
                var mentions = FindStepsMentioning(ingredient, steps);
                issues.Add(
                    mentions.Count == 0
                        ? $"{name}: measured ingredient is not linked to any step."
                        : $"{name}: measured ingredient is not linked to any step; {DescribeSteps(mentions)} {(mentions.Count == 1 ? "mentions" : "mention")} it, so add {i} to the ingredientIndices of each step that uses it."
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
            var instruction = step.Instruction ?? "";
            var fixedAmounts = AmountInInstruction
                .Matches(instruction)
                .Where(m => !ProcessMeasurementPrefix.IsMatch(instruction[..m.Index]))
                .Select(m => Quote(m.Value))
                .Distinct()
                .ToList();
            if (fixedAmounts.Count > 0)
            {
                issues.Add(
                    $"Step {i + 1}: contains fixed ingredient amounts ({string.Join(", ", fixedAmounts)}); split measured portions and link them before removing those amounts from prose."
                );
            }
            if (
                DividedAddition.IsMatch(instruction)
                && (step.IngredientIndices ?? []).Any(index =>
                    index >= 0
                    && index < ingredients.Count
                    && ingredients[index].Quantity is > 0
                    && step.IngredientAmounts?.Any(a =>
                        a.IngredientIndex == index && a.Measurement?.Quantity is > 0
                    ) != true
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
            var occurrences = sourceLines.Count(candidate =>
                NormalizeLine(candidate) == normalizedLine
            );
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
            var sourcePair = SourceMeasurementParser.FindAlternative(line);
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
                var actualQuantity = ingredient.Quantity;
                var actualMaximum = ingredient.MaxQuantity ?? ingredient.Quantity;
                if (
                    matches.Count == 1
                    && sourcePair is { } pair
                    && actualUnit == pair.Alternative.Unit
                    && actualQuantity == pair.Alternative.Quantity
                    && actualMaximum == (pair.Alternative.MaxQuantity ?? pair.Alternative.Quantity)
                )
                {
                    actualUnit = pair.Primary.Unit;
                    actualQuantity = pair.Primary.Quantity;
                    actualMaximum = pair.Primary.MaxQuantity ?? pair.Primary.Quantity;
                }
                if (
                    actualQuantity is null
                    || !TryConvert(actualQuantity.Value, actualUnit, unit.Value, out var quantity)
                    || !TryConvert(
                        actualMaximum ?? actualQuantity.Value,
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
                    $"Source ingredient line {lineIndex + 1}: {Quote(line)} portion quantities/units do not conserve the source total ({expected}-{expectedMax} {measurement.Groups["unit"].Value})."
                );
            }
        }

        var instructionHeader = (source ?? "").IndexOf(
            "\nInstructions:",
            StringComparison.OrdinalIgnoreCase
        );
        if (instructionHeader >= 0 && steps.Count > 0)
        {
            var sourceMethod = source![(instructionHeader + 14)..];
            var sourceTimers = StepTimerExtractor.DetectTimers(sourceMethod);
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
                var extractedCount = extractedDurations.Count(duration => duration == group.Key);
                if (extractedCount < group.Count())
                {
                    var sentences = FindTimedSentences(sourceMethod, group.Key);
                    var context =
                        sentences.Count == 0
                            ? ""
                            : $" (source has {group.Count()}, extracted {extractedCount}: {string.Join("; ", sentences.Select(sentence => Quote(sentence)))})";
                    issues.Add(
                        $"Source timed phases: missing a {group.Key}-second timer{context}; preserve all cooking/waiting durations and use range lower bounds."
                    );
                }
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

    private static List<string> FindTimedSentences(string method, int durationSeconds) =>
        SentenceBreak
            .Split(method)
            .Where(sentence =>
                StepTimerExtractor
                    .DetectTimers(sentence)
                    .Any(timer => timer.DurationSeconds == durationSeconds)
            )
            .Distinct()
            .Take(3)
            .ToList();

    // Steps (1-based) whose text names an unlinked ingredient: the full name first, then its last
    // word (usually the food, as in "unsalted butter"), then its first word (as in "fennel bulb").
    private static List<int> FindStepsMentioning(
        LlmExtractedIngredientItem ingredient,
        IReadOnlyList<LlmExtractedStepItem> steps
    )
    {
        var names = new[] { ingredient.DisplayName, ingredient.CanonicalFoodName }
            .Select(name => Whitespace.Replace(name ?? "", " ").Trim())
            .Where(name => name.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var words = names.Select(name => name.Split(' ')).ToList();
        foreach (
            var terms in new[]
            {
                names,
                words.Select(parts => parts[^1]).ToList(),
                words.Select(parts => parts[0]).ToList(),
            }
        )
        {
            var patterns = terms
                .Where(term => term.Length >= 3)
                .Select(term => new Regex(
                    $@"\b{Regex.Escape(term)}(?:e?s)?\b",
                    RegexOptions.IgnoreCase,
                    RegexTimeout
                ))
                .ToList();
            var matches = Enumerable
                .Range(0, steps.Count)
                .Where(index =>
                    patterns.Any(pattern => pattern.IsMatch(steps[index].Instruction ?? ""))
                )
                .Select(index => index + 1)
                .ToList();
            if (matches.Count > 0)
            {
                return matches;
            }
        }
        return [];
    }

    private static string DescribeSteps(IReadOnlyList<int> stepNumbers) =>
        stepNumbers.Count switch
        {
            1 => $"step {stepNumbers[0]}",
            <= 3 =>
                $"steps {string.Join(", ", stepNumbers.Take(stepNumbers.Count - 1))} and {stepNumbers[^1]}",
            _ => $"steps {string.Join(", ", stepNumbers.Take(3))} and {stepNumbers.Count - 3} more",
        };

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
