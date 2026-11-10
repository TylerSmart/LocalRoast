using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

/// <summary>
/// Checks the tagged inline amounts of a (possibly edited) draft: each span must cover a
/// distinct, still-parseable amount of its step and reference a valid row, and the step
/// amounts must reconcile with the ingredient rows.
/// </summary>
public static class TaggedStepDiagnostics
{
    public static IReadOnlyList<string> GetIssues(LlmExtractedRecipePayload recipe)
    {
        var issues = new List<string>();
        var ingredients = recipe.Ingredients ?? [];
        var steps = recipe.Steps ?? [];
        for (var i = 0; i < steps.Count; i++)
        {
            var instruction = steps[i].Instruction ?? "";
            var previousEnd = 0;
            foreach (var amount in steps[i].InlineAmounts ?? [])
            {
                var inRange =
                    amount.Start >= 0
                    && amount.Length > 0
                    && amount.Start + amount.Length <= instruction.Length;
                var text = inRange
                    ? instruction.Substring(amount.Start, amount.Length)
                    : amount.Text;
                var prefix = $"Step {i + 1}: inline amount \"{text}\"";
                if (!inRange)
                {
                    issues.Add($"{prefix} is outside the step text.");
                    continue;
                }
                if (amount.Start < previousEnd)
                    issues.Add($"{prefix} overlaps or is out of order with another amount.");
                previousEnd = Math.Max(previousEnd, amount.Start + amount.Length);
                var validIndex =
                    amount.IngredientIndex is { } index && index >= 0 && index < ingredients.Count;
                if (amount.Kind == InlineStepAmountKind.Ingredient && !validIndex)
                    issues.Add($"{prefix} is tagged as an ingredient without a valid ingredient.");
                else if (
                    amount.Kind != InlineStepAmountKind.Ingredient
                    && amount.IngredientIndex is not null
                )
                    issues.Add($"{prefix} references an ingredient but is not tagged as one.");
                if (
                    SourceMeasurementParser.FindSpans(text).Count == 0
                    && !IsCountAmount(text, amount.Measurement)
                )
                    issues.Add($"{prefix} is no longer a measured amount.");

                if (amount.IngredientSpanStart.HasValue && amount.IngredientSpanLength.HasValue)
                {
                    var ingStart = amount.IngredientSpanStart.Value;
                    var ingLen = amount.IngredientSpanLength.Value;
                    if (
                        ingStart < amount.Start + amount.Length
                        || ingStart + ingLen > instruction.Length
                        || ingLen < 1
                    )
                    {
                        issues.Add(
                            $"{prefix} ingredient name span is invalid or outside the step text."
                        );
                    }
                    else
                    {
                        previousEnd = Math.Max(previousEnd, ingStart + ingLen);
                    }
                }
                else if (
                    amount.IngredientSpanStart.HasValue != amount.IngredientSpanLength.HasValue
                )
                {
                    issues.Add($"{prefix} ingredient name span is incomplete.");
                }
            }
        }
        issues.AddRange(InstructionAmountTagger.Reconcile(ingredients, steps));
        return issues;
    }

    private static readonly Regex CountAmountRegex = new(
        @"^(?:\d+(?:\s+\d+/\d+)?|\d+/\d+|\d*[½¼¾⅓⅔⅛]|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)(?:\s+(?<unit>[a-z]+))?$",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1)
    );

    /// <summary>
    /// A bare count ("2" in "2 apples") or a count of a custom unit ("1 piece") is a valid
    /// amount for a whole-item or custom-unit row even though it has no standard unit.
    /// </summary>
    private static bool IsCountAmount(string text, RecipeMeasurement measurement)
    {
        var match = CountAmountRegex.Match(text.Trim());
        if (!match.Success)
            return false;
        var unit = match.Groups["unit"];
        if (!unit.Success)
            return measurement.Unit is null or MeasurementUnit.Whole;
        return measurement.Unit == MeasurementUnit.Custom
            && measurement.CustomUnitName is { } custom
            && string.Equals(
                custom.Trim().TrimEnd('s'),
                unit.Value.TrimEnd('s'),
                StringComparison.OrdinalIgnoreCase
            );
    }
}
