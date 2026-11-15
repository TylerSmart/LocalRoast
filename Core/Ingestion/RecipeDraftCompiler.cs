using System.Globalization;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Core.Ingestion;

/// <summary>
/// Stage 4 deterministic compiler: transforms an <see cref="ExtractedRecipeDto"/> and resolved
/// Stage 3 entity matches into an <see cref="IngestionExtractedDraftDto"/> with compiled
/// <c>||{...}||</c> inline step tokens, step ingredient amounts, timers, and audit metadata.
/// </summary>
public static partial class RecipeDraftCompiler
{
    [GeneratedRegex(@"\{\{([^{}\r\n]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(
        @"\b(?:bake|roast|broil|simmer|boil|cook|saut[eé]|fry|brown|sear|grill|steam|toast|microwave|reduce|char|warm|melt|crisp)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ActiveCookTimerLabelRegex();

    [GeneratedRegex(
        @"\b(?:chill|refrigerat|freeze|overnight|rise|proof|soak|cool|marinate|rest|stand|sit|set\s+aside|thaw)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PassiveRestTimerLabelRegex();

    [GeneratedRegex(
        @"^(?:custom|inch(?:es)?|in|cm|mm|centimeters?|millimeters?|degrees?|deg|fahrenheit|celsius|f|c|°f|°c)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex DisallowedCustomUnitRegex();

    [GeneratedRegex(
        @"\b(?:frozen|defrost(?:ed)?|thaw(?:ed)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex FrozenStorageCueRegex();

    [GeneratedRegex(
        @"\b(?:cans?|canned|jars?|jarred|bottled|tinned|packed\s+in|tomato\s+paste|sun[- ]dried)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex CannedJarredCueRegex();

    [GeneratedRegex(
        @"\b(?:refrigerated|chilled|cold)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex RefrigeratedCueRegex();

    [GeneratedRegex(
        @"\b(?:divided|to\s+taste|as\s+needed|for\s+(?:serving|garnish|garnishing|dusting|greasing|frying|brushing|basting)|plus\s+(?:more|extra|additional))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BackfillExcludedRawRegex();

    [GeneratedRegex(
        @"\b(?:remaining|reserved|extra|more|additional|another|half(?:\s+of)?|remainder|rest\s+of|some|handful(?:\s+of)?|pinch(?:\s+of)?|dash(?:\s+of)?|splash(?:\s+of)?|drizzle(?:\s+of)?|portion\s+of|pieces?\s+of|slices?\s+of|halves?\s+of|quarters?\s+of|few|several|little)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex BackfillPartialQualifierRegex();

    [GeneratedRegex(
        @"\b(?:ground|cracked|coarse(?:ly)?|fine(?:ly)?|freshly\s+ground|to\s+taste|season(?:ing)?|table\s+pepper)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex GroundOrSpicePepperCueRegex();

    [GeneratedRegex(
        @"\b(?:bell|sweet|poblano|jalape[nñ]o|serrano|habanero|anaheim|fresno|thai|bird'?s?\s+eye|cherry|shishito|banana|chili|chile|seeded|cored|deseeded|ribs?|stemmed|diced|sliced|chopped|julienned|halved|quartered|rings?|roasted|charred|red|yellow|orange|green)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex FreshProducePepperCueRegex();

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

    [GeneratedRegex(@",\s*(?=,|$)|^\s*[,;:]\s*|\s*[,;:]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DanglingPunctuationRegex();

    [GeneratedRegex(
        @"[\d\u00BC-\u00BE\u2153\u2154\u215B-\u215E]\s*$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex TrailingNumericQuantityRegex();

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

    private static readonly HashSet<string> EntreeCourseTagNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Main Course",
        "Dinner",
    };

    public static IngestionExtractedDraftDto Compile(
        ExtractedRecipeDto extracted,
        RecipeValidationResult validation,
        EntityMatchResult matches,
        string? sourceUrl = null,
        string? fallbackSourceName = null,
        string? fallbackSourceAuthor = null,
        string? coverImagePath = null,
        string? thumbnailImagePath = null,
        string? sourceBookTitle = null,
        int? sourceBookPage = null,
        bool retainSourceAttachments = true,
        string? extractedModel = null,
        IReadOnlyList<Guid>? suggestedCollectionIds = null,
        string? sourceText = null
    )
    {
        ArgumentNullException.ThrowIfNull(extracted);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(matches);

        extracted = RecipeValidator.NormalizeCompoundIngredients(extracted);

        var (compiledIngredients, refIdToIndex) = CompileIngredients(
            extracted.Ingredients ?? [],
            extracted.Steps ?? [],
            matches
        );
        var compiledSteps = CompileSteps(extracted.Steps ?? [], compiledIngredients, refIdToIndex);
        var (prepTimeMinutes, cookTimeMinutes, totalTimeMinutes) = ResolveCompiledTimes(
            extracted,
            compiledSteps
        );
        var resolvedTotalMinutes =
            totalTimeMinutes ?? ResolveEffectiveTotalMinutes(extracted, totalTimeMinutes);
        var (suggestedTagIds, proposedTagNames) = CompileTags(
            extracted,
            matches,
            compiledIngredients,
            compiledSteps,
            cookTimeMinutes,
            resolvedTotalMinutes
        );

        var qualityWarnings = BuildQualityWarnings(extracted, validation);
        var validationIssues = validation
            .Errors.Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var confidenceScore = CalculateConfidenceScore(
            compiledIngredients.Count,
            compiledSteps.Count,
            validationIssues.Count,
            qualityWarnings.Count
        );

        var title = string.IsNullOrWhiteSpace(extracted.Title)
            ? "Untitled Recipe"
            : Truncate(extracted.Title.Trim(), 200);

        var servings = extracted.Servings is >= 0.25m and <= 999m ? extracted.Servings.Value : 4m;

        return new IngestionExtractedDraftDto(
            Title: title,
            Description: ExpandPlainTextPlaceholders(
                extracted.Description,
                extracted.Steps ?? [],
                2000
            ),
            BaseServings: servings,
            YieldDisplay: ResolveYieldDisplay(extracted.YieldDisplay, sourceText),
            PrepTimeMinutes: prepTimeMinutes,
            CookTimeMinutes: cookTimeMinutes,
            TotalTimeMinutes: totalTimeMinutes,
            Difficulty: ParseDifficulty(extracted.Difficulty),
            SourceUrl: CleanOptionalText(sourceUrl, 2048),
            SourceName: CleanOptionalText(extracted.SourceName ?? fallbackSourceName, 200),
            SourceAuthor: CleanOptionalText(extracted.SourceAuthor ?? fallbackSourceAuthor, 200),
            CoverImagePath: CleanOptionalText(coverImagePath, 500),
            ThumbnailImagePath: CleanOptionalText(thumbnailImagePath, 500),
            SuggestedCollectionIds: suggestedCollectionIds,
            SuggestedTagIds: suggestedTagIds,
            Ingredients: compiledIngredients,
            Steps: compiledSteps,
            ConfidenceScore: confidenceScore,
            SourceBookTitle: CleanOptionalText(sourceBookTitle, 200),
            SourceBookPage: sourceBookPage is >= 1 and <= 10000 ? sourceBookPage : null,
            RetainSourceAttachments: retainSourceAttachments,
            ExtractedModel: CleanOptionalText(extractedModel, 200),
            ProposedTagNames: proposedTagNames,
            MeasurementWarnings: null,
            QualityWarnings: qualityWarnings,
            ValidationIssues: validationIssues
        );
    }

    private static (
        List<IngestionDraftIngredientDto> Ingredients,
        Dictionary<string, int> RefIdToIndex
    ) CompileIngredients(
        IReadOnlyList<ExtractedIngredientDto> sourceIngredients,
        IReadOnlyList<ExtractedStepDto> sourceSteps,
        EntityMatchResult matches
    )
    {
        var list = new List<IngestionDraftIngredientDto>(sourceIngredients.Count);
        var refIdToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in sourceIngredients)
        {
            var refId = src.RefId?.Trim() ?? string.Empty;
            var section = NormalizeSectionNameOrNull(src.SectionName);

            var (unit, customUnit) = RecipeUnitAndSectionHelpers.NormalizeUnit(src.Unit);
            if (
                unit == MeasurementUnit.Custom
                && RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(customUnit)
            )
            {
                unit = null;
                customUnit = null;
            }

            var (qty, maxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                NormalizePrecision(src.Quantity),
                NormalizePrecision(src.MaxQuantity)
            );
            if (qty is <= 0m)
            {
                qty = null;
            }
            if (maxQty is <= 0m || (qty.HasValue && maxQty < qty))
            {
                maxQty = null;
            }
            if (!qty.HasValue && !maxQty.HasValue)
            {
                unit = null;
                customUnit = null;
            }

            RecipeMeasurement? altMeasurement = null;
            var altQty = NormalizePrecision(src.AltQuantity);
            if (altQty is >= 0.0001m and <= 999999.9999m)
            {
                var (altUnit, altCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(src.AltUnit);
                if (
                    (altUnit == MeasurementUnit.Custom || !altUnit.HasValue)
                    && (
                        RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(altCustom)
                        || DisallowedCustomUnitRegex().IsMatch(altCustom?.Trim() ?? string.Empty)
                        || DisallowedCustomUnitRegex().IsMatch(src.AltUnit?.Trim() ?? string.Empty)
                    )
                )
                {
                    altUnit = null;
                    altCustom = null;
                }

                if (altUnit.HasValue)
                {
                    altMeasurement = new RecipeMeasurement(
                        Quantity: altQty,
                        Unit: altUnit,
                        MaxQuantity: null,
                        CustomUnitName: CleanOptionalText(altCustom, 50)
                    );
                }
            }

            if (altMeasurement is not null)
            {
                if (
                    altMeasurement
                        is { Quantity: > 0m, Unit: { } altU and not MeasurementUnit.Custom }
                    && qty is > 0m
                    && unit is { } priU and not MeasurementUnit.Custom
                    && CulinaryMath.TryConvertQuantity(
                        altMeasurement.Quantity.Value,
                        altU,
                        priU,
                        out var convertedAlt
                    )
                    && Math.Abs(convertedAlt - qty.Value) > Math.Max(0.1m, qty.Value * 0.15m)
                )
                {
                    altMeasurement = null;
                }
            }

            if (altMeasurement is not null)
            {
                if (!qty.HasValue || qty.Value <= 0m)
                {
                    qty = altMeasurement.Quantity;
                    maxQty = null;
                    unit = altMeasurement.Unit;
                    customUnit = altMeasurement.CustomUnitName;
                    altMeasurement = null;
                }
                else if (!unit.HasValue)
                {
                    unit = MeasurementUnit.Whole;
                    customUnit = null;
                }
            }

            var rawDisplay = !string.IsNullOrWhiteSpace(src.DisplayName)
                ? src.DisplayName
                : (
                    !string.IsNullOrWhiteSpace(src.CanonicalFoodItem)
                        ? src.CanonicalFoodItem
                        : src.RawText
                );
            var displayName = Truncate(
                RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(rawDisplay),
                200
            );
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "Ingredient";
            }

            var rawProposed = !string.IsNullOrWhiteSpace(src.CanonicalFoodItem)
                ? src.CanonicalFoodItem.Trim()
                : displayName;
            var normalizedProposed = ResolveNormalizedCanonicalFoodName(src);
            var proposedName = Truncate(
                string.IsNullOrWhiteSpace(normalizedProposed) ? rawProposed : normalizedProposed,
                150
            );

            matches.IngredientMatches.TryGetValue(refId, out var match);

            var isRecipeRef = src.IsRecipeReference;
            var referencedTitle = isRecipeRef
                ? Truncate(
                    RecipeUnitAndSectionHelpers.CleanRecipeReferenceTitle(
                        src.ReferencedRecipeTitle ?? displayName
                    ),
                    200
                )
                : null;

            var matchedId = isRecipeRef ? null : match?.MatchedFoodItemId;
            var matchedName = isRecipeRef ? null : match?.MatchedFoodItemName;
            var isNewFood = !isRecipeRef && !matchedId.HasValue;

            FoodCategory? category = null;
            if (!isRecipeRef)
            {
                if (match?.MatchedCategory is { } matchedCategory)
                {
                    category = matchedCategory;
                }
                else
                {
                    var parsedLlmCategory = FoodCategories.Parse(src.Category);
                    var classifiedProposed = FoodCategoryClassifier.Classify(proposedName);
                    var classifiedDisplay = FoodCategoryClassifier.Classify(displayName);
                    var deterministicCategory =
                        classifiedProposed != FoodCategory.Other
                            ? classifiedProposed
                            : classifiedDisplay;

                    if (
                        ShouldAllowLlmStorageCategoryOverride(
                            parsedLlmCategory,
                            deterministicCategory,
                            src.RawText,
                            src.PreparationNotes,
                            displayName
                        )
                    )
                    {
                        category = parsedLlmCategory!.Value;
                    }
                    else if (deterministicCategory != FoodCategory.Other)
                    {
                        category = deterministicCategory;
                    }
                    else
                    {
                        category = parsedLlmCategory ?? FoodCategory.Other;
                    }
                }
            }

            var isPantryStaple =
                !isRecipeRef
                && (
                    match is { IsNewFoodItem: false }
                        ? match.IsPantryStaple
                        : FoodCategoryClassifier.IsLikelyPantryStaple(
                            proposedName,
                            category ?? FoodCategory.Other
                        )
                );

            var index = list.Count;
            if (!string.IsNullOrWhiteSpace(refId))
            {
                refIdToIndex.TryAdd(refId, index);
            }

            list.Add(
                new IngestionDraftIngredientDto(
                    SectionName: section,
                    Quantity: qty,
                    MaxQuantity: maxQty,
                    Unit: unit,
                    CustomUnitName: CleanOptionalText(customUnit, 50),
                    MatchedFoodItemId: matchedId,
                    MatchedFoodItemName: matchedName,
                    IsNewFoodItem: isNewFood,
                    ProposedFoodItemName: isRecipeRef ? null : proposedName,
                    SuggestedIsPantryStaple: isPantryStaple,
                    CreateNewFoodItem: isNewFood,
                    DisplayName: displayName,
                    PreparationNote: ExpandPlainTextPlaceholders(
                        src.PreparationNotes,
                        sourceSteps,
                        200,
                        cleanDanglingPunctuation: true
                    ),
                    IsOptional: src.IsOptional,
                    OriginalRawText: CleanOptionalText(src.RawText, 500),
                    AlternativeMeasurement: altMeasurement,
                    PreferAlternativeMeasurement: false,
                    SuggestedCategory: category,
                    IsRecipeReference: isRecipeRef,
                    ReferencedRecipeTitle: referencedTitle
                )
            );
        }

        return (list, refIdToIndex);
    }

    private static List<IngestionDraftStepDto> CompileSteps(
        IReadOnlyList<ExtractedStepDto> sourceSteps,
        IReadOnlyList<IngestionDraftIngredientDto> compiledIngredients,
        Dictionary<string, int> refIdToIndex
    )
    {
        var orderedSteps = sourceSteps
            .Select((step, idx) => (Step: step, OriginalIndex: idx))
            .OrderBy(x => x.Step.StepNumber > 0 ? x.Step.StepNumber : x.OriginalIndex + 1)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Step)
            .ToList();

        var ingredientOccurrenceCounts = CountIngredientTokenOccurrences(
            orderedSteps,
            refIdToIndex
        );
        var result = new List<IngestionDraftStepDto>(orderedSteps.Count);

        foreach (var step in orderedSteps)
        {
            var ingTokens = BuildTokenMap(step.IngredientTokens, t => t.TokenKey);
            var timerTokens = BuildTokenMap(step.TimerTokens, t => t.TokenKey);
            var tempTokens = BuildTokenMap(step.TemperatureTokens, t => t.TokenKey);

            var stepIngredientIndices = new List<int>();
            var seenIndices = new HashSet<int>();
            var stepAmountsByIngredient = new Dictionary<int, RecipeMeasurement>();
            var templateText = step.TemplateText ?? string.Empty;

            var compiledText = PlaceholderRegex()
                .Replace(
                    templateText,
                    match =>
                    {
                        var key = RecipeValidator.NormalizeTokenKey(match.Groups[1].Value);

                        if (ingTokens.TryGetValue(key, out var ingTok))
                        {
                            var precedingText = templateText[..match.Index];
                            return CompileIngredientToken(
                                ingTok,
                                precedingText,
                                compiledIngredients,
                                refIdToIndex,
                                ingredientOccurrenceCounts,
                                stepIngredientIndices,
                                seenIndices,
                                stepAmountsByIngredient
                            );
                        }

                        if (timerTokens.TryGetValue(key, out var timerTok))
                        {
                            return CompileTimerToken(timerTok);
                        }

                        if (tempTokens.TryGetValue(key, out var tempTok))
                        {
                            return CompileTemperatureToken(tempTok);
                        }

                        return string.Empty;
                    }
                );

            compiledText = StepInstructionParser.CleanStepInstruction(compiledText);
            compiledText = MultiSpaceRegex().Replace(compiledText, " ").Trim();

            var stepTimers = StepInstructionParser
                .ParseSegments(compiledText)
                .Select(s => s.Token)
                .OfType<StepTimerToken>()
                .Select(t => new RecipeStepTimerInput(t.Seconds, t.Label ?? string.Empty))
                .ToList();

            var ingredientAmounts = stepAmountsByIngredient
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new RecipeStepIngredientAmountInput(kvp.Key, kvp.Value))
                .ToList();

            result.Add(
                new IngestionDraftStepDto(
                    SectionName: NormalizeSectionNameOrNull(step.SectionName),
                    Instruction: Truncate(compiledText, 4000),
                    IngredientIndices: stepIngredientIndices.Count > 0
                        ? stepIngredientIndices
                        : null,
                    Timers: stepTimers.Count > 0 ? stepTimers : null,
                    ImagePath: null,
                    IngredientAmounts: ingredientAmounts.Count > 0 ? ingredientAmounts : null
                )
            );
        }

        return result;
    }

    private static Dictionary<int, int> CountIngredientTokenOccurrences(
        IReadOnlyList<ExtractedStepDto> steps,
        Dictionary<string, int> refIdToIndex
    )
    {
        var counts = new Dictionary<int, int>();
        foreach (var step in steps)
        {
            var ingTokens = BuildTokenMap(step.IngredientTokens, t => t.TokenKey);
            var template = step.TemplateText ?? string.Empty;
            foreach (Match m in PlaceholderRegex().Matches(template))
            {
                var key = RecipeValidator.NormalizeTokenKey(m.Groups[1].Value);
                if (!ingTokens.TryGetValue(key, out var ingTok))
                {
                    continue;
                }

                var refId = ingTok.IngredientRefId?.Trim() ?? string.Empty;
                if (refIdToIndex.TryGetValue(refId, out var ingIndex))
                {
                    counts[ingIndex] = counts.TryGetValue(ingIndex, out var existing)
                        ? existing + 1
                        : 1;
                }
            }
        }

        return counts;
    }

    private static string CompileIngredientToken(
        StepIngredientTokenDto tok,
        string precedingTemplateText,
        IReadOnlyList<IngestionDraftIngredientDto> compiledIngredients,
        Dictionary<string, int> refIdToIndex,
        IReadOnlyDictionary<int, int> ingredientOccurrenceCounts,
        List<int> stepIngredientIndices,
        HashSet<int> seenIndices,
        Dictionary<int, RecipeMeasurement> stepAmountsByIngredient
    )
    {
        var refId = tok.IngredientRefId?.Trim() ?? string.Empty;
        if (!refIdToIndex.TryGetValue(refId, out var ingIndex))
        {
            return !string.IsNullOrWhiteSpace(tok.FallbackDisplay)
                ? tok.FallbackDisplay.Trim()
                : string.Empty;
        }

        if (seenIndices.Add(ingIndex))
        {
            stepIngredientIndices.Add(ingIndex);
        }

        var master = compiledIngredients[ingIndex];
        var (parsedUnit, parsedCustom) = RecipeUnitAndSectionHelpers.NormalizeUnit(tok.StepUnit);
        if (
            parsedUnit == MeasurementUnit.Custom
            && RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(parsedCustom)
        )
        {
            parsedUnit = null;
            parsedCustom = null;
        }

        var amount = tok.StepQuantity is >= 0.0001m and <= 999999.9999m
            ? NormalizePrecision(tok.StepQuantity)
            : null;

        if (
            !amount.HasValue
            && !parsedUnit.HasValue
            && string.IsNullOrWhiteSpace(parsedCustom)
            && ShouldBackfillSingleOccurrenceAmount(
                ingIndex,
                master,
                ingredientOccurrenceCounts,
                precedingTemplateText,
                tok.FallbackDisplay
            )
        )
        {
            amount = NormalizePrecision(master.Quantity);
            parsedUnit = master.Unit;
            parsedCustom = master.CustomUnitName;
        }

        MeasurementUnit? tokenUnit = null;
        string? tokenCustom = null;
        if (amount.HasValue)
        {
            tokenUnit = parsedUnit ?? master.Unit;
            tokenCustom = parsedUnit.HasValue ? parsedCustom : master.CustomUnitName;

            var amountUnit = tokenUnit ?? MeasurementUnit.Whole;
            var amountCustom = amountUnit == MeasurementUnit.Custom ? tokenCustom : null;
            if (amountUnit != MeasurementUnit.Custom || !string.IsNullOrWhiteSpace(amountCustom))
            {
                var measurement = new RecipeMeasurement(amount, amountUnit, null, amountCustom);
                if (stepAmountsByIngredient.TryGetValue(ingIndex, out var existing))
                {
                    if (
                        existing.Unit == measurement.Unit
                        && string.Equals(
                            existing.CustomUnitName,
                            measurement.CustomUnitName,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && existing.Quantity.HasValue
                    )
                    {
                        stepAmountsByIngredient[ingIndex] = existing with
                        {
                            Quantity = existing.Quantity.Value + amount.Value,
                        };
                    }
                    else if (
                        CulinaryMath.TryConvertQuantity(
                            amount.Value,
                            measurement.Unit!.Value,
                            existing.Unit!.Value,
                            out var converted
                        ) && existing.Quantity.HasValue
                    )
                    {
                        stepAmountsByIngredient[ingIndex] = existing with
                        {
                            Quantity = existing.Quantity.Value + converted,
                        };
                    }
                }
                else
                {
                    stepAmountsByIngredient[ingIndex] = measurement;
                }
            }
        }

        var displayName =
            !string.IsNullOrWhiteSpace(tok.FallbackDisplay) && !amount.HasValue
                ? StripRedundantLeadingQualifier(tok.FallbackDisplay.Trim(), precedingTemplateText)
                : master.DisplayName;

        var stepToken = new StepIngredientToken(
            Ingredient: displayName,
            IngredientId: null,
            Amount: amount,
            Unit: tokenUnit,
            CustomUnitName: tokenCustom
        );

        return StepInstructionParser.FormatToken(stepToken);
    }

    private static string StripRedundantLeadingQualifier(
        string fallbackDisplay,
        string precedingTemplateText
    )
    {
        if (string.IsNullOrWhiteSpace(precedingTemplateText))
        {
            return fallbackDisplay;
        }

        var trimmedPreceding = precedingTemplateText.TrimEnd();
        foreach (var qualifier in new[] { "remaining", "reserved", "extra", "more", "additional" })
        {
            if (
                fallbackDisplay.StartsWith(qualifier + " ", StringComparison.OrdinalIgnoreCase)
                && fallbackDisplay.Length > qualifier.Length + 1
                && trimmedPreceding.EndsWith(qualifier, StringComparison.OrdinalIgnoreCase)
                && (
                    trimmedPreceding.Length == qualifier.Length
                    || !char.IsLetterOrDigit(trimmedPreceding[^(qualifier.Length + 1)])
                )
            )
            {
                return fallbackDisplay[(qualifier.Length + 1)..].TrimStart();
            }
        }

        return fallbackDisplay;
    }

    private static string CompileTimerToken(StepTimerTokenDto tok)
    {
        var seconds = Math.Clamp(tok.DurationSeconds, 1, 864000);
        var cleanLabel =
            !string.IsNullOrWhiteSpace(tok.Label)
            && !StepInstructionParser.IsDurationLabel(tok.Label)
                ? Truncate(tok.Label.Trim(), 150)
                : null;

        return StepInstructionParser.FormatToken(new StepTimerToken(seconds, cleanLabel));
    }

    private static string CompileTemperatureToken(StepTemperatureTokenDto tok)
    {
        var rounded = (int)Math.Round(tok.Value ?? 0m, MidpointRounding.AwayFromZero);
        if (rounded <= 0)
        {
            return string.Empty;
        }

        var isCelsius = string.Equals(
            tok.BaseUnit?.Trim(),
            "C",
            StringComparison.OrdinalIgnoreCase
        );

        var tempToken = isCelsius
            ? new StepTemperatureToken(Fahrenheit: null, Celsius: rounded)
            : new StepTemperatureToken(Fahrenheit: rounded, Celsius: null);

        return StepInstructionParser.FormatToken(tempToken);
    }

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

    private static (List<Guid> TagIds, List<string> ProposedTagNames) CompileTags(
        ExtractedRecipeDto extracted,
        EntityMatchResult matches,
        IReadOnlyList<IngestionDraftIngredientDto> compiledIngredients,
        IReadOnlyList<IngestionDraftStepDto> compiledSteps,
        int? cookTimeMinutes,
        int? resolvedTotalMinutes
    )
    {
        var rawProposedTags = extracted.ProposedTags ?? [];
        var matchedEntries = new List<(Guid Id, string Name)>();
        var seenTagIds = new HashSet<Guid>();
        var proposedNames = new List<string>();
        var seenProposedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exceedsQuickThreshold = resolvedTotalMinutes is > 30;

        foreach (var rawTag in rawProposedTags)
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

            if (exceedsQuickThreshold && QuickTimingTagNames.Contains(tagKey))
            {
                continue;
            }

            if (
                matches.TagMatches.TryGetValue(tagKey, out var match)
                && match.MatchedTagId is { } id
            )
            {
                var resolvedName = !string.IsNullOrWhiteSpace(match.MatchedTagName)
                    ? match.MatchedTagName
                    : tagKey;
                if (exceedsQuickThreshold && QuickTimingTagNames.Contains(resolvedName))
                {
                    continue;
                }

                if (seenTagIds.Add(id))
                {
                    matchedEntries.Add((id, resolvedName));
                }
                continue;
            }

            var formatted = RecipeUnitAndSectionHelpers.ToTagDisplayCase(tagKey);
            if (
                exceedsQuickThreshold
                && !string.IsNullOrWhiteSpace(formatted)
                && QuickTimingTagNames.Contains(formatted)
            )
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(formatted) && seenProposedNames.Add(formatted))
            {
                proposedNames.Add(formatted);
            }
        }

        bool HasTag(string tagName) =>
            matchedEntries.Any(e =>
                string.Equals(e.Name, tagName, StringComparison.OrdinalIgnoreCase)
            )
            || proposedNames.Any(n => string.Equals(n, tagName, StringComparison.OrdinalIgnoreCase))
            || rawProposedTags.Any(t =>
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

        void TryAddMatchedTag(string tagName)
        {
            if (
                matches.TagMatches.TryGetValue(tagName, out var tagMatch)
                && tagMatch.MatchedTagId is { } tagId
                && seenTagIds.Add(tagId)
            )
            {
                matchedEntries.Add((tagId, tagMatch.MatchedTagName ?? tagName));
            }
        }

        var extractedStepTemps = (extracted.Steps ?? [])
            .Select((s, idx) => (Step: s, OriginalIndex: idx))
            .OrderBy(x => x.Step.StepNumber > 0 ? x.Step.StepNumber : x.OriginalIndex + 1)
            .ThenBy(x => x.OriginalIndex)
            .Select(x =>
                (IReadOnlyList<decimal?>)
                    (x.Step.TemperatureTokens ?? [])
                        .Select(t => ToFahrenheit(t.Value, t.BaseUnit))
                        .ToList()
            )
            .ToList();

        var profile = AnalyzeDietaryAndCookingProfile(
            extracted.Title,
            extracted.Description,
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
            compiledSteps.Select(
                (s, idx) =>
                    (
                        StepText: s.Instruction,
                        TempFahrenheit: idx < extractedStepTemps.Count
                            ? extractedStepTemps[idx]
                            : (IReadOnlyList<decimal?>)[]
                    )
            ),
            cookTimeMinutes,
            HasTag
        );

        // Dietary guardrails: never contradict meat/dairy/egg/honey ingredients.
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

        // Valid Vegan recipes implicitly satisfy Vegetarian and Dairy-Free.
        var hasValidVegan =
            !profile.HasMeatOrSeafood
            && !profile.HasDairy
            && !profile.HasEggOrHoney
            && (
                matchedEntries.Any(e =>
                    string.Equals(e.Name, "Vegan", StringComparison.OrdinalIgnoreCase)
                )
                || proposedNames.Any(n =>
                    string.Equals(n, "Vegan", StringComparison.OrdinalIgnoreCase)
                )
            );
        if (hasValidVegan)
        {
            TryAddMatchedTag("Vegetarian");
            TryAddMatchedTag("Dairy-Free");
        }

        // Method guardrails: Deep-Fry vs Pan-Sear, Stir-Fry hallucination, One-Pan/One-Pot multi-vessel.
        if (HasTag("Deep-Fry"))
        {
            RemoveTag("Pan-Sear");
        }

        if (HasTag("Stir-Fry"))
        {
            var combinedContext = string.Join(
                "\n",
                new[] { extracted.Title, extracted.Description }
                    .Concat(compiledSteps.Select(s => s.Instruction))
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
            var combinedSteps = string.Join("\n", compiledSteps.Select(s => s.Instruction));
            if (CountDistinctVesselFamilies(combinedSteps) >= 2)
            {
                RemoveTag("One-Pan");
                RemoveTag("One-Pot");
            }
        }

        // No-Cook guardrail: strip if thermal cooking occurs; attach resolved No-Cook tag if truly no-cook.
        if (!profile.IsNoCookCandidate)
        {
            RemoveTag("No-Cook");
        }
        else
        {
            TryAddMatchedTag("No-Cook");
        }

        var hasNonEntreeCourse =
            matchedEntries.Any(e => NonEntreeCourseTagNames.Contains(e.Name))
            || proposedNames.Any(n => NonEntreeCourseTagNames.Contains(n));
        if (hasNonEntreeCourse)
        {
            matchedEntries.RemoveAll(e => EntreeCourseTagNames.Contains(e.Name));
            proposedNames.RemoveAll(n => EntreeCourseTagNames.Contains(n));
        }

        // Protein / base grounding guardrail: strip concrete meat/seafood/protein tags when
        // neither title/description nor any ingredient mentions that protein.
        var rawProteinCorpus = string.Join(
            " ",
            new[] { extracted.Title, extracted.Description }
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

    private static string? ResolveYieldDisplay(string? extractedYieldDisplay, string? sourceText)
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

    internal static string ResolveNormalizedCanonicalFoodName(ExtractedIngredientDto src)
    {
        var rawDisplay = !string.IsNullOrWhiteSpace(src.DisplayName)
            ? src.DisplayName
            : (
                !string.IsNullOrWhiteSpace(src.CanonicalFoodItem)
                    ? src.CanonicalFoodItem
                    : src.RawText
            );
        var displayName = RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(rawDisplay);
        var rawProposed = !string.IsNullOrWhiteSpace(src.CanonicalFoodItem)
            ? src.CanonicalFoodItem.Trim()
            : displayName;
        var normalized = FoodNameSanitizer.NormalizeCanonicalFoodName(rawProposed);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return rawProposed;
        }

        if (
            string.Equals(normalized, "pepper", StringComparison.OrdinalIgnoreCase)
            && IsBareSpicePepper(src)
        )
        {
            return "black pepper";
        }

        return normalized;
    }

    internal static bool IsBareSpicePepper(ExtractedIngredientDto src)
    {
        var combined = string.Join(
            " ",
            new[] { src.RawText, src.DisplayName, src.PreparationNotes }.Where(s =>
                !string.IsNullOrWhiteSpace(s)
            )
        );

        if (GroundOrSpicePepperCueRegex().IsMatch(combined))
        {
            return true;
        }

        if (FreshProducePepperCueRegex().IsMatch(combined))
        {
            return false;
        }

        var (unit, _) = RecipeUnitAndSectionHelpers.NormalizeUnit(src.Unit);
        return unit
                is MeasurementUnit.Tsp
                    or MeasurementUnit.Tbsp
                    or MeasurementUnit.Pinch
                    or MeasurementUnit.Dash
            || (unit is null && src.Quantity is null or <= 0m);
    }

    internal readonly record struct DietaryAndCookingProfile(
        bool HasMeatOrSeafood,
        bool HasDairy,
        bool HasEggOrHoney,
        bool IsNoCookCandidate
    );

    internal static bool IsNoCookRecipe(ExtractedRecipeDto extracted)
    {
        var profile = AnalyzeDietaryAndCookingProfile(extracted);
        return profile.IsNoCookCandidate;
    }

    internal static DietaryAndCookingProfile AnalyzeDietaryAndCookingProfile(
        ExtractedRecipeDto extracted
    )
    {
        var rawTags = new HashSet<string>(
            (extracted.ProposedTags ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim()),
            StringComparer.OrdinalIgnoreCase
        );

        return AnalyzeDietaryAndCookingProfile(
            extracted.Title,
            extracted.Description,
            (extracted.Ingredients ?? []).Select(i =>
                (
                    Name: string.Join(
                        " ",
                        new[] { i.CanonicalFoodItem, i.DisplayName, i.RawText }.Where(s =>
                            !string.IsNullOrWhiteSpace(s)
                        )
                    ),
                    Category: FoodCategories.Parse(i.Category)
                )
            ),
            (extracted.Steps ?? []).Select(s =>
                (
                    StepText: s.TemplateText ?? string.Empty,
                    TempFahrenheit: (IReadOnlyList<decimal?>)
                        (s.TemperatureTokens ?? [])
                            .Select(t => ToFahrenheit(t.Value, t.BaseUnit))
                            .ToList()
                )
            ),
            ClampMinutes(extracted.CookTimeMinutes),
            tagName => rawTags.Contains(tagName)
        );
    }

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

    private static decimal? ToFahrenheit(decimal? value, string? unit)
    {
        if (value is not > 0m)
        {
            return null;
        }

        return string.Equals(unit?.Trim(), "C", StringComparison.OrdinalIgnoreCase)
            ? (value.Value * 9m / 5m) + 32m
            : value.Value;
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

    private static string? ExpandPlainTextPlaceholders(
        string? rawText,
        IReadOnlyList<ExtractedStepDto> steps,
        int maxLength,
        bool cleanDanglingPunctuation = false
    )
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var trimmed = rawText.Trim();
        if (!PlaceholderRegex().IsMatch(trimmed))
        {
            return Truncate(trimmed, maxLength);
        }

        var timersByKey = new Dictionary<string, StepTimerTokenDto>(
            StringComparer.OrdinalIgnoreCase
        );
        var tempsByKey = new Dictionary<string, StepTemperatureTokenDto>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var step in steps)
        {
            foreach (var timer in step.TimerTokens ?? [])
            {
                var k = RecipeValidator.NormalizeTokenKey(timer.TokenKey);
                if (!string.IsNullOrWhiteSpace(k))
                {
                    timersByKey.TryAdd(k, timer);
                }
            }

            foreach (var temp in step.TemperatureTokens ?? [])
            {
                var k = RecipeValidator.NormalizeTokenKey(temp.TokenKey);
                if (!string.IsNullOrWhiteSpace(k))
                {
                    tempsByKey.TryAdd(k, temp);
                }
            }
        }

        var resolved = PlaceholderRegex()
            .Replace(
                trimmed,
                match =>
                {
                    var key = RecipeValidator.NormalizeTokenKey(match.Groups[1].Value);
                    if (
                        timersByKey.TryGetValue(key, out var timerTok)
                        && timerTok.DurationSeconds > 0
                    )
                    {
                        return FormatPlainDuration(timerTok.DurationSeconds);
                    }

                    if (tempsByKey.TryGetValue(key, out var tempTok) && tempTok.Value is > 0m)
                    {
                        var rounded = (int)
                            Math.Round(tempTok.Value.Value, MidpointRounding.AwayFromZero);
                        var unit = string.Equals(
                            tempTok.BaseUnit?.Trim(),
                            "C",
                            StringComparison.OrdinalIgnoreCase
                        )
                            ? "°C"
                            : "°F";
                        return $"{rounded}{unit}";
                    }

                    return string.Empty;
                }
            );

        resolved = MultiSpaceRegex().Replace(resolved, " ").Trim();
        if (cleanDanglingPunctuation)
        {
            resolved = DanglingPunctuationRegex().Replace(resolved, " ").Trim();
            resolved = MultiSpaceRegex().Replace(resolved, " ").Trim();
        }
        return string.IsNullOrWhiteSpace(resolved) ? null : Truncate(resolved, maxLength);
    }

    private static string FormatPlainDuration(int durationSeconds)
    {
        if (durationSeconds % 3600 == 0)
        {
            var hours = durationSeconds / 3600;
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        if (durationSeconds % 60 == 0)
        {
            var minutes = durationSeconds / 60;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        return durationSeconds == 1 ? "1 second" : $"{durationSeconds} seconds";
    }

    private static (int? PrepMinutes, int? CookMinutes, int? TotalMinutes) ResolveCompiledTimes(
        ExtractedRecipeDto extracted,
        IReadOnlyList<IngestionDraftStepDto> compiledSteps
    )
    {
        var prep = ClampMinutes(extracted.PrepTimeMinutes);
        var cook = ClampMinutes(extracted.CookTimeMinutes);
        var total = ClampMinutes(extracted.TotalTimeMinutes);

        if (cook is null && compiledSteps.Count > 0)
        {
            var activeSeconds = compiledSteps
                .SelectMany(s => s.Timers ?? [])
                .Where(t =>
                    t.DurationSeconds > 0
                    && t.DurationSeconds <= 14400
                    && !string.IsNullOrWhiteSpace(t.Label)
                    && ActiveCookTimerLabelRegex().IsMatch(t.Label)
                    && !PassiveRestTimerLabelRegex().IsMatch(t.Label)
                )
                .Sum(t => t.DurationSeconds);

            if (activeSeconds >= 60)
            {
                cook = Math.Clamp(
                    (int)Math.Round(activeSeconds / 60m, MidpointRounding.AwayFromZero),
                    1,
                    10080
                );
            }
        }

        if (
            cook is null
            && prep is > 0
            && compiledSteps.Count > 0
            && !HasThermalCookingTemperature(compiledSteps)
        )
        {
            cook = 0;
        }

        if (total is null or 0)
        {
            var combined = (prep ?? 0) + (cook ?? 0);
            total = combined > 0 ? Math.Min(combined, 10080) : null;
        }

        if (cook is 0 && prep is null && total is null)
        {
            cook = null;
        }

        return (prep, cook, total);
    }

    private static bool ShouldAllowLlmStorageCategoryOverride(
        FoodCategory? parsedLlmCategory,
        FoodCategory deterministicCategory,
        string? rawText,
        string? preparationNotes,
        string displayName
    )
    {
        return parsedLlmCategory switch
        {
            FoodCategory.Frozen => deterministicCategory
                is FoodCategory.Other
                    or FoodCategory.Frozen
                || HasStorageCue(FrozenStorageCueRegex(), rawText, preparationNotes, displayName),
            FoodCategory.CannedAndJarred => deterministicCategory
                is FoodCategory.Other
                    or FoodCategory.CannedAndJarred
                || HasStorageCue(CannedJarredCueRegex(), rawText, preparationNotes, displayName),
            FoodCategory.Refrigerated => deterministicCategory
                is not (
                    FoodCategory.SpicesAndSeasonings
                    or FoodCategory.Baking
                    or FoodCategory.PantryDryGoods
                    or FoodCategory.CondimentsAndSauces
                )
                || HasStorageCue(RefrigeratedCueRegex(), rawText, preparationNotes, displayName),
            _ => false,
        };
    }

    private static bool HasStorageCue(
        Regex cueRegex,
        string? rawText,
        string? preparationNotes,
        string displayName
    ) =>
        (!string.IsNullOrWhiteSpace(rawText) && cueRegex.IsMatch(rawText))
        || (!string.IsNullOrWhiteSpace(preparationNotes) && cueRegex.IsMatch(preparationNotes))
        || (!string.IsNullOrWhiteSpace(displayName) && cueRegex.IsMatch(displayName));

    private static bool ShouldBackfillSingleOccurrenceAmount(
        int ingIndex,
        IngestionDraftIngredientDto master,
        IReadOnlyDictionary<int, int> ingredientOccurrenceCounts,
        string precedingTemplateText,
        string? fallbackDisplay
    )
    {
        if (
            !ingredientOccurrenceCounts.TryGetValue(ingIndex, out var occurrences)
            || occurrences != 1
        )
        {
            return false;
        }

        if (
            master.IsOptional
            || master.IsRecipeReference
            || master.Quantity is not > 0m
            || !master.Unit.HasValue
            || (master.MaxQuantity.HasValue && master.MaxQuantity > master.Quantity)
        )
        {
            return false;
        }

        if (
            master.Unit == MeasurementUnit.Custom
            && DisallowedCustomUnitRegex().IsMatch(master.CustomUnitName?.Trim() ?? string.Empty)
        )
        {
            return false;
        }

        if (
            !string.IsNullOrWhiteSpace(master.OriginalRawText)
            && BackfillExcludedRawRegex().IsMatch(master.OriginalRawText)
        )
        {
            return false;
        }

        if (
            !string.IsNullOrWhiteSpace(fallbackDisplay)
            && BackfillPartialQualifierRegex().IsMatch(fallbackDisplay)
        )
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(precedingTemplateText))
        {
            var tailStart = Math.Max(0, precedingTemplateText.Length - 40);
            var precedingTail = precedingTemplateText[tailStart..];
            if (
                TrailingNumericQuantityRegex().IsMatch(precedingTail)
                || BackfillPartialQualifierRegex().IsMatch(precedingTail)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasThermalCookingTemperature(
        IReadOnlyList<IngestionDraftStepDto> compiledSteps
    ) =>
        compiledSteps
            .Where(s => !string.IsNullOrWhiteSpace(s.Instruction))
            .SelectMany(s => StepInstructionParser.ParseSegments(s.Instruction))
            .Select(seg => seg.Token)
            .OfType<StepTemperatureToken>()
            .Any(t => t.Fahrenheit >= 150 || t.Celsius >= 66);

    private static int? ResolveEffectiveTotalMinutes(
        ExtractedRecipeDto extracted,
        int? compiledTotalMinutes
    )
    {
        if (compiledTotalMinutes is > 0)
        {
            return compiledTotalMinutes;
        }

        if (extracted.TotalTimeMinutes is > 0)
        {
            return extracted.TotalTimeMinutes;
        }

        var sum = (extracted.PrepTimeMinutes ?? 0) + (extracted.CookTimeMinutes ?? 0);
        return sum > 0 ? sum : null;
    }

    private static decimal? NormalizePrecision(decimal? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        var rounded = Math.Round(value.Value, 4, MidpointRounding.AwayFromZero);
        if (rounded == value.Value)
        {
            return value.Value;
        }

        return decimal.Parse(
            rounded.ToString("0.####", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture
        );
    }

    private static List<string> BuildQualityWarnings(
        ExtractedRecipeDto extracted,
        RecipeValidationResult validation
    )
    {
        var list = new List<string>();
        if (extracted.QualityAudit?.IsPlausibleRecipe == false)
        {
            list.Add("Source content may not be a complete recipe.");
        }

        foreach (var w in extracted.QualityAudit?.Warnings ?? [])
        {
            if (!string.IsNullOrWhiteSpace(w))
            {
                list.Add(w.Trim());
            }
        }

        foreach (var w in validation.Warnings)
        {
            if (!string.IsNullOrWhiteSpace(w))
            {
                list.Add(w.Trim());
            }
        }

        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static decimal CalculateConfidenceScore(
        int ingredientCount,
        int stepCount,
        int validationIssueCount,
        int qualityWarningCount
    )
    {
        if (ingredientCount == 0 || stepCount == 0)
        {
            return 0.20m;
        }

        var score = 0.96m - (validationIssueCount * 0.15m) - (qualityWarningCount * 0.10m);
        return Math.Clamp(score, 0.10m, 0.99m);
    }

    private static Dictionary<string, TToken> BuildTokenMap<TToken>(
        IReadOnlyList<TToken>? tokens,
        Func<TToken, string> keySelector
    )
    {
        var map = new Dictionary<string, TToken>(StringComparer.OrdinalIgnoreCase);
        if (tokens is null)
        {
            return map;
        }

        foreach (var tok in tokens)
        {
            var key = RecipeValidator.NormalizeTokenKey(keySelector(tok));
            if (!string.IsNullOrWhiteSpace(key))
            {
                map.TryAdd(key, tok);
            }
        }

        return map;
    }

    private static string? NormalizeSectionNameOrNull(string? sectionName)
    {
        if (
            string.IsNullOrWhiteSpace(sectionName)
            || RecipeUnitAndSectionHelpers.IsGenericTopLevelSection(sectionName)
        )
        {
            return null;
        }

        return Truncate(sectionName.Trim(), 150);
    }

    private static RecipeDifficulty? ParseDifficulty(string? difficulty)
    {
        if (string.IsNullOrWhiteSpace(difficulty))
        {
            return null;
        }

        return difficulty.Trim().ToLowerInvariant() switch
        {
            "easy" or "simple" or "quick" => RecipeDifficulty.Easy,
            "medium" or "moderate" or "intermediate" => RecipeDifficulty.Medium,
            "project" or "hard" or "advanced" or "complex" => RecipeDifficulty.Project,
            _ => null,
        };
    }

    private static int? ClampMinutes(int? minutes) => minutes is >= 0 and <= 10080 ? minutes : null;

    private static string? CleanOptionalText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}
