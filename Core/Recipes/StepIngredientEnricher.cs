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
        "kernel",
        "kernels",
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
        "extract",
    };

    private static readonly HashSet<string> SafeSingleWordFallbackNouns = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "oil",
        "cream",
        "butter",
        "sugar",
        "salt",
        "flour",
        "vinegar",
        "wine",
        "sherry",
        "milk",
        "buttermilk",
        "spinach",
        "rice",
        "chocolate",
        "broth",
        "stock",
    };

    private static readonly HashSet<string> ConflictingModifierPrefixes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "red",
        "green",
        "yellow",
        "white",
        "sweet",
        "spring",
        "pearl",
        "pickled",
        "brown",
        "powdered",
        "confectioners",
        "unsweetened",
        "semisweet",
        "bittersweet",
        "baking",
        "kosher",
        "sea",
        "smoked",
        "balsamic",
        "cider",
        "sherry",
        "distilled",
        "rice",
        "apple",
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
        "juice",
        "juices",
        "liquid",
        "drippings",
    };

    private static readonly Dictionary<MeasurementUnit, string[]> RedundantCountUnitWords = new()
    {
        [MeasurementUnit.Clove] = ["cloves", "clove"],
        [MeasurementUnit.Slice] = ["slices", "slice"],
        [MeasurementUnit.Can] = ["cans", "can"],
        [MeasurementUnit.Bunch] = ["bunches", "bunch"],
    };

    [GeneratedRegex(
        @"\b(?:to\s+taste|as\s+needed|if\s+needed|if\s+using|\boptional\b|for\s+garnish|to\s+garnish|for\s+serving|to\s+serve|extra(?![\s-]+virgin)|additional|more\s+for|for\s+dusting|for\s+greasing)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex AdHocOrGarnishRegex();

    [GeneratedRegex(
        @"\b(?:each|apiece|per\s+(?:serving|person|piece|slice|toast|portion|plate|dish|cake|muffin|cookie|taco|bowl|roll|bun|burger|sandwich|wrap|tortilla|pita|skewer|cutlet|fillet|steak|chop|breast|thigh|half|halves|egg|tart|biscuit|cut))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DistributiveContextRegex();

    [GeneratedRegex(
        @"^\s*(?:at\s+a\s+time|in\s+\S+\s+increments?|increments?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex IncrementalRateFollowingRegex();

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
        @"^\s+(?:powder|flakes|paste|pur[eé]es?|sauce|oil|vinegar|broth|stock|juice|zest|seeds?|yolks?|whites?|shells?|skins?|mixtures?|batters?|doughs?|fillings?|glazes?|slurr(?:y|ies)|crusts?|fats?|drippings?|liquids?|quarters?|halves|half|wedges?|slices?|pieces?|chunks?|strips?|spears?|rounds?|rings?|cubes?|segments?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex CompoundFoodFollowingRegex();

    [GeneratedRegex(
        @"\binto\b[^.?!]{0,50}\b(?:meatballs?|balls?|mounds?|patt(?:ies|y)|cookies?|rolls?|cutlets?|dumplings?|puffs?|buns?|tartlets?|burgers?|cakes?|logs?|disks?)\b[^.?!]{0,90}\b(?:each|repeat)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RepeatPerItemFollowingRegex();

    [GeneratedRegex(
        @"\b(?:soak|let\s+stand|boil|simmer|blanch|parboil)\b.*\bdrain\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SoakAndDrainStepRegex();

    [GeneratedRegex(
        @"\bplus\s+(?:more|extra|additional|[a-z\s]{2,30}?)\s+(?:for\s+|as\s+needed\b|to\s+taste\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PlusExtraPrepAllowanceRegex();

    private static bool HasOpenEndedPlusExtraAllowance(
        string? preparationNote,
        string? originalRawText
    ) =>
        (
            !string.IsNullOrWhiteSpace(preparationNote)
            && PlusExtraPrepAllowanceRegex().IsMatch(preparationNote)
        )
        || (
            !string.IsNullOrWhiteSpace(originalRawText)
            && PlusExtraPrepAllowanceRegex().IsMatch(originalRawText)
        );

    [GeneratedRegex(
        @"\b(?:to\s+temper|tempering|from\s+(?:the\s+)?saucepan|from\s+(?:the\s+)?pot|hot\s+milk|warm\s+milk)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex TemperOrHotTransferClauseRegex();

    [GeneratedRegex(
        @"^(?:(?:rendered|pan|excess|skimmed|reserved|defatted|accumulated|cooking|poaching|braising|strained|simmering)\s+)?(?:fat|drippings|juices|liquid|soup|stew|batter|dough|puree|purée)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex UnlinkedPanFatRegex();

    [GeneratedRegex(
        @"(?:(?:bowl|pot|saucepan|pan|skillet|cup|glass)\s+of\s+(?:[\p{L}-]+\s+)?|\b(?:excess|running|soaking|cooking|pasta)\s+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ExcludedPlainWaterPrecedingRegex();

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

        // Pass 1A: Link unindexed StepIngredientTokens, mark fixed per-item mixtures, and heal unlinked pan fat.
        for (var s = 0; s < steps.Count; s++)
        {
            var segs = stepSegments[s];
            var section = steps[s].SectionName;
            for (var i = 0; i < segs.Count; i++)
            {
                var prevText = i > 0 && !segs[i - 1].IsToken ? segs[i - 1].Text : string.Empty;
                var nextText =
                    i + 1 < segs.Count && !segs[i + 1].IsToken ? segs[i + 1].Text : string.Empty;

                if (segs[i].Token is StepMixtureToken mixTok)
                {
                    if (
                        mixTok.Amount is > 0m
                        && mixTok.Unit is not null
                        && !mixTok.Fixed
                        && (
                            HasDistributiveInClause(prevText, nextText)
                            || RepeatPerItemFollowingRegex()
                                .IsMatch(BuildStepRestAfterSegment(segs, i))
                        )
                    )
                    {
                        segs[i].Token = mixTok with { Fixed = true };
                    }
                    continue;
                }

                if (segs[i].Token is not StepIngredientToken ingTok)
                {
                    continue;
                }

                var resolvedIdx = RevalidateOrMatchIngredientIndex(
                    ingTok,
                    prevText,
                    nextText,
                    section,
                    ingredients
                );
                var updatedTok =
                    resolvedIdx != ingTok.IngredientIndex
                        ? ingTok with
                        {
                            IngredientIndex = resolvedIdx,
                        }
                        : ingTok;

                if (
                    updatedTok.Amount.HasValue
                    && !updatedTok.Fixed
                    && !resolvedIdx.HasValue
                    && (
                        HasDistributiveInClause(prevText, nextText)
                        || IncrementalRateFollowingRegex().IsMatch(nextText)
                    )
                )
                {
                    updatedTok = updatedTok with { Fixed = true };
                }

                if (
                    !resolvedIdx.HasValue
                    && updatedTok.Amount is > 0m
                    && updatedTok.Unit.HasValue
                    && UnlinkedPanFatRegex().IsMatch(updatedTok.Ingredient.Trim())
                )
                {
                    segs[i].Token = new StepMixtureToken(
                        Name: updatedTok.Ingredient.Trim(),
                        Amount: updatedTok.Amount,
                        Unit: updatedTok.Unit,
                        CustomUnitName: updatedTok.CustomUnitName,
                        MaxAmount: updatedTok.MaxAmount,
                        Fixed: updatedTok.Fixed
                    );
                    continue;
                }

                if (!Equals(updatedTok, ingTok))
                {
                    segs[i].Token = updatedTok;
                }
            }

            CollapseParentheticalEquivalentTokens(segs);
        }

        // Balance duplicate-name ingredient allocations (e.g., 1 tbsp vs 5 tbsp olive oil across steps).
        RebalanceDuplicateIngredientAllocations(ingredients, steps, stepSegments);

        // Unlink incidental soak-and-drain or splash water tokens when an exact water subset accounts for listed water.
        UnlinkIncidentalOverAllocatedWaterTokens(ingredients, stepSegments);

        // Pass 1B: Link untokenized plain-text mentions for ingredients that have 0 token links.
        LinkUntokenizedIngredients(ingredients, steps, stepSegments);

        foreach (var segs in stepSegments)
        {
            CoalesceTextSegments(segs);
        }

        // Pass 2 & 3: Classify contexts, inject measurements, resolve remainders, check over-use.
        EnrichOccurrencesAndWarn(ingredients, stepSegments, measurementWarnings);

        // Pass 4: Unwrap remaining unquantified non-sub-recipe ingredient tokens back to plain text.
        // Sub-recipe references (IsRecipeReference == true) keep their link even without a printed unit.
        UnwrapUnquantifiedNonSubRecipeTokens(ingredients, stepSegments);

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
        var cleanStripped = StripPrepAdjectives(StripFormSuffixes(cleanFull));
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

            // Reject impossible qualifier or cross-section water mismatches (e.g. 4 qt boiling water bound to 0.5 cup ice water,
            // or [Beef] water bound to [Slaw] water).
            if (
                IsQualifierMismatchForExisting(
                    cleanFull,
                    tok,
                    curIng,
                    prevText,
                    nextText,
                    stepSectionName
                )
            )
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

    private static bool IsPlainWaterOrIce(IngestionDraftIngredientDto ing) =>
        NormalizePhrase(ing.DisplayName)
            is "water"
                or "ice"
                or "ice water"
                or "cold water"
                or "warm water"
                or "hot water"
                or "boiling water";

    private static bool IsQualifierMismatchForExisting(
        string tokenCleanFull,
        StepIngredientToken tok,
        IngestionDraftIngredientDto ing,
        string prevText,
        string nextText,
        string? stepSectionName
    )
    {
        if (
            IsPlainWaterOrIce(ing)
            && !string.IsNullOrWhiteSpace(stepSectionName)
            && !string.IsNullOrWhiteSpace(ing.SectionName)
            && !string.Equals(
                stepSectionName.Trim(),
                ing.SectionName.Trim(),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return true;
        }

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
                    if (IsPlainWaterOrIce(ingredients[i]))
                    {
                        continue;
                    }

                    if (
                        tok.Amount.HasValue
                        && ingredients[i].Quantity.HasValue
                        && TrySumInTargetUnit(
                            [tok],
                            ingredients[i].Unit,
                            ingredients[i].CustomUnitName,
                            out var crossAmt
                        )
                        && crossAmt
                            > (ingredients[i].MaxQuantity ?? ingredients[i].Quantity!.Value) * 1.05m
                    )
                    {
                        continue;
                    }

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

    private static void RebalanceDuplicateIngredientAllocations(
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        IReadOnlyList<IngestionDraftStepDto> steps,
        List<List<MutableSegment>> stepSegments
    )
    {
        var groups = Enumerable
            .Range(0, ingredients.Count)
            .Where(i => ingredients[i].Quantity is > 0m)
            .GroupBy(i => NormalizePhrase(ingredients[i].DisplayName), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() >= 2);

        foreach (var group in groups)
        {
            var members = group.ToList();
            var tokensByMember =
                new Dictionary<int, List<(int StepIdx, int SegIdx, StepIngredientToken Tok)>>();
            var sumByMember = new Dictionary<int, decimal>();

            foreach (var idx in members)
            {
                tokensByMember[idx] = [];
            }

            for (var s = 0; s < stepSegments.Count; s++)
            {
                var segs = stepSegments[s];
                for (var i = 0; i < segs.Count; i++)
                {
                    if (
                        segs[i].Token is not StepIngredientToken tok
                        || !tok.Amount.HasValue
                        || tok.IngredientIndex is not int idx
                        || !tokensByMember.TryGetValue(idx, out var list)
                    )
                    {
                        continue;
                    }

                    var prevText = i > 0 && !segs[i - 1].IsToken ? segs[i - 1].Text : string.Empty;
                    var nextText =
                        i + 1 < segs.Count && !segs[i + 1].IsToken
                            ? segs[i + 1].Text
                            : string.Empty;
                    if (HasDistributiveInClause(prevText, nextText))
                    {
                        continue;
                    }

                    list.Add((s, i, tok));
                }
            }

            foreach (var idx in members)
            {
                var ing = ingredients[idx];
                sumByMember[idx] = TrySumInTargetUnit(
                    tokensByMember[idx].Select(x => x.Tok),
                    ing.Unit,
                    ing.CustomUnitName,
                    out var total
                )
                    ? total
                    : -1m;
            }

            foreach (var overIdx in members)
            {
                var overIng = ingredients[overIdx];
                var overMax = overIng.MaxQuantity ?? overIng.Quantity!.Value;
                if (sumByMember[overIdx] < 0m || sumByMember[overIdx] <= overMax * 1.05m)
                {
                    continue;
                }

                var underIdx = members
                    .Where(u =>
                        u != overIdx
                        && sumByMember[u] >= 0m
                        && sumByMember[u]
                            < (ingredients[u].MaxQuantity ?? ingredients[u].Quantity!.Value)
                                - 0.001m
                    )
                    .OrderByDescending(u =>
                        (ingredients[u].MaxQuantity ?? ingredients[u].Quantity!.Value)
                        - sumByMember[u]
                    )
                    .FirstOrDefault(-1);

                if (underIdx < 0)
                {
                    continue;
                }

                var underIng = ingredients[underIdx];
                var underMax = underIng.MaxQuantity ?? underIng.Quantity!.Value;
                var overSec = overIng.SectionName?.Trim() ?? string.Empty;
                var underSec = underIng.SectionName?.Trim() ?? string.Empty;

                var ordered = tokensByMember[overIdx]
                    .OrderByDescending(t =>
                    {
                        var stepSec = steps[t.StepIdx].SectionName?.Trim() ?? string.Empty;
                        var matchesUnder =
                            !string.IsNullOrEmpty(underSec)
                            && string.Equals(stepSec, underSec, StringComparison.OrdinalIgnoreCase);
                        var matchesOver =
                            !string.IsNullOrEmpty(overSec)
                            && string.Equals(stepSec, overSec, StringComparison.OrdinalIgnoreCase);
                        if (matchesUnder && !matchesOver)
                        {
                            return 2;
                        }
                        return !matchesOver ? 1 : 0;
                    })
                    .ThenByDescending(t => t.StepIdx)
                    .ThenByDescending(t => t.SegIdx)
                    .ToList();

                foreach (var entry in ordered)
                {
                    if (sumByMember[overIdx] <= overMax * 1.05m)
                    {
                        break;
                    }

                    if (
                        !TrySumInTargetUnit(
                            [entry.Tok],
                            overIng.Unit,
                            overIng.CustomUnitName,
                            out var convInOver
                        )
                        || !TrySumInTargetUnit(
                            [entry.Tok],
                            underIng.Unit,
                            underIng.CustomUnitName,
                            out var convInUnder
                        )
                    )
                    {
                        continue;
                    }

                    if (sumByMember[underIdx] + convInUnder <= underMax * 1.05m)
                    {
                        stepSegments[entry.StepIdx][entry.SegIdx].Token = entry.Tok with
                        {
                            IngredientIndex = underIdx,
                        };
                        sumByMember[overIdx] -= convInOver;
                        sumByMember[underIdx] += convInUnder;
                    }
                }
            }
        }
    }

    private static void UnlinkIncidentalOverAllocatedWaterTokens(
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        List<List<MutableSegment>> stepSegments
    )
    {
        for (var idx = 0; idx < ingredients.Count; idx++)
        {
            var ing = ingredients[idx];
            var isWaterOrIce = IsPlainWaterOrIce(ing);
            var hasPlusExtra = HasOpenEndedPlusExtraAllowance(
                ing.PreparationNote,
                ing.OriginalRawText
            );
            if ((!isWaterOrIce && !hasPlusExtra) || ing.Quantity is not > 0m)
            {
                continue;
            }

            var targetQty = ing.Quantity.Value;
            var targetMax = ing.MaxQuantity ?? targetQty;
            var entries =
                new List<(
                    int StepIdx,
                    int SegIdx,
                    StepIngredientToken Tok,
                    decimal ConvAmt,
                    bool IsSoakAndDrain
                )>();
            var allConvertible = true;

            for (var s = 0; s < stepSegments.Count; s++)
            {
                var stepPlain = string.Concat(
                    stepSegments[s].Select(seg => seg.IsToken ? " " : seg.Text)
                );
                var isSoakAndDrain =
                    SoakAndDrainStepRegex().IsMatch(stepPlain)
                    || (!isWaterOrIce && hasPlusExtra && BoilingContextRegex().IsMatch(stepPlain));
                var segs = stepSegments[s];
                for (var i = 0; i < segs.Count; i++)
                {
                    if (
                        segs[i].Token is not StepIngredientToken tok
                        || tok.IngredientIndex != idx
                        || !tok.Amount.HasValue
                        || tok.Fixed
                    )
                    {
                        continue;
                    }

                    var prevText = i > 0 && !segs[i - 1].IsToken ? segs[i - 1].Text : string.Empty;
                    var nextText =
                        i + 1 < segs.Count && !segs[i + 1].IsToken
                            ? segs[i + 1].Text
                            : string.Empty;
                    if (HasDistributiveInClause(prevText, nextText))
                    {
                        continue;
                    }

                    if (!TrySumInTargetUnit([tok], ing.Unit, ing.CustomUnitName, out var convAmt))
                    {
                        allConvertible = false;
                        break;
                    }

                    entries.Add((s, i, tok, convAmt, isSoakAndDrain));
                }

                if (!allConvertible)
                {
                    break;
                }
            }

            if (!allConvertible || entries.Count < 2)
            {
                continue;
            }

            var totalSum = entries.Sum(e => e.ConvAmt);
            if (totalSum <= targetMax * 1.05m)
            {
                continue;
            }

            var minMatch = targetQty * 0.95m;
            var maxMatch = targetMax * 1.05m;
            var bestSubset = FindExactWaterSubset(entries, minMatch, maxMatch, targetQty);
            if (bestSubset is null)
            {
                continue;
            }

            var keepSet = bestSubset.ToHashSet();
            for (var e = 0; e < entries.Count; e++)
            {
                if (!keepSet.Contains(e))
                {
                    var entry = entries[e];
                    stepSegments[entry.StepIdx][entry.SegIdx].Token = entry.Tok with
                    {
                        IngredientIndex = null,
                    };
                }
            }
        }
    }

    private static List<int>? FindExactWaterSubset(
        IReadOnlyList<(
            int StepIdx,
            int SegIdx,
            StepIngredientToken Tok,
            decimal ConvAmt,
            bool IsSoakAndDrain
        )> entries,
        decimal minMatch,
        decimal maxMatch,
        decimal targetQty
    )
    {
        if (entries.Count <= 12)
        {
            List<int>? bestIndices = null;
            var bestSoakCount = int.MaxValue;
            var bestDiff = decimal.MaxValue;
            var bestPopCount = int.MinValue;

            var totalMasks = 1 << entries.Count;
            for (var mask = 1; mask < totalMasks; mask++)
            {
                var sum = 0m;
                var soakCount = 0;
                var popCount = 0;
                for (var bit = 0; bit < entries.Count; bit++)
                {
                    if ((mask & (1 << bit)) == 0)
                    {
                        continue;
                    }

                    sum += entries[bit].ConvAmt;
                    if (entries[bit].IsSoakAndDrain)
                    {
                        soakCount++;
                    }
                    popCount++;
                }

                if (sum < minMatch || sum > maxMatch)
                {
                    continue;
                }

                var diff = Math.Abs(sum - targetQty);
                var isBetter =
                    bestIndices is null
                    || soakCount < bestSoakCount
                    || (soakCount == bestSoakCount && diff < bestDiff)
                    || (soakCount == bestSoakCount && diff == bestDiff && popCount > bestPopCount);
                if (isBetter)
                {
                    bestSoakCount = soakCount;
                    bestDiff = diff;
                    bestPopCount = popCount;
                    bestIndices = [];
                    for (var bit = 0; bit < entries.Count; bit++)
                    {
                        if ((mask & (1 << bit)) != 0)
                        {
                            bestIndices.Add(bit);
                        }
                    }
                }
            }

            return bestIndices;
        }

        var singleCandidates = Enumerable
            .Range(0, entries.Count)
            .Where(i => entries[i].ConvAmt >= minMatch && entries[i].ConvAmt <= maxMatch)
            .OrderBy(i => entries[i].IsSoakAndDrain ? 1 : 0)
            .ThenBy(i => Math.Abs(entries[i].ConvAmt - targetQty))
            .ToList();
        if (singleCandidates.Count > 0)
        {
            return [singleCandidates[0]];
        }

        var pairCandidates = new List<(int A, int B, int SoakCount, decimal Diff)>();
        for (var a = 0; a < entries.Count; a++)
        {
            for (var b = a + 1; b < entries.Count; b++)
            {
                var pairSum = entries[a].ConvAmt + entries[b].ConvAmt;
                if (pairSum >= minMatch && pairSum <= maxMatch)
                {
                    var soakCount =
                        (entries[a].IsSoakAndDrain ? 1 : 0) + (entries[b].IsSoakAndDrain ? 1 : 0);
                    pairCandidates.Add((a, b, soakCount, Math.Abs(pairSum - targetQty)));
                }
            }
        }

        if (pairCandidates.Count > 0)
        {
            var best = pairCandidates.OrderBy(p => p.SoakCount).ThenBy(p => p.Diff).First();
            return [best.A, best.B];
        }

        return null;
    }

    private static string BuildStepRestAfterSegment(
        IReadOnlyList<MutableSegment> segments,
        int tokenSegmentIndex
    )
    {
        var sb = new StringBuilder();
        for (var i = tokenSegmentIndex + 1; i < segments.Count; i++)
        {
            if (segments[i].Token is not null)
            {
                sb.Append(' ');
                sb.Append(
                    segments[i].Token switch
                    {
                        StepIngredientToken ing => ing.Ingredient,
                        StepMixtureToken mix => mix.Name,
                        StepFixedToken fx => fx.Name ?? string.Empty,
                        StepDimensionToken dim => dim.Unit.ToString().ToLowerInvariant(),
                        _ => string.Empty,
                    }
                );
                continue;
            }

            sb.Append(segments[i].Text);
            if (segments[i].Text.IndexOfAny(['.', '!', '?']) >= 0 || sb.Length > 180)
            {
                break;
            }
        }

        return sb.ToString();
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
        var tokenSingularFull = SingularizePhrase(tokenFull);
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

            if (
                !string.IsNullOrEmpty(tokenSingularFull)
                && string.Equals(
                    SingularizePhrase(candFull),
                    tokenSingularFull,
                    StringComparison.Ordinal
                )
            )
            {
                maxBase = Math.Max(maxBase, 96);
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

        // Process more specific (longer) unlinked ingredient names before shorter ones
        // (e.g., "red onion" before "onion", "garlic cloves" before "ground cloves").
        var orderedUnlinkedIndices = Enumerable
            .Range(0, ingredients.Count)
            .Where(i => !referencedIndices.Contains(i))
            .OrderByDescending(i => NormalizePhrase(ingredients[i].DisplayName).Length)
            .ThenBy(i => i)
            .ToList();

        foreach (var idx in orderedUnlinkedIndices)
        {
            var ing = ingredients[idx];
            var hasSection = !string.IsNullOrWhiteSpace(ing.SectionName);
            var matchedInSameSection = false;
            if (hasSection)
            {
                var sectionAliases = BuildSearchAliases(
                    ing,
                    ingredients,
                    idx,
                    sameSectionOnly: true
                );
                if (sectionAliases.Count > 0)
                {
                    matchedInSameSection = TryWrapAliasesInSteps(
                        idx,
                        sectionAliases,
                        steps,
                        stepSegments,
                        requireSectionMatch: true,
                        targetSectionName: ing.SectionName,
                        ingredients
                    );
                }
            }

            if (!matchedInSameSection)
            {
                var globalAliases = BuildSearchAliases(
                    ing,
                    ingredients,
                    idx,
                    sameSectionOnly: false
                );
                if (globalAliases.Count > 0)
                {
                    matchedInSameSection = TryWrapAliasesInSteps(
                        idx,
                        globalAliases,
                        steps,
                        stepSegments,
                        requireSectionMatch: false,
                        targetSectionName: null,
                        ingredients
                    );
                }
            }

            if (
                !matchedInSameSection
                && ing.Quantity is > 0m
                && string.Equals(
                    NormalizePhrase(ing.DisplayName),
                    "water",
                    StringComparison.Ordinal
                )
            )
            {
                TryWrapSafeSingleUseWater(idx, ing.SectionName, steps, stepSegments);
            }
        }
    }

    private static void TryWrapSafeSingleUseWater(
        int ingredientIdx,
        string? sectionName,
        IReadOnlyList<IngestionDraftStepDto> steps,
        List<List<MutableSegment>> stepSegments
    )
    {
        var restrictSection = !string.IsNullOrWhiteSpace(sectionName);
        var waterMatches = new List<(int StepIdx, int SegIdx, Match Match)>();

        for (var s = 0; s < steps.Count; s++)
        {
            if (
                restrictSection
                && !string.Equals(
                    steps[s].SectionName?.Trim(),
                    sectionName!.Trim(),
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

                var text = segs[i].Text;
                foreach (
                    Match m in Regex.Matches(
                        text,
                        @"\bwater\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    )
                )
                {
                    var before = text[..m.Index];
                    var after = text[(m.Index + m.Length)..];
                    if (
                        EquipmentFollowingRegex().IsMatch(after)
                        || CompoundFoodFollowingRegex().IsMatch(after)
                        || OmitPrecedingRegex().IsMatch(before)
                        || ExcludedPlainWaterPrecedingRegex().IsMatch(before)
                    )
                    {
                        continue;
                    }

                    waterMatches.Add((s, i, m));
                    if (waterMatches.Count > 1)
                    {
                        return;
                    }
                }
            }
        }

        if (waterMatches.Count != 1)
        {
            return;
        }

        var (stepIdx, segIdx, match) = waterMatches[0];
        var segsTarget = stepSegments[stepIdx];
        var srcText = segsTarget[segIdx].Text;
        var prefixText = srcText[..match.Index];
        var suffixText = srcText[(match.Index + match.Length)..];
        var replacement = new List<MutableSegment>(3);
        if (!string.IsNullOrEmpty(prefixText))
        {
            replacement.Add(new MutableSegment { Text = prefixText });
        }
        replacement.Add(
            new MutableSegment
            {
                Token = new StepIngredientToken(
                    Ingredient: match.Value,
                    IngredientIndex: ingredientIdx
                ),
            }
        );
        if (!string.IsNullOrEmpty(suffixText))
        {
            replacement.Add(new MutableSegment { Text = suffixText });
        }

        segsTarget.RemoveAt(segIdx);
        segsTarget.InsertRange(segIdx, replacement);
    }

    private static List<MutableSegment>? TryWrapFirstAliasInText(
        string text,
        IReadOnlyList<string> aliases,
        int ingredientIdx,
        IReadOnlyList<IngestionDraftIngredientDto> allIngredients,
        string selfDisplayNorm
    )
    {
        var selfWords = selfDisplayNorm
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(SingularizeWord)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in aliases)
        {
            var pattern = $@"(?<![\p{{L}}\p{{N}}-]){Regex.Escape(alias)}(?![\p{{L}}\p{{N}}-])";
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

                if (
                    ShouldSkipDueToPrecedingModifier(
                        before,
                        match.Value,
                        ingredientIdx,
                        allIngredients,
                        selfWords
                    )
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
                    var rest = TryWrapFirstAliasInText(
                        suffixText,
                        aliases,
                        ingredientIdx,
                        allIngredients,
                        selfDisplayNorm
                    );
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

    private static bool ShouldSkipDueToPrecedingModifier(
        string before,
        string matchedText,
        int selfIdx,
        IReadOnlyList<IngestionDraftIngredientDto> allIngredients,
        HashSet<string> selfWords
    )
    {
        var prevWordMatch = Regex.Match(before, @"(?<prevWord>[\p{L}-]+)\s+$");
        if (!prevWordMatch.Success)
        {
            return false;
        }

        var prevWord = NormalizePhrase(prevWordMatch.Groups["prevWord"].Value);
        var prevWordSing = SingularizeWord(prevWord);
        if (string.IsNullOrWhiteSpace(prevWord) || selfWords.Contains(prevWordSing))
        {
            return false;
        }

        var matchSing = SingularizePhrase(NormalizePhrase(matchedText));
        if (prevWordSing == "garlic" && matchSing is "clove" && !selfWords.Contains("garlic"))
        {
            return true;
        }

        if (ConflictingModifierPrefixes.Contains(prevWord))
        {
            return true;
        }

        var combined = NormalizePhrase($"{prevWord} {matchedText}");
        var combinedSing = SingularizePhrase(combined);
        for (var i = 0; i < allIngredients.Count; i++)
        {
            if (i == selfIdx)
            {
                continue;
            }

            var otherNorm = NormalizePhrase(allIngredients[i].DisplayName);
            var otherSing = SingularizePhrase(otherNorm);
            if (
                string.Equals(otherNorm, combined, StringComparison.Ordinal)
                || string.Equals(otherSing, combinedSing, StringComparison.Ordinal)
                || ContainsWholePhrase(otherNorm, combined)
                || ContainsWholePhrase(otherNorm, combinedSing)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> BuildSearchAliases(
        IngestionDraftIngredientDto ing,
        IReadOnlyList<IngestionDraftIngredientDto> allIngredients,
        int selfIdx,
        bool sameSectionOnly = false
    )
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddAlias(string? candidate, bool checkConflict, bool exactDisplayConflictOnly = false)
        {
            var cleaned = NormalizePhrase(candidate);
            if (cleaned.Length < 3 || StopAliases.Contains(cleaned) || seen.Contains(cleaned))
            {
                return;
            }
            if (
                checkConflict
                && AliasConflictsWithOtherIngredient(
                    cleaned,
                    allIngredients,
                    selfIdx,
                    sameSectionOnly,
                    exactDisplayConflictOnly
                )
            )
            {
                return;
            }
            seen.Add(cleaned);
            results.Add(cleaned);
        }

        void AddAliasAndNumberVariants(
            string? candidate,
            bool checkConflict,
            bool exactDisplayConflictOnly = false
        )
        {
            var cleaned = NormalizePhrase(candidate);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return;
            }

            AddAlias(cleaned, checkConflict, exactDisplayConflictOnly);
            var sing = SingularizePhrase(cleaned);
            AddAlias(sing, checkConflict, exactDisplayConflictOnly);
            var plur = PluralizePhrase(cleaned);
            AddAlias(plur, checkConflict, exactDisplayConflictOnly);
        }

        var displayNorm = NormalizePhrase(ing.DisplayName);
        AddAliasAndNumberVariants(
            displayNorm,
            checkConflict: true,
            exactDisplayConflictOnly: !sameSectionOnly && string.IsNullOrWhiteSpace(ing.SectionName)
        );

        var formStripped = StripFormSuffixes(displayNorm);
        AddAliasAndNumberVariants(formStripped, checkConflict: true);

        var prepStripped = StripPrepAdjectives(formStripped);
        AddAliasAndNumberVariants(prepStripped, checkConflict: true);

        // Progressive trailing suffixes: always keep multi-word suffixes (e.g. "brown sugar"),
        // and allow single-word tail fallbacks only for safe pantry staples (oil, cream, butter, sugar, salt, flour, etc.)
        var words = formStripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var start = 1; start < words.Length; start++)
        {
            var suffix = string.Join(' ', words[start..]);
            var remainingWordCount = words.Length - start;
            var tailSing = SingularizeWord(words[^1]);
            if (
                remainingWordCount >= 2
                || SafeSingleWordFallbackNouns.Contains(tailSing)
                || tailSing == "clove"
            )
            {
                AddAliasAndNumberVariants(suffix, checkConflict: true);
            }
        }

        // Distinguishing modifier before generic head nouns (e.g., "serrano chile" -> "serrano", "feta cheese" -> "feta", "vanilla extract" -> "vanilla")
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
                var singularHead = SingularizeWord(headWord);
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
            if (words[end - 1] is "clove" or "cloves" && words[end - 2] is "ground" or "whole")
            {
                break;
            }
            end--;
        }
        return end < words.Length ? string.Join(' ', words[..end]) : normalizedPhrase;
    }

    private static bool AliasConflictsWithOtherIngredient(
        string candidate,
        IReadOnlyList<IngestionDraftIngredientDto> allIngredients,
        int selfIdx,
        bool sameSectionOnly = false,
        bool exactDisplayConflictOnly = false
    )
    {
        var selfNorm = NormalizePhrase(allIngredients[selfIdx].DisplayName);
        var selfSection = allIngredients[selfIdx].SectionName?.Trim() ?? string.Empty;
        var singular = SingularizePhrase(candidate.ToLowerInvariant());
        var singleWord = !candidate.Contains(' ');
        for (var i = 0; i < allIngredients.Count; i++)
        {
            if (i == selfIdx)
            {
                continue;
            }

            if (sameSectionOnly && !string.IsNullOrEmpty(selfSection))
            {
                var otherSec = allIngredients[i].SectionName?.Trim() ?? string.Empty;
                if (!string.Equals(otherSec, selfSection, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var otherNorm = NormalizePhrase(allIngredients[i].DisplayName);
            var otherSing = SingularizePhrase(otherNorm);

            if (exactDisplayConflictOnly)
            {
                if (
                    string.Equals(otherNorm, candidate, StringComparison.Ordinal)
                    || string.Equals(otherSing, singular, StringComparison.Ordinal)
                )
                {
                    return true;
                }
                continue;
            }

            // Allow spice "ground/whole cloves" to use "cloves" without being blocked by "garlic cloves"
            // ("garlic cloves" in step text is already guarded by ShouldSkipDueToPrecedingModifier).
            if (
                singular == "clove"
                && (
                    selfNorm.Contains("ground clove", StringComparison.Ordinal)
                    || selfNorm.Contains("whole clove", StringComparison.Ordinal)
                )
                && otherNorm.Contains("garlic", StringComparison.Ordinal)
            )
            {
                continue;
            }

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
        string? targetSectionName,
        IReadOnlyList<IngestionDraftIngredientDto> allIngredients
    )
    {
        var selfDisplayNorm = NormalizePhrase(allIngredients[ingredientIdx].DisplayName);
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

                var replacement = TryWrapFirstAliasInText(
                    segs[i].Text,
                    aliases,
                    ingredientIdx,
                    allIngredients,
                    selfDisplayNorm
                );
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

                var isDistributive = HasDistributiveInClause(prevText, nextText);
                var isIncrementalRate = IncrementalRateFollowingRegex().IsMatch(nextText);

                if (
                    tok.Amount.HasValue
                    && !tok.Fixed
                    && (
                        isIncrementalRate
                        || (
                            isDistributive
                            && (
                                !ingredients[idx].Quantity.HasValue
                                || (
                                    TrySumInTargetUnit(
                                        [tok],
                                        ingredients[idx].Unit,
                                        ingredients[idx].CustomUnitName,
                                        out var stepInIngUnit
                                    )
                                    && stepInIngUnit < ingredients[idx].Quantity!.Value * 0.95m
                                )
                            )
                        )
                    )
                )
                {
                    tok = tok with { Fixed = true };
                    segs[i].Token = tok;
                }

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
                        IsDistributive: isDistributive || isIncrementalRate || tok.Fixed,
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
            var primaryUnquantified =
                nonGarnishUnquantified.Count == 1
                    ? nonGarnishUnquantified[0]
                    : (
                        nonGarnishUnquantified.Count > 1
                        && !IsPostAdditionStateOrDiscardReference(nonGarnishUnquantified[0])
                        && nonGarnishUnquantified.Skip(1).All(IsPostAdditionStateOrDiscardReference)
                            ? nonGarnishUnquantified[0]
                            : null
                    );
            if (!anyQuantified && primaryUnquantified is not null && ing.Quantity.HasValue)
            {
                var occ = primaryUnquantified;
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

            // 3C-bis. Convert post-exhaustion tempering/transfer mentions to StepMixtureToken
            if (ing.Quantity.HasValue)
            {
                var qtyListed = ing.Quantity.Value;
                var maxListed = ing.MaxQuantity ?? qtyListed;
                var nonDist = occs.Where(o => o.Token.Amount.HasValue && !o.IsDistributive)
                    .ToList();
                if (
                    maxListed > 0m
                    && nonDist.Count >= 2
                    && TrySumInTargetUnit(
                        nonDist.Select(o => o.Token),
                        ing.Unit,
                        ing.CustomUnitName,
                        out var sumAll
                    )
                    && sumAll > maxListed * 1.05m
                )
                {
                    var runningSum = 0m;
                    var sawRemaining = false;
                    var exhausted = false;
                    var converted = new HashSet<TokenOccurrence>();

                    foreach (var occ in nonDist)
                    {
                        if (
                            !TrySumInTargetUnit(
                                [occ.Token],
                                ing.Unit,
                                ing.CustomUnitName,
                                out var convAmt
                            )
                        )
                        {
                            break;
                        }

                        if (!exhausted)
                        {
                            if (
                                LeadingRemainingPhraseRegex().IsMatch(occ.PrevText)
                                || occ.PrevText.Contains(
                                    "remaining",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                sawRemaining = true;
                            }
                            runningSum += convAmt;
                            if (runningSum >= qtyListed * 0.95m && runningSum <= maxListed * 1.05m)
                            {
                                exhausted = true;
                            }
                            continue;
                        }

                        var clause = $"{occ.PrevText} {occ.NextText}";
                        if (sawRemaining || TemperOrHotTransferClauseRegex().IsMatch(clause))
                        {
                            var mixName = !string.IsNullOrWhiteSpace(occ.Token.Ingredient)
                                ? occ.Token.Ingredient.Trim()
                                : ing.DisplayName;
                            stepSegments[occ.StepIndex][occ.SegmentIndex].Token =
                                new StepMixtureToken(
                                    Name: mixName,
                                    Amount: occ.Token.Amount,
                                    Unit: occ.Token.Unit,
                                    CustomUnitName: occ.Token.CustomUnitName,
                                    MaxAmount: occ.Token.MaxAmount
                                );
                            converted.Add(occ);
                        }
                    }

                    if (converted.Count > 0)
                    {
                        occs.RemoveAll(converted.Contains);
                    }
                }
            }

            // 3D. True over-allocation warning (excluding distributive and hypothetical/discard uses)
            if (
                measurementWarnings is not null
                && ing.Quantity.HasValue
                && !HasOpenEndedPlusExtraAllowance(ing.PreparationNote, ing.OriginalRawText)
            )
            {
                var maxAllowed = ing.MaxQuantity ?? ing.Quantity.Value;
                if (maxAllowed > 0m)
                {
                    var nonDistQuantified = occs.Where(o =>
                            o.Token.Amount.HasValue
                            && !o.IsDistributive
                            && !IsPostAdditionStateOrDiscardReference(o)
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

    private static bool IsPostAdditionStateOrDiscardReference(TokenOccurrence occ)
    {
        var lastBoundary = occ.PrevText.LastIndexOfAny(['.', '!', '?', ';']);
        var prevClause = lastBoundary >= 0 ? occ.PrevText[(lastBoundary + 1)..] : occ.PrevText;
        var nextBoundary = occ.NextText.IndexOfAny(['.', '!', '?', ';']);
        var nextClause = nextBoundary >= 0 ? occ.NextText[..nextBoundary] : occ.NextText;

        if (
            Regex.IsMatch(
                prevClause,
                @"\b(?:until|when|once|whether)\b[^.!?;]*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
            && Regex.IsMatch(
                nextClause,
                @"^\s*(?:is|are|was|were|has|have|should|begins?|starts?|turns?|becomes?|melts?|softens?|dissolves?|browns?|darkens?|thickens?|cools?|simmers?|boils?|registers?|reaches?|clings?|offers?|holds?|pulls?|comes?|looks?|feels?|appears?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            return true;
        }

        if (
            prevClause.TrimEnd().EndsWith('(')
            && Regex.IsMatch(
                nextClause,
                @"^\s*should\s+be\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            return true;
        }

        if (
            Regex.IsMatch(
                prevClause,
                @"\b(?:discard(?:ing)?|remove\s+and\s+discard)\s+(?:the\s+|any\s+)?(?:[\p{L}-]+\s+){0,3}(?:and\s+(?:the\s+)?)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            return true;
        }

        if (
            Regex.IsMatch(
                prevClause,
                @"\b(?:instead\s+of(?:\s+[\p{L}-]+)?|rather\s+than(?:\s+[\p{L}-]+)?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        )
        {
            return true;
        }

        return false;
    }

    private static bool HasDistributiveInClause(string prevText, string nextText)
    {
        var lastBoundary = prevText.LastIndexOfAny(['.', '!', '?', ';']);
        var prevClause = lastBoundary >= 0 ? prevText[(lastBoundary + 1)..] : prevText;
        if (prevClause.Length > 60)
        {
            prevClause = prevClause[^60..];
        }

        var nextBoundary = nextText.IndexOfAny(['.', '!', '?', ';']);
        var nextClause = nextBoundary >= 0 ? nextText[..nextBoundary] : nextText;
        if (nextClause.Length > 60)
        {
            nextClause = nextClause[..60];
        }

        return DistributiveContextRegex().IsMatch(prevClause)
            || DistributiveContextRegex().IsMatch(nextClause);
    }

    private static void UnwrapUnquantifiedNonSubRecipeTokens(
        IReadOnlyList<IngestionDraftIngredientDto> ingredients,
        List<List<MutableSegment>> stepSegments
    )
    {
        foreach (var segs in stepSegments)
        {
            var changed = false;
            foreach (var seg in segs)
            {
                if (seg.Token is not StepIngredientToken { Amount: null } unqTok)
                {
                    continue;
                }

                var isSubRecipe =
                    unqTok.IngredientIndex is int idx
                    && idx >= 0
                    && idx < ingredients.Count
                    && ingredients[idx].IsRecipeReference;
                if (isSubRecipe)
                {
                    continue;
                }

                seg.Text = !string.IsNullOrWhiteSpace(unqTok.DisplayText)
                    ? unqTok.DisplayText
                    : unqTok.Ingredient;
                seg.Token = null;
                changed = true;
            }

            if (changed)
            {
                CoalesceTextSegments(segs);
            }
        }
    }

    /// <summary>
    /// Returns true if the ingredient's normalized aliases or head noun appear in plain step prose.
    /// </summary>
    public static bool IsIngredientReferencedInText(
        IngestionDraftIngredientDto ingredient,
        string normalizedPlainStepCorpus
    )
    {
        if (string.IsNullOrWhiteSpace(normalizedPlainStepCorpus))
        {
            return false;
        }

        foreach (var candidate in EnumerateCandidateLabels(ingredient))
        {
            var norm = NormalizePhrase(candidate);
            if (norm.Length < 3)
            {
                continue;
            }

            var formStripped = StripFormSuffixes(norm);
            var prepStripped = StripPrepAdjectives(formStripped);

            foreach (var probe in new[] { norm, formStripped, prepStripped })
            {
                if (probe.Length < 3 || StopAliases.Contains(probe))
                {
                    continue;
                }

                if (
                    ContainsWholePhrase(normalizedPlainStepCorpus, probe)
                    || ContainsWholePhrase(normalizedPlainStepCorpus, SingularizePhrase(probe))
                )
                {
                    return true;
                }
            }

            var head = ExtractHeadNoun(prepStripped);
            if (
                head.Length >= 3
                && !StopAliases.Contains(head)
                && ContainsWholePhrase(normalizedPlainStepCorpus, head)
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the quantified ingredient is reasonably accounted for by linked step tokens.
    /// </summary>
    public static bool IsQuantifiedIngredientReconciled(
        IngestionDraftIngredientDto ingredient,
        IReadOnlyList<StepIngredientToken> linkedTokens
    )
    {
        if (!ingredient.Quantity.HasValue || ingredient.Quantity.Value <= 0m)
        {
            return true;
        }

        if (linkedTokens.Count == 0)
        {
            return false;
        }

        if (linkedTokens.Any(t => t.Fixed && t.Amount is > 0m))
        {
            return true;
        }

        var quantified = linkedTokens.Where(t => t.Amount is > 0m).ToList();
        if (quantified.Count == 0)
        {
            return false;
        }

        if (
            !TrySumInTargetUnit(
                quantified,
                ingredient.Unit,
                ingredient.CustomUnitName,
                out var totalStepSum
            )
        )
        {
            return true;
        }

        var minAllowed = ingredient.Quantity.Value * 0.85m;
        var maxAllowed = (ingredient.MaxQuantity ?? ingredient.Quantity.Value) * 1.08m;
        return totalStepSum >= minAllowed && totalStepSum <= maxAllowed;
    }

    internal static string NormalizeTextForReferenceScan(string raw) => NormalizePhrase(raw);

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

    private static readonly HashSet<string> DiscreteCountCustomUnits = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "sheet",
        "piece",
        "leaf",
        "sprig",
        "stalk",
        "rib",
        "strip",
        "ear",
        "pod",
        "fillet",
        "wedge",
        "loaf",
        "head",
        "stick",
        "cutlet",
        "roll",
        "tortilla",
        "wrapper",
        "bun",
        "half",
    };

    private static bool IsDiscreteCountUnit(MeasurementUnit? unit, string? customUnit)
    {
        if (
            unit
            is MeasurementUnit.Whole
                or MeasurementUnit.Slice
                or MeasurementUnit.Clove
                or MeasurementUnit.Can
                or MeasurementUnit.Bunch
        )
        {
            return true;
        }

        if (unit == MeasurementUnit.Custom && !string.IsNullOrWhiteSpace(customUnit))
        {
            var sing = SingularizeWord(customUnit);
            return DiscreteCountCustomUnits.Contains(sing);
        }

        return false;
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

            if (
                (tok.Unit is null && IsDiscreteCountUnit(targetUnit, targetCustomUnit))
                || (targetUnit is null && IsDiscreteCountUnit(tok.Unit, tok.CustomUnitName))
            )
            {
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
        var cleaned = Regex.Replace(withoutParens.ToLowerInvariant(), @"[^\p{L}\p{N}\s-]", " ");
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

    private static string SingularizeWord(string word)
    {
        var lower = word.Trim().ToLowerInvariant();
        return lower switch
        {
            "leaves" => "leaf",
            "loaves" => "loaf",
            "halves" => "half",
            _ => RecipeUnitAndSectionHelpers.Singularize(lower),
        };
    }

    private static string SingularizePhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return string.Empty;
        }

        var parts = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        parts[^1] = SingularizeWord(parts[^1]);
        return string.Join(' ', parts);
    }

    private static string PluralizeWord(string word)
    {
        var lower = word.Trim().ToLowerInvariant();
        if (lower.Length < 2)
        {
            return lower;
        }

        var sing = SingularizeWord(lower);
        if (!string.Equals(sing, lower, StringComparison.Ordinal))
        {
            return lower;
        }

        return lower switch
        {
            "leaf" => "leaves",
            "loaf" => "loaves",
            "half" => "halves",
            "knife" => "knives",
            "tomato" => "tomatoes",
            "potato" => "potatoes",
            _ when lower.EndsWith("y", StringComparison.Ordinal)
                    && lower.Length > 2
                    && lower[^2] is not ('a' or 'e' or 'i' or 'o' or 'u') => lower[..^1] + "ies",
            _ when lower.EndsWith("ch", StringComparison.Ordinal)
                    || lower.EndsWith("sh", StringComparison.Ordinal)
                    || lower.EndsWith('x')
                    || lower.EndsWith('z') => lower + "es",
            _ when lower.EndsWith('s') => lower,
            _ => lower + "s",
        };
    }

    private static string PluralizePhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return string.Empty;
        }

        var parts = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        parts[^1] = PluralizeWord(parts[^1]);
        return string.Join(' ', parts);
    }

    private static string ExtractHeadNoun(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return string.Empty;
        }

        var last = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last is null ? string.Empty : SingularizeWord(last);
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
