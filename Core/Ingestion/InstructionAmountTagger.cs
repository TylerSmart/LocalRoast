using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using App.Core.Recipes;

namespace App.Core.Ingestion;

public sealed record MethodParagraph(int Number, string? SectionName, string Text);

public sealed record MethodAmountCandidate(
    string Id,
    int Paragraph,
    int Start,
    int Length,
    RecipeMeasurement Measurement,
    RecipeMeasurement? Alternative
);

public sealed record MethodDurationCandidate(
    string Id,
    int Paragraph,
    int Start,
    int Length,
    int Seconds
);

public sealed record MethodCandidates(
    IReadOnlyList<MethodParagraph> Paragraphs,
    IReadOnlyList<MethodAmountCandidate> Amounts,
    IReadOnlyList<MethodDurationCandidate> Durations
);

/// <summary>Model answers keyed by candidate id (A1, T1) and paragraph number.</summary>
public sealed record MethodTags(
    IReadOnlyDictionary<string, string> Amounts,
    IReadOnlyDictionary<string, string> Timers,
    IReadOnlyDictionary<int, IReadOnlyList<string>> StepIngredients
);

public sealed record TaggedMethodResult(
    IReadOnlyList<LlmExtractedStepItem> Steps,
    IReadOnlyList<string> Issues
);

/// <summary>
/// Keeps the source method verbatim. Code finds every amount and duration; the model only says
/// what each one measures, so amounts are never rewritten, dropped or miscopied.
/// </summary>
public static class InstructionAmountTagger
{
    public const string MixtureLabel = "mixture";
    public const string OtherLabel = "other";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex OrSeparator = new(
        @",?\s+or\s+",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex NotATimerBefore = new(
        @"\b(?:every|within|up\s+to|no\s+(?:more|longer)\s+than)\s+(?:about\s+)?$",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex NotATimerAfter = new(
        @"^\s+(?:before|ahead|in\s+advance|after\s+being|after\s+\w+ing\b|window\b)",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex InstructionsHeader = new(
        @"^[ \t]*Instructions:[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline,
        RegexTimeout
    );
    private static readonly Regex BracketSection = new(
        @"^\[(?<section>[^\]]+)\]\s*",
        RegexOptions.None,
        RegexTimeout
    );
    private static readonly Regex StepNumber = new(@"^\d+[.)]\s+", RegexOptions.None, RegexTimeout);
    private static readonly Regex SubStepNumber = new(
        @"^\d+[A-Za-z][.)]\s+",
        RegexOptions.None,
        RegexTimeout
    );
    private static readonly Regex CapsHeading = new(
        @"^(?<heading>[A-Z][A-Z0-9 ,'&/-]*[A-Z0-9]):\s+",
        RegexOptions.None,
        RegexTimeout
    );

    private const string DurationNumber =
        @"\d+(?:\.\d+)?(?:\s*[½¼¾⅓⅔])?|[½¼¾⅓⅔]|forty-five|eighteen|fifteen|eleven|twelve|twenty|thirty|forty|sixty|ninety|one|two|three|four|five|six|seven|eight|nine|ten";
    private const string DurationUnit = @"seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|weeks?";
    private static readonly Regex CompositeDuration = new(
        $@"(?<![\w.½¼¾-])(?<hours>{DurationNumber})[-\s]*(?:hours?|hrs?)\s*,?\s*(?:and\s+)?(?<minutes>{DurationNumber})[-\s]*(?:minutes?|mins?)\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex RangeDuration = new(
        $@"(?<![\w.½¼¾-])(?<low>{DurationNumber})(?:-?\s*(?:to|or)\s+|\s*[-–—]\s*)(?<high>{DurationNumber})[-\s]?(?<unit>{DurationUnit})\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex SingleDuration = new(
        $@"(?<![\w.½¼¾-])(?<value>{DurationNumber})[-\s]?(?<unit>{DurationUnit})\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );

    private static readonly Dictionary<string, decimal> NumberWords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["fifteen"] = 15,
        ["eighteen"] = 18,
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["forty-five"] = 45,
        ["sixty"] = 60,
        ["ninety"] = 90,
    };

    /// <summary>Reads the paragraphs of a canonical "Instructions:" section.</summary>
    public static IReadOnlyList<MethodParagraph> ParseMethod(string? source)
    {
        var paragraphs = new List<MethodParagraph>();
        var header = InstructionsHeader.Match(source ?? "");
        if (!header.Success)
            return paragraphs;
        string? heading = null;
        foreach (var rawLine in source![(header.Index + header.Length)..].Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            string? bracketSection = null;
            var bracket = BracketSection.Match(line);
            if (bracket.Success)
            {
                bracketSection = bracket.Groups["section"].Value.Trim();
                line = line[bracket.Length..];
            }
            line = SubStepNumber.Replace(StepNumber.Replace(line, ""), "");
            if (line.EndsWith(':') && line.Length <= 80 && !Regex.IsMatch(line, @"[.!?]"))
            {
                heading = line.TrimEnd(':').Trim();
                continue;
            }
            var caps = CapsHeading.Match(line);
            if (caps.Success && caps.Groups["heading"].Value.Count(char.IsLetter) >= 3)
            {
                heading = ToSentenceCase(caps.Groups["heading"].Value);
                line = line[caps.Length..];
            }
            if (line.Length > 0)
                paragraphs.Add(
                    new MethodParagraph(paragraphs.Count + 1, bracketSection ?? heading, line)
                );
        }
        return paragraphs;
    }

    public static MethodCandidates FindCandidates(IReadOnlyList<MethodParagraph> paragraphs)
    {
        var amounts = new List<MethodAmountCandidate>();
        var durations = new List<MethodDurationCandidate>();
        foreach (var paragraph in paragraphs)
        {
            var taken = new List<(int Start, int End)>();
            foreach (var span in SourceMeasurementParser.FindSpans(paragraph.Text))
            {
                amounts.Add(
                    new MethodAmountCandidate(
                        $"A{amounts.Count + 1}",
                        paragraph.Number,
                        span.Index,
                        span.Length,
                        span.Measurement,
                        span.Alternative
                    )
                );
                taken.Add((span.Index, span.Index + span.Length));
            }
            var found = new List<(int Start, int Length, int Seconds)>();
            void Add(Match match, decimal? seconds)
            {
                if (
                    seconds is null or < 1 or > 2_592_000
                    || taken.Any(t => match.Index < t.End && t.Start < match.Index + match.Length)
                )
                    return;
                taken.Add((match.Index, match.Index + match.Length));
                // Frequencies, limits and schedules are never something to time now.
                if (
                    NotATimerBefore.IsMatch(paragraph.Text[..match.Index])
                    || NotATimerAfter.IsMatch(paragraph.Text[(match.Index + match.Length)..])
                )
                    return;
                found.Add((match.Index, match.Length, (int)Math.Round(seconds.Value)));
            }
            foreach (Match match in CompositeDuration.Matches(paragraph.Text))
                Add(
                    match,
                    ParseDurationNumber(match.Groups["hours"].Value) * 3600
                        + ParseDurationNumber(match.Groups["minutes"].Value) * 60
                );
            foreach (Match match in RangeDuration.Matches(paragraph.Text))
            {
                var low = ParseDurationNumber(match.Groups["low"].Value);
                var high = ParseDurationNumber(match.Groups["high"].Value);
                if (low < high)
                    Add(match, low * UnitSeconds(match.Groups["unit"].Value));
            }
            foreach (Match match in SingleDuration.Matches(paragraph.Text))
                Add(
                    match,
                    ParseDurationNumber(match.Groups["value"].Value)
                        * UnitSeconds(match.Groups["unit"].Value)
                );
            foreach (var (start, length, seconds) in found.OrderBy(f => f.Start))
                durations.Add(
                    new MethodDurationCandidate(
                        $"T{durations.Count + 1}",
                        paragraph.Number,
                        start,
                        length,
                        seconds
                    )
                );
        }
        return new MethodCandidates(paragraphs, amounts, durations);
    }

    public static IReadOnlyList<string> GetIngredientLabels(
        IReadOnlyList<LlmExtractedIngredientItem> ingredients
    ) =>
        ingredients
            .Select(
                (ingredient, index) =>
                {
                    var name = (
                        ingredient.DisplayName ?? ingredient.CanonicalFoodName ?? "ingredient"
                    ).Trim();
                    var label = string.IsNullOrWhiteSpace(ingredient.PreparationNote)
                        ? $"{index} {name}"
                        : $"{index} {name} ({ingredient.PreparationNote.Trim()})";
                    return label.Length <= 90 ? label : label[..90];
                }
            )
            .ToList();

    public static string BuildPrompt(
        IReadOnlyList<LlmExtractedIngredientItem> ingredients,
        MethodCandidates candidates
    )
    {
        var labels = GetIngredientLabels(ingredients);
        var builder = new StringBuilder("INGREDIENTS:\n");
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ingredient = ingredients[i];
            var amount = ingredient.Quantity is null
                ? "unmeasured"
                : $"{FormatNumber(ingredient.Quantity.Value)}{(ingredient.MaxQuantity is { } max ? $"-{FormatNumber(max)}" : "")} {ingredient.Unit}".Trim();
            builder.Append(labels[i]).Append(" | ").Append(amount);
            if (ingredient.IsOptional)
                builder.Append(", optional");
            if (!string.IsNullOrWhiteSpace(ingredient.OriginalRawText))
                builder
                    .Append(" | source: \"")
                    .Append(ingredient.OriginalRawText.Trim())
                    .Append('"');
            builder.Append('\n');
        }
        builder.Append("\nMETHOD:\n");
        foreach (var paragraph in candidates.Paragraphs)
        {
            var marks = candidates
                .Amounts.Where(a => a.Paragraph == paragraph.Number)
                .Select(a => (a.Start, a.Length, a.Id))
                .Concat(
                    candidates
                        .Durations.Where(d => d.Paragraph == paragraph.Number)
                        .Select(d => (d.Start, d.Length, d.Id))
                )
                .OrderBy(m => m.Start);
            var text = new StringBuilder();
            var position = 0;
            foreach (var (start, length, id) in marks)
            {
                text.Append(paragraph.Text, position, start - position)
                    .Append('[')
                    .Append(id)
                    .Append(": ")
                    .Append(paragraph.Text, start, length)
                    .Append(']');
                position = start + length;
            }
            text.Append(paragraph.Text, position, paragraph.Text.Length - position);
            builder.Append("Step ").Append(paragraph.Number);
            if (paragraph.SectionName is not null)
                builder.Append(" (").Append(paragraph.SectionName).Append(')');
            builder.Append(": ").Append(text).Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>
    /// Builds verbatim steps from the tags and reconciles tagged amounts with ingredient totals.
    /// A single linked step without a stated amount receives the unstated remainder.
    /// </summary>
    public static TaggedMethodResult Assemble(
        IReadOnlyList<LlmExtractedIngredientItem> ingredients,
        MethodCandidates candidates,
        MethodTags tags
    )
    {
        var issues = new List<string>();
        var steps =
            new List<(
                MethodParagraph Paragraph,
                SortedSet<int> Indices,
                List<InlineStepAmount> Inline,
                Dictionary<int, RecipeMeasurement> Amounts,
                List<LlmExtractedTimerItem> Timers
            )>();
        foreach (var paragraph in candidates.Paragraphs)
        {
            var indices = new SortedSet<int>(
                (tags.StepIngredients.GetValueOrDefault(paragraph.Number) ?? [])
                    .Select(label => ParseIngredientLabel(label, ingredients.Count))
                    .OfType<int>()
            );
            var inline = new List<InlineStepAmount>();
            var amounts = new Dictionary<int, RecipeMeasurement>();
            foreach (
                var candidate in candidates.Amounts.Where(a => a.Paragraph == paragraph.Number)
            )
            {
                var text = paragraph.Text.Substring(candidate.Start, candidate.Length);
                if (!tags.Amounts.TryGetValue(candidate.Id, out var label))
                    issues.Add($"Step {paragraph.Number}: \"{text}\" was not classified.");
                var index = ParseIngredientLabel(label, ingredients.Count);
                var kind =
                    index is not null ? InlineStepAmountKind.Ingredient
                    : string.Equals(label?.Trim(), MixtureLabel, StringComparison.OrdinalIgnoreCase)
                        ? InlineStepAmountKind.Mixture
                    : InlineStepAmountKind.Fixed;
                inline.Add(
                    new InlineStepAmount(
                        candidate.Start,
                        candidate.Length,
                        text,
                        kind,
                        index,
                        candidate.Measurement,
                        candidate.Alternative
                    )
                );
                if (index is not { } ingredientIndex)
                    continue;
                indices.Add(ingredientIndex);
                if (!amounts.TryGetValue(ingredientIndex, out var existing))
                    amounts[ingredientIndex] = candidate.Measurement;
                else if (TryAdd(existing, candidate.Measurement, out var sum))
                    amounts[ingredientIndex] = sum;
                else
                    issues.Add(
                        $"Step {paragraph.Number}: \"{text}\" uses a different unit than another amount of the same ingredient."
                    );
            }
            var timers = candidates
                .Durations.Where(d => d.Paragraph == paragraph.Number)
                .Select(d => (d, Label: tags.Timers.GetValueOrDefault(d.Id)?.Trim()))
                .Where(t => !string.IsNullOrEmpty(t.Label))
                .Select(t => new LlmExtractedTimerItem(t.d.Seconds, t.Label))
                .ToList();
            steps.Add((paragraph, indices, inline, amounts, timers));
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            // "5 cups flour, plus extra": once a step has used the whole row without stating an
            // amount, later explicit amounts of that food come from the extra row.
            if (ingredients[i].Quantity is not > 0)
                continue;
            var extra = FindExtraSibling(ingredients, i);
            var firstUnstated = steps.FindIndex(s =>
                s.Indices.Contains(i) && !s.Amounts.ContainsKey(i)
            );
            var firstStated = steps.FindIndex(s => s.Amounts.ContainsKey(i));
            if (extra is not { } j || firstUnstated < 0 || firstStated < firstUnstated)
                continue;
            foreach (var step in steps.Where(s => s.Amounts.ContainsKey(i)))
            {
                step.Amounts[j] =
                    step.Amounts.TryGetValue(j, out var existing)
                    && TryAdd(existing, step.Amounts[i], out var sum)
                        ? sum
                        : step.Amounts[i];
                step.Amounts.Remove(i);
                step.Indices.Remove(i);
                step.Indices.Add(j);
                for (var k = 0; k < step.Inline.Count; k++)
                {
                    if (step.Inline[k].IngredientIndex == i)
                        step.Inline[k] = step.Inline[k] with { IngredientIndex = j };
                }
            }
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            // An amount that overflows its row but exactly matches an otherwise unused row of the
            // same food ("3 cups flour" + "1 tablespoon flour, for the pan") belongs to that row.
            var row = ingredients[i];
            if (row.Quantity is not > 0)
                continue;
            var rowHigh = row.MaxQuantity ?? row.Quantity.Value;
            var tolerance = Math.Max(0.0005m, rowHigh * 0.01m);
            var spans = steps
                .SelectMany(s => s.Inline.Select((amount, k) => (Step: s, K: k, Amount: amount)))
                .Where(x => x.Amount.IngredientIndex == i)
                .ToList();
            decimal total = 0;
            foreach (var span in spans)
            {
                if (
                    TryToRowUnit(
                        span.Amount.Measurement.Quantity!.Value,
                        span.Amount.Measurement.Unit,
                        row,
                        out var value
                    )
                )
                    total += value;
            }
            if (total <= rowHigh + tolerance)
                continue;
            var siblings = Enumerable
                .Range(0, ingredients.Count)
                .Where(j =>
                    j != i
                    && ingredients[j].Quantity is > 0
                    && string.Equals(
                        FoodName(ingredients[j]),
                        FoodName(row),
                        StringComparison.OrdinalIgnoreCase
                    )
                    && !steps.Any(s => s.Inline.Any(a => a.IngredientIndex == j))
                )
                .ToList();
            foreach (var j in siblings)
            {
                var sibling = ingredients[j];
                var match = spans.FindIndex(x =>
                    TryToRowUnit(
                        x.Amount.Measurement.Quantity!.Value,
                        x.Amount.Measurement.Unit,
                        sibling,
                        out var value
                    )
                    && Math.Abs(value - sibling.Quantity!.Value)
                        <= Math.Max(0.0005m, sibling.Quantity.Value * 0.01m)
                );
                if (match < 0)
                    continue;
                var (step, k, amount) = spans[match];
                spans.RemoveAt(match);
                step.Inline[k] = amount with { IngredientIndex = j };
                RebuildAmount(step.Inline, step.Amounts, i);
                RebuildAmount(step.Inline, step.Amounts, j);
                step.Indices.Add(j);
                if (!step.Inline.Any(a => a.IngredientIndex == i))
                    step.Indices.Remove(i);
                if (
                    TryToRowUnit(
                        amount.Measurement.Quantity!.Value,
                        amount.Measurement.Unit,
                        row,
                        out var moved
                    )
                )
                    total -= moved;
                if (total <= rowHigh + tolerance)
                    break;
            }
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            if (ingredients[i].Quantity is not > 0 || steps.Any(s => s.Indices.Contains(i)))
                continue;
            var mention = MentionPattern(ingredients[i]);
            var step = mention is null
                ? -1
                : steps.FindIndex(s => mention.IsMatch(s.Paragraph.Text));
            if (step >= 0)
                steps[step].Indices.Add(i);
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            // A single linked step without a stated amount receives the unstated remainder.
            var ingredient = ingredients[i];
            var tagged = steps.Where(s => s.Amounts.ContainsKey(i)).ToList();
            if (ingredient.Quantity is not > 0 || tagged.Count == 0)
                continue;
            if (
                !TrySumInRowUnit(
                    tagged.Select(s => s.Amounts[i]),
                    ingredient,
                    out var sumLow,
                    out var sumHigh
                )
            )
                continue;
            var rowLow = ingredient.Quantity.Value;
            var rowHigh = ingredient.MaxQuantity ?? rowLow;
            var tolerance = Math.Max(0.0005m, rowHigh * 0.01m);
            var remainderHigh = rowHigh - sumHigh;
            if (sumLow > rowHigh + tolerance || remainderHigh <= tolerance)
                continue;
            var remainderLow = rowLow - sumLow;
            var untagged = steps
                .Where(s => s.Indices.Contains(i) && !s.Amounts.ContainsKey(i))
                .ToList();
            if (untagged.Count != 1)
                continue;
            var (unit, custom) = RecipeDraftNormalizer.NormalizeUnit(
                ingredient.Unit,
                ingredient.CustomUnitName
            );
            untagged[0].Amounts[i] =
                remainderLow > tolerance
                    ? new RecipeMeasurement(
                        Math.Round(remainderLow, 4),
                        unit,
                        remainderHigh - remainderLow > tolerance
                            ? Math.Round(remainderHigh, 4)
                            : null,
                        custom
                    )
                    : new RecipeMeasurement(Math.Round(remainderHigh, 4), unit, null, custom);
        }

        var result = steps
            .Select(s => new LlmExtractedStepItem(
                s.Paragraph.SectionName,
                s.Paragraph.Text,
                s.Indices.ToList(),
                s.Timers,
                s.Amounts.OrderBy(a => a.Key)
                    .Select(a => new LlmExtractedStepIngredientAmountItem(
                        a.Key,
                        new LlmExtractedMeasurementItem(
                            a.Value.Quantity,
                            a.Value.Unit == MeasurementUnit.Custom
                                ? a.Value.CustomUnitName
                                : a.Value.Unit?.ToString().ToLowerInvariant(),
                            a.Value.MaxQuantity
                        )
                    ))
                    .ToList(),
                s.Paragraph.Text,
                s.Inline
            ))
            .ToList();
        issues.AddRange(Reconcile(ingredients, result));
        return new TaggedMethodResult(result, issues);
    }

    /// <summary>
    /// Checks the step amounts against the shopping-list rows: every measured row is used by a
    /// step, and the step amounts of a row add up to (and never exceed) its total.
    /// </summary>
    public static IReadOnlyList<string> Reconcile(
        IReadOnlyList<LlmExtractedIngredientItem> ingredients,
        IReadOnlyList<LlmExtractedStepItem> steps
    )
    {
        var issues = new List<string>();
        for (var i = 0; i < ingredients.Count; i++)
        {
            if (
                ingredients[i].Quantity is > 0
                && !steps.Any(s =>
                    s.IngredientIndices?.Contains(i) == true
                    || s.IngredientAmounts?.Any(a => a.IngredientIndex == i) == true
                )
            )
                issues.Add(
                    $"{ingredients[i].DisplayName ?? ingredients[i].CanonicalFoodName}: not used in any step."
                );
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            var ingredient = ingredients[i];
            var amounts = steps
                .Select(s =>
                    s.IngredientAmounts?.FirstOrDefault(a =>
                        a.IngredientIndex == i && a.Measurement?.Quantity is not null
                    )
                )
                .ToList();
            if (ingredient.Quantity is not > 0 || amounts.All(a => a is null))
                continue;
            var name = ingredient.DisplayName ?? ingredient.CanonicalFoodName ?? $"Ingredient {i}";
            if (
                !TrySumInRowUnit(
                    amounts
                        .OfType<LlmExtractedStepIngredientAmountItem>()
                        .Select(a => ToMeasurement(a.Measurement)),
                    ingredient,
                    out var sumLow,
                    out var sumHigh
                )
            )
            {
                issues.Add(
                    $"{name}: step amounts use units that cannot be compared with the ingredient list."
                );
                continue;
            }
            var rowLow = ingredient.Quantity.Value;
            var rowHigh = ingredient.MaxQuantity ?? rowLow;
            var tolerance = Math.Max(0.0005m, rowHigh * 0.01m);
            if (sumLow > rowHigh + tolerance)
            {
                issues.Add(
                    $"{name}: step amounts ({FormatNumber(sumLow)} {ingredient.Unit}) exceed the ingredient list ({FormatNumber(rowHigh)} {ingredient.Unit})."
                );
                continue;
            }
            if (rowHigh - sumHigh <= tolerance)
                continue;
            var untagged = steps
                .Where(
                    (s, k) =>
                        amounts[k] is null
                        && (
                            s.IngredientIndices?.Contains(i) == true
                            || s.IngredientAmounts?.Any(a => a.IngredientIndex == i) == true
                        )
                )
                .Count();
            // A single linked step without a stated amount implicitly uses the rest.
            if (untagged == 1)
                continue;
            issues.Add(
                untagged == 0
                    ? $"{name}: steps use {FormatNumber(sumLow)} of {FormatNumber(rowLow)} {ingredient.Unit}; link the step that uses the rest."
                    : $"{name}: steps use {FormatNumber(sumLow)} of {FormatNumber(rowLow)} {ingredient.Unit}; {untagged} steps could use the rest."
            );
        }
        return issues;
    }

    private static RecipeMeasurement ToMeasurement(LlmExtractedMeasurementItem measurement)
    {
        var (unit, custom) = RecipeDraftNormalizer.NormalizeUnit(measurement.Unit);
        return new RecipeMeasurement(measurement.Quantity, unit, measurement.MaxQuantity, custom);
    }

    private static bool TrySumInRowUnit(
        IEnumerable<RecipeMeasurement> amounts,
        LlmExtractedIngredientItem ingredient,
        out decimal sumLow,
        out decimal sumHigh
    )
    {
        sumLow = 0;
        sumHigh = 0;
        foreach (var amount in amounts)
        {
            if (
                !TryToRowUnit(amount.Quantity!.Value, amount.Unit, ingredient, out var low)
                || !TryToRowUnit(
                    amount.MaxQuantity ?? amount.Quantity!.Value,
                    amount.Unit,
                    ingredient,
                    out var high
                )
            )
                return false;
            sumLow += low;
            sumHigh += high;
        }
        return true;
    }

    /// <summary>
    /// Deterministic fixes for the shopping list: an "or" alternative the model split into its
    /// own row is folded back into the first food, and a dropped "(25 ounces)" equivalent is
    /// restored from the source line.
    /// </summary>
    public static IReadOnlyList<LlmExtractedIngredientItem> RepairIngredientRows(
        IReadOnlyList<LlmExtractedIngredientItem> rows
    )
    {
        var result = new List<LlmExtractedIngredientItem>();
        foreach (var row in rows)
        {
            var current = RestoreSourceMeasurement(row);
            if (result.Count > 0 && IsSplitAlternative(result[^1], current))
            {
                var note = $"or {current.DisplayName ?? current.CanonicalFoodName}";
                result[^1] = result[^1] with
                {
                    PreparationNote = string.IsNullOrWhiteSpace(result[^1].PreparationNote)
                        ? note
                        : $"{result[^1].PreparationNote}; {note}",
                };
                continue;
            }
            result.Add(current);
        }
        return result;
    }

    private static string SourceLine(LlmExtractedIngredientItem row) =>
        (row.OriginalRawText ?? "").Trim().TrimStart('-', '•', '*').Trim();

    private static bool IsSplitAlternative(
        LlmExtractedIngredientItem first,
        LlmExtractedIngredientItem second
    )
    {
        var line = SourceLine(first);
        if (line.Length == 0 || !string.Equals(line, SourceLine(second), StringComparison.Ordinal))
            return false;
        var or = OrSeparator.Match(line);
        if (!or.Success)
            return false;
        var (before, after) = (line[..or.Index], line[(or.Index + or.Length)..]);
        var firstName = MentionPattern(first);
        var secondName = MentionPattern(second);
        return firstName is not null
            && secondName is not null
            && firstName.IsMatch(before)
            && secondName.IsMatch(after)
            && !secondName.IsMatch(before);
    }

    private static LlmExtractedIngredientItem RestoreSourceMeasurement(
        LlmExtractedIngredientItem row
    )
    {
        if (row.Quantity is not > 0)
            return row;
        var (unit, _) = RecipeDraftNormalizer.NormalizeUnit(row.Unit, row.CustomUnitName);
        var span = SourceMeasurementParser
            .FindSpans(SourceLine(row))
            .FirstOrDefault(s =>
                s.Measurement.Quantity == row.Quantity && s.Measurement.Unit == unit
            );
        if (span is null)
            return row;
        // The source line decides the range; models sometimes put "(24 3/4 ounces)" in maxQuantity.
        row = row with
        {
            MaxQuantity = span.Measurement.MaxQuantity,
        };
        return
            row.AlternativeMeasurement is null
            && span.Alternative is { Quantity: not null } alternative
            ? row with
            {
                AlternativeMeasurement = new LlmExtractedMeasurementItem(
                    alternative.Quantity,
                    alternative.Unit?.ToString().ToLowerInvariant(),
                    alternative.MaxQuantity
                ),
            }
            : row;
    }

    private static void RebuildAmount(
        List<InlineStepAmount> inline,
        Dictionary<int, RecipeMeasurement> amounts,
        int index
    )
    {
        amounts.Remove(index);
        foreach (var amount in inline.Where(a => a.IngredientIndex == index))
        {
            amounts[index] =
                !amounts.TryGetValue(index, out var existing) ? amount.Measurement
                : TryAdd(existing, amount.Measurement, out var sum) ? sum
                : existing;
        }
    }

    private static int? FindExtraSibling(
        IReadOnlyList<LlmExtractedIngredientItem> ingredients,
        int index
    )
    {
        var name = FoodName(ingredients[index]);
        if (name is null)
            return null;
        for (var j = 0; j < ingredients.Count; j++)
        {
            if (
                j != index
                && ingredients[j].Quantity is null
                && string.Equals(FoodName(ingredients[j]), name, StringComparison.OrdinalIgnoreCase)
            )
                return j;
        }
        return null;
    }

    private static string? FoodName(LlmExtractedIngredientItem ingredient) =>
        (ingredient.CanonicalFoodName ?? ingredient.DisplayName)?.Trim() is { Length: > 0 } name
            ? name
            : null;

    /// <summary>Matches the ingredient's name or head noun, but not "flour mixture".</summary>
    private static Regex? MentionPattern(LlmExtractedIngredientItem ingredient)
    {
        var names = new[] { ingredient.DisplayName, ingredient.CanonicalFoodName }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .SelectMany(n => new[] { n!.Trim(), n.Trim().Split(' ')[^1] })
            .Where(n => n.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => Regex.Escape(n.TrimEnd('s')))
            .ToList();
        return names.Count == 0
            ? null
            : new Regex(
                $@"\b(?:{string.Join('|', names)})(?:s|es)?\b(?!\s+mixture)",
                RegexOptions.IgnoreCase,
                RegexTimeout
            );
    }

    public static int? ParseIngredientLabel(string? label, int ingredientCount)
    {
        var match = Regex.Match(label ?? "", @"^\s*(\d+)\b", RegexOptions.None, RegexTimeout);
        return
            match.Success
            && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var index)
            && index < ingredientCount
            ? index
            : null;
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
            return false;
        if (CulinaryMath.TryConvertQuantity(quantity, fromUnit, toUnit, out result))
            return true;
        // A weight in the step against a volume row (or vice versa) uses the listed equivalent.
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

    private static bool TryAdd(
        RecipeMeasurement first,
        RecipeMeasurement second,
        out RecipeMeasurement sum
    )
    {
        sum = first;
        if (
            first.Unit is not { } unit
            || second.Unit is not { } secondUnit
            || !CulinaryMath.TryConvertQuantity(
                second.Quantity!.Value,
                secondUnit,
                unit,
                out var low
            )
            || !CulinaryMath.TryConvertQuantity(
                second.MaxQuantity ?? second.Quantity!.Value,
                secondUnit,
                unit,
                out var high
            )
        )
            return false;
        var max =
            first.MaxQuantity is null && second.MaxQuantity is null
                ? (decimal?)null
                : (first.MaxQuantity ?? first.Quantity!.Value) + high;
        sum = first with { Quantity = first.Quantity!.Value + low, MaxQuantity = max };
        return true;
    }

    private static decimal? ParseDurationNumber(string value) =>
        NumberWords.TryGetValue(value.Trim(), out var word)
            ? word
            : FlexibleNullableDecimalJsonConverter.ParseNumber(value);

    private static decimal UnitSeconds(string unit) =>
        char.ToLowerInvariant(unit[0]) switch
        {
            's' => 1,
            'm' => 60,
            'h' => 3600,
            'd' => 86400,
            _ => 604800,
        };

    private static string ToSentenceCase(string heading)
    {
        var lower = heading.Trim().ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    private static string FormatNumber(decimal value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
