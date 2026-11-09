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
    IReadOnlyList<string> Issues,
    IReadOnlyList<LlmExtractedIngredientItem>? Ingredients = null
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
    private static readonly Regex AlternativeFoodName = new(
        @"^(?<first>.+?)\s+(?:and/or|or)\s+(?<second>.+)$",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex TrailingPreparation = new(
        @",\s*(?<prep>(?:(?:finely|coarsely|roughly|thinly)\s+)?(?:chopped|minced|diced|sliced|grated|peeled|softened|melted|drained|toasted|cut)\b.*)$",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex PlusPurposeRegex = new(
        @",\s*plus\s+(?:more\s+|extra\s+)?(?<extraFood>[a-z\s]*?)\bfor\s+(?<purpose>[^,.;()]+)\s*$",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex CleanPlusFromPrepNote = new(
        @"\(?\s*plus\s+.*?\bfor\s+[^)]+\)?",
        RegexOptions.IgnoreCase,
        RegexTimeout
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
    private static readonly Regex RatePhraseAfter = new(
        @"^\s*(?:[a-z]+\s+){0,4}at\s+a\s+time\b|^\s*(?:[a-z]+\s+){0,4}every\s+\d+",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex ConditionalTopUpClause = new(
        @"\bif\s+[^.;()]*?\b(?:dry|thick|thin|stiff|tight|evaporated|needed|necessary)\b|\badd\s+up\s+to\b|\bplus\s+(?:more|extra)\s+as\s+needed\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex BatchOrItemPattern = new(
        @"\b(?:toss|rub|brush|coat|season|sprinkle|divide)\s+(?:the\s+)?(?:interior\s+of\s+)?each\s+(?<item>batch|sheet|pan|thigh|breast|half|piece|fillet|cutlet|side)\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex PassivePrecedingClauseRegex = new(
        @"^\b(?:while|as|until|once|after|when|drain|rinse|return|transfer)\b",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex PassivePrecedingPrepositionRegex = new(
        @"(?:to|over|into)\s+(?:the\s+|warm\s+|hot\s+|cooled\s+|cooked\s+)?$|with\s+(?:the\s+|warm\s+|hot\s+|cooled\s+|cooked\s+)$",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex IngredientPrefixRegex = new(
        @"^\s*(?:of\s+)?(?:the\s+)?(?:reserved\s+|extra\s+|warm\s+|hot\s+|cold\s+|boiling\s+|minced\s+|chopped\s+|sliced\s+|diced\s+|grated\s+|crushed\s+|ground\s+|fresh\s+|dry\s+|whole\s+|melted\s+|softened\s+|raw\s+|cooked\s+)*",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex IngredientPrefixFullRegex = new(
        @"^\s*(?:of\s+)?(?:the\s+)?(?:reserved\s+|extra\s+|warm\s+|hot\s+|cold\s+|boiling\s+|minced\s+|chopped\s+|sliced\s+|diced\s+|grated\s+|crushed\s+|ground\s+|fresh\s+|dry\s+|whole\s+|melted\s+|softened\s+|raw\s+|cooked\s+)*$",
        RegexOptions.IgnoreCase,
        RegexTimeout
    );
    private static readonly Regex MixtureWordRegex = new(
        @"^(?:water|mixture|broth|stock|liquid|sauce)\b",
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
        IReadOnlyList<LlmExtractedIngredientItem> rawIngredients,
        MethodCandidates candidates,
        MethodTags tags
    )
    {
        var ingredients = rawIngredients.ToList();
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

        // Reclassify rate spans (e.g. "stir in 2 tablespoons milk at a time (up to 1/2 cup)") as Fixed
        foreach (var step in steps)
        {
            var perIngredient = step
                .Inline.Where(a => a.IngredientIndex.HasValue)
                .GroupBy(a => a.IngredientIndex!.Value)
                .Where(g => g.Count() > 1);

            foreach (var group in perIngredient)
            {
                var ingIndex = group.Key;
                foreach (var span in group)
                {
                    var afterSpan = step.Paragraph.Text.Substring(span.Start + span.Length);
                    if (RatePhraseAfter.IsMatch(afterSpan))
                    {
                        var spanIndex = step.Inline.IndexOf(span);
                        if (spanIndex >= 0)
                        {
                            step.Inline[spanIndex] = span with
                            {
                                Kind = InlineStepAmountKind.Fixed,
                                IngredientIndex = null,
                            };
                            RebuildAmount(step.Inline, step.Amounts, ingIndex);
                        }
                    }
                }
            }
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
                {
                    var stepGroups = spans.GroupBy(x => x.Step).ToList();
                    foreach (var group in stepGroups)
                    {
                        decimal stepSum = 0;
                        var allConverted = true;
                        foreach (var x in group)
                        {
                            if (
                                TryToRowUnit(
                                    x.Amount.Measurement.Quantity!.Value,
                                    x.Amount.Measurement.Unit,
                                    sibling,
                                    out var v
                                )
                            )
                            {
                                stepSum += v;
                            }
                            else
                            {
                                allConverted = false;
                                break;
                            }
                        }
                        if (
                            allConverted
                            && Math.Abs(stepSum - sibling.Quantity!.Value)
                                <= Math.Max(0.0005m, sibling.Quantity.Value * 0.01m)
                        )
                        {
                            var targetStep = group.Key;
                            for (var stepK = 0; stepK < targetStep.Inline.Count; stepK++)
                            {
                                if (targetStep.Inline[stepK].IngredientIndex == i)
                                {
                                    targetStep.Inline[stepK] = targetStep.Inline[stepK] with
                                    {
                                        IngredientIndex = j,
                                    };
                                }
                            }
                            RebuildAmount(targetStep.Inline, targetStep.Amounts, i);
                            RebuildAmount(targetStep.Inline, targetStep.Amounts, j);
                            targetStep.Indices.Add(j);
                            targetStep.Indices.Remove(i);
                            spans.RemoveAll(x => x.Step == targetStep);
                            total -= stepSum;
                            match = 0;
                            break;
                        }
                    }
                }
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

            if (total > rowHigh + tolerance)
            {
                var unmeasuredSiblings = Enumerable
                    .Range(0, ingredients.Count)
                    .Where(j =>
                        j != i
                        && ingredients[j].Quantity is null
                        && string.Equals(
                            FoodName(ingredients[j]),
                            FoodName(row),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ToList();
                foreach (var j in unmeasuredSiblings)
                {
                    var sibling = ingredients[j];
                    var match = spans.FindIndex(x =>
                        (
                            !string.IsNullOrWhiteSpace(sibling.PreparationNote)
                            && x.Step.Paragraph.Text.Contains(
                                sibling.PreparationNote.Replace("for ", "").Trim(),
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        || (
                            TryToRowUnit(
                                x.Amount.Measurement.Quantity!.Value,
                                x.Amount.Measurement.Unit,
                                row,
                                out var v
                            )
                            && v > rowHigh
                        )
                    );
                    if (match < 0 && spans.Count > 1)
                    {
                        match = spans.FindIndex(x =>
                            TryToRowUnit(
                                x.Amount.Measurement.Quantity!.Value,
                                x.Amount.Measurement.Unit,
                                row,
                                out var v
                            )
                            && v > (rowHigh - (total - v))
                        );
                    }
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

            if (total > rowHigh + tolerance && IsWaterIngredient(row))
            {
                for (var sIdx = 0; sIdx < spans.Count; sIdx++)
                {
                    var span = spans[sIdx];
                    if (
                        TryToRowUnit(
                            span.Amount.Measurement.Quantity!.Value,
                            span.Amount.Measurement.Unit,
                            row,
                            out var v
                        )
                        && v > rowHigh + tolerance
                    )
                    {
                        span.Step.Inline[span.K] = span.Amount with
                        {
                            Kind = InlineStepAmountKind.Fixed,
                            IngredientIndex = null,
                        };
                        RebuildAmount(span.Step.Inline, span.Step.Amounts, i);
                        if (!span.Step.Inline.Any(a => a.IngredientIndex == i))
                            span.Step.Indices.Remove(i);
                        total -= v;
                    }
                }
            }

            if (total > rowHigh + tolerance && IsOilOrFatIngredient(row))
            {
                for (var sIdx = spans.Count - 1; sIdx >= 0 && total > rowHigh + tolerance; sIdx--)
                {
                    var span = spans[sIdx];
                    var clause = GetClauseAroundSpan(
                        span.Step.Paragraph.Text,
                        span.Amount.Start,
                        span.Amount.Length
                    );
                    var isRenderedFat =
                        Regex.IsMatch(
                            clause,
                            @"\b(?:pour|drain|skim|spoon)\s+off\s+all\s+but\b|\breserved?\s+[^.;]*?\b(?:fat|butter|oil)\b|\bto\s+fat\b[^.;]*?\bto\s+equal\b|\b(?:rendered|chicken|bacon|pork|duck)\s+fat\b",
                            RegexOptions.IgnoreCase
                        )
                        || (
                            Regex.IsMatch(span.Amount.Text, @"\bfat\b", RegexOptions.IgnoreCase)
                            && !Regex.IsMatch(
                                row.CanonicalFoodName ?? row.DisplayName ?? "",
                                @"\bfat\b",
                                RegexOptions.IgnoreCase
                            )
                        );

                    if (isRenderedFat)
                    {
                        span.Step.Inline[span.K] = span.Amount with
                        {
                            Kind = InlineStepAmountKind.Mixture,
                            IngredientIndex = null,
                        };
                        RebuildAmount(span.Step.Inline, span.Step.Amounts, i);
                        if (!span.Step.Inline.Any(a => a.IngredientIndex == i))
                            span.Step.Indices.Remove(i);
                        if (
                            TryToRowUnit(
                                span.Amount.Measurement.Quantity!.Value,
                                span.Amount.Measurement.Unit,
                                row,
                                out var v
                            )
                        )
                        {
                            total -= v;
                        }
                    }
                }
            }

            if (total > rowHigh + tolerance && IsSaltIngredient(row))
            {
                for (var sIdx = spans.Count - 1; sIdx >= 0 && total > rowHigh + tolerance; sIdx--)
                {
                    var span = spans[sIdx];
                    var stepText = span.Step.Paragraph.Text;
                    if (
                        Regex.IsMatch(
                            stepText,
                            @"\b(?:boil|soak|soaking|pot)\b",
                            RegexOptions.IgnoreCase
                        ) && Regex.IsMatch(stepText, @"\bdrain\b", RegexOptions.IgnoreCase)
                    )
                    {
                        var unmeasuredSalt = Enumerable
                            .Range(0, ingredients.Count)
                            .FirstOrDefault(
                                j =>
                                    j != i
                                    && ingredients[j].Quantity is null
                                    && IsSaltIngredient(ingredients[j]),
                                -1
                            );

                        if (unmeasuredSalt >= 0)
                        {
                            span.Step.Inline[span.K] = span.Amount with
                            {
                                IngredientIndex = unmeasuredSalt,
                            };
                            span.Step.Indices.Add(unmeasuredSalt);
                        }
                        else
                        {
                            span.Step.Inline[span.K] = span.Amount with
                            {
                                Kind = InlineStepAmountKind.Fixed,
                                IngredientIndex = null,
                            };
                        }
                        RebuildAmount(span.Step.Inline, span.Step.Amounts, i);
                        if (!span.Step.Inline.Any(a => a.IngredientIndex == i))
                            span.Step.Indices.Remove(i);
                        if (
                            TryToRowUnit(
                                span.Amount.Measurement.Quantity!.Value,
                                span.Amount.Measurement.Unit,
                                row,
                                out var v
                            )
                        )
                        {
                            total -= v;
                        }
                    }
                }
            }

            if (total > rowHigh + tolerance)
            {
                for (var sIdx = spans.Count - 1; sIdx >= 0 && total > rowHigh + tolerance; sIdx--)
                {
                    var span = spans[sIdx];
                    var clause = GetClauseAroundSpan(
                        span.Step.Paragraph.Text,
                        span.Amount.Start,
                        span.Amount.Length
                    );
                    var isReservedOrPreppedReMeasurement =
                        Regex.IsMatch(
                            clause,
                            @"\breserved?\s+(?:remaining\s+)?(?:\d+|[½¼¾⅓⅔]|one|two|three|four)?\s*(?:tablespoons?|teaspoons?|cups?|slices?)?\s*(?:butter|oil|fat|portion|mixture|sauce|marinade|liquid)?\b",
                            RegexOptions.IgnoreCase
                        )
                        || (
                            span.Step.Paragraph.Number > 1
                            && Regex.IsMatch(
                                clause,
                                @"\bwrap\s+each\s+[^.;]*?\bwith\s+\d+\s+slice\b",
                                RegexOptions.IgnoreCase
                            )
                        );

                    if (isReservedOrPreppedReMeasurement)
                    {
                        span.Step.Inline[span.K] = span.Amount with
                        {
                            Kind = InlineStepAmountKind.Mixture,
                            IngredientIndex = null,
                        };
                        RebuildAmount(span.Step.Inline, span.Step.Amounts, i);
                        if (!span.Step.Inline.Any(a => a.IngredientIndex == i))
                            span.Step.Indices.Remove(i);
                        if (
                            TryToRowUnit(
                                span.Amount.Measurement.Quantity!.Value,
                                span.Amount.Measurement.Unit,
                                row,
                                out var v
                            )
                        )
                        {
                            total -= v;
                        }
                    }
                }
            }

            if (total > rowHigh + tolerance)
            {
                for (var sIdx = spans.Count - 1; sIdx >= 0 && total > rowHigh + tolerance; sIdx--)
                {
                    var span = spans[sIdx];
                    var clause = GetClauseAroundSpan(
                        span.Step.Paragraph.Text,
                        span.Amount.Start,
                        span.Amount.Length
                    );
                    var isConditional =
                        ConditionalTopUpClause.IsMatch(clause)
                        || Regex.IsMatch(
                            span.Step.Paragraph.Text,
                            @"\bif\s+(?:the\s+)?(?:pan|skillet|pot|mixture)\s+is\s+dry\b|\bif\s+needed\b|\bas\s+needed\b",
                            RegexOptions.IgnoreCase
                        )
                        || row.OriginalRawText?.Contains(
                            "plus more as needed",
                            StringComparison.OrdinalIgnoreCase
                        ) == true
                        || row.OriginalRawText?.Contains(
                            "plus extra as needed",
                            StringComparison.OrdinalIgnoreCase
                        ) == true;

                    if (isConditional)
                    {
                        span.Step.Inline[span.K] = span.Amount with
                        {
                            Kind = InlineStepAmountKind.Fixed,
                            IngredientIndex = null,
                        };
                        RebuildAmount(span.Step.Inline, span.Step.Amounts, i);
                        if (!span.Step.Inline.Any(a => a.IngredientIndex == i))
                            span.Step.Indices.Remove(i);
                        if (
                            TryToRowUnit(
                                span.Amount.Measurement.Quantity!.Value,
                                span.Amount.Measurement.Unit,
                                row,
                                out var v
                            )
                        )
                        {
                            total -= v;
                        }
                    }
                }
            }
        }

        // Multi-step batch/item multiplier resolution
        for (var i = 0; i < ingredients.Count; i++)
        {
            var row = ingredients[i];
            if (row.Quantity is not > 0)
                continue;
            var rowHigh = row.MaxQuantity ?? row.Quantity.Value;
            var tolerance = Math.Max(0.0005m, rowHigh * 0.01m);

            var taggedSteps = steps.Where(s => s.Amounts.ContainsKey(i)).ToList();
            if (taggedSteps.Count == 0)
                continue;

            foreach (var step in taggedSteps)
            {
                var stepAmount = step.Amounts[i];
                if (stepAmount.Quantity is not > 0)
                    continue;

                var span = step.Inline.FirstOrDefault(a => a.IngredientIndex == i);
                if (span is null)
                    continue;

                var sentence = GetClauseAroundSpan(step.Paragraph.Text, span.Start, span.Length);
                var match = BatchOrItemPattern.Match(sentence);
                if (!match.Success)
                    continue;

                var itemNoun = match.Groups["item"].Value.ToLowerInvariant();
                int? multiplier = null;

                var countMatch = Regex.Match(
                    step.Paragraph.Text,
                    @"\b(\d+|two|three|four|five|six|eight)\s+(?:prepared\s+)?(?:"
                        + Regex.Escape(itemNoun)
                        + @"s?|sheets|pans|batches|thighs|breasts|halves|pieces|fillets|cutlets|sides)\b",
                    RegexOptions.IgnoreCase
                );
                if (countMatch.Success)
                {
                    multiplier = TryParseCountWordOrDigit(countMatch.Groups[1].Value);
                }

                if (multiplier is null or < 2 && itemNoun is "thigh" or "half" or "side" or "batch")
                {
                    multiplier = 2;
                }

                if (multiplier is null or < 2)
                {
                    var primaryRow = ingredients.FirstOrDefault(r =>
                        r.Quantity is >= 2
                        && r.Unit is not null
                        && RecipeDraftNormalizer.NormalizeUnit(r.Unit, r.CustomUnitName).Unit
                            == MeasurementUnit.Whole
                        && (
                            r.CanonicalFoodName?.Contains(
                                itemNoun,
                                StringComparison.OrdinalIgnoreCase
                            ) == true
                            || r.DisplayName?.Contains(itemNoun, StringComparison.OrdinalIgnoreCase)
                                == true
                        )
                    );
                    if (primaryRow?.Quantity is { } pq && pq >= 2 && pq <= 12)
                    {
                        multiplier = (int)Math.Round(pq);
                    }
                }

                if (multiplier is >= 2)
                {
                    var m = multiplier.Value;
                    var multipliedQty = stepAmount.Quantity.Value * m;
                    var multipliedMax = stepAmount.MaxQuantity.HasValue
                        ? stepAmount.MaxQuantity.Value * m
                        : (decimal?)null;
                    var testMeasurement = stepAmount with
                    {
                        Quantity = multipliedQty,
                        MaxQuantity = multipliedMax,
                    };

                    var otherSteps = taggedSteps
                        .Where(s => s != step)
                        .Select(s => s.Amounts[i])
                        .ToList();
                    otherSteps.Add(testMeasurement);

                    if (
                        TrySumInRowUnit(otherSteps, row, out var testSumLow, out var testSumHigh)
                        && Math.Abs(testSumHigh - rowHigh) <= tolerance
                    )
                    {
                        step.Amounts[i] = testMeasurement;
                        break;
                    }
                }
            }
        }

        // Single-step per-item / rate consumption
        for (var i = 0; i < ingredients.Count; i++)
        {
            var row = ingredients[i];
            if (row.Quantity is not > 0)
                continue;
            var rowLow = row.Quantity.Value;
            var rowHigh = row.MaxQuantity ?? rowLow;

            var taggedSteps = steps.Where(s => s.Amounts.ContainsKey(i)).ToList();
            if (taggedSteps.Count != 1)
                continue;

            var singleStep = taggedSteps[0];
            if (!TrySumInRowUnit([singleStep.Amounts[i]], row, out var sumLow, out var sumHigh))
                continue;

            if (sumHigh >= rowLow)
                continue;

            var hasOtherRemainderStep = steps.Any(s => s != singleStep && s.Indices.Contains(i));
            if (hasOtherRemainderStep)
                continue;

            var stepText = singleStep.Paragraph.Text;
            if (
                Regex.IsMatch(
                    stepText,
                    @"\b(?:each|per|every|individual|at\s+a\s+time|in\s+batches)\b",
                    RegexOptions.IgnoreCase
                )
            )
            {
                var (rowUnit, custom) = RecipeDraftNormalizer.NormalizeUnit(
                    row.Unit,
                    row.CustomUnitName
                );
                singleStep.Amounts[i] = new RecipeMeasurement(
                    rowLow,
                    rowUnit,
                    row.MaxQuantity,
                    custom
                );
            }
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            if (ingredients[i].Quantity is not > 0)
                continue;
            var mention = MentionPattern(ingredients[i]);
            if (mention is null)
                continue;
            for (var sIdx = 0; sIdx < steps.Count; sIdx++)
            {
                var step = steps[sIdx];
                if (step.Indices.Contains(i))
                    continue;

                var alreadyHasFood =
                    step.Amounts.Keys.Any(k =>
                        string.Equals(
                            FoodName(ingredients[k]),
                            FoodName(ingredients[i]),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    || step.Inline.Any(a =>
                        a.IngredientIndex.HasValue
                        && string.Equals(
                            FoodName(ingredients[a.IngredientIndex.Value]),
                            FoodName(ingredients[i]),
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                if (alreadyHasFood)
                    continue;

                var stepText = step.Paragraph.Text;
                var alreadyUsedInEarlierStep = steps
                    .Take(sIdx)
                    .Any(s => s.Amounts.ContainsKey(i) || s.Indices.Contains(i));

                var matches = mention.Matches(stepText);
                var hasActiveMention = matches
                    .Cast<Match>()
                    .Any(m => !IsPassiveReference(stepText, m, alreadyUsedInEarlierStep));

                if (hasActiveMention)
                {
                    step.Indices.Add(i);
                }
            }
        }

        for (var sIdx = 0; sIdx < steps.Count; sIdx++)
        {
            var step = steps[sIdx];
            var stepText = step.Paragraph.Text;
            foreach (var i in step.Indices.ToList())
            {
                if (step.Amounts.ContainsKey(i) || step.Inline.Any(a => a.IngredientIndex == i))
                    continue;

                var alreadyUsedInEarlierStep = steps
                    .Take(sIdx)
                    .Any(s => s.Amounts.ContainsKey(i) || s.Indices.Contains(i));

                if (!alreadyUsedInEarlierStep)
                    continue;

                var mention = MentionPattern(ingredients[i]);
                if (mention is null)
                    continue;

                var matches = mention.Matches(stepText);
                var allPassive =
                    matches.Count > 0
                    && matches
                        .Cast<Match>()
                        .All(m => IsPassiveReference(stepText, m, alreadyUsedInEarlierStep));

                if (allPassive)
                {
                    step.Indices.Remove(i);
                }
            }
        }

        // Auto-link unmeasured ingredients used in "to taste" or "for serving" contexts
        for (var i = 0; i < ingredients.Count; i++)
        {
            if (ingredients[i].Quantity is not null)
                continue;
            var mention = MentionPattern(ingredients[i]);
            if (mention is null)
                continue;
            for (var sIdx = 0; sIdx < steps.Count; sIdx++)
            {
                var stepText = steps[sIdx].Paragraph.Text;
                if (
                    Regex.IsMatch(
                        stepText,
                        @"\b(?:season|sprinkle|serve|garnish)\b[^.;]*?\b(?:to\s+taste|for\s+serving|with\s+(?:the\s+)?lemon\s+wedges)\b",
                        RegexOptions.IgnoreCase
                    ) && mention.IsMatch(stepText)
                )
                {
                    steps[sIdx].Indices.Add(i);
                }
            }
        }

        // Link unstated remainder step if measured ingredient has 0 < sumHigh < rowHigh and no untagged step linked
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ingredient = ingredients[i];
            if (ingredient.Quantity is not > 0)
                continue;

            var tagged = steps.Where(s => s.Amounts.ContainsKey(i)).ToList();
            if (tagged.Count == 0)
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

            if (sumHigh >= rowHigh - tolerance)
                continue;

            var untagged = steps
                .Where(s => s.Indices.Contains(i) && !s.Amounts.ContainsKey(i))
                .ToList();
            if (untagged.Count > 0)
                continue;

            var mention = MentionPattern(ingredient);
            if (mention is null)
                continue;

            var candidatesWithoutAmount = steps.Where(s => !s.Amounts.ContainsKey(i)).ToList();
            var candidateMatches =
                new List<(
                    MethodParagraph Paragraph,
                    SortedSet<int> Indices,
                    List<InlineStepAmount> Inline,
                    Dictionary<int, RecipeMeasurement> Amounts,
                    List<LlmExtractedTimerItem> Timers
                )>();

            foreach (var cand in candidatesWithoutAmount)
            {
                var text = cand.Paragraph.Text;
                var foodWord = Regex.Escape(FoodName(ingredient) ?? "");
                var isMixtureOnly =
                    foodWord.Length > 0
                    && Regex.IsMatch(text, $@"\b{foodWord}\s+mixture\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(
                        text,
                        $@"\b(?:add|stir\s+in|whisk\s+in|transfer|remaining|pour)\b[^.;]*?\b{foodWord}\b(?!\s+mixture\b)",
                        RegexOptions.IgnoreCase
                    );

                if (!isMixtureOnly && mention.IsMatch(text))
                {
                    candidateMatches.Add(cand);
                }
            }

            if (candidateMatches.Count == 1)
            {
                candidateMatches[0].Indices.Add(i);
            }
            else if (candidateMatches.Count > 1)
            {
                var foodWord = Regex.Escape(FoodName(ingredient) ?? "");
                if (foodWord.Length > 0)
                {
                    var explicitMatch = candidateMatches.FirstOrDefault(c =>
                        Regex.IsMatch(
                            c.Paragraph.Text,
                            $@"\b(?:add\s+(?:the\s+)?|remaining\s+){foodWord}\b",
                            RegexOptions.IgnoreCase
                        )
                    );
                    if (explicitMatch.Paragraph is not null)
                    {
                        explicitMatch.Indices.Add(i);
                    }
                }
            }
        }

        for (var i = 0; i < ingredients.Count; i++)
        {
            var ingredient = ingredients[i];
            if (ingredient.Quantity is not > 0)
                continue;
            var linkedSteps = steps.Where(s => s.Indices.Contains(i)).ToList();
            if (
                linkedSteps.Count == 1
                && !linkedSteps[0].Amounts.ContainsKey(i)
                && !linkedSteps[0].Inline.Any(a => a.IngredientIndex == i)
            )
            {
                var (unit, custom) = RecipeDraftNormalizer.NormalizeUnit(
                    ingredient.Unit,
                    ingredient.CustomUnitName
                );
                linkedSteps[0].Amounts[i] = new RecipeMeasurement(
                    ingredient.Quantity.Value,
                    unit,
                    ingredient.MaxQuantity,
                    custom
                );
            }
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

        // Auto-heal divided ingredient source arithmetic typos
        for (var i = 0; i < ingredients.Count; i++)
        {
            var ingredient = ingredients[i];
            var rawText = ingredient.OriginalRawText ?? "";
            var note = ingredient.PreparationNote ?? "";
            var isDivided =
                Regex.IsMatch(rawText, @"\bdivided\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(note, @"\bdivided\b", RegexOptions.IgnoreCase);

            if (!isDivided || ingredient.Quantity is not > 0)
                continue;

            var tagged = steps.Where(s => s.Amounts.ContainsKey(i)).ToList();
            if (tagged.Count < 2)
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
            var (rowUnit, _) = RecipeDraftNormalizer.NormalizeUnit(
                ingredient.Unit,
                ingredient.CustomUnitName
            );

            if (sumLow > rowHigh && rowUnit is { } u)
            {
                var excess = sumLow - rowHigh;
                if (
                    CulinaryMath.TryConvertQuantity(
                        excess,
                        u,
                        MeasurementUnit.Tbsp,
                        out var excessTbsp
                    )
                    && excessTbsp <= 1.05m
                )
                {
                    var hasExplicitRemainingAmount = tagged.Any(s =>
                        Regex.IsMatch(
                            s.Paragraph.Text,
                            @"\bremaining\b[^.;]*?\b(?:tablespoons?|teaspoons?|cups?|oz|ounces?|pinch|dash|\d+|[½¼¾⅓⅔])\b",
                            RegexOptions.IgnoreCase
                        )
                    );

                    if (hasExplicitRemainingAmount)
                    {
                        var healedQuantity = Math.Round(sumLow, 4);
                        ingredients[i] = ingredient with
                        {
                            Quantity = healedQuantity,
                            MaxQuantity = null,
                        };
                    }
                }
            }
        }

        for (var stepIdx = 0; stepIdx < steps.Count; stepIdx++)
        {
            var step = steps[stepIdx];
            var stepText = step.Paragraph.Text;
            var unstatedIngredients = step
                .Amounts.Keys.Where(i => !step.Inline.Any(a => a.IngredientIndex == i))
                .ToList();

            if (unstatedIngredients.Count > 0)
            {
                var candidatesToInsert = new List<(int IngredientIndex, Match Match)>();
                foreach (var i in unstatedIngredients)
                {
                    var pattern = MentionPattern(ingredients[i]);
                    if (pattern is null)
                        continue;

                    foreach (Match m in pattern.Matches(stepText))
                    {
                        var overlaps = step.Inline.Any(span =>
                            m.Index < span.Start + span.Length && span.Start < m.Index + m.Length
                        );
                        if (!overlaps)
                        {
                            candidatesToInsert.Add((i, m));
                            break;
                        }
                    }
                }

                foreach (var (i, _) in candidatesToInsert.OrderBy(c => c.Match.Index))
                {
                    var amount = step.Amounts[i];
                    var scaleResult = CulinaryMath.ScaleAndConvert(
                        amount.Quantity,
                        amount.MaxQuantity,
                        amount.Unit,
                        amount.CustomUnitName,
                        1m,
                        UnitSystem.Original
                    );
                    var formattedAmount = string.IsNullOrWhiteSpace(scaleResult.UnitLabel)
                        ? scaleResult.FormattedAmount
                        : $"{scaleResult.FormattedAmount} {scaleResult.UnitLabel}";

                    if (string.IsNullOrWhiteSpace(formattedAmount))
                        continue;

                    var pattern = MentionPattern(ingredients[i]);
                    if (pattern is null)
                        continue;

                    Match? mentionMatch = null;
                    foreach (Match m in pattern.Matches(stepText))
                    {
                        var overlaps = step.Inline.Any(span =>
                        {
                            var spanEnd = span.Start + span.Length;
                            if (m.Index < spanEnd && span.Start < m.Index + m.Length)
                                return true;
                            if (
                                span.IngredientSpanStart.HasValue
                                && span.IngredientSpanLength.HasValue
                            )
                            {
                                var ingEnd =
                                    span.IngredientSpanStart.Value
                                    + span.IngredientSpanLength.Value;
                                if (
                                    m.Index < ingEnd
                                    && span.IngredientSpanStart.Value < m.Index + m.Length
                                )
                                    return true;
                            }
                            return false;
                        });
                        if (!overlaps)
                        {
                            mentionMatch = m;
                            break;
                        }
                    }

                    if (mentionMatch is null)
                        continue;

                    var hasPrecedingAmount = step.Inline.Any(span =>
                    {
                        var spanEnd = span.Start + span.Length;
                        if (spanEnd > mentionMatch.Index)
                            return false;
                        var between = stepText[spanEnd..mentionMatch.Index];
                        return IngredientPrefixFullRegex.IsMatch(between) && between.Length <= 25;
                    });
                    if (hasPrecedingAmount)
                        continue;

                    var sourceSpans = SourceMeasurementParser.FindSpans(
                        stepText[..mentionMatch.Index]
                    );
                    if (sourceSpans.Count > 0)
                    {
                        var lastSpan = sourceSpans[^1];
                        var lastSpanEnd = lastSpan.Index + lastSpan.Length;
                        var between = stepText[lastSpanEnd..mentionMatch.Index];
                        if (IngredientPrefixFullRegex.IsMatch(between) && between.Length <= 25)
                        {
                            continue;
                        }
                    }

                    var insertedPrefix = $"{formattedAmount} ";
                    var insertIndex = mentionMatch.Index;

                    stepText = stepText.Insert(insertIndex, insertedPrefix);

                    for (var sIdx = 0; sIdx < step.Inline.Count; sIdx++)
                    {
                        var existing = step.Inline[sIdx];
                        var newStart =
                            existing.Start >= insertIndex
                                ? existing.Start + insertedPrefix.Length
                                : existing.Start;
                        var newIngStart =
                            existing.IngredientSpanStart.HasValue
                            && existing.IngredientSpanStart.Value >= insertIndex
                                ? existing.IngredientSpanStart.Value + insertedPrefix.Length
                                : existing.IngredientSpanStart;

                        step.Inline[sIdx] = existing with
                        {
                            Start = newStart,
                            IngredientSpanStart = newIngStart,
                        };
                    }

                    var newSpan = new InlineStepAmount(
                        Start: insertIndex,
                        Length: formattedAmount.Length,
                        Text: formattedAmount,
                        Kind: InlineStepAmountKind.Ingredient,
                        IngredientIndex: i,
                        Measurement: amount,
                        AlternativeMeasurement: null,
                        IngredientSpanStart: insertIndex + insertedPrefix.Length,
                        IngredientSpanLength: mentionMatch.Length
                    );
                    step.Inline.Add(newSpan);
                }

                step = step with { Paragraph = step.Paragraph with { Text = stepText } };
                steps[stepIdx] = step;
            }

            for (var sIdx = 0; sIdx < step.Inline.Count; sIdx++)
            {
                var span = step.Inline[sIdx];
                if (span.IngredientSpanStart.HasValue)
                    continue;

                var afterIndex = span.Start + span.Length;
                if (afterIndex >= stepText.Length)
                    continue;

                var afterAmount = stepText[afterIndex..];
                var prefixMatch = IngredientPrefixRegex.Match(afterAmount);
                var candidateOffset = prefixMatch.Success ? prefixMatch.Length : 0;
                var candidateText = afterAmount[candidateOffset..];

                if (span.Kind == InlineStepAmountKind.Ingredient && span.IngredientIndex.HasValue)
                {
                    var ing = ingredients[span.IngredientIndex.Value];
                    var pattern = MentionPattern(ing);
                    if (pattern is not null)
                    {
                        var nameMatch = pattern.Match(candidateText);
                        if (nameMatch.Success && nameMatch.Index == 0)
                        {
                            var ingStart = afterIndex + candidateOffset;
                            var ingLength = nameMatch.Length;
                            step.Inline[sIdx] = span with
                            {
                                IngredientSpanStart = ingStart,
                                IngredientSpanLength = ingLength,
                            };
                        }
                    }
                }
                else if (span.Kind == InlineStepAmountKind.Mixture)
                {
                    var mixtureMatch = MixtureWordRegex.Match(candidateText);
                    if (mixtureMatch.Success && mixtureMatch.Index == 0)
                    {
                        var ingStart = afterIndex + candidateOffset;
                        var ingLength = mixtureMatch.Length;
                        step.Inline[sIdx] = span with
                        {
                            IngredientSpanStart = ingStart,
                            IngredientSpanLength = ingLength,
                        };
                    }
                    else
                    {
                        for (var ingI = 0; ingI < ingredients.Count; ingI++)
                        {
                            var pattern = MentionPattern(ingredients[ingI]);
                            if (pattern is null)
                                continue;
                            var nameMatch = pattern.Match(candidateText);
                            if (nameMatch.Success && nameMatch.Index == 0)
                            {
                                var ingStart = afterIndex + candidateOffset;
                                var ingLength = nameMatch.Length;
                                step.Inline[sIdx] = span with
                                {
                                    IngredientSpanStart = ingStart,
                                    IngredientSpanLength = ingLength,
                                };
                                break;
                            }
                        }
                    }
                }
            }
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
                s.Inline.OrderBy(a => a.Start).ToList()
            ))
            .ToList();
        issues.AddRange(Reconcile(ingredients, result));
        return new TaggedMethodResult(result, issues, ingredients);
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
            if (ingredient.IsRecipeReference)
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
                var incompatibleAmounts =
                    new List<(
                        int StepNum,
                        LlmExtractedStepIngredientAmountItem Amount,
                        decimal? InTbsp
                    )>();
                for (var sIdx = 0; sIdx < steps.Count; sIdx++)
                {
                    var a = steps[sIdx]
                        .IngredientAmounts?.FirstOrDefault(x =>
                            x.IngredientIndex == i && x.Measurement?.Quantity is not null
                        );
                    if (a is null)
                        continue;
                    var m = ToMeasurement(a.Measurement);
                    if (
                        !TryToRowUnit(m.Quantity!.Value, m.Unit, ingredient, out _)
                        || !TryToRowUnit(
                            m.MaxQuantity ?? m.Quantity!.Value,
                            m.Unit,
                            ingredient,
                            out _
                        )
                    )
                    {
                        decimal? inTbsp = null;
                        if (
                            m.Unit.HasValue
                            && CulinaryMath.TryConvertQuantity(
                                m.Quantity!.Value,
                                m.Unit.Value,
                                MeasurementUnit.Tbsp,
                                out var tbspVal
                            )
                        )
                        {
                            inTbsp = tbspVal;
                        }
                        incompatibleAmounts.Add((sIdx + 1, a, inTbsp));
                    }
                }

                var hasRemainingMention = steps.Any(s =>
                    (
                        s.IngredientIndices?.Contains(i) == true
                        || s.IngredientAmounts?.Any(a => a.IngredientIndex == i) == true
                    )
                    && Regex.IsMatch(
                        s.Instruction ?? "",
                        @"\b(?:remaining|all\s+but|transfer\s+remaining)\b",
                        RegexOptions.IgnoreCase
                    )
                );

                var hasBulkStep = steps.Any(s =>
                    (
                        s.IngredientIndices?.Contains(i) == true
                        || s.IngredientAmounts?.Any(a => a.IngredientIndex == i) == true
                    )
                    && (
                        s.IngredientAmounts is null
                        || !s.IngredientAmounts.Any(a =>
                            a.IngredientIndex == i
                            && a.Measurement?.Quantity is not null
                            && incompatibleAmounts.Any(ia => ia.Amount == a)
                        )
                    )
                );

                var allSubPortions =
                    incompatibleAmounts.Count > 0
                    && incompatibleAmounts.All(ia => ia.InTbsp is <= 8m);

                if (
                    (hasBulkStep && allSubPortions)
                    || (
                        hasRemainingMention
                        && incompatibleAmounts.All(ia => ia.InTbsp is <= 8m || ia.InTbsp is null)
                    )
                )
                {
                    continue;
                }

                var firstIncompatible = incompatibleAmounts.FirstOrDefault();
                var stepNum = firstIncompatible.StepNum > 0 ? firstIncompatible.StepNum : 1;
                var amountText = firstIncompatible.Amount?.Measurement?.Quantity is not null
                    ? $"{FormatNumber(firstIncompatible.Amount.Measurement.Quantity.Value)} {firstIncompatible.Amount.Measurement.Unit}"
                    : "amount";
                if (firstIncompatible.StepNum > 0 && firstIncompatible.StepNum <= steps.Count)
                {
                    var matchingInline = steps[firstIncompatible.StepNum - 1]
                        .InlineAmounts?.FirstOrDefault(ia => ia.IngredientIndex == i);
                    if (!string.IsNullOrWhiteSpace(matchingInline?.Text))
                    {
                        amountText = matchingInline.Text;
                    }
                }
                var stepUnit =
                    firstIncompatible.Amount?.Measurement?.Unit?.ToString() ?? "unknown unit";
                var rowLowFmt = FormatNumber(ingredient.Quantity ?? 0);
                var rowUnitFmt = ingredient.Unit?.ToString() ?? ingredient.CustomUnitName ?? "unit";

                issues.Add(
                    $"Step {stepNum} (\"{amountText}\"): {name} step amount uses a unit ({stepUnit}) that cannot be compared with the ingredient list ({rowLowFmt} {rowUnitFmt})."
                );
                continue;
            }
            var rowLow = ingredient.Quantity.Value;
            var rowHigh = ingredient.MaxQuantity ?? rowLow;
            var tolerance = Math.Max(0.0005m, rowHigh * 0.01m);
            if (sumLow > rowHigh + tolerance)
            {
                if (ReconcilesAcrossSameFoodGroup(ingredients, steps, i))
                    continue;

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
            // When remaining/rest is mentioned or stated amounts are small sub-portions (<= 4 tbsp),
            // the linked bulk steps consume the row.
            var hasRemainingStep = steps.Any(s =>
                (
                    s.IngredientIndices?.Contains(i) == true
                    || s.IngredientAmounts?.Any(a => a.IngredientIndex == i) == true
                )
                && (
                    s.Instruction?.Contains("remaining", StringComparison.OrdinalIgnoreCase) == true
                    || s.Instruction?.Contains("the rest", StringComparison.OrdinalIgnoreCase)
                        == true
                )
            );
            var isSmallSubPortion = amounts
                .Where(a => a is not null)
                .All(a =>
                {
                    var m = ToMeasurement(a!.Measurement);
                    return m.Unit.HasValue
                        && CulinaryMath.TryConvertQuantity(
                            m.Quantity ?? 0,
                            m.Unit.Value,
                            MeasurementUnit.Tbsp,
                            out var tbsp
                        )
                        && tbsp <= 4m;
                });
            var hasRepeatStep = steps.Any(s =>
                s.Instruction is not null
                && (
                    Regex.IsMatch(
                        s.Instruction,
                        @"\brepeat(?:ing)?\b.*\b(?:second|other|remaining|next|each)\b|\brepeat(?:ing)?\s+(?:with\s+)?(?:the\s+)?remaining\b|\brepeat\s+step\b",
                        RegexOptions.IgnoreCase
                    )
                )
            );
            var isExactRepeatFraction =
                hasRepeatStep
                && (
                    Math.Abs(sumLow - (0.5m * rowHigh)) <= tolerance
                    || Math.Abs(sumLow - (0.3333m * rowHigh)) <= tolerance
                    || Math.Abs(sumLow - (0.25m * rowHigh)) <= tolerance
                    || Math.Abs(sumLow - (0.2m * rowHigh)) <= tolerance
                );

            if (
                untagged >= 1
                || isExactRepeatFraction
                || (untagged == 1 || (untagged > 1 && (hasRemainingStep || isSmallSubPortion)))
                || ReconcilesAcrossSameFoodGroup(ingredients, steps, i)
            )
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
        IReadOnlyList<LlmExtractedIngredientItem> rows,
        string? source = null
    )
    {
        var sanitized = rows.Select(SanitizeFoodNamesAndAlternatives).ToList();
        var result = new List<LlmExtractedIngredientItem>();
        for (var i = 0; i < sanitized.Count; i++)
        {
            var current = RestoreSourceMeasurement(sanitized[i], sanitized);
            if (current.IsRecipeReference)
            {
                var (normUnit, normCustom) = RecipeDraftNormalizer.NormalizeUnit(
                    current.Unit,
                    current.CustomUnitName
                );
                if (
                    normUnit == MeasurementUnit.Custom
                    && (
                        string.IsNullOrWhiteSpace(normCustom)
                        || normCustom.Equals("custom", StringComparison.OrdinalIgnoreCase)
                        || normCustom.Equals("recipe", StringComparison.OrdinalIgnoreCase)
                        || normCustom.Equals("batch", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    current = current with
                    {
                        Unit = MeasurementUnit.Whole.ToString(),
                        CustomUnitName = null,
                    };
                }
            }
            else
            {
                var textToCheck = current.OriginalRawText ?? current.DisplayName;
                if (
                    !string.IsNullOrWhiteSpace(textToCheck)
                    && RecipeDraftNormalizer.RecipeReferenceRegex.IsMatch(textToCheck)
                )
                {
                    var refTitle = RecipeDraftNormalizer.NormalizeRecipeReferenceTitle(textToCheck);
                    current = current with
                    {
                        IsRecipeReference = true,
                        ReferencedRecipeTitle = refTitle,
                        DisplayName = refTitle ?? current.DisplayName,
                        Unit = MeasurementUnit.Whole.ToString(),
                        CustomUnitName = null,
                    };
                }
            }

            if (result.Count > 0 && IsSplitAlternative(result[^1], current))
            {
                string note;
                if (
                    current.Quantity is > 0
                    && (
                        current.Quantity != result[^1].Quantity
                        || !string.Equals(
                            current.Unit,
                            result[^1].Unit,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                {
                    var unitStr =
                        current.Unit == MeasurementUnit.Custom.ToString()
                            ? current.CustomUnitName
                            : current.Unit?.ToLowerInvariant();
                    note =
                        $"or {FormatNumber(current.Quantity.Value)} {unitStr} {current.DisplayName ?? current.CanonicalFoodName}".Trim();
                }
                else
                {
                    note = $"or {current.DisplayName ?? current.CanonicalFoodName}";
                }
                result[^1] = result[^1] with
                {
                    PreparationNote = string.IsNullOrWhiteSpace(result[^1].PreparationNote)
                        ? note
                        : $"{result[^1].PreparationNote}; {note}",
                };
                continue;
            }

            if (
                !string.IsNullOrWhiteSpace(current.OriginalRawText)
                && rows.Count(r =>
                    string.Equals(
                        SourceLine(r),
                        SourceLine(current),
                        StringComparison.OrdinalIgnoreCase
                    )
                ) == 1
            )
            {
                var plusMatch = PlusPurposeRegex.Match(current.OriginalRawText);
                if (plusMatch.Success)
                {
                    var purpose = plusMatch.Groups["purpose"].Value.Trim();
                    var cleanedPrep = current.PreparationNote;
                    if (!string.IsNullOrWhiteSpace(cleanedPrep))
                    {
                        cleanedPrep = CleanPlusFromPrepNote
                            .Replace(cleanedPrep, "")
                            .Trim()
                            .Trim(';', ',')
                            .Trim();
                        if (string.IsNullOrWhiteSpace(cleanedPrep))
                            cleanedPrep = null;
                    }
                    var primaryRow = current with { PreparationNote = cleanedPrep };
                    var siblingRow = primaryRow with
                    {
                        Quantity = null,
                        MaxQuantity = null,
                        Unit = null,
                        CustomUnitName = null,
                        AlternativeMeasurement = null,
                        IsOptional = false,
                        PreparationNote = $"for {purpose}",
                    };
                    result.Add(primaryRow);
                    result.Add(siblingRow);
                    continue;
                }
            }

            result.Add(current);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            var sourceLines = RecipeExtractionDiagnostics.GetSourceIngredientLines(source);
            if (sourceLines.Count > 0)
            {
                // First: if any result row has missing OriginalRawText, match it against sourceLines
                for (var rIdx = 0; rIdx < result.Count; rIdx++)
                {
                    if (string.IsNullOrWhiteSpace(result[rIdx].OriginalRawText))
                    {
                        var food = FoodName(result[rIdx]);
                        if (!string.IsNullOrWhiteSpace(food))
                        {
                            var matchingLine = sourceLines.FirstOrDefault(sl =>
                                Regex.IsMatch(
                                    sl,
                                    $@"\b{Regex.Escape(food)}\b",
                                    RegexOptions.IgnoreCase
                                )
                            );
                            if (matchingLine is not null)
                            {
                                result[rIdx] = result[rIdx] with { OriginalRawText = matchingLine };
                            }
                        }
                    }
                }

                var hasAnyMatchingIngredient = result.Any(r =>
                    !string.IsNullOrWhiteSpace(FoodName(r))
                    && sourceLines.Any(sl =>
                        Regex.IsMatch(
                            sl,
                            $@"\b{Regex.Escape(FoodName(r)!)}\b",
                            RegexOptions.IgnoreCase
                        )
                    )
                );

                if (hasAnyMatchingIngredient)
                {
                    for (var lineIdx = 0; lineIdx < sourceLines.Count; lineIdx++)
                    {
                        var line = sourceLines[lineIdx];
                        var norm = NormalizeLine(line);
                        var hasMatch = result.Any(r =>
                            NormalizeLine(r.OriginalRawText) == norm
                            || (
                                !string.IsNullOrWhiteSpace(FoodName(r))
                                && Regex.IsMatch(
                                    line,
                                    $@"\b{Regex.Escape(FoodName(r)!)}\b",
                                    RegexOptions.IgnoreCase
                                )
                            )
                        );
                        if (!hasMatch)
                        {
                            var section = FindSectionForSourceLine(source, line);
                            var synthesized = SourceMeasurementParser.TryParseSourceIngredientLine(
                                line,
                                section
                            );
                            if (synthesized is not null)
                            {
                                var insertIdx = result.Count;
                                for (var prev = lineIdx - 1; prev >= 0; prev--)
                                {
                                    var prevNorm = NormalizeLine(sourceLines[prev]);
                                    var matchIdx = result.FindLastIndex(r =>
                                        NormalizeLine(r.OriginalRawText) == prevNorm
                                    );
                                    if (matchIdx >= 0)
                                    {
                                        insertIdx = matchIdx + 1;
                                        break;
                                    }
                                }
                                if (insertIdx > result.Count)
                                    insertIdx = result.Count;
                                result.Insert(insertIdx, synthesized);
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    private static LlmExtractedIngredientItem SanitizeFoodNamesAndAlternatives(
        LlmExtractedIngredientItem row
    )
    {
        var displayName = row.DisplayName;
        var canonical = row.CanonicalFoodName;
        var prepNote = row.PreparationNote;

        var nameToCheck = !string.IsNullOrWhiteSpace(displayName) ? displayName : canonical;
        if (!string.IsNullOrWhiteSpace(nameToCheck))
        {
            var match = AlternativeFoodName.Match(nameToCheck);
            if (match.Success)
            {
                var first = match.Groups["first"].Value.Trim();
                var second = match.Groups["second"].Value.Trim();
                var secondWords = second.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string firstChoice;
                if (!first.Contains(' ') && secondWords.Length >= 2)
                {
                    var lastNoun = secondWords[^1];
                    firstChoice = $"{first} {lastNoun}";
                }
                else
                {
                    firstChoice = first;
                }

                displayName = firstChoice;
                canonical = firstChoice;
                var noteToAdd = $"or {second}";
                if (string.IsNullOrWhiteSpace(prepNote))
                {
                    prepNote = noteToAdd;
                }
                else if (!prepNote.Contains(noteToAdd, StringComparison.OrdinalIgnoreCase))
                {
                    prepNote = $"{prepNote.TrimEnd(';', ',')}; {noteToAdd}";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var prepMatch = TrailingPreparation.Match(displayName);
            if (prepMatch.Success)
            {
                var prep = prepMatch.Groups["prep"].Value.Trim();
                displayName = TrailingPreparation.Replace(displayName, "").Trim();
                if (string.IsNullOrWhiteSpace(prepNote))
                {
                    prepNote = prep;
                }
                else if (!prepNote.Contains(prep, StringComparison.OrdinalIgnoreCase))
                {
                    prepNote = $"{prepNote.TrimEnd(';', ',')}; {prep}";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(canonical))
        {
            var prepMatch = TrailingPreparation.Match(canonical);
            if (prepMatch.Success)
            {
                var prep = prepMatch.Groups["prep"].Value.Trim();
                canonical = TrailingPreparation.Replace(canonical, "").Trim();
                if (string.IsNullOrWhiteSpace(prepNote))
                {
                    prepNote = prep;
                }
                else if (!prepNote.Contains(prep, StringComparison.OrdinalIgnoreCase))
                {
                    prepNote = $"{prepNote.TrimEnd(';', ',')}; {prep}";
                }
            }
        }

        return row with
        {
            DisplayName = displayName,
            CanonicalFoodName = canonical,
            PreparationNote = prepNote,
        };
    }

    private static string SourceLine(LlmExtractedIngredientItem row) =>
        (row.OriginalRawText ?? "").Trim().TrimStart('-', '•', '*').Trim();

    private static bool IsSplitAlternative(
        LlmExtractedIngredientItem first,
        LlmExtractedIngredientItem second
    )
    {
        var line = SourceLine(first);
        if (
            line.Length == 0
            || !string.Equals(line, SourceLine(second), StringComparison.OrdinalIgnoreCase)
        )
            return false;
        if (Regex.IsMatch(line, @"\bplus\b|^table\s+salt\s+and\b", RegexOptions.IgnoreCase))
            return false;
        var or = OrSeparator.Match(line);
        if (!or.Success)
            return false;
        var (before, after) = (line[..or.Index], line[(or.Index + or.Length)..]);

        var firstWords = GetFoodKeyWords(first);
        var secondWords = GetFoodKeyWords(second);
        var commonWords = firstWords
            .Intersect(secondWords, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var firstDistinct = firstWords.Where(w => !commonWords.Contains(w)).ToList();
        var secondDistinct = secondWords.Where(w => !commonWords.Contains(w)).ToList();

        if (firstDistinct.Count > 0 && secondDistinct.Count > 0)
        {
            var firstMatchesBefore = firstDistinct.Any(w =>
                Regex.IsMatch(before, $@"\b{Regex.Escape(w)}(?:s|es)?\b", RegexOptions.IgnoreCase)
            );
            var secondMatchesAfter = secondDistinct.Any(w =>
                Regex.IsMatch(after, $@"\b{Regex.Escape(w)}(?:s|es)?\b", RegexOptions.IgnoreCase)
            );
            var secondInBefore = secondDistinct.Any(w =>
                Regex.IsMatch(before, $@"\b{Regex.Escape(w)}(?:s|es)?\b", RegexOptions.IgnoreCase)
            );
            return firstMatchesBefore && secondMatchesAfter && !secondInBefore;
        }

        var firstName = MentionPattern(first);
        var secondName = MentionPattern(second);
        return firstName is not null
            && secondName is not null
            && firstName.IsMatch(before)
            && secondName.IsMatch(after)
            && !secondName.IsMatch(before);
    }

    private static List<string> GetFoodKeyWords(LlmExtractedIngredientItem item) =>
        new[] { item.DisplayName, item.CanonicalFoodName }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .SelectMany(n => n!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.Trim().TrimEnd('s'))
            .Where(w => w.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? FindSectionForSourceLine(string source, string line)
    {
        string? currentSection = null;
        var inIngredients = false;
        foreach (var raw in source.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Equals("Ingredients:", StringComparison.OrdinalIgnoreCase))
            {
                inIngredients = true;
                continue;
            }
            if (
                inIngredients && trimmed.Equals("Instructions:", StringComparison.OrdinalIgnoreCase)
            )
            {
                break;
            }
            if (!inIngredients)
                continue;

            var bracket = BracketSection.Match(trimmed);
            if (bracket.Success)
            {
                currentSection = bracket.Groups["section"].Value.Trim();
                continue;
            }
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed.Trim('[', ']').Trim();
                continue;
            }

            if (NormalizeLine(trimmed) == NormalizeLine(line))
            {
                return currentSection;
            }
        }
        return currentSection;
    }

    private static string NormalizeLine(string? line) =>
        Regex.Replace((line ?? "").Trim().TrimStart('-', '•', '*', ' '), @"\s+", " ");

    private static LlmExtractedIngredientItem RestoreSourceMeasurement(
        LlmExtractedIngredientItem row,
        IReadOnlyList<LlmExtractedIngredientItem> allRows
    )
    {
        var line = SourceLine(row);
        if (
            allRows.Count(r => SourceLine(r) == line) == 1
            && SourceMeasurementParser.TrySumCompoundLeadingMeasurement(line) is { } compound
        )
        {
            row = row with
            {
                Quantity = compound.Quantity,
                MaxQuantity = null,
                Unit =
                    compound.Unit == MeasurementUnit.Custom
                        ? compound.CustomUnitName
                        : compound.Unit?.ToString().ToLowerInvariant(),
                CustomUnitName =
                    compound.Unit == MeasurementUnit.Custom ? compound.CustomUnitName : null,
            };
        }

        if (row.Quantity is not > 0)
            return row;
        var (unit, _) = RecipeDraftNormalizer.NormalizeUnit(row.Unit, row.CustomUnitName);
        var span = SourceMeasurementParser
            .FindSpans(line)
            .FirstOrDefault(s =>
                s.Measurement.Quantity is { } sq
                && Math.Abs(sq - row.Quantity.Value) <= 0.02m
                && s.Measurement.Unit == unit
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

    public static bool IsPassiveReference(
        string stepText,
        Match mentionMatch,
        bool alreadyUsedInEarlierStep
    )
    {
        if (!alreadyUsedInEarlierStep)
        {
            return false;
        }

        var beforeMatch = stepText[..mentionMatch.Index];
        if (PassivePrecedingPrepositionRegex.IsMatch(beforeMatch))
        {
            return true;
        }

        var lastBoundary = beforeMatch.LastIndexOfAny(['.', ';', ',', '\n']);
        var clause = (lastBoundary >= 0 ? beforeMatch[(lastBoundary + 1)..] : beforeMatch).Trim();
        if (PassivePrecedingClauseRegex.IsMatch(clause))
        {
            return true;
        }

        return false;
    }

    /// <summary>Matches the ingredient's name or head noun, but not "flour mixture".</summary>
    private static Regex? MentionPattern(LlmExtractedIngredientItem ingredient)
    {
        var names = new[] { ingredient.DisplayName, ingredient.CanonicalFoodName }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .SelectMany(n => new[] { n!.Trim(), n.Trim().Split(' ')[^1] })
            .Where(n => n.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length)
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
        if (ingredient.IsRecipeReference)
        {
            result = quantity;
            return true;
        }

        var (rowUnit, _) = RecipeDraftNormalizer.NormalizeUnit(
            ingredient.Unit,
            ingredient.CustomUnitName
        );
        if (from is not { } fromUnit || rowUnit is not { } toUnit)
            return false;
        if (CulinaryMath.TryConvertQuantity(quantity, fromUnit, toUnit, out result))
            return true;

        // A weight in the step against a volume row (or vice versa) uses the listed equivalent.
        var altFromParser = SourceMeasurementParser
            .FindAlternative(ingredient.OriginalRawText)
            ?.Alternative;
        var altQty = ingredient.AlternativeMeasurement?.Quantity ?? altFromParser?.Quantity;
        var altUnitStr = ingredient.AlternativeMeasurement?.Unit ?? altFromParser?.Unit?.ToString();
        var (alternativeUnit, _) = RecipeDraftNormalizer.NormalizeUnit(altUnitStr);
        if (
            altQty is > 0
            && ingredient.Quantity is > 0
            && alternativeUnit is { } altUnit
            && CulinaryMath.TryConvertQuantity(quantity, fromUnit, altUnit, out var inAlternative)
        )
        {
            result = inAlternative * ingredient.Quantity.Value / altQty.Value;
            return true;
        }

        if (IsShreddedOrGratedCheese(ingredient) || IsDicedMeatOrPancetta(ingredient))
        {
            // 1 cup ≈ 4 oz = 1/4 lb (1 oz ≈ 1/4 cup = 4 tbsp)
            var isFromVolume = CulinaryMath.TryConvertQuantity(
                quantity,
                fromUnit,
                MeasurementUnit.Cup,
                out var inCups
            );
            var isToWeight = CulinaryMath.TryConvertQuantity(
                4m,
                MeasurementUnit.Oz,
                toUnit,
                out var ozInToUnit
            );
            if (isFromVolume && isToWeight)
            {
                result = inCups * ozInToUnit;
                return true;
            }

            var isFromWeight = CulinaryMath.TryConvertQuantity(
                quantity,
                fromUnit,
                MeasurementUnit.Oz,
                out var inOz
            );
            var isToVolume = CulinaryMath.TryConvertQuantity(
                0.25m,
                MeasurementUnit.Cup,
                toUnit,
                out var cupsInToUnit
            );
            if (isFromWeight && isToVolume)
            {
                result = inOz * cupsInToUnit;
                return true;
            }
        }

        if (IsScallionOrGreenOnion(ingredient))
        {
            // 1 whole scallion ≈ 2 tbsp (0.125 cup) sliced
            if (
                toUnit == MeasurementUnit.Whole
                && CulinaryMath.TryConvertQuantity(
                    quantity,
                    fromUnit,
                    MeasurementUnit.Tbsp,
                    out var inTbsp
                )
            )
            {
                result = inTbsp / 2m;
                return true;
            }
            if (
                fromUnit == MeasurementUnit.Whole
                && CulinaryMath.TryConvertQuantity(
                    quantity * 2m,
                    MeasurementUnit.Tbsp,
                    toUnit,
                    out var inToUnit
                )
            )
            {
                result = inToUnit;
                return true;
            }
        }

        // Check container unit size in OriginalRawText (e.g., 2 (15-ounce) cans chickpeas)
        if (rowUnit == MeasurementUnit.Can && ingredient.Quantity is > 0)
        {
            var container = SourceMeasurementParser.FindContainerUnitSize(
                ingredient.OriginalRawText
            );
            if (container?.Unit is not null && container.Quantity is > 0)
            {
                if (
                    CulinaryMath.TryConvertQuantity(
                        quantity,
                        fromUnit,
                        container.Unit.Value,
                        out var inContainerUnit
                    )
                )
                {
                    result = inContainerUnit / container.Quantity.Value;
                    return true;
                }
                if (
                    CulinaryMath.TryConvertQuantity(
                        quantity,
                        fromUnit,
                        MeasurementUnit.Tbsp,
                        out var inTbsp
                    )
                )
                {
                    var canTbsp = container.Unit.Value is MeasurementUnit.Oz or MeasurementUnit.FlOz
                        ? (
                            container.Quantity.Value >= 14m && container.Quantity.Value <= 16m
                                ? 28m
                                : container.Quantity.Value * 2m
                        )
                        : 28m;
                    result = inTbsp / canTbsp;
                    return true;
                }
            }
            else if (
                CulinaryMath.TryConvertQuantity(
                    quantity,
                    fromUnit,
                    MeasurementUnit.Tbsp,
                    out var inTbsp
                )
            )
            {
                result = inTbsp / 28m;
                return true;
            }
        }

        // Culinary count conversions: Clove
        if (rowUnit == MeasurementUnit.Clove)
        {
            // 1 clove ≈ 1 tsp minced = 1/3 tbsp
            if (
                CulinaryMath.TryConvertQuantity(
                    quantity,
                    fromUnit,
                    MeasurementUnit.Tsp,
                    out var inTsp
                )
            )
            {
                result = inTsp;
                return true;
            }
        }

        // Culinary count conversions: Whole citrus
        if (rowUnit == MeasurementUnit.Whole)
        {
            var food = FoodName(ingredient);
            if (!string.IsNullOrWhiteSpace(food))
            {
                if (
                    food.Contains("lemon", StringComparison.OrdinalIgnoreCase)
                    || food.Contains("lime", StringComparison.OrdinalIgnoreCase)
                )
                {
                    // 1 whole lemon/lime ≈ 3 tbsp juice
                    if (
                        CulinaryMath.TryConvertQuantity(
                            quantity,
                            fromUnit,
                            MeasurementUnit.Tbsp,
                            out var inTbsp
                        )
                    )
                    {
                        result = inTbsp / 3m;
                        return true;
                    }
                }
                else if (food.Contains("orange", StringComparison.OrdinalIgnoreCase))
                {
                    // 1 whole orange ≈ 1/3 cup ≈ 5.3333 tbsp
                    if (
                        CulinaryMath.TryConvertQuantity(
                            quantity,
                            fromUnit,
                            MeasurementUnit.Tbsp,
                            out var inTbsp
                        )
                    )
                    {
                        result = inTbsp / 5.3333m;
                        return true;
                    }
                }
            }
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

    private static bool IsShreddedOrGratedCheese(LlmExtractedIngredientItem ingredient)
    {
        var text =
            $"{ingredient.DisplayName} {ingredient.CanonicalFoodName} {ingredient.PreparationNote} {ingredient.OriginalRawText}";
        return Regex.IsMatch(
                text,
                @"\b(?:cheese|parmesan|cheddar|mozzarella|monterey\s+jack|jack|provolone|pecorino|asiago|gruyere|swiss|fontina|gorgonzola|feta)\b",
                RegexOptions.IgnoreCase
            )
            && Regex.IsMatch(
                text,
                @"\b(?:shredded|grated|crumbled|finely\s+shredded|coarsely\s+shredded)\b",
                RegexOptions.IgnoreCase
            );
    }

    private static bool IsDicedMeatOrPancetta(LlmExtractedIngredientItem ingredient)
    {
        var text =
            $"{ingredient.DisplayName} {ingredient.CanonicalFoodName} {ingredient.PreparationNote} {ingredient.OriginalRawText}";
        return Regex.IsMatch(
                text,
                @"\b(?:pancetta|bacon|prosciutto|ham|sausage|chicken|beef|pork|turkey)\b",
                RegexOptions.IgnoreCase
            )
            && Regex.IsMatch(text, @"\b(?:diced|chopped|cubed|minced)\b", RegexOptions.IgnoreCase);
    }

    private static bool IsWaterIngredient(LlmExtractedIngredientItem ingredient)
    {
        var text = $"{ingredient.DisplayName} {ingredient.CanonicalFoodName}".Trim();
        return Regex.IsMatch(text, @"\bwater\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(
                text,
                @"\b(?:rose|orange\s+blossom|chestnut)\b",
                RegexOptions.IgnoreCase
            );
    }

    private static bool IsOilOrFatIngredient(LlmExtractedIngredientItem ingredient)
    {
        var text = $"{ingredient.DisplayName} {ingredient.CanonicalFoodName}".Trim();
        return Regex.IsMatch(
            text,
            @"\b(?:oil|butter|ghee|lard|shortening)\b",
            RegexOptions.IgnoreCase
        );
    }

    private static bool IsSaltIngredient(LlmExtractedIngredientItem ingredient)
    {
        var text = $"{ingredient.DisplayName} {ingredient.CanonicalFoodName}".Trim();
        return Regex.IsMatch(
            text,
            @"\b(?:salt|table\s+salt|kosher\s+salt|sea\s+salt)\b",
            RegexOptions.IgnoreCase
        );
    }

    private static bool IsScallionOrGreenOnion(LlmExtractedIngredientItem ingredient)
    {
        var text =
            $"{ingredient.DisplayName} {ingredient.CanonicalFoodName} {ingredient.OriginalRawText}";
        return Regex.IsMatch(
            text,
            @"\b(?:scallions?|green\s+onions?|spring\s+onions?)\b",
            RegexOptions.IgnoreCase
        );
    }

    private static string GetClauseAroundSpan(string text, int start, int length)
    {
        var prevBreak = text.LastIndexOfAny(['.', ';', '(', ')'], Math.Max(0, start));
        var clauseStart = prevBreak >= 0 ? prevBreak + 1 : 0;
        var nextBreak = text.IndexOfAny(
            ['.', ';', '(', ')'],
            Math.Min(text.Length, start + length)
        );
        var clauseEnd = nextBreak >= 0 ? nextBreak : text.Length;
        return text.Substring(clauseStart, clauseEnd - clauseStart);
    }

    private static int? TryParseCountWordOrDigit(string text)
    {
        if (int.TryParse(text, CultureInfo.InvariantCulture, out var n))
            return n;
        return NumberWords.TryGetValue(text, out var val) ? (int)val : null;
    }

    private static bool ReconcilesAcrossSameFoodGroup(
        IReadOnlyList<LlmExtractedIngredientItem> ingredients,
        IReadOnlyList<LlmExtractedStepItem> steps,
        int ingredientIndex
    )
    {
        var targetFood = FoodName(ingredients[ingredientIndex]);
        if (string.IsNullOrWhiteSpace(targetFood))
            return false;

        var groupIndices = Enumerable
            .Range(0, ingredients.Count)
            .Where(idx =>
                ingredients[idx].Quantity is > 0
                && string.Equals(
                    FoodName(ingredients[idx]),
                    targetFood,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();

        if (groupIndices.Count <= 1)
            return false;

        var baseUnits = new[] { MeasurementUnit.Tbsp, MeasurementUnit.Oz, MeasurementUnit.Whole };
        foreach (var baseUnit in baseUnits)
        {
            decimal totalRow = 0;
            decimal totalStep = 0;
            var canConvertAll = true;

            foreach (var idx in groupIndices)
            {
                var ing = ingredients[idx];
                var (ingUnit, _) = RecipeDraftNormalizer.NormalizeUnit(
                    ing.Unit,
                    ing.CustomUnitName
                );
                if (
                    ingUnit is { } u
                    && CulinaryMath.TryConvertQuantity(
                        ing.Quantity!.Value,
                        u,
                        baseUnit,
                        out var rVal
                    )
                )
                {
                    totalRow += rVal;
                }
                else
                {
                    canConvertAll = false;
                    break;
                }

                foreach (var step in steps)
                {
                    var match = step.IngredientAmounts?.FirstOrDefault(a =>
                        a.IngredientIndex == idx && a.Measurement?.Quantity is not null
                    );
                    if (match is not null)
                    {
                        var m = ToMeasurement(match.Measurement);
                        if (
                            m.Unit is { } stepU
                            && CulinaryMath.TryConvertQuantity(
                                m.Quantity!.Value,
                                stepU,
                                baseUnit,
                                out var sVal
                            )
                        )
                        {
                            totalStep += sVal;
                        }
                        else
                        {
                            canConvertAll = false;
                            break;
                        }
                    }
                }
                if (!canConvertAll)
                    break;
            }

            if (canConvertAll && totalRow > 0)
            {
                var tol = Math.Max(0.0005m, totalRow * 0.02m);
                if (Math.Abs(totalStep - totalRow) <= tol)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
