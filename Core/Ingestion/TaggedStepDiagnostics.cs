using System.Globalization;
using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

/// <summary>
/// Checks the inline tokens in each step: validates JSON syntax and references against ingredient rows.
/// </summary>
public static partial class TaggedStepDiagnostics
{
    private static readonly Regex OpenEndedPlusRegex = new(
        @"\bplus\s+(?:more|extra|[\w\s]+for)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1)
    );

    [GeneratedRegex(
        @"\{\s*""(?:type|ingredient|amount|displayAmount|displayText|unit|seconds|fahrenheit|celsius)""\s*:[^}\r\n]*?\}",
        RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BareJsonDiagnosticRegex();

    public static IReadOnlyList<string> GetIssues(LlmExtractedRecipePayload recipe)
    {
        var issues = new List<string>();
        var ingredients = recipe.Ingredients ?? [];
        var steps = recipe.Steps ?? [];

        if (!string.IsNullOrWhiteSpace(recipe.Title))
        {
            var titleNorm = recipe.Title.Trim();
            for (var i = 0; i < ingredients.Count; i++)
            {
                var ing = ingredients[i];
                var name = ing.DisplayName ?? ing.CanonicalFoodName;
                if (
                    !string.IsNullOrWhiteSpace(name)
                    && string.Equals(name.Trim(), titleNorm, StringComparison.OrdinalIgnoreCase)
                    && !RecipeDraftNormalizer.RecipeReferenceRegex.IsMatch(
                        ing.OriginalRawText ?? ""
                    )
                )
                {
                    issues.Add(
                        $"Ingredient \"{name}\" is named after the recipe title \"{recipe.Title}\". Use the specific food name from the source ingredient line (e.g. \"{ing.OriginalRawText}\") instead of the recipe title."
                    );
                }
            }
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var instruction = steps[i].Instruction ?? "";
            var segments = StepInstructionParser.ParseSegments(instruction);
            var stepIngredientCounts = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase
            );

            foreach (var segment in segments)
            {
                if (segment.Token is null)
                {
                    // If segment looks like a broken token, report it
                    if (segment.Text.StartsWith("||{") && segment.Text.EndsWith("}||"))
                    {
                        issues.Add($"Step {i + 1}: unparseable inline token \"{segment.Text}\".");
                    }
                    else
                    {
                        var bareMatch = BareJsonDiagnosticRegex().Match(segment.Text);
                        if (bareMatch.Success)
                        {
                            issues.Add(
                                $"Step {i + 1}: unbracketed JSON token \"{bareMatch.Value}\" is missing \"||\" delimiters."
                            );
                        }
                    }
                    continue;
                }

                if (segment.Token is StepIngredientToken ing)
                {
                    if (string.IsNullOrWhiteSpace(ing.Ingredient))
                    {
                        issues.Add(
                            $"Step {i + 1}: inline ingredient token {segment.Text} has no ingredient name. Every ingredient token must include \"ingredient\": \"<name>\" or be written in plain text."
                        );
                    }
                    else
                    {
                        var ingKey = ing.Ingredient.Trim();
                        stepIngredientCounts[ingKey] =
                            stepIngredientCounts.GetValueOrDefault(ingKey, 0) + 1;

                        if (ing.Amount.HasValue && ing.Amount.Value <= 0)
                        {
                            issues.Add(
                                $"Step {i + 1}: ingredient \"{ing.Ingredient}\" has a non-positive amount ({ing.Amount.Value})."
                            );
                        }
                    }
                }
                else if (segment.Token is StepTimerToken timer)
                {
                    if (timer.Seconds <= 0)
                    {
                        issues.Add(
                            $"Step {i + 1}: timer token has an invalid duration ({timer.Seconds} seconds)."
                        );
                    }
                }
            }

            foreach (var (ingName, count) in stepIngredientCounts)
            {
                if (count >= 3)
                {
                    issues.Add(
                        $"Step {i + 1}: ingredient \"{ingName}\" is tagged {count} times in the same step. Only tag ingredients where introduced, added, or measured; subsequent narrative mentions (e.g. \"flipping\", \"until softened\", \"let cool\") should be plain prose."
                    );
                }
            }
        }

        if (steps.Count > 0)
        {
            for (var i = 0; i < ingredients.Count; i++)
            {
                var ingredient = ingredients[i];
                var name =
                    ingredient.DisplayName ?? ingredient.CanonicalFoodName ?? $"Ingredient {i + 1}";
                var rowLow = ingredient.Quantity ?? 0;
                var rowHigh = ingredient.MaxQuantity ?? rowLow;

                var isUsed = false;
                decimal sumLow = 0;
                var hasComparableAmounts = false;
                var stepAmounts = new List<decimal>();
                var unmeasuredMentionsCount = 0;

                for (var sIdx = 0; sIdx < steps.Count; sIdx++)
                {
                    var s = steps[sIdx];
                    var stepUses =
                        (s.IngredientIndices?.Contains(i) == true)
                        || (s.IngredientAmounts?.Any(a => a.IngredientIndex == i) == true);

                    var tokens = StepInstructionParser.ExtractTokens(s.Instruction ?? "");
                    foreach (var token in tokens)
                    {
                        if (token is StepIngredientToken ingToken)
                        {
                            var bestIdx = RecipeDraftNormalizer.FindBestMatchingIngredientIndex(
                                ingToken,
                                s.SectionName,
                                ingredients,
                                s.IngredientIndices,
                                s.Instruction
                            );

                            if (bestIdx == i)
                            {
                                stepUses = true;
                                if (!ingToken.Amount.HasValue && !ingToken.DisplayAmount.HasValue)
                                {
                                    unmeasuredMentionsCount++;
                                }

                                if (ingToken.Amount.HasValue && ingredient.Quantity is > 0)
                                {
                                    if (
                                        TryToRowUnit(
                                            ingToken.Amount.Value,
                                            ingToken.Unit,
                                            ingredient,
                                            out var inRowUnit
                                        )
                                    )
                                    {
                                        sumLow += inRowUnit;
                                        stepAmounts.Add(inRowUnit);
                                        hasComparableAmounts = true;
                                    }
                                }
                            }
                        }
                    }

                    if (s.IngredientAmounts is not null)
                    {
                        foreach (
                            var a in s.IngredientAmounts.Where(a =>
                                a.IngredientIndex == i && a.Measurement?.Quantity is not null
                            )
                        )
                        {
                            stepUses = true;
                            var (aUnit, _) = RecipeDraftNormalizer.NormalizeUnit(
                                a.Measurement.Unit
                            );
                            if (
                                TryToRowUnit(
                                    a.Measurement.Quantity!.Value,
                                    aUnit,
                                    ingredient,
                                    out var inRowUnit
                                )
                            )
                            {
                                sumLow += inRowUnit;
                                hasComparableAmounts = true;
                            }
                        }
                    }

                    if (stepUses)
                    {
                        isUsed = true;
                    }
                }

                if (rowLow > 0 && !isUsed)
                {
                    issues.Add($"{name}: not used in any step.");
                    continue;
                }

                if (unmeasuredMentionsCount >= 4)
                {
                    issues.Add(
                        $"{name}: tagged without amounts {unmeasuredMentionsCount} times across steps. Only tag ingredients where introduced, added, or measured; subsequent narrative mentions (e.g. \"flipping\", \"let cool\", doneness checks) should be plain prose."
                    );
                }

                if (hasComparableAmounts && rowHigh > 0)
                {
                    var tolerance = Math.Max(0.0005m, rowHigh * 0.01m);
                    if (sumLow > rowHigh + tolerance)
                    {
                        // If all step mentions each specify the full batch amount (e.g. "cook 6 eggs", then "peel 6 eggs"),
                        // this is a repeat mention of the full batch rather than an overflow.
                        var isRepeatMentionOfFullAmount =
                            stepAmounts.Count > 1
                            && stepAmounts.All(a => Math.Abs(a - rowHigh) <= tolerance);

                        var hasOpenEndedPlusQualifier =
                            OpenEndedPlusRegex.IsMatch(ingredient.OriginalRawText ?? "")
                            || OpenEndedPlusRegex.IsMatch(ingredient.PreparationNote ?? "");

                        if (!isRepeatMentionOfFullAmount && !hasOpenEndedPlusQualifier)
                        {
                            issues.Add(
                                $"{name}: step amounts ({FormatNumber(sumLow)} {ingredient.Unit}) exceed the ingredient list ({FormatNumber(rowHigh)} {ingredient.Unit})."
                            );
                        }
                    }
                }
            }
        }

        return issues;
    }

    private static bool MatchesIngredient(
        string? tokenIngredient,
        LlmExtractedIngredientItem ingredient
    )
    {
        if (string.IsNullOrWhiteSpace(tokenIngredient))
        {
            return false;
        }

        var tokenName = tokenIngredient.Trim();
        var displayName = ingredient.DisplayName?.Trim();
        var canonicalName = ingredient.CanonicalFoodName?.Trim();

        if (
            !string.IsNullOrWhiteSpace(displayName)
            && (
                string.Equals(tokenName, displayName, StringComparison.OrdinalIgnoreCase)
                || displayName.Contains(tokenName, StringComparison.OrdinalIgnoreCase)
                || tokenName.Contains(displayName, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return true;
        }

        if (
            !string.IsNullOrWhiteSpace(canonicalName)
            && (
                string.Equals(tokenName, canonicalName, StringComparison.OrdinalIgnoreCase)
                || canonicalName.Contains(tokenName, StringComparison.OrdinalIgnoreCase)
                || tokenName.Contains(canonicalName, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return true;
        }

        return false;
    }

    private static bool TryToRowUnit(
        decimal quantity,
        MeasurementUnit? from,
        LlmExtractedIngredientItem ingredient,
        out decimal result
    )
    {
        result = 0;
        var (rowUnit, _) = RecipeDraftNormalizer.NormalizeUnit(
            ingredient.Unit,
            ingredient.CustomUnitName
        );
        if (from is not { } fromUnit || rowUnit is not { } toUnit)
        {
            return false;
        }
        if (CulinaryMath.TryConvertQuantity(quantity, fromUnit, toUnit, out result))
        {
            return true;
        }
        var alternative = ingredient.AlternativeMeasurement;
        var (alternativeUnit, _) = RecipeDraftNormalizer.NormalizeUnit(alternative?.Unit);
        if (
            alternative?.Quantity is > 0
            && alternativeUnit is { } altUnit
            && CulinaryMath.TryConvertQuantity(quantity, fromUnit, altUnit, out var inAlternative)
        )
        {
            result = inAlternative * ingredient.Quantity!.Value / alternative.Quantity.Value;
            return true;
        }
        return false;
    }

    private static string FormatNumber(decimal value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
