using System.Text;
using System.Text.RegularExpressions;

namespace App.Core.Ingestion;

/// <summary>What one source line holds, as labeled by the model.</summary>
public enum RecipeSourceLineLabel
{
    Skip,
    Title,
    Detail,
    Description,
    Note,
    IngredientGroup,
    Ingredient,
    IngredientWrap,
    Instruction,
}

/// <summary>
/// Builds the canonical source layout (<c>Ingredients:</c> with <c>- </c> lines followed by
/// <c>Instructions:</c>) that <see cref="RecipeExtractionDiagnostics"/> validates against, from
/// one model label per source line. Every emitted word is copied verbatim from the source, so
/// nothing can be reworded or invented; only line breaks inside a wrapped ingredient, sentence
/// or numbered step are removed. <see cref="ApplyLayoutRules"/> first corrects the
/// labeling mistakes small models make on hard-wrapped or split text. The rules only look at
/// line shape (list markers, step numbers, punctuation, letter case and quantities), never at
/// any one site's or book's wording.
/// </summary>
public static class RecipeSourceSectioner
{
    /// <summary>Longer sources are extracted without sectioning to bound the request size.</summary>
    public const int MaxLines = 600;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly (string Name, RecipeSourceLineLabel Label)[] Labels =
    [
        ("title", RecipeSourceLineLabel.Title),
        ("detail", RecipeSourceLineLabel.Detail),
        ("description", RecipeSourceLineLabel.Description),
        ("note", RecipeSourceLineLabel.Note),
        ("ingredient_group", RecipeSourceLineLabel.IngredientGroup),
        ("ingredient", RecipeSourceLineLabel.Ingredient),
        ("ingredient_wrap", RecipeSourceLineLabel.IngredientWrap),
        ("instruction", RecipeSourceLineLabel.Instruction),
        ("skip", RecipeSourceLineLabel.Skip),
    ];

    /// <summary>The label names the model may return, in prompt order.</summary>
    public static IReadOnlyList<string> LabelNames { get; } = Labels.Select(l => l.Name).ToList();

    private static readonly Regex ListMarker = CreateRegex(
        @"^(?:[-*\u2022\u00B7\u2023\u2043\u25E6\u25AA\u25AB\u25A0\u25A1\u25A2\u25CB\u25CF\u2610\u2611\u2612\u2713\u2714\u2013\u2014]\s*)+"
    );

    private static readonly Regex SectionHeading = CreateRegex(
        @"^(?:ingredients?(?:\s+list)?|instructions?|directions?|method|preparation|steps?|notes?|recipe\s+notes|tips|recipe\s+tips|nutrition(?:\s+facts)?|equipment)\s*:?$",
        RegexOptions.IgnoreCase
    );

    // "Step 2" or a bare "2." on its own line.
    private static readonly Regex StepHeading = CreateRegex(
        @"^(?:step\s*)?\d+\s*[.):]?$",
        RegexOptions.IgnoreCase
    );

    // A line that starts a numbered step: "2. Whisk", "2) Whisk" or "Step 2: Whisk".
    private static readonly Regex StepNumber = CreateRegex(
        @"^(?:step\s*\d+\s*[.):]?|\d+\s*[.)])\s+\S",
        RegexOptions.IgnoreCase
    );

    private static readonly Regex Breadcrumb = CreateRegex(@"\s(?:/|>|\u00BB|\u203A)\s");

    // Sentence or heading end, optionally followed by closing quotes or brackets.
    private static readonly Regex TerminalPunctuation = CreateRegex(
        @"[.!?:][""')\]\u201D\u2019]*$"
    );

    // An ingredient line ending like this continues on the next line.
    private static readonly Regex ContinuedEnding = CreateRegex(
        @"(?:[,(/\u2013\u2014-]|\b(?:and|or|plus|about|to|of|for|with|into|in|at|by|from|such\s+as))$",
        RegexOptions.IgnoreCase
    );

    // A lower-case ingredient line starting like this finishes the previous line.
    private static readonly Regex ContinuingStart = CreateRegex(
        @"^(?:and|or|plus|into|onto|for|from|with|to|in|at|of|then)\b"
    );

    private static readonly Regex QuantityStart = CreateRegex(@"^[\d\u00BC-\u00BE\u2150-\u215E]");

    // An amount with no food, such as "1 1/2 cups", whose food is on the next line.
    private static readonly Regex QuantityOnly = CreateRegex(
        @"^[\d\s/.,\u00BC-\u00BE\u2150-\u215E-]+(?:\s*(?:cups?|c\.?|tablespoons?|tbsps?\.?|tbs\.?|teaspoons?|tsps?\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|g|kilograms?|kg|ml|millilit(?:er|re)s?|l|lit(?:er|re)s?|pinch(?:es)?|dash(?:es)?|cloves?|cans?|sticks?|quarts?|qts?\.?|pints?|pts?\.?))?$",
        RegexOptions.IgnoreCase
    );

    private static Regex CreateRegex(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>True when the source already has an <c>Ingredients:</c> list followed by <c>Instructions:</c>.</summary>
    public static bool HasCanonicalSections(string? source)
    {
        var sawIngredients = false;
        var sawIngredientLine = false;
        foreach (var line in (source ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("Ingredients:", StringComparison.OrdinalIgnoreCase))
            {
                sawIngredients = true;
            }
            else if (sawIngredients && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                sawIngredientLine = true;
            }
            else if (trimmed.Equals("Instructions:", StringComparison.OrdinalIgnoreCase))
            {
                return sawIngredientLine;
            }
        }
        return false;
    }

    /// <summary>Non-blank, trimmed source lines in their original order.</summary>
    public static IReadOnlyList<string> GetContentLines(string? source) =>
        (source ?? "")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    public static string NumberLines(IReadOnlyList<string> lines) =>
        string.Join('\n', lines.Select((line, index) => $"{index + 1}| {line}"));

    /// <summary>Maps a model label name to its label; unknown names become <see cref="RecipeSourceLineLabel.Skip"/>.</summary>
    public static RecipeSourceLineLabel ParseLabel(string? name)
    {
        var normalized = name?.Trim();
        foreach (var (labelName, label) in Labels)
        {
            if (string.Equals(labelName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }
        }
        return RecipeSourceLineLabel.Skip;
    }

    /// <summary>
    /// Returns the canonical text, or null when the labels have no usable ingredient or
    /// instruction lines.
    /// </summary>
    public static string? BuildStructuredText(
        IReadOnlyList<string> lines,
        IReadOnlyList<RecipeSourceLineLabel> labels
    )
    {
        var kinds = ApplyLayoutRules(lines, labels);
        var ingredientLines = Enumerable.Range(0, lines.Count).Where(i => IsIngredient(kinds[i]));
        var ingredientLineCount = ingredientLines.Count();
        var markedCount = ingredientLines.Count(i => ListMarker.IsMatch(lines[i]));
        // When the list marks every item, an unmarked line can only continue the item above it.
        var usesMarkers = ingredientLineCount >= 3 && markedCount * 3 >= ingredientLineCount * 2;
        // With numbered steps, every unnumbered method line belongs to the step above it.
        var numberedSteps =
            Enumerable
                .Range(0, lines.Count)
                .Count(i =>
                    kinds[i] == RecipeSourceLineLabel.Instruction && StepNumber.IsMatch(lines[i])
                ) >= 2;

        var title = new List<string>();
        var details = new List<string>();
        var description = new List<string>();
        var notes = new List<string>();
        var ingredients = new List<string>();
        var instructions = new List<string>();
        var ingredientCount = 0;
        var previousItem = -1;
        RecipeSourceLineLabel? previousKind = null;
        var inNumberedStep = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i];
            var kind = kinds[i];
            if (IsIngredient(kind))
            {
                if (ListMarker.IsMatch(text))
                {
                    kind = RecipeSourceLineLabel.Ingredient;
                }
                else if (LooksLikeGroupHeading(text))
                {
                    kind = RecipeSourceLineLabel.IngredientGroup;
                }
                else if (
                    kind == RecipeSourceLineLabel.Ingredient
                    && usesMarkers
                    && previousItem >= 0
                )
                {
                    kind = RecipeSourceLineLabel.IngredientWrap;
                }
            }

            if (kind == RecipeSourceLineLabel.Skip)
            {
                continue;
            }
            var continuesProse = previousKind == kind;
            previousKind = kind;
            switch (kind)
            {
                case RecipeSourceLineLabel.Title:
                    title.Add(text);
                    break;
                case RecipeSourceLineLabel.Detail:
                    AppendProse(
                        details,
                        text,
                        continuesProse && ContinuesSentence(details[^1], text, allowCapital: false)
                    );
                    break;
                case RecipeSourceLineLabel.Description:
                    AppendProse(
                        description,
                        text,
                        continuesProse && ContinuesSentence(description[^1], text)
                    );
                    break;
                case RecipeSourceLineLabel.Note:
                    AppendProse(notes, text, continuesProse && ContinuesSentence(notes[^1], text));
                    break;
                case RecipeSourceLineLabel.Instruction:
                    var startsStep = StartsStep(text);
                    inNumberedStep |= numberedSteps && StepNumber.IsMatch(text);
                    AppendProse(
                        instructions,
                        text,
                        continuesProse
                            && !startsStep
                            && !IsMethodHeading(lines, kinds, i)
                            && (inNumberedStep || ContinuesSentence(instructions[^1], text))
                    );
                    break;
                case RecipeSourceLineLabel.IngredientGroup:
                    ingredients.Add(text);
                    break;
                case RecipeSourceLineLabel.IngredientWrap when previousItem >= 0:
                    ingredients[previousItem] += " " + text;
                    continue;
                default:
                    var item = ListMarker.Replace(text, "").Trim();
                    if (item.Length > 0)
                    {
                        ingredients.Add("- " + item);
                        previousItem = ingredients.Count - 1;
                        ingredientCount++;
                    }
                    continue;
            }
            previousItem = -1;
        }

        if (ingredientCount == 0 || instructions.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        if (title.Count > 0)
        {
            var titleText = string.Join(' ', title);
            if (!titleText.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append("Title: ");
            }
            builder.Append(titleText).Append('\n');
        }
        foreach (var line in details)
        {
            builder.Append(line).Append('\n');
        }
        AppendSection(builder, "Description:", description);
        AppendSection(builder, "Notes:", notes);
        AppendSection(builder, "Ingredients:", ingredients);
        AppendSection(builder, "Instructions:", instructions);
        return builder.ToString().Trim();
    }

    /// <summary>
    /// Corrects model labels with source-format-independent layout evidence. Missing labels
    /// count as <see cref="RecipeSourceLineLabel.Skip"/>.
    /// </summary>
    public static IReadOnlyList<RecipeSourceLineLabel> ApplyLayoutRules(
        IReadOnlyList<string> lines,
        IReadOnlyList<RecipeSourceLineLabel> labels
    )
    {
        var kinds = Enumerable
            .Range(0, lines.Count)
            .Select(i => i < labels.Count ? labels[i] : RecipeSourceLineLabel.Skip)
            .ToArray();
        SkipBreadcrumbTitles(lines, kinds);
        SkipSectionHeadings(lines, kinds);
        FixBlockOrder(lines, kinds);
        JoinContinuationLines(lines, kinds);
        FixIngredientShapes(lines, kinds);
        return kinds;
    }

    // Breadcrumbs such as "Home / Dinners / Orzo" often repeat the title.
    private static void SkipBreadcrumbTitles(
        IReadOnlyList<string> lines,
        RecipeSourceLineLabel[] kinds
    )
    {
        var titles = Enumerable
            .Range(0, kinds.Length)
            .Where(i => kinds[i] == RecipeSourceLineLabel.Title)
            .ToList();
        if (!titles.Any(i => !Breadcrumb.IsMatch(lines[i])))
        {
            return;
        }
        foreach (var i in titles.Where(i => Breadcrumb.IsMatch(lines[i])))
        {
            kinds[i] = RecipeSourceLineLabel.Skip;
        }
    }

    // "Ingredients", "Method", "Notes", "Step 2" and the like are headings, not content.
    private static void SkipSectionHeadings(
        IReadOnlyList<string> lines,
        RecipeSourceLineLabel[] kinds
    )
    {
        for (var i = 0; i < kinds.Length; i++)
        {
            var line = lines[i];
            if (
                SectionHeading.IsMatch(line)
                || (
                    StepHeading.IsMatch(line)
                    && (kinds[i] == RecipeSourceLineLabel.Instruction || char.IsLetter(line[0]))
                )
            )
            {
                kinds[i] = RecipeSourceLineLabel.Skip;
            }
        }
    }

    private static void FixBlockOrder(IReadOnlyList<string> lines, RecipeSourceLineLabel[] kinds)
    {
        var ingredientBlock = Enumerable
            .Range(0, kinds.Length)
            .Where(i => IsIngredient(kinds[i]) || kinds[i] == RecipeSourceLineLabel.IngredientGroup)
            .ToList();
        if (ingredientBlock.Count == 0)
        {
            return;
        }

        // The method follows the ingredient list, so "method" text above the list is a
        // headnote explaining how the recipe works.
        var first = ingredientBlock[0];
        var last = ingredientBlock[^1];
        if (kinds.Skip(last + 1).Contains(RecipeSourceLineLabel.Instruction))
        {
            for (var i = 0; i < first; i++)
            {
                if (kinds[i] == RecipeSourceLineLabel.Instruction)
                {
                    kinds[i] = RecipeSourceLineLabel.Description;
                }
            }
        }

        // With numbered steps the method starts at step 1, so quantity lines between the
        // ingredient list and step 1 are the last ingredients.
        var numbered = Enumerable
            .Range(0, kinds.Length)
            .Where(i =>
                kinds[i] == RecipeSourceLineLabel.Instruction && StepNumber.IsMatch(lines[i])
            )
            .ToList();
        if (numbered.Count < 2)
        {
            return;
        }
        var start = numbered[0];
        while (start > 0 && kinds[start - 1] == RecipeSourceLineLabel.Instruction)
        {
            start--;
        }
        if (
            start < numbered[0]
            && start > 0
            && IsIngredient(kinds[start - 1])
            && Enumerable
                .Range(start, numbered[0] - start)
                .All(i => QuantityStart.IsMatch(lines[i]))
        )
        {
            for (var i = start; i < numbered[0]; i++)
            {
                kinds[i] = RecipeSourceLineLabel.Ingredient;
            }
        }
    }

    // Hard-wrapped text: a line that continues the sentence or item above it shares its label.
    private static void JoinContinuationLines(
        IReadOnlyList<string> lines,
        RecipeSourceLineLabel[] kinds
    )
    {
        for (var i = 1; i < kinds.Length; i++)
        {
            var line = lines[i];
            var previousLine = lines[i - 1];
            var previous = kinds[i - 1];
            if (ListMarker.IsMatch(line) || StepNumber.IsMatch(line))
            {
                continue;
            }

            if (IsIngredient(previous) && IsIngredient(kinds[i]))
            {
                if (
                    kinds[i] == RecipeSourceLineLabel.Ingredient
                    && (ContinuedEnding.IsMatch(previousLine) || ContinuingStart.IsMatch(line))
                )
                {
                    kinds[i] = RecipeSourceLineLabel.IngredientWrap;
                }
                else if (
                    kinds[i] == RecipeSourceLineLabel.IngredientWrap
                    && QuantityStart.IsMatch(line)
                    && !ContinuedEnding.IsMatch(previousLine)
                    && !QuantityOnly.IsMatch(StripListMarker(previousLine))
                )
                {
                    kinds[i] = RecipeSourceLineLabel.Ingredient;
                }
                continue;
            }

            if (TerminalPunctuation.IsMatch(previousLine))
            {
                continue;
            }
            if (!StartsLowerCase(line))
            {
                // A wrapped sentence can also go on with a number, as in "1-inch border".
                if (
                    IsSentenceProse(previous)
                    && IsSentenceProse(kinds[i])
                    && QuantityStart.IsMatch(line)
                    && previousLine.Length > 40
                )
                {
                    kinds[i] = previous;
                }
                continue;
            }
            if (
                previous
                    is RecipeSourceLineLabel.Description
                        or RecipeSourceLineLabel.Note
                        or RecipeSourceLineLabel.Instruction
                && (
                    IsProse(kinds[i])
                    || IsIngredient(kinds[i])
                    || kinds[i] == RecipeSourceLineLabel.Skip
                )
            )
            {
                kinds[i] = previous;
            }
            else if (previous == RecipeSourceLineLabel.Detail && IsProse(kinds[i]))
            {
                kinds[i] = RecipeSourceLineLabel.Detail;
            }
            else if (IsIngredient(previous) && IsProse(kinds[i]))
            {
                kinds[i] = RecipeSourceLineLabel.IngredientWrap;
            }
        }
    }

    private static void FixIngredientShapes(
        IReadOnlyList<string> lines,
        RecipeSourceLineLabel[] kinds
    )
    {
        // A continuation needs an ingredient above it.
        RecipeSourceLineLabel? lastKind = null;
        for (var i = 0; i < kinds.Length; i++)
        {
            if (
                kinds[i] == RecipeSourceLineLabel.IngredientWrap
                && !(lastKind is { } k && IsIngredient(k))
            )
            {
                kinds[i] = RecipeSourceLineLabel.Ingredient;
            }
            if (kinds[i] != RecipeSourceLineLabel.Skip)
            {
                lastKind = kinds[i];
            }
        }

        var ingredientLines = Enumerable
            .Range(0, kinds.Length)
            .Where(i => IsIngredient(kinds[i]))
            .ToList();
        var upperCaseCount = ingredientLines.Count(i => IsUpperCase(lines[i]));
        for (var i = 0; i < kinds.Length; i++)
        {
            var line = lines[i];
            if (
                kinds[i] == RecipeSourceLineLabel.IngredientGroup
                && QuantityStart.IsMatch(StripListMarker(line))
            )
            {
                kinds[i] = RecipeSourceLineLabel.Ingredient;
            }
            // A short all-caps line such as "SAUCE" between items heads a group, unless the
            // whole list is in capitals.
            else if (
                IsIngredient(kinds[i])
                && !ListMarker.IsMatch(line)
                && line.Length <= 40
                && IsUpperCase(line)
                && !line.Any(char.IsDigit)
                && upperCaseCount * 2 < ingredientLines.Count
                && i + 1 < kinds.Length
                && IsIngredient(kinds[i + 1])
            )
            {
                kinds[i] = RecipeSourceLineLabel.IngredientGroup;
            }
            else if (
                kinds[i] == RecipeSourceLineLabel.Ingredient
                && i > 0
                && IsIngredient(kinds[i - 1])
                && !ListMarker.IsMatch(line)
                && QuantityOnly.IsMatch(StripListMarker(lines[i - 1]))
            )
            {
                kinds[i] = RecipeSourceLineLabel.IngredientWrap;
            }
        }
    }

    // Hard-wrapped prose is rejoined so a sentence, amount or duration is never split across
    // lines; the words themselves are still copied verbatim.
    private static void AppendProse(List<string> target, string text, bool join)
    {
        if (join)
        {
            target[^1] += " " + text;
        }
        else
        {
            target.Add(text);
        }
    }

    private static bool StartsStep(string line) =>
        StepNumber.IsMatch(line) || ListMarker.IsMatch(line) || StepHeading.IsMatch(line);

    // A line continues the sentence above when that line has no closing punctuation and this
    // one starts in lower case, or the line above is long enough to have been cut by wrapping.
    private static bool ContinuesSentence(string previous, string line, bool allowCapital = true) =>
        !StartsStep(line)
        && !TerminalPunctuation.IsMatch(previous)
        && (StartsLowerCase(line) || (allowCapital && previous.Length > 40));

    // A short capitalized method line such as "For the glaze" that leads into a new step.
    private static bool IsMethodHeading(
        IReadOnlyList<string> lines,
        IReadOnlyList<RecipeSourceLineLabel> kinds,
        int index
    )
    {
        var line = lines[index];
        if (line.Length > 40 || !char.IsUpper(line[0]) || line[^1] is '.' or '!' or '?')
        {
            return false;
        }
        for (var next = index + 1; next < lines.Count; next++)
        {
            if (kinds[next] != RecipeSourceLineLabel.Skip)
            {
                return kinds[next] != RecipeSourceLineLabel.Instruction || StartsStep(lines[next]);
            }
        }
        return true;
    }

    private static bool IsIngredient(RecipeSourceLineLabel label) =>
        label is RecipeSourceLineLabel.Ingredient or RecipeSourceLineLabel.IngredientWrap;

    private static bool IsSentenceProse(RecipeSourceLineLabel label) =>
        label
            is RecipeSourceLineLabel.Description
                or RecipeSourceLineLabel.Note
                or RecipeSourceLineLabel.Instruction;

    private static bool IsProse(RecipeSourceLineLabel label) =>
        label
            is RecipeSourceLineLabel.Description
                or RecipeSourceLineLabel.Note
                or RecipeSourceLineLabel.Instruction
                or RecipeSourceLineLabel.Detail;

    private static bool StartsLowerCase(string line)
    {
        var text = line.TrimStart('(', '"', '\'', '\u201C', '\u2018', '[');
        return text.Length > 0 && char.IsLower(text[0]);
    }

    private static bool IsUpperCase(string line) =>
        line.Any(char.IsLetter) && !line.Any(char.IsLower);

    private static string StripListMarker(string line) => ListMarker.Replace(line, "").Trim();

    private static void AppendSection(StringBuilder builder, string heading, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }
        if (builder.Length > 0)
        {
            builder.Append('\n');
        }
        builder.Append(heading).Append('\n');
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }
    }

    // A short line ending in a colon without any quantity is a sub-section heading such as
    // "For the sauce:" in any source format.
    private static bool LooksLikeGroupHeading(string text) =>
        text.Length <= 80 && text.EndsWith(':') && !text.Any(char.IsDigit);
}
