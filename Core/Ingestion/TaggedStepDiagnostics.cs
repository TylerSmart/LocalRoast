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
                if (SourceMeasurementParser.FindSpans(text).Count == 0)
                    issues.Add($"{prefix} is no longer a measured amount.");
            }
        }
        issues.AddRange(InstructionAmountTagger.Reconcile(ingredients, steps));
        return issues;
    }
}
