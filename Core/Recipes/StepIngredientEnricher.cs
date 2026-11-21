using System.Text;
using System.Text.RegularExpressions;
using App.Core.Ingestion;

namespace App.Core.Recipes;

/// <summary>
/// Post-processes compiled ingestion steps to link unindexed/untokenized ingredient mentions,
/// inject explicit measurements for single-use and fractional references, resolve "remaining"
/// divided amounts, and flag over-allocated ingredient quantities.
/// </summary>
public static partial class StepIngredientEnricher
{
    private sealed class MutableSegment
    {
        public string Text { get; set; } = string.Empty;
        public StepToken? Token { get; set; }
        public bool IsToken => Token is not null;
    }

    private sealed record TokenOccurrence(
        int StepIndex,
        int SegmentIndex,
        StepIngredientToken Token,
        string PrevText,
        string NextText,
        bool IsAdHoc,
        bool IsDistributive,
        bool IsFractional,
        decimal FractionValue,
        bool IsRemaining
    );

    private static readonly HashSet<string> PrepAndRecipeAdjectives = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "easy",
        "easy-peel",
        "peel",
        "quick",
        "fast",
        "simple",
        "basic",
        "classic",
        "traditional",
        "homemade",
        "best",
        "perfect",
        "ultimate",
        "ultracreamy",
        "crispy",
        "crunchy",
        "hard-cooked",
        "hard-boiled",
        "soft-cooked",
        "soft-boiled",
        "fresh",
        "freshly",
        "dried",
        "roasted",
        "toasted",
        "minced",
        "chopped",
        "diced",
        "sliced",
        "grated",
        "shredded",
        "ground",
        "melted",
        "softened",
        "warm",
        "warmed",
        "cold",
        "chilled",
        "hot",
        "room-temperature",
        "unsalted",
        "salted",
        "kosher",
        "fine",
        "coarse",
        "extra-virgin",
        "extra",
        "virgin",
        "large",
        "medium",
        "small",
        "whole",
        "boneless",
        "skinless",
        "skin-on",
        "bone-in",
        "prepared",
        "store-bought",
        "table",
        "granulated",
        "powdered",
        "confectioners",
        "all-purpose",
        "brown",
        "dark",
        "light",
        "white",
        "yellow",
        "red",
        "green",
        "black",
        "dijon",
        "whole-grain",
        "distilled",
        "cider",
        "sherry",
        "balsamic",
        "dry",
        "sweet",
        "unsweetened",
        "plain",
        "canned",
        "frozen",
        "thawed",
        "jarred",
        "packed",
        "crushed",
        "firm",
        "soft",
        "sharp",
        "mild",
        "smoked",
        "fire-roasted",
        "plum",
        "roma",
        "cherry",
        "yukon",
        "gold",
        "russet",
        "hearty",
        "sandwich",
        "lean",
        "percent",
        "long-grain",
        "short-grain",
    };

    private static readonly HashSet<string> FormSuffixWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "clove",
        "cloves",
        "leaf",
        "leaves",
        "floret",
        "florets",
        "wedge",
        "wedges",
        "sprig",
        "sprigs",
        "stalk",
        "stalks",
        "thread",
        "threads",
        "fillet",
        "fillets",
    };

    private static readonly HashSet<string> GenericHeadNounsForModifierExtract = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "pepper",
        "cheese",
        "chile",
        "chiles",
        "flakes",
    };

    private static readonly HashSet<string> StopAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "side",
        "batch",
        "serving",
        "servings",
        "recipe",
        "recipes",
        "piece",
        "pieces",
        "half",
        "part",
        "parts",
        "mixture",
        "top",
        "bottom",
        "heat",
        "pan",
        "pot",
        "bowl",
        "oven",
        "white",
        "black",
        "red",
        "green",
        "hot",
        "cold",
        "warm",
        "dry",
        "fresh",
        "dark",
        "light",
        "ground",
        "large",
        "small",
        "medium",
        "whole",
        "extra",
        "water",
        "ice",
    };

    private static readonly Dictionary<MeasurementUnit, string[]> RedundantCountUnitWords = new()
    {
        [MeasurementUnit.Clove] = ["cloves", "clove"],
        [MeasurementUnit.Slice] = ["slices", "slice"],
        [MeasurementUnit.Can] = ["cans", "can"],
        [MeasurementUnit.Bunch] = ["bunches", "bunch"],
    };

    [GeneratedRegex(
        @"\b(?:to\s+taste|as\s+needed|if\s+needed|for\s+garnish|to\s+garnish|for\s+serving|to\s+serve|extra(?![\s-]+virgin)|additional|more\s+for|for\s+dusting|for\s+greasing)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex AdHocOrGarnishRegex();

    [GeneratedRegex(
        @"\b(?:each|apiece|per\s+(?:serving|person|piece|slice|toast|portion|plate|dish|cake|muffin|cookie|taco|bowl))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DistributiveContextRegex();

    [GeneratedRegex(@"(?:^|[.!?]\s+)The\s+$", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceInitialTheRegex();

    [GeneratedRegex(
        @"^\s*(?:should|will|must|is|are|can|may)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SubjectVerbAfterTokenRegex();

    [GeneratedRegex(
        @"(?<prefix>(?:about\s+)?(?<frac>one-half|half|1/2|one-third|1/3|one-quarter|a\s+quarter|1/4)\s+(?:of\s+)?(?:the\s+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LeadingFractionPhraseRegex();

    [GeneratedRegex(
        @"(?<phrase>(?:(?<hasThe>\bthe)\s+)?(?:(?<rest>rest\s+of\s+the)|remaining)\s+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex LeadingRemainingPhraseRegex();

    [GeneratedRegex(
        @"\b(?:all\s+of\s+the|all\s+the)\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TrailingAllOfTheRegex();

    [GeneratedRegex(
        @"(?<=[a-zA-Z0-9,\s]\s+|[a-zA-Z0-9],)the\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex NonSentenceInitialTheRegex();

    [GeneratedRegex(
        @"(?:(?<=\s|,)the\s+|(?<=^|\s|,))(?<mods>(?:(?:lightly|finely|coarsely|freshly|thinly|roughly)\s+)?(?:melted|softened|chilled|warm|warmed|hot|cold|room-temperature|minced|chopped|diced|sliced|grated|shredded|toasted|crushed|beaten|whisked|sifted|drained|rinsed|peeled|halved|quartered|fresh|dried|frozen|thawed|cooked)(?:\s+(?:melted|softened|chilled|warm|warmed|hot|cold|minced|chopped|diced|sliced|grated|shredded|toasted|crushed|beaten|whisked|sifted|drained|rinsed|peeled|halved|quartered|fresh|dried))?)\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TrailingCulinaryModifierRegex();

    [GeneratedRegex(
        @"\b(?:omits?|omitting|without|excludes?|excluding)\b[^.?!,;]{0,50}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex OmitPrecedingRegex();

    [GeneratedRegex(
        @"\b(?:boil|boiling|simmer|simmering|steam|steaming)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BoilingContextRegex();

    [GeneratedRegex(
        @"^\s+(?:pan|pot|skillet|sheet|bowl|bath|knife|rack|towel|paper|plate|spoon|whisk|processor|blender|strainer|colander|thermometer|oven|burner|grate|press|shaker|peeler|grater|mill)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex EquipmentFollowingRegex();

    [GeneratedRegex(
        @"^\s+(?:powder|flakes|paste|sauce|oil|vinegar|broth|stock|juice|zest|seeds?|yolks?|whites?|shells?|skins?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex CompoundFoodFollowingRegex();

    [GeneratedRegex(
        @"(?:cayenne|bell|chili|chile|jalapeñ?o|serrano|white|red|crushed|longhorn)\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PepperModifierPrecedingRegex();

    [GeneratedRegex(
        @"(?:\b(?<qty>\d+(?:\s+\d+/\d+|/\d+|\.\d+)?|[½¼¾⅓⅔⅛⅜⅝⅞])\s*(?:(?:to|-|–)\s*(?<maxQty>\d+(?:\s+\d+/\d+|/\d+|\.\d+)?|[½¼¾⅓⅔⅛⅜⅝⅞])\s*)?(?<unit>teaspoons?|tsp\.?|tablespoons?|tbsp\.?|cups?|fl(?:uid)?\.?\s*oz\.?|pints?|pt\.?|quarts?|qt\.?|gallons?|gal\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|g|kilograms?|kg|milliliters?|ml|liters?|l|pinches?|dashes?|cloves?|slices?|cans?|heads?|sprigs?|stalks?|bunches?)?(?:\s+of)?)\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PrecedingRawMeasurementRegex();

    public static List<IngestionDraftStepDto> EnrichSteps(
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        IReadOnlyList<IngestionDraftStepDto> steps,
        List<string>? measurementWarnings = null
    )
    {
        if (steps.Count == 0 || ingredients.Count == 0)
        {
            return [.. steps];
        }

        var stepSegments = steps.Select(step => ParseToMutable(step.Instruction)).ToList();

        // Pass 1A: Link unindexed StepIngredientTokens and revalidate ambiguous/mismatched indices.
        for (var s = 0; s < steps.Count; s++)
        {
            var segs = stepSegments[s];
            var section = steps[s].SectionName;
            for (var i = 0; i < segs.Count; i++)
            {
                if (segs[i].Token is not StepIngredientToken ingTok)
                {
                    continue;
                }

                var prevText = i > 0 && !segs[i - 1].IsToken ? segs[i - 1].Text : string.Empty;
                var nextText =
                    i + 1 < segs.Count && !segs[i + 1].IsToken ? segs[i + 1].Text : string.Empty;

                var resolvedIdx = RevalidateOrMatchIngredientIndex(
                    ingTok,
                    prevText,
                    nextText,
                    section,
                    ingredients
                );
                if (resolvedIdx != ingTok.IngredientIndex)
                {
                    segs[i].Token = ingTok with { IngredientIndex = resolvedIdx };
                }
            }

            CollapseParentheticalEquivalentTokens(segs);
        }

        // Pass 1B: Link untokenized plain-text mentions for ingredients that have 0 token links.
        LinkUntokenizedIngredients(ingredients, steps, stepSegments);

        foreach (var segs in stepSegments)
        {
            CoalesceTextSegments(segs);
        }

        // Pass 2 & 3: Classify contexts, inject measurements, resolve remainders, check over-use.
        EnrichOccurrencesAndWarn(ingredients, stepSegments, measurementWarnings);

        var result = new List<IngestionDraftStepDto>(steps.Count);
        for (var s = 0; s < steps.Count; s++)
        {
            var rebuilt = RebuildInstruction(stepSegments[s]);
            if (rebuilt.Length > 4000)
            {
                rebuilt = rebuilt[..4000];
            }

            result.Add(steps[s] with { Instruction = rebuilt });
        }

        return result;
    }

    private static List<MutableSegment> ParseToMutable(string? instruction)
    {
        var parsed = StepInstructionParser.ParseSegments(instruction);
        var list = new List<MutableSegment>(parsed.Count);
        foreach (var seg in parsed)
        {
            list.Add(new MutableSegment { Text = seg.Text, Token = seg.Token });
        }
        CoalesceTextSegments(list);
        return list;
    }

    private static void CoalesceTextSegments(List<MutableSegment> segments)
    {
        for (var i = segments.Count - 2; i >= 0; i--)
        {
            if (!segments[i].IsToken && !segments[i + 1].IsToken)
            {
                segments[i].Text += segments[i + 1].Text;
                segments.RemoveAt(i + 1);
            }
        }
    }

    private static string RebuildInstruction(IReadOnlyList<MutableSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            if (seg.Token is not null)
            {
                sb.Append(StepInstructionParser.FormatToken(seg.Token));
            }
            else
            {
                sb.Append(seg.Text);
            }
        }
        return sb.ToString();
    }

    private static int? RevalidateOrMatchIngredientIndex(
        StepIngredientToken tok,
        string prevText,
        string nextText,
        string? stepSectionName,
        IReadOnlyList<IngestionDraftIngredientDto> ingredients
    )
    {
        var cleanFull = NormalizePhrase(tok.Ingredient);
        var cleanStripped = StripPrepAdjectives(cleanFull);
        var tokenHead = ExtractHeadNoun(cleanStripped);
        if (string.IsNullOrWhiteSpace(cleanFull))
        {
            return tok.IngredientIndex is >= 0 and var cur && cur < ingredients.Count ? cur : null;
        }

        var hasValidExisting =
            tok.IngredientIndex.HasValue
            && tok.IngredientIndex.Value >= 0
            && tok.IngredientIndex.Value < ingredients.Count;

        if (hasValidExisting)
        {
            var curIdx = tok.IngredientIndex!.Value;
            var curIng = ingredients[curIdx];
            var curLexical = ScoreIngredientMatch(
                cleanFull,
                cleanStripped,
                tokenHead,
                curIng,
                stepSection: string.Empty
            );

            // Reject impossible qualifier mismatches (e.g. 4 qt boiling water bound to 0.5 cup ice water).
            if (IsQualifierMismatchForExisting(cleanFull, tok, curIng, prevText, nextText))
            {
                return MatchBestDuplicateCandidate(
                    tok,
                    cleanFull,
                    cleanStripped,
                    tokenHead,
                    stepSectionName,
                    ingredients,
                    preferredTieIndex: null,
                    excludedIndex: curIdx
                );
            }

            if (curLexical == 0 && !curIng.IsRecipeReference)
            {
                return MatchBestDuplicateCandidate(
                    tok,
                    cleanFull,
                    cleanStripped,
                    tokenHead,
                    stepSectionName,
                    ingredients,
                    preferredTieIndex: null,
                    excludedIndex: curIdx
                );
            }

            return MatchBestDuplicateCandidate(
                    tok,
                    cleanFull,
                    cleanStripped,
                    tokenHead,
                    stepSectionName,
                    ingredients,
                    preferredTieIndex: curIdx,
                    excludedIndex: null
                ) ?? curIdx;
        }

        return MatchBestDuplicateCandidate(
            tok,
            cleanFull,
            cleanStripped,
            tokenHead,
            stepSectionName,
            ingredients,
            preferredTieIndex: null,
            excludedIndex: null
        );
    }

    private static bool IsQualifierMismatchForExisting(
        string tokenCleanFull,
        StepIngredientToken tok,
        IngestionDraftIngredientDto ing,
        string prevText,
        string nextText
    )
    {
        var ingNorm = NormalizePhrase(ing.DisplayName);
        if (
            !ingNorm.StartsWith("ice ", StringComparison.Ordinal)
            || ContainsWholePhrase(tokenCleanFull, "ice")
        )
        {
            return false;
        }

        var prevWin = prevText.Length > 45 ? prevText[^45..] : prevText;
        var nextWin = nextText.Length > 45 ? nextText[..45] : nextText;
        if (BoilingContextRegex().IsMatch(prevWin) || BoilingContextRegex().IsMatch(nextWin))
        {
            return true;
        }

        if (
            tok.Amount.HasValue
            && ing.Quantity.HasValue
            && TrySumInTargetUnit([tok], ing.Unit, ing.CustomUnitName, out var convAmt)
        )
        {
            var maxAllowed = ing.MaxQuantity ?? ing.Quantity.Value;
            if (maxAllowed > 0m && convAmt > maxAllowed * 2m)
            {
                return true;
            }
        }

        return false;
    }

    private static int? MatchBestDuplicateCandidate(
        StepIngredientToken tok,
        string cleanFull,
        string cleanStripped,
        string tokenHead,
        string? stepSectionName,
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        int? preferredTieIndex,
        int? excludedIndex
    )
    {
        var normalizedSection = stepSectionName?.Trim() ?? string.Empty;
        var hasStepSection = !string.IsNullOrEmpty(normalizedSection);

        var candidates = new List<(int Index, int BaseScore, int TotalScore)>();
        for (var i = 0; i < ingredients.Count; i++)
        {
            if (excludedIndex == i)
            {
                continue;
            }

            var baseScore = ScoreIngredientMatch(
                cleanFull,
                cleanStripped,
                tokenHead,
                ingredients[i],
                stepSection: string.Empty
            );
            if (baseScore < 50)
            {
                continue;
            }

            var total = baseScore;
            var ingSection = ingredients[i].SectionName?.Trim() ?? string.Empty;

            if (hasStepSection)
            {
                if (
                    string.Equals(normalizedSection, ingSection, StringComparison.OrdinalIgnoreCase)
                )
                {
                    total += 30;
                }
                else if (!string.IsNullOrEmpty(ingSection))
                {
                    total -= 25;
                }
            }
            else
            {
                total += string.IsNullOrEmpty(ingSection) ? 20 : -15;
            }

            if (tok.Amount.HasValue)
            {
                if (
                    ingredients[i].Quantity.HasValue
                    && TrySumInTargetUnit(
                        [tok],
                        ingredients[i].Unit,
                        ingredients[i].CustomUnitName,
                        out var convAmt
                    )
                )
                {
                    var listedMax = ingredients[i].MaxQuantity ?? ingredients[i].Quantity!.Value;
                    if (Math.Abs(convAmt - ingredients[i].Quantity!.Value) <= 0.01m)
                    {
                        total += 25;
                    }
                    else if (listedMax > 0m && convAmt <= listedMax * 1.05m)
                    {
                        total += 12;
                    }
                    else if (listedMax > 0m && convAmt > listedMax * 2.0m)
                    {
                        total -= 35;
                    }
                }
                else if (!ingredients[i].Quantity.HasValue)
                {
                    total += 8;
                }
            }

            candidates.Add((i, baseScore, total));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0].Index;
        }

        var maxTotal = candidates.Max(c => c.TotalScore);
        var topMatches = candidates.Where(c => c.TotalScore == maxTotal).ToList();
        if (topMatches.Count == 1)
        {
            return topMatches[0].Index;
        }

        if (preferredTieIndex.HasValue && topMatches.Any(c => c.Index == preferredTieIndex.Value))
        {
            return preferredTieIndex.Value;
        }

        // Only return ambiguous multi-match if resolving an existing index tie, not guessing unindexed ties.
        return null;
    }

    private static void CollapseParentheticalEquivalentTokens(List<MutableSegment> segments)
    {
        for (var i = 0; i <= segments.Count - 4; i++)
        {
            if (
                segments[i].Token is not StepIngredientToken tokA
                || segments[i + 1].IsToken
                || !string.Equals(segments[i + 1].Text.Trim(), "(", StringComparison.Ordinal)
                || segments[i + 2].Token is not StepIngredientToken tokB
                || segments[i + 3].IsToken
            )
            {
                continue;
            }

            if (!tokA.Amount.HasValue || !tokB.Amount.HasValue)
            {
                continue;
            }

            var sameTarget =
                (tokA.IngredientIndex.HasValue && tokA.IngredientIndex == tokB.IngredientIndex)
                || string.Equals(
                    NormalizePhrase(tokA.Ingredient),
                    NormalizePhrase(tokB.Ingredient),
                    StringComparison.Ordinal
                );
            if (!sameTarget)
            {
                continue;
            }

            var after = segments[i + 3].Text;
            var closeMatch = Regex.Match(after, @"^\s*\)");
            if (!closeMatch.Success)
            {
                continue;
            }

            var remainder = after[closeMatch.Length..];
            foreach (
                var dupName in new[] { tokA.Ingredient.Trim(), tokB.Ingredient.Trim() }
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            )
            {
                var dupRegex = $@"^\s*{Regex.Escape(dupName)}\b";
                var dupMatch = Regex.Match(
                    remainder,
                    dupRegex,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                );
                if (dupMatch.Success)
                {
                    remainder = remainder[dupMatch.Length..];
                    break;
                }
            }

            segments[i + 3].Text = remainder;
            segments.RemoveAt(i + 2);
            segments.RemoveAt(i + 1);
            CoalesceTextSegments(segments);
        }
    }

    private static int ScoreIngredientMatch(
        string tokenFull,
        string tokenStripped,
        string tokenHead,
        IngestionDraftIngredientDto ing,
        string stepSection
    )
    {
        var candidates = EnumerateCandidateLabels(ing);
        var maxBase = 0;

        foreach (var rawCandidate in candidates)
        {
            var candFull = NormalizePhrase(rawCandidate);
            if (string.IsNullOrEmpty(candFull))
            {
                continue;
            }

            if (string.Equals(candFull, tokenFull, StringComparison.Ordinal))
            {
                maxBase = Math.Max(maxBase, 100);
                continue;
            }

            var candStripped = StripPrepAdjectives(StripFormSuffixes(candFull));
            if (
                !string.IsNullOrEmpty(candStripped)
                && string.Equals(candStripped, tokenStripped, StringComparison.Ordinal)
            )
            {
                maxBase = Math.Max(maxBase, 85);
                continue;
            }

            if (
                tokenStripped.Contains(' ')
                && (
                    ContainsWholePhrase(candFull, tokenStripped)
                    || ContainsWholePhrase(candStripped, tokenStripped)
                )
            )
            {
                maxBase = Math.Max(maxBase, 70);
                continue;
            }

            if (
                !string.IsNullOrEmpty(candStripped)
                && candStripped.Contains(' ')
                && ContainsWholePhrase(tokenStripped, candStripped)
            )
            {
                maxBase = Math.Max(maxBase, 68);
                continue;
            }

            var candHead = ExtractHeadNoun(candStripped);
            if (
                !string.IsNullOrEmpty(tokenHead)
                && tokenHead.Length >= 3
                && string.Equals(candHead, tokenHead, StringComparison.Ordinal)
            )
            {
                maxBase = Math.Max(maxBase, 55);
            }
        }

        if (maxBase == 0)
        {
            return 0;
        }

        if (
            !string.IsNullOrEmpty(stepSection)
            && string.Equals(
                ing.SectionName?.Trim(),
                stepSection,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return maxBase + 15;
        }

        return maxBase;
    }

    private static IEnumerable<string> EnumerateCandidateLabels(IngestionDraftIngredientDto ing)
    {
        if (!string.IsNullOrWhiteSpace(ing.DisplayName))
        {
            yield return ing.DisplayName;
        }
        if (!string.IsNullOrWhiteSpace(ing.MatchedFoodItemName))
        {
            yield return ing.MatchedFoodItemName;
        }
        if (!string.IsNullOrWhiteSpace(ing.ProposedFoodItemName))
        {
            yield return ing.ProposedFoodItemName;
        }
        if (!string.IsNullOrWhiteSpace(ing.ReferencedRecipeTitle))
        {
            yield return ing.ReferencedRecipeTitle;
        }
        if (!string.IsNullOrWhiteSpace(ing.MatchedRecipeTitle))
        {
            yield return ing.MatchedRecipeTitle;
        }
    }

    private static void LinkUntokenizedIngredients(
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        IReadOnlyList<IngestionDraftStepDto> steps,
        List<List<MutableSegment>> stepSegments
    )
    {
        var referencedIndices = new HashSet<int>();
        foreach (var segs in stepSegments)
        {
            foreach (var seg in segs)
            {
                if (seg.Token is StepIngredientToken { IngredientIndex: int idx } && idx >= 0)
                {
                    referencedIndices.Add(idx);
                }
            }
        }

        for (var idx = 0; idx < ingredients.Count; idx++)
        {
            if (referencedIndices.Contains(idx))
            {
                continue;
            }

            var ing = ingredients[idx];
            var aliases = BuildSearchAliases(ing, ingredients, idx);
            if (aliases.Count == 0)
            {
                continue;
            }

            var hasSection = !string.IsNullOrWhiteSpace(ing.SectionName);
            var matchedInSameSection = TryWrapAliasesInSteps(
                idx,
                aliases,
                steps,
                stepSegments,
                requireSectionMatch: hasSection,
                targetSectionName: ing.SectionName
            );

            if (!matchedInSameSection && hasSection)
            {
                TryWrapAliasesInSteps(
                    idx,
                    aliases,
                    steps,
                    stepSegments,
                    requireSectionMatch: false,
                    targetSectionName: null
                );
            }
        }
    }

    private static List<MutableSegment>? TryWrapFirstAliasInText(
        string text,
        IReadOnlyList<string> aliases,
        int ingredientIdx
    )
    {
        foreach (var alias in aliases)
        {
            var pattern = $@"\b{Regex.Escape(alias)}\b";
            var match = Regex.Match(
                text,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );
            while (match.Success)
            {
                var after = text[(match.Index + match.Length)..];
                if (EquipmentFollowingRegex().IsMatch(after))
                {
                    match = match.NextMatch();
                    continue;
                }

                var compoundMatch = CompoundFoodFollowingRegex().Match(after);
                if (
                    compoundMatch.Success
                    && !alias.EndsWith(
                        compoundMatch.Value.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    match = match.NextMatch();
                    continue;
                }

                var before = text[..match.Index];
                if (OmitPrecedingRegex().IsMatch(before))
                {
                    match = match.NextMatch();
                    continue;
                }

                if (
                    string.Equals(alias, "pepper", StringComparison.OrdinalIgnoreCase)
                    && PepperModifierPrecedingRegex().IsMatch(before)
                )
                {
                    match = match.NextMatch();
                    continue;
                }
                decimal? parsedQty = null;
                decimal? parsedMaxQty = null;
                MeasurementUnit? parsedUnit = null;
                string? parsedCustomUnit = null;

                var leadMeasure = PrecedingRawMeasurementRegex().Match(before);
                var consumedPrefixStart = match.Index;
                if (leadMeasure.Success)
                {
                    var qtyVal = ParsePlainNumber(leadMeasure.Groups["qty"].Value);
                    var maxVal = leadMeasure.Groups["maxQty"].Success
                        ? ParsePlainNumber(leadMeasure.Groups["maxQty"].Value)
                        : null;
                    if (qtyVal.HasValue && qtyVal.Value > 0m)
                    {
                        var rawUnit = leadMeasure.Groups["unit"].Value.Trim().TrimEnd('.');
                        var (u, cu) = string.IsNullOrWhiteSpace(rawUnit)
                            ? (null, null)
                            : RecipeUnitAndSectionHelpers.NormalizeUnit(rawUnit);
                        if (
                            u == MeasurementUnit.Custom
                            && RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(
                                cu,
                                match.Value
                            )
                        )
                        {
                            u = null;
                            cu = null;
                        }
                        parsedQty = qtyVal;
                        parsedMaxQty = maxVal;
                        parsedUnit = u;
                        parsedCustomUnit = u == MeasurementUnit.Custom ? cu : null;
                        consumedPrefixStart = leadMeasure.Index;
                    }
                }

                var prefixText = text[..consumedPrefixStart];
                var suffixText = text[(match.Index + match.Length)..];

                var list = new List<MutableSegment>(3);
                if (!string.IsNullOrEmpty(prefixText))
                {
                    list.Add(new MutableSegment { Text = prefixText });
                }

                var token = new StepIngredientToken(
                    Ingredient: match.Value,
                    Amount: parsedQty,
                    Unit: parsedUnit,
                    CustomUnitName: parsedCustomUnit,
                    MaxAmount: parsedMaxQty,
                    IngredientIndex: ingredientIdx
                );
                list.Add(new MutableSegment { Token = token });

                if (!string.IsNullOrEmpty(suffixText))
                {
                    // Recursively wrap any additional mentions later in the same plain text segment
                    var rest = TryWrapFirstAliasInText(suffixText, aliases, ingredientIdx);
                    if (rest is not null)
                    {
                        list.AddRange(rest);
                    }
                    else
                    {
                        list.Add(new MutableSegment { Text = suffixText });
                    }
                }

                return list;
            }
        }

        return null;
    }

    private static List<string> BuildSearchAliases(
        IngestionDraftIngredientDto ing,
        IReadOnlyList<IngestionDraftIngredientDto> allIngredients,
        int selfIdx
    )
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddAlias(string? candidate, bool checkConflict)
        {
            var cleaned = NormalizePhrase(candidate);
            if (cleaned.Length < 3 || StopAliases.Contains(cleaned) || seen.Contains(cleaned))
            {
                return;
            }
            if (
                checkConflict && AliasConflictsWithOtherIngredient(cleaned, allIngredients, selfIdx)
            )
            {
                return;
            }
            seen.Add(cleaned);
            results.Add(cleaned);
        }

        var displayNorm = NormalizePhrase(ing.DisplayName);
        AddAlias(displayNorm, checkConflict: false);

        var formStripped = StripFormSuffixes(displayNorm);
        AddAlias(formStripped, checkConflict: true);

        var prepStripped = StripPrepAdjectives(formStripped);
        AddAlias(prepStripped, checkConflict: true);

        // Progressive trailing suffixes (e.g., "green bell pepper" -> "bell pepper", "table salt" -> "salt")
        var words = formStripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var start = 1; start < words.Length; start++)
        {
            AddAlias(string.Join(' ', words[start..]), checkConflict: true);
        }

        // Distinguishing modifier before generic head nouns (e.g., "serrano chile" -> "serrano", "feta cheese" -> "feta")
        if (words.Length >= 2 && GenericHeadNounsForModifierExtract.Contains(words[^1]))
        {
            var end = words.Length - 1;
            while (end > 0 && GenericHeadNounsForModifierExtract.Contains(words[end]))
            {
                end--;
            }
            var trimmedMod = string.Join(' ', words[..(end + 1)]);
            AddAlias(trimmedMod, checkConflict: true);
            var strippedMod = StripPrepAdjectives(trimmedMod);
            AddAlias(strippedMod, checkConflict: true);
        }

        if (ing.IsRecipeReference)
        {
            AddAlias(
                StripPrepAdjectives(NormalizePhrase(ing.ReferencedRecipeTitle)),
                checkConflict: true
            );
            AddAlias(
                StripPrepAdjectives(NormalizePhrase(ing.MatchedRecipeTitle)),
                checkConflict: true
            );

            var headWord = ExtractLastRawWord(ing.DisplayName);
            AddAlias(headWord, checkConflict: true);
            if (!string.IsNullOrEmpty(headWord))
            {
                var singularHead = RecipeUnitAndSectionHelpers.Singularize(headWord);
                AddAlias(singularHead, checkConflict: true);
            }
        }

        return results.OrderByDescending(a => a.Length).ToList();
    }

    private static string StripFormSuffixes(string normalizedPhrase)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhrase))
        {
            return string.Empty;
        }

        var words = normalizedPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var end = words.Length;
        while (end > 1 && FormSuffixWords.Contains(words[end - 1]))
        {
            end--;
        }
        return end < words.Length ? string.Join(' ', words[..end]) : normalizedPhrase;
    }

    private static bool AliasConflictsWithOtherIngredient(
        string candidate,
        IReadOnlyList<IngestionDraftIngredientDto> allIngredients,
        int selfIdx
    )
    {
        var singular = RecipeUnitAndSectionHelpers.Singularize(candidate.ToLowerInvariant());
        var singleWord = !candidate.Contains(' ');
        for (var i = 0; i < allIngredients.Count; i++)
        {
            if (i == selfIdx)
            {
                continue;
            }

            var otherNorm = NormalizePhrase(allIngredients[i].DisplayName);
            if (
                ContainsWholePhrase(otherNorm, candidate)
                || ContainsWholePhrase(otherNorm, singular)
            )
            {
                return true;
            }

            if (singleWord)
            {
                var otherHead = ExtractHeadNoun(otherNorm);
                if (string.Equals(otherHead, singular, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryWrapAliasesInSteps(
        int ingredientIdx,
        IReadOnlyList<string> aliases,
        IReadOnlyList<IngestionDraftStepDto> steps,
        List<List<MutableSegment>> stepSegments,
        bool requireSectionMatch,
        string? targetSectionName
    )
    {
        var wrappedAny = false;
        for (var s = 0; s < steps.Count; s++)
        {
            if (
                requireSectionMatch
                && !string.Equals(
                    steps[s].SectionName?.Trim(),
                    targetSectionName?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var segs = stepSegments[s];
            for (var i = 0; i < segs.Count; i++)
            {
                if (segs[i].IsToken || string.IsNullOrWhiteSpace(segs[i].Text))
                {
                    continue;
                }

                var replacement = TryWrapFirstAliasInText(segs[i].Text, aliases, ingredientIdx);
                if (replacement is null)
                {
                    continue;
                }

                segs.RemoveAt(i);
                segs.InsertRange(i, replacement);
                wrappedAny = true;
                i += replacement.Count - 1;
            }
        }

        return wrappedAny;
    }

    private static void EnrichOccurrencesAndWarn(
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        List<List<MutableSegment>> stepSegments,
        List<string>? measurementWarnings
    )
    {
        // Build index -> occurrences
        var byIngredient = new Dictionary<int, List<TokenOccurrence>>();
        for (var s = 0; s < stepSegments.Count; s++)
        {
            var segs = stepSegments[s];
            for (var i = 0; i < segs.Count; i++)
            {
                if (
                    segs[i].Token is not StepIngredientToken tok
                    || tok.IngredientIndex is not int idx
                    || idx < 0
                    || idx >= ingredients.Count
                )
                {
                    continue;
                }

                var prevText = i > 0 && !segs[i - 1].IsToken ? segs[i - 1].Text : string.Empty;
                var nextText =
                    i + 1 < segs.Count && !segs[i + 1].IsToken ? segs[i + 1].Text : string.Empty;

                var prevWindow = prevText.Length > 45 ? prevText[^45..] : prevText;
                var nextWindow = nextText.Length > 45 ? nextText[..45] : nextText;

                var isAdHoc =
                    (
                        !string.IsNullOrWhiteSpace(tok.Notes)
                        && AdHocOrGarnishRegex().IsMatch(tok.Notes)
                    )
                    || AdHocOrGarnishRegex().IsMatch(prevWindow)
                    || AdHocOrGarnishRegex().IsMatch(nextWindow)
                    || OmitPrecedingRegex().IsMatch(prevText);

                var isDistributive =
                    DistributiveContextRegex().IsMatch(prevWindow)
                    || DistributiveContextRegex().IsMatch(nextWindow);

                var fracMatch = LeadingFractionPhraseRegex().Match(prevText);
                var fracVal = 0m;
                if (fracMatch.Success && !tok.Amount.HasValue)
                {
                    fracVal = fracMatch.Groups["frac"].Value.ToLowerInvariant() switch
                    {
                        "one-half" or "half" or "1/2" => 0.5m,
                        "one-third" or "1/3" => 1m / 3m,
                        "one-quarter" or "a quarter" or "1/4" => 0.25m,
                        _ => 0m,
                    };
                }

                var remMatch = LeadingRemainingPhraseRegex().Match(prevText);

                if (!byIngredient.TryGetValue(idx, out var occs))
                {
                    occs = [];
                    byIngredient[idx] = occs;
                }

                occs.Add(
                    new TokenOccurrence(
                        StepIndex: s,
                        SegmentIndex: i,
                        Token: tok,
                        PrevText: prevText,
                        NextText: nextText,
                        IsAdHoc: isAdHoc,
                        IsDistributive: isDistributive,
                        IsFractional: fracVal > 0m,
                        FractionValue: fracVal,
                        IsRemaining: !tok.Amount.HasValue && remMatch.Success
                    )
                );
            }
        }

        foreach (var (idx, occs) in byIngredient)
        {
            var ing = ingredients[idx];
            if (ShouldSkipSubRecipeQuantityInjection(ing))
            {
                if (
                    ing.Unit == MeasurementUnit.Whole
                    && ing.Quantity is 1m
                    && !ing.MaxQuantity.HasValue
                )
                {
                    for (var k = 0; k < occs.Count; k++)
                    {
                        var occ = occs[k];
                        if (
                            occ.Token.Amount is 1m
                            && !occ.Token.MaxAmount.HasValue
                            && occ.Token.Unit == MeasurementUnit.Whole
                        )
                        {
                            var stripped = occ.Token with
                            {
                                Amount = null,
                                MaxAmount = null,
                                Unit = null,
                                CustomUnitName = null,
                            };
                            stepSegments[occ.StepIndex][occ.SegmentIndex].Token = stripped;
                            occs[k] = occ with { Token = stripped };
                        }
                    }
                }
                continue;
            }

            // 3A. Inject fractional references ("half of the ||butter||")
            for (var k = 0; k < occs.Count; k++)
            {
                var occ = occs[k];
                if (
                    occ.IsFractional
                    && !occ.IsAdHoc
                    && ing.Quantity.HasValue
                    && !ing.MaxQuantity.HasValue
                    && ing.Quantity.Value > 0m
                )
                {
                    var fracQty = decimal.Round(ing.Quantity.Value * occ.FractionValue, 4);
                    var segs = stepSegments[occ.StepIndex];
                    if (occ.SegmentIndex > 0 && !segs[occ.SegmentIndex - 1].IsToken)
                    {
                        var fracMatch = LeadingFractionPhraseRegex()
                            .Match(segs[occ.SegmentIndex - 1].Text);
                        if (fracMatch.Success)
                        {
                            segs[occ.SegmentIndex - 1].Text = segs[occ.SegmentIndex - 1].Text[
                                ..fracMatch.Index
                            ];
                        }
                    }

                    var injected = ApplyExplicitMeasurement(
                        occ.Token,
                        fracQty,
                        null,
                        ing.Unit,
                        ing.CustomUnitName
                    );
                    segs[occ.SegmentIndex].Token = injected;
                    occs[k] = occ with { Token = injected, IsFractional = false };
                }
            }

            // 3B. Resolve "remaining" / "rest of the" after earlier quantified uses
            if (ing.Quantity.HasValue && !ing.MaxQuantity.HasValue && ing.Quantity.Value > 0m)
            {
                var remainingCandidates = occs.Where(o => o.IsRemaining && !o.IsAdHoc).ToList();
                var otherUnquantified = occs.Where(o =>
                        !o.Token.Amount.HasValue && !o.IsRemaining && !o.IsAdHoc
                    )
                    .ToList();

                if (remainingCandidates.Count == 1 && otherUnquantified.Count == 0)
                {
                    var remOcc = remainingCandidates[0];
                    var remIdx = occs.IndexOf(remOcc);
                    var earlierQuantified = occs.Take(remIdx)
                        .Where(o => o.Token.Amount.HasValue && !o.IsDistributive)
                        .ToList();

                    if (
                        earlierQuantified.Count >= 1
                        && TrySumInTargetUnit(
                            earlierQuantified.Select(o => o.Token),
                            ing.Unit,
                            ing.CustomUnitName,
                            out var usedSum
                        )
                    )
                    {
                        var remQty = decimal.Round(ing.Quantity.Value - usedSum, 4);
                        if (remQty > 0.001m && remQty < ing.Quantity.Value)
                        {
                            var segs = stepSegments[remOcc.StepIndex];
                            if (remOcc.SegmentIndex > 0 && !segs[remOcc.SegmentIndex - 1].IsToken)
                            {
                                var prev = segs[remOcc.SegmentIndex - 1].Text;
                                var rm = LeadingRemainingPhraseRegex().Match(prev);
                                if (rm.Success && rm.Groups["rest"].Success)
                                {
                                    var prefix = prev[..rm.Index];
                                    var lead = rm.Groups["hasThe"].Success
                                        ? "the remaining "
                                        : "remaining ";
                                    segs[remOcc.SegmentIndex - 1].Text = prefix + lead;
                                }
                            }

                            var injected = ApplyExplicitMeasurement(
                                remOcc.Token,
                                remQty,
                                null,
                                ing.Unit,
                                ing.CustomUnitName
                            );
                            segs[remOcc.SegmentIndex].Token = injected;
                            occs[remIdx] = remOcc with { Token = injected, IsRemaining = false };
                        }
                    }
                }
            }

            // 3C. Single-use unquantified injection
            var anyQuantified = occs.Any(o => o.Token.Amount.HasValue);
            var nonGarnishUnquantified = occs.Where(o => !o.Token.Amount.HasValue && !o.IsAdHoc)
                .ToList();
            if (!anyQuantified && nonGarnishUnquantified.Count == 1 && ing.Quantity.HasValue)
            {
                var occ = nonGarnishUnquantified[0];
                var segs = stepSegments[occ.StepIndex];
                var prevText =
                    occ.SegmentIndex > 0 && !segs[occ.SegmentIndex - 1].IsToken
                        ? segs[occ.SegmentIndex - 1].Text
                        : string.Empty;
                var nextText =
                    occ.SegmentIndex + 1 < segs.Count && !segs[occ.SegmentIndex + 1].IsToken
                        ? segs[occ.SegmentIndex + 1].Text
                        : string.Empty;

                var isSubjectAfterThe =
                    SentenceInitialTheRegex().IsMatch(prevText)
                    && SubjectVerbAfterTokenRegex().IsMatch(nextText);

                if (!isSubjectAfterThe)
                {
                    var updatedName = occ.Token.Ingredient;
                    var modMatch = TrailingCulinaryModifierRegex().Match(prevText);
                    if (modMatch.Success)
                    {
                        var mods = modMatch.Groups["mods"].Value.Trim();
                        if (
                            !string.IsNullOrEmpty(mods)
                            && !updatedName.StartsWith(mods, StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            updatedName = $"{mods} {updatedName}";
                        }
                        prevText = prevText[..modMatch.Index];
                    }
                    else
                    {
                        prevText = TrailingAllOfTheRegex().Replace(prevText, string.Empty);
                        prevText = NonSentenceInitialTheRegex().Replace(prevText, string.Empty);
                    }

                    if (occ.SegmentIndex > 0 && !segs[occ.SegmentIndex - 1].IsToken)
                    {
                        segs[occ.SegmentIndex - 1].Text = prevText;
                    }

                    var updatedTok = ApplyExplicitMeasurement(
                        occ.Token with
                        {
                            Ingredient = updatedName,
                        },
                        ing.Quantity.Value,
                        ing.MaxQuantity,
                        ing.Unit,
                        ing.CustomUnitName
                    );
                    segs[occ.SegmentIndex].Token = updatedTok;
                    var occIdx = occs.IndexOf(occ);
                    occs[occIdx] = occ with { Token = updatedTok };
                }
            }

            // 3D. True over-allocation warning (excluding distributive uses)
            if (measurementWarnings is not null && ing.Quantity.HasValue)
            {
                var maxAllowed = ing.MaxQuantity ?? ing.Quantity.Value;
                if (maxAllowed > 0m)
                {
                    var nonDistQuantified = occs.Where(o =>
                            o.Token.Amount.HasValue && !o.IsDistributive
                        )
                        .ToList();
                    if (
                        nonDistQuantified.Count >= 1
                        && TrySumInTargetUnit(
                            nonDistQuantified.Select(o => o.Token),
                            ing.Unit,
                            ing.CustomUnitName,
                            out var totalStepSum
                        )
                        && totalStepSum > maxAllowed * 1.05m
                    )
                    {
                        measurementWarnings.Add(
                            $"Ingredient '{ing.DisplayName}' uses {totalStepSum:0.##} across steps, exceeding listed {maxAllowed:0.##}."
                        );
                    }
                }
            }
        }
    }

    private static bool ShouldSkipSubRecipeQuantityInjection(IngestionDraftIngredientDto ing)
    {
        if (!ing.IsRecipeReference)
        {
            return false;
        }

        if (ing.Unit == MeasurementUnit.Custom && !string.IsNullOrWhiteSpace(ing.CustomUnitName))
        {
            var cu = ing.CustomUnitName.Trim().ToLowerInvariant();
            if (
                cu
                is "recipe"
                    or "recipes"
                    or "batch"
                    or "batches"
                    or "serving"
                    or "servings"
                    or "portion"
                    or "portions"
            )
            {
                return true;
            }
        }

        return ing.Quantity is 1m
            && !ing.MaxQuantity.HasValue
            && (ing.Unit is null or MeasurementUnit.Whole)
            && string.IsNullOrWhiteSpace(ing.CustomUnitName);
    }

    private static StepIngredientToken ApplyExplicitMeasurement(
        StepIngredientToken token,
        decimal quantity,
        decimal? maxQuantity,
        MeasurementUnit? unit,
        string? customUnitName
    )
    {
        var cleanName = token.Ingredient.Trim();
        var finalUnit = unit;
        var finalCustom = unit == MeasurementUnit.Custom ? customUnitName?.Trim() : null;

        if (
            finalUnit == MeasurementUnit.Custom
            && RecipeUnitAndSectionHelpers.IsRedundantSelfCustomUnit(finalCustom, cleanName)
        )
        {
            finalUnit = null;
            finalCustom = null;
        }
        else if (
            finalUnit.HasValue
            && RedundantCountUnitWords.TryGetValue(finalUnit.Value, out var words)
        )
        {
            foreach (var word in words)
            {
                var suffix = " " + word;
                if (
                    cleanName.Length > suffix.Length
                    && cleanName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                )
                {
                    cleanName = cleanName[..^suffix.Length].TrimEnd();
                    break;
                }
            }
        }

        var (cleanQty, cleanMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
            quantity / 1.0000000000000000000000000000m,
            maxQuantity.HasValue ? maxQuantity.Value / 1.0000000000000000000000000000m : null
        );

        return token with
        {
            Ingredient = cleanName,
            Amount = cleanQty,
            MaxAmount = cleanMaxQty,
            Unit = finalUnit,
            CustomUnitName = finalUnit == MeasurementUnit.Custom ? finalCustom : null,
        };
    }

    private static bool TrySumInTargetUnit(
        IEnumerable<StepIngredientToken> tokens,
        MeasurementUnit? targetUnit,
        string? targetCustomUnit,
        out decimal sum
    )
    {
        sum = 0m;
        foreach (var tok in tokens)
        {
            if (!tok.Amount.HasValue)
            {
                continue;
            }

            if (tok.Unit is null && targetUnit is null)
            {
                var tokCustom = tok.CustomUnitName?.Trim();
                var ingCustom = targetCustomUnit?.Trim();
                if (!string.Equals(tokCustom, ingCustom, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                sum += tok.Amount.Value;
                continue;
            }

            if (tok.Unit == MeasurementUnit.Custom && targetUnit == MeasurementUnit.Custom)
            {
                if (
                    !string.Equals(
                        tok.CustomUnitName?.Trim(),
                        targetCustomUnit?.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return false;
                }

                sum += tok.Amount.Value;
                continue;
            }

            if (!tok.Unit.HasValue || !targetUnit.HasValue)
            {
                return false;
            }

            if (
                !CulinaryMath.TryConvertQuantity(
                    tok.Amount.Value,
                    tok.Unit.Value,
                    targetUnit.Value,
                    out var converted
                )
            )
            {
                return false;
            }

            sum += converted;
        }

        return true;
    }

    private static string NormalizePhrase(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var withoutParens = Regex.Replace(raw, @"\([^)]*\)", " ");
        var cleaned = Regex.Replace(withoutParens.ToLowerInvariant(), @"[^a-z0-9\s-]", " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static string StripPrepAdjectives(string normalizedPhrase)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhrase))
        {
            return string.Empty;
        }

        var parts = normalizedPhrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = parts.Where(p => !PrepAndRecipeAdjectives.Contains(p)).ToArray();
        return kept.Length > 0 ? string.Join(' ', kept) : normalizedPhrase;
    }

    private static string ExtractHeadNoun(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return string.Empty;
        }

        var last = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last is null ? string.Empty : RecipeUnitAndSectionHelpers.Singularize(last);
    }

    private static string ExtractLastRawWord(string? raw)
    {
        var norm = NormalizePhrase(raw);
        if (string.IsNullOrWhiteSpace(norm))
        {
            return string.Empty;
        }
        return norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            ?? string.Empty;
    }

    private static bool ContainsWholePhrase(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return false;
        }

        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var leftOk = idx == 0 || haystack[idx - 1] == ' ';
            var end = idx + needle.Length;
            var rightOk = end == haystack.Length || haystack[end] == ' ';
            if (leftOk && rightOk)
            {
                return true;
            }
            idx = haystack.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static decimal? ParsePlainNumber(string text)
    {
        text = text.Trim();
        return text switch
        {
            "½" => 0.5m,
            "¼" => 0.25m,
            "¾" => 0.75m,
            "⅓" => 1m / 3m,
            "⅔" => 2m / 3m,
            "⅛" => 0.125m,
            "⅜" => 0.375m,
            "⅝" => 0.625m,
            "⅞" => 0.875m,
            _ => ParseFractionOrDecimal(text),
        };
    }

    private static decimal? ParseFractionOrDecimal(string text)
    {
        var spaceParts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (spaceParts.Length == 2 && decimal.TryParse(spaceParts[0], out var whole))
        {
            var frac = ParseFractionOrDecimal(spaceParts[1]);
            return frac.HasValue ? whole + frac.Value : null;
        }

        var slashIdx = text.IndexOf('/');
        if (slashIdx > 0 && slashIdx < text.Length - 1)
        {
            if (
                decimal.TryParse(text[..slashIdx], out var num)
                && decimal.TryParse(text[(slashIdx + 1)..], out var den)
                && den != 0m
            )
            {
                return num / den;
            }
        }

        return decimal.TryParse(text, out var val) ? val : null;
    }
}
