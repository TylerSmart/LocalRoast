using System.Globalization;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Ingestion;

/// <summary>
/// Deterministic metadata, tag-guardrail, yield, and note helpers shared by multi-stage
/// recipe ingestion and catalog materialization.
/// </summary>
public static partial class IngestionMetadataAndTagHelper
{
    private static readonly HashSet<string> QuickTimingTagNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Quick Prep",
        "30-Minute Meal",
        "30 Minute Meal",
        "30 Minutes",
        "Quick",
        "Quick And Easy",
    };

    private static readonly HashSet<string> ThermalCookingMethodTagNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Baking",
        "Roasting",
        "Broiling",
        "Deep-Fry",
        "Shallow-Fry",
        "Pan-Sear",
        "Stir-Fry",
        "Sauté",
        "Braise",
        "Boil & Simmer",
        "Steamed",
        "Grill",
        "Charcoal Grill",
        "Gas Grill",
        "Smoker",
        "Campfire",
        "Air Fryer",
        "Instant Pot",
        "Pressure Cooker",
        "Slow Cooker",
        "Wok",
        "Microwave",
        "Toaster Oven",
        "Waffle Maker",
        "Bread",
        "Baked Goods",
    };

    private static readonly HashSet<string> NonEntreeCourseTagNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Appetizer",
        "Starter",
        "Side Dish",
        "Bread",
        "Baked Goods",
        "Snack",
        "Beverage",
        "Drink",
        "Cocktail",
        "Mocktail",
        "Dip",
        "Spread",
        "Topping",
        "Condiment",
        "Sauce",
        "Dressing",
        "Marinade",
        "Dessert",
    };

    private static readonly HashSet<string> EntreeCourseTagNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Main Course",
        "Dinner",
    };

    private static readonly (string[] TagNames, Regex EvidenceRegex)[] ProteinTagGroundingRules =
    [
        (
            ["Beef"],
            new Regex(
                @"\b(?:beef|steak|veal|brisket|chuck|sirloin|ribeye|rib[- ]?eye|tri[- ]?tip|flank|skirt|short\s+ribs?|oxtail|corned\s+beef|pastrami|bresaola|stew\s+meat|roast\s+beef|carne\s+asada|filet\s+mignon|porterhouse|t[- ]?bone|prime\s+rib|london\s+broil|hamburgers?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Ground Beef"],
            new Regex(
                @"\b(?:ground\s+(?:beef|chuck|sirloin|round)|hamburgers?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Steak"],
            new Regex(
                @"\b(?:steak|ribeye|rib[- ]?eye|sirloin|filet\s+mignon|flank|skirt|porterhouse|t[- ]?bone|london\s+broil|carne\s+asada|tri[- ]?tip|strip\s+loin|hanger|bavette)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Pork", "Pork Chops", "Pork Tenderloin", "Pork Shoulder"],
            new Regex(
                @"\b(?:pork|bacon|pancetta|prosciutto|guanciale|lardons?|ham|sausage|chorizo|carnitas|spare\s+ribs?|baby\s+back|boston\s+butt|picnic\s+roast)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Bacon"],
            new Regex(
                @"\b(?:bacon|pancetta|lardons?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Sausage"],
            new Regex(
                @"\b(?:sausage|chorizo|kielbasa|andouille|bratwurst|linguica|mortadella|salami|pepperoni|boudin|merguez|longaniza)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Ham"],
            new Regex(
                @"\b(?:ham|prosciutto|jam[oó]n)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Lamb"],
            new Regex(
                @"\b(?:lamb|mutton)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Chicken", "Chicken Breast", "Chicken Thighs"],
            new Regex(
                @"\b(?:chicken|poultry|cornish\s+hen|rotisserie)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Turkey"],
            new Regex(@"\bturkey\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (["Duck"], new Regex(@"\bduck\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (
            ["Salmon"],
            new Regex(@"\bsalmon\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (["Tuna"], new Regex(@"\btuna\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (
            ["Shrimp"],
            new Regex(
                @"\b(?:shrimp|prawns?)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Scallops"],
            new Regex(@"\bscallops?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            ["Crab"],
            new Regex(
                @"\b(?:crab|crabmeat)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Lobster"],
            new Regex(@"\blobsters?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            ["Mussels"],
            new Regex(@"\bmussels?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            ["White Fish"],
            new Regex(
                @"\b(?:fish|cod|halibut|tilapia|sea\s+bass|snapper|trout|flounder|sole|haddock|mahi|catfish|pollock|branzino|barramundi|grouper|perch|swordfish|monkfish)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (["Tofu"], new Regex(@"\btofu\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (
            ["Tempeh"],
            new Regex(@"\btempeh\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
        (
            ["Chickpeas"],
            new Regex(
                @"\b(?:chickpeas?|garbanzo)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        ),
        (
            ["Lentils"],
            new Regex(@"\blentils?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        ),
    ];

    /// <summary>
    /// Returns true when <paramref name="rawText"/> refers to "(see this page)" / "facing page"
    /// alongside raw grocery indicators or physical weights/measures without a recipe cue.
    /// </summary>
    public static bool IsRawGrocerySeePageNote(string? rawText)
    {
        if (
            string.IsNullOrWhiteSpace(rawText)
            || !SeeThisPageRegex().IsMatch(rawText)
            || RecipeOrFollowsRegex().IsMatch(rawText)
        )
        {
            return false;
        }

        if (RawGroceryIndicatorRegex().IsMatch(rawText))
        {
            return true;
        }

        var spans = SourceMeasurementParser.FindSpans(rawText);
        return spans.Any(s => s.Measurement.Unit is not null and not MeasurementUnit.Whole);
    }

    public static int? ClampMinutes(int? value) => value is >= 0 and <= 10080 ? value : null;

    public static RecipeDifficulty? ParseDifficulty(string? difficulty)
    {
        if (string.IsNullOrWhiteSpace(difficulty))
        {
            return null;
        }

        return difficulty.Trim().ToLowerInvariant() switch
        {
            "easy" => RecipeDifficulty.Easy,
            "medium" => RecipeDifficulty.Medium,
            "project" => RecipeDifficulty.Project,
            _ => null,
        };
    }

    public static string? ResolveYieldDisplay(string? extractedYieldDisplay, string? sourceText)
    {
        var cleaned = CleanOptionalText(extractedYieldDisplay, 100);
        if (cleaned is not null)
        {
            return cleaned;
        }

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return null;
        }

        var match = SourceYieldHeaderRegex().Match(sourceText);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups[1].Value.Trim();
        raw = YieldBaseServingsSuffixRegex().Replace(raw, string.Empty);
        raw = YieldSeasonSuffixRegex().Replace(raw, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = YieldLeadingVerbRegex()
            .Replace(raw, m => char.ToUpperInvariant(m.Value[0]) + m.Value[1..].ToLowerInvariant());

        if (raw == raw.ToUpperInvariant() && raw.Any(char.IsLetter))
        {
            raw = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.ToLowerInvariant());
        }

        return CleanOptionalText(raw, 100);
    }

    public static (List<Guid> TagIds, List<string> ProposedTagNames) ResolveAndGuardTags(
        string? title,
        string? description,
        IReadOnlyList<string>? rawProposedTags,
        IReadOnlyList<CatalogTagLookupItem> catalogTags,
        IReadOnlyList<IngestionDraftIngredientDto> compiledIngredients,
        IReadOnlyList<IngestionDraftStepDto> compiledSteps,
        int? cookTimeMinutes,
        int? resolvedTotalMinutes
    )
    {
        var tagByKey = new Dictionary<string, CatalogTagLookupItem>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var tag in catalogTags)
        {
            foreach (var raw in new[] { tag.Name, tag.NormalizedName, tag.Slug.Replace('-', ' ') })
            {
                var norm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(raw);
                if (!string.IsNullOrWhiteSpace(norm))
                {
                    tagByKey.TryAdd(norm, tag);
                }

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    tagByKey.TryAdd(raw.Trim(), tag);
                }
            }
        }

        var rawList = rawProposedTags ?? [];
        var matchedEntries = new List<(Guid Id, string Name)>();
        var seenTagIds = new HashSet<Guid>();
        var proposedNames = new List<string>();
        var seenProposedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exceedsQuickThreshold = resolvedTotalMinutes is > 30;

        CatalogTagLookupItem? FindCatalogTag(string nameOrAlias)
        {
            var resolved = ResolveCanonicalTagAlias(nameOrAlias);
            if (tagByKey.TryGetValue(resolved, out var direct))
            {
                return direct;
            }

            var norm = RecipeUnitAndSectionHelpers.NormalizeLookupKey(resolved);
            return !string.IsNullOrWhiteSpace(norm) && tagByKey.TryGetValue(norm, out var byNorm)
                ? byNorm
                : null;
        }

        foreach (var rawTag in rawList)
        {
            if (string.IsNullOrWhiteSpace(rawTag))
            {
                continue;
            }

            var tagKey = rawTag.Trim();
            if (BuiltInTags.IsDisallowedTagName(tagKey))
            {
                continue;
            }

            var canonicalAlias = ResolveCanonicalTagAlias(tagKey);
            if (
                exceedsQuickThreshold
                && (
                    QuickTimingTagNames.Contains(tagKey)
                    || QuickTimingTagNames.Contains(canonicalAlias)
                )
            )
            {
                continue;
            }

            var exactTag = FindCatalogTag(tagKey);
            if (exactTag is not null)
            {
                if (exceedsQuickThreshold && QuickTimingTagNames.Contains(exactTag.Name))
                {
                    continue;
                }

                if (seenTagIds.Add(exactTag.Id))
                {
                    matchedEntries.Add((exactTag.Id, exactTag.Name));
                }
                continue;
            }

            var formatted = RecipeUnitAndSectionHelpers.ToTagDisplayCase(canonicalAlias);
            if (
                exceedsQuickThreshold
                && !string.IsNullOrWhiteSpace(formatted)
                && QuickTimingTagNames.Contains(formatted)
            )
            {
                continue;
            }

            if (
                !string.IsNullOrWhiteSpace(formatted)
                && !BuiltInTags.IsDisallowedTagName(formatted)
                && seenProposedNames.Add(formatted)
            )
            {
                proposedNames.Add(formatted);
            }
        }

        bool HasTag(string tagName) =>
            matchedEntries.Any(e =>
                string.Equals(e.Name, tagName, StringComparison.OrdinalIgnoreCase)
            )
            || proposedNames.Any(n => string.Equals(n, tagName, StringComparison.OrdinalIgnoreCase))
            || rawList.Any(t =>
                string.Equals(t?.Trim(), tagName, StringComparison.OrdinalIgnoreCase)
            );

        void RemoveTag(string tagName)
        {
            matchedEntries.RemoveAll(e =>
                string.Equals(e.Name, tagName, StringComparison.OrdinalIgnoreCase)
            );
            proposedNames.RemoveAll(n =>
                string.Equals(n, tagName, StringComparison.OrdinalIgnoreCase)
            );
        }

        void TryAddCatalogTag(string tagName)
        {
            var tag = FindCatalogTag(tagName);
            if (tag is not null && seenTagIds.Add(tag.Id))
            {
                matchedEntries.Add((tag.Id, tag.Name));
            }
        }

        var profile = AnalyzeDietaryAndCookingProfile(
            title,
            description,
            compiledIngredients.Select(i =>
                (
                    Name: string.Join(
                        " ",
                        new[]
                        {
                            i.ProposedFoodItemName,
                            i.MatchedFoodItemName,
                            i.DisplayName,
                            i.OriginalRawText,
                        }.Where(s => !string.IsNullOrWhiteSpace(s))
                    ),
                    Category: i.SuggestedCategory
                )
            ),
            compiledSteps.Select(s =>
            {
                var tokens = StepInstructionParser.ExtractTokens(s.Instruction);
                var tempsF = tokens
                    .OfType<StepTemperatureToken>()
                    .Select(t =>
                        t.Fahrenheit
                        ?? (t.Celsius.HasValue ? (t.Celsius.Value * 9m / 5m) + 32m : (decimal?)null)
                    )
                    .ToList();
                return (
                    StepText: StepInstructionParser.ToPlainText(s.Instruction),
                    TempFahrenheit: (IReadOnlyList<decimal?>)tempsF
                );
            }),
            cookTimeMinutes,
            HasTag
        );

        if (profile.HasMeatOrSeafood)
        {
            RemoveTag("Vegetarian");
            RemoveTag("Vegan");
        }

        if (profile.HasDairy)
        {
            RemoveTag("Vegan");
            RemoveTag("Dairy-Free");
        }

        if (profile.HasEggOrHoney)
        {
            RemoveTag("Vegan");
        }

        var hasValidVegan =
            !profile.HasMeatOrSeafood
            && !profile.HasDairy
            && !profile.HasEggOrHoney
            && HasTag("Vegan");
        if (hasValidVegan)
        {
            TryAddCatalogTag("Vegetarian");
            TryAddCatalogTag("Dairy-Free");
        }

        if (HasTag("Deep-Fry"))
        {
            RemoveTag("Pan-Sear");
        }

        if (HasTag("Stir-Fry"))
        {
            var combinedContext = string.Join(
                "\n",
                new[] { title, description }
                    .Concat(
                        compiledSteps.Select(s => StepInstructionParser.ToPlainText(s.Instruction))
                    )
                    .Where(s => !string.IsNullOrWhiteSpace(s))
            );
            var hasStirFryCue =
                StirFryStepCueRegex().IsMatch(combinedContext)
                || (
                    (WokCueRegex().IsMatch(combinedContext) || HasTag("Wok")) && !HasTag("Deep-Fry")
                );
            if (!hasStirFryCue)
            {
                RemoveTag("Stir-Fry");
            }
        }

        if (HasTag("One-Pan") || HasTag("One-Pot"))
        {
            var combinedSteps = string.Join(
                "\n",
                compiledSteps.Select(s => StepInstructionParser.ToPlainText(s.Instruction))
            );
            if (CountDistinctVesselFamilies(combinedSteps) >= 2)
            {
                RemoveTag("One-Pan");
                RemoveTag("One-Pot");
            }
        }

        if (!profile.IsNoCookCandidate)
        {
            RemoveTag("No-Cook");
        }
        else
        {
            TryAddCatalogTag("No-Cook");
        }

        var hasNonEntreeCourse =
            matchedEntries.Any(e => NonEntreeCourseTagNames.Contains(e.Name))
            || proposedNames.Any(n => NonEntreeCourseTagNames.Contains(n));
        if (hasNonEntreeCourse)
        {
            matchedEntries.RemoveAll(e => EntreeCourseTagNames.Contains(e.Name));
            proposedNames.RemoveAll(n => EntreeCourseTagNames.Contains(n));
        }

        var rawProteinCorpus = string.Join(
            " ",
            new[] { title, description }
                .Concat(
                    compiledIngredients.SelectMany(i =>
                        new[]
                        {
                            i.ProposedFoodItemName,
                            i.MatchedFoodItemName,
                            i.DisplayName,
                            i.OriginalRawText,
                        }
                    )
                )
                .Where(s => !string.IsNullOrWhiteSpace(s))
        );
        var cleanProteinCorpus = VeganOrProduceMeatExclusionRegex().Replace(rawProteinCorpus, " ");
        foreach (var (tagNames, evidenceRegex) in ProteinTagGroundingRules)
        {
            if (tagNames.Any(HasTag) && !evidenceRegex.IsMatch(cleanProteinCorpus))
            {
                foreach (var tagName in tagNames)
                {
                    RemoveTag(tagName);
                }
            }
        }

        return (matchedEntries.Select(e => e.Id).ToList(), proposedNames);
    }

    public static string? CleanOptionalText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    public static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;

    private static string ResolveCanonicalTagAlias(string tagKey)
    {
        if (BuiltInTags.CanonicalAliases.TryGetValue(tagKey, out var alias))
        {
            return alias;
        }

        var display = RecipeUnitAndSectionHelpers.ToTagDisplayCase(tagKey);
        return BuiltInTags.CanonicalAliases.TryGetValue(display, out var displayAlias)
            ? displayAlias
            : tagKey;
    }

    private readonly record struct DietaryAndCookingProfile(
        bool HasMeatOrSeafood,
        bool HasDairy,
        bool HasEggOrHoney,
        bool IsNoCookCandidate
    );

    private static DietaryAndCookingProfile AnalyzeDietaryAndCookingProfile(
        string? title,
        string? description,
        IEnumerable<(string Name, FoodCategory? Category)> ingredients,
        IEnumerable<(string StepText, IReadOnlyList<decimal?> TempFahrenheit)> steps,
        int? cookTimeMinutes,
        Func<string, bool> hasTag
    )
    {
        var hasMeatOrSeafood = false;
        var hasDairy = false;
        var hasEggOrHoney = false;

        foreach (var (rawName, category) in ingredients)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var withoutMeatExclusions = VeganOrProduceMeatExclusionRegex().Replace(rawName, " ");
            var isPlantProtein = PlantProteinExceptionRegex().IsMatch(withoutMeatExclusions);
            if (
                MeatOrSeafoodRegex().IsMatch(withoutMeatExclusions)
                || (category == FoodCategory.MeatAndSeafood && !isPlantProtein)
            )
            {
                hasMeatOrSeafood = true;
            }

            var withoutDairyExclusions = NonDairyPhraseExclusionRegex().Replace(rawName, " ");
            var isEggOnly = EggOrHoneyRegex().IsMatch(withoutDairyExclusions);
            if (
                DairyIngredientRegex().IsMatch(withoutDairyExclusions)
                || (category == FoodCategory.DairyAndEggs && !isEggOnly && !isPlantProtein)
            )
            {
                hasDairy = true;
            }

            var withoutEggExclusions = VeganEggOrHoneyExclusionRegex().Replace(rawName, " ");
            if (EggOrHoneyRegex().IsMatch(withoutEggExclusions))
            {
                hasEggOrHoney = true;
            }
        }

        var stepList = steps.ToList();
        var hasThermalCooking = cookTimeMinutes is > 0 || stepList.Count == 0;

        if (!hasThermalCooking)
        {
            foreach (var thermalTag in ThermalCookingMethodTagNames)
            {
                if (hasTag(thermalTag))
                {
                    hasThermalCooking = true;
                    break;
                }
            }
        }

        if (!hasThermalCooking)
        {
            var meta = $"{title}\n{description}";
            if (NoCookDisqualifyingMetaRegex().IsMatch(meta))
            {
                hasThermalCooking = true;
            }
        }

        if (!hasThermalCooking)
        {
            foreach (var (stepText, tempsF) in stepList)
            {
                if (tempsF.Any(t => t is >= 120m))
                {
                    hasThermalCooking = true;
                    break;
                }

                var cleanedStep = NonThermalStepPhraseExclusionRegex()
                    .Replace(stepText ?? string.Empty, " ");
                if (
                    ThermalStepVerbRegex().IsMatch(cleanedStep)
                    || ThermalApplianceNounRegex().IsMatch(cleanedStep)
                )
                {
                    hasThermalCooking = true;
                    break;
                }
            }
        }

        return new DietaryAndCookingProfile(
            hasMeatOrSeafood,
            hasDairy,
            hasEggOrHoney,
            !hasThermalCooking
        );
    }

    private static int CountDistinctVesselFamilies(string stepText)
    {
        if (string.IsNullOrWhiteSpace(stepText))
        {
            return 0;
        }

        var count = 0;
        if (VesselSkilletRegex().IsMatch(stepText))
            count++;
        if (VesselSaucepanRegex().IsMatch(stepText))
            count++;
        if (VesselPotRegex().IsMatch(stepText))
            count++;
        if (VesselBakingSheetRegex().IsMatch(stepText))
            count++;
        if (VesselBakingDishRegex().IsMatch(stepText))
            count++;
        if (WokCueRegex().IsMatch(stepText))
            count++;
        if (VesselFoodProcessorRegex().IsMatch(stepText))
            count++;
        if (VesselBlenderRegex().IsMatch(stepText))
            count++;
        if (VesselStandMixerRegex().IsMatch(stepText))
            count++;
        return count;
    }

    [GeneratedRegex(
        @"(?:\(\s*(?:see\s+)?(?:this|facing)\s+page\s*\)|\b(?:see\s+)?(?:this|facing)\s+page\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SeeThisPageRegex();

    [GeneratedRegex(
        @"\b(?:recipes?|follows?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RecipeOrFollowsRegex();

    [GeneratedRegex(
        @"\b(?:lb|lbs|pounds?|oz|ounces?|g|grams?|kg|per\s+pound|peeled|deveined|tails?|trimmed|cored|seeded|stemmed)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RawGroceryIndicatorRegex();

    [GeneratedRegex(@"(?im)^Yield:\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceYieldHeaderRegex();

    [GeneratedRegex(
        @"\s*\(\s*Base\s+Servings\s*:[^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex YieldBaseServingsSuffixRegex();

    [GeneratedRegex(
        @"\s+\bSEASON\s+\d+\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex YieldSeasonSuffixRegex();

    [GeneratedRegex(@"^(SERVES|MAKES)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YieldLeadingVerbRegex();

    [GeneratedRegex(
        @"\bstir[- ]?fry(?:ing|ed)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex StirFryStepCueRegex();

    [GeneratedRegex(@"\bwok\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WokCueRegex();

    [GeneratedRegex(
        @"\b(?:vegan\s+(?:chicken|beef|pork|sausage|bacon|ham|steak|turkey|duck|lamb|fish|salmon|shrimp|crab|lobster|oyster\s+sauce|fish\s+sauce|broth|stock)|vegetarian\s+(?:chicken|beef|pork|sausage|bacon|oyster\s+sauce|fish\s+sauce|broth|stock)|plant[- ]based\s+(?:chicken|beef|pork|sausage|bacon|fish|meat)|oyster\s+mushrooms?|beefsteak\s+tomato(?:es)?|crabapples?|butternut|buttercup|eggplant|nutmeg)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex VeganOrProduceMeatExclusionRegex();

    [GeneratedRegex(
        @"\b(?:tofu|tempeh|seitan|textured\s+vegetable\s+protein|tvp)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PlantProteinExceptionRegex();

    [GeneratedRegex(
        @"\b(?:beef|steak|brisket|chuck|sirloin|ribeye|flank|skirt\s+steak|short\s+ribs?|ground\s+beef|veal|pork|bacon|ham|pancetta|prosciutto|guanciale|sausage|chorizo|pepperoni|salami|mortadella|lamb|mutton|goat|venison|chicken|turkey|duck|goose|cornish\s+hen|quail|poultry|meatball|lard|tallow|suet|schmaltz|bone\s+broth|chicken\s+(?:broth|stock)|beef\s+(?:broth|stock)|pork\s+(?:broth|stock)|fish\s+stock|seafood\s+stock|clam\s+juice|dashi|bonito|gelatin|anchov(?:y|ies)|fish|salmon|tuna|cod|halibut|tilapia|trout|snapper|sea\s+bass|mahi|sardine|mackerel|herring|catfish|haddock|pollock|flounder|sole|swordfish|shrimp|prawns?|crab|lobster|crawfish|crayfish|scallops?|clams?|mussels?|oysters?|squid|calamari|octopus|fish\s+sauce|oyster\s+sauce|shrimp\s+paste)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex MeatOrSeafoodRegex();

    [GeneratedRegex(
        @"\b(?:coconut|almond|oat|soy|cashew|rice|hemp|flax|macadamia|pistachio|hazelnut)\s+(?:milk|cream|yogurt)\b|\b(?:peanut|almond|cashew|sunflower(?:\s+seed)?|tahini|sesame|cocoa|apple|pumpkin|shea|vegan|plant[- ]based|non[- ]dairy|dairy[- ]free)\s+butter\b|\b(?:vegan|plant[- ]based|non[- ]dairy|dairy[- ]free)\s+(?:cheese|cream|yogurt|cheddar|mozzarella|parmesan|feta|sour\s+cream|cream\s+cheese|ghee|ice\s+cream)\b|\b(?:cream\s+of\s+tartar|butternut|buttercup|butter\s+beans?|butter\s+lettuce|butterhead)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex NonDairyPhraseExclusionRegex();

    [GeneratedRegex(
        @"\b(?:butter|buttermilk|ghee|milk|whole\s+milk|skim\s+milk|condensed\s+milk|evaporated\s+milk|half[- ]and[- ]half|heavy\s+cream|whipping\s+cream|light\s+cream|sour\s+cream|cr[eè]me\s+fra[iî]che|mascarpone|ricotta|cream\s+cheese|cottage\s+cheese|yogurt|kefir|whey|casein|cheese|cheesecake|parmesan|parmigiano|pecorino|romano|cheddar|mozzarella|feta|goat\s+cheese|gruy[eè]re|brie|camembert|gouda|provolone|swiss\s+cheese|monterey\s+jack|pepper\s+jack|cotija|queso|paneer|halloumi|burrata|asiago|fontina|manchego|gorgonzola|blue\s+cheese|roquefort|stilton|ice\s+cream|gelato)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DairyIngredientRegex();

    [GeneratedRegex(
        @"\b(?:eggplant|vegan\s+(?:egg|mayo|mayonnaise|honey)|flax\s+egg|chia\s+egg)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex VeganEggOrHoneyExclusionRegex();

    [GeneratedRegex(
        @"\b(?:eggs?|egg\s+yolks?|egg\s+whites?|mayonnaise|mayo|aioli|honey|beeswax)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex EggOrHoneyRegex();

    [GeneratedRegex(
        @"\b(?:yeast|knead(?:ing)?|proof(?:ing)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex NoCookDisqualifyingMetaRegex();

    [GeneratedRegex(
        @"\b(?:no[- ]cook|no[- ]bake|uncooked|without\s+cooking|already\s+cooked|previously\s+cooked|store[- ]bought|precooked|toasted|roasted|baked|brown\s+sugar|brown\s+rice|golden\s+brown|light\s+brown|dark\s+brown)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex NonThermalStepPhraseExclusionRegex();

    [GeneratedRegex(
        @"\b(?:bake|baking|roast|roasting|broil|broiling|simmer|simmering|boil|boiling|saut[eé]|saut[eé]ing|fry|frying|deep[- ]fry|pan[- ]fry|stir[- ]fry|sear|searing|brown|browning|grill|grilling|steam|steaming|toast|toasting|microwave|preheat|poach|braise|blanch|caramelize|char|barbecue|cook|cooking)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ThermalStepVerbRegex();

    [GeneratedRegex(
        @"\b(?:oven|broiler|skillet|frying\s+pan|saucepan|dutch\s+oven|stockpot|stovetop|stove|burner|air\s+fryer|slow\s+cooker|pressure\s+cooker|instant\s+pot|waffle\s+maker|toaster\s+oven)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ThermalApplianceNounRegex();

    [GeneratedRegex(
        @"\b(?:skillet|frying\s+pan|saut[eé]\s+pan|grill\s+pan)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex VesselSkilletRegex();

    [GeneratedRegex(@"\bsaucepan\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VesselSaucepanRegex();

    [GeneratedRegex(
        @"(?<!one[- ]|instant\s+|crock[- ]?)\b(?:stockpot|dutch\s+oven|soup\s+pot|(?:large|medium|small|heavy|covered)\s+pot)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex VesselPotRegex();

    [GeneratedRegex(
        @"\b(?:baking\s+sheet|sheet\s+pan|cookie\s+sheet|jelly[- ]roll\s+pan)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex VesselBakingSheetRegex();

    [GeneratedRegex(
        @"\b(?:baking\s+dish|casserole(?:\s+dish)?|roasting\s+pan|tart\s+pan|cake\s+pan|loaf\s+pan|pie\s+(?:plate|dish|pan)|muffin\s+tin)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex VesselBakingDishRegex();

    [GeneratedRegex(
        @"\bfood\s+processor\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex VesselFoodProcessorRegex();

    [GeneratedRegex(
        @"\b(?:immersion\s+blender|blender)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex VesselBlenderRegex();

    [GeneratedRegex(@"\bstand\s+mixer\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VesselStandMixerRegex();
}
