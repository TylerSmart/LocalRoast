using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Ingestion;
using App.Core.Recipes;
using IngestionPoc.Models;

namespace IngestionPoc.Services;

public static partial class PipelineScorer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> StandardizedAndCustomSingularUnits = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "drop",
        "smidgen",
        "pinch",
        "dash",
        "teaspoon",
        "tablespoon",
        "fluid ounce",
        "cup",
        "pint",
        "quart",
        "gallon",
        "milliliter",
        "centiliter",
        "deciliter",
        "liter",
        "ounce",
        "pound",
        "milligram",
        "gram",
        "kilogram",
        "inch",
        "foot",
        "yard",
        "millimeter",
        "centimeter",
        "meter",
        "second",
        "minute",
        "hour",
        "day",
        "clove",
        "can",
        "jar",
        "bottle",
        "package",
        "packet",
        "box",
        "bag",
        "bunch",
        "head",
        "stalk",
        "sprig",
        "leaf",
        "slice",
        "piece",
        "strip",
        "fillet",
        "breast",
        "thigh",
        "link",
        "patty",
        "sheet",
        "stick",
        "cube",
        "ear",
        "rib",
        "wedge",
        "recipe",
        "batch",
        "loaf",
        "pouch",
    };

    private static readonly HashSet<string> PluralUnitSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "drops",
        "smidgens",
        "pinches",
        "dashes",
        "teaspoons",
        "tablespoons",
        "fluid ounces",
        "cups",
        "pints",
        "quarts",
        "gallons",
        "milliliters",
        "centiliters",
        "deciliters",
        "liters",
        "ounces",
        "pounds",
        "milligrams",
        "grams",
        "kilograms",
        "inches",
        "feet",
        "yards",
        "cloves",
        "cans",
        "slices",
        "bunches",
        "sticks",
        "sprigs",
        "leaves",
    };

    private static readonly HashSet<string> InvalidMetaTagHeadings = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "protein",
        "course",
        "cuisine",
        "dietary",
        "method",
        "occasion",
        "meal",
        "difficulty",
        "flavor",
        "season",
    };

    [GeneratedRegex(
        @"\|\|\{""type""\s*:\s*""([^""]+)""[\s\S]*?\}\s*\|\||\|\|\{[\s\S]*?\}\|\|",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex ProdInlineTokenRegex();

    [GeneratedRegex(
        @"\b\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*(?:teaspoons?|tsp\.?|tablespoons?|tbsp\.?|cups?|pints?|quarts?|gallons?|fl(?:uid)?\s+oz\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|kilograms?|milliliters?|liters?|minutes?|mins?|seconds?|secs?|hours?|hrs?|degrees?|°\s*[FC])\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex UntokenizedMeasurementRegex();

    [GeneratedRegex(@"[a-zA-Z]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex WordTokenRegex();

    [GeneratedRegex(
        @"(?:^|\n)\s*Instructions\s*:\s*([\s\S]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex CanonicalInstructionsBlockRegex();

    public sealed record ScoreableIngredient(
        string Name,
        decimal? Quantity,
        decimal? MaxQuantity,
        string? Unit,
        string? PrepNote,
        string? MatchType,
        Guid? MatchedFoodId,
        string? MatchedFoodName,
        Guid? MatchedRecipeId,
        string? MatchedRecipeTitle,
        bool HasValidationWarning
    );

    public sealed record ScoreableRecipe(
        string Title,
        string? Description,
        int? PrepMinutes,
        int? CookMinutes,
        int? TotalMinutes,
        IReadOnlyList<string> Tags,
        IReadOnlyList<ScoreableIngredient> Ingredients,
        IReadOnlyList<string> RenderedSteps,
        int UntokenizedQuantityHits,
        int TokenStructuralWarnings
    );

    public static RecipeQualityScorecard ScoreRecipe(
        string normalizedSourceText,
        ScoreableRecipe recipe,
        CatalogSnapshot catalog
    )
    {
        var deductions = new List<string>();
        var ingScore = 30;
        var stepScore = 40;
        var resScore = 20;
        var timingScore = 10;

        // 1. Ingredient Completeness & Hygiene (30 pts)
        if (recipe.Ingredients.Count == 0)
        {
            ingScore = 0;
            deductions.Add("No extracted ingredients (-30)");
        }
        else
        {
            var sourceIngLines = ExtractSourceIngredientLines(normalizedSourceText);
            if (sourceIngLines.Count > 0)
            {
                var splitGrace = sourceIngLines.Count(l =>
                    Regex.IsMatch(
                        l,
                        @"\bsalt\s+and\s+(?:(?:freshly\s+)?ground\s+)?(?:black\s+)?pepper\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    )
                    || Regex.IsMatch(
                        l,
                        @"\bplus\s+(?:\d|a\s+pinch|a\s+dash)",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                    )
                );
                if (recipe.Ingredients.Count > sourceIngLines.Count + splitGrace)
                {
                    var extra = recipe.Ingredients.Count - (sourceIngLines.Count + splitGrace);
                    var pen = Math.Min(12, extra * 4);
                    ingScore -= pen;
                    deductions.Add(
                        $"Over-extracted ingredients ({recipe.Ingredients.Count} vs {sourceIngLines.Count} source lines) (-{pen})"
                    );
                }
                else if (recipe.Ingredients.Count < sourceIngLines.Count)
                {
                    var missing = sourceIngLines.Count - recipe.Ingredients.Count;
                    var pen = Math.Min(15, missing * 5);
                    ingScore -= pen;
                    deductions.Add(
                        $"Dropped source ingredients ({recipe.Ingredients.Count} vs {sourceIngLines.Count} source lines) (-{pen})"
                    );
                }
            }

            var dupPrepHits = 0;
            var bogusQtyHits = 0;
            var pluralUnitHits = 0;
            foreach (var ing in recipe.Ingredients)
            {
                if (
                    !string.IsNullOrWhiteSpace(ing.PrepNote)
                    && HasDuplicatedPrepWords(ing.Name, ing.PrepNote)
                )
                {
                    dupPrepHits++;
                    deductions.Add(
                        $"Duplicate prep in ingredient name '{ing.Name}' [{ing.PrepNote}] (-2)"
                    );
                }

                if (
                    ing.Quantity is < 0m
                    || (
                        ing.Unit is "pinch" or "dash" or "smidgen" or "Pinch" or "Dash"
                        && ing.Quantity is > 0m and < 0.1m
                    )
                )
                {
                    bogusQtyHits++;
                    deductions.Add(
                        $"Corrupt small/negative quantity on '{ing.Name}': {ing.Quantity} {ing.Unit} (-4)"
                    );
                }

                if (!string.IsNullOrWhiteSpace(ing.Unit) && PluralUnitSet.Contains(ing.Unit.Trim()))
                {
                    pluralUnitHits++;
                    deductions.Add($"Plural unit '{ing.Unit}' on '{ing.Name}' (-2)");
                }
            }

            ingScore -= Math.Min(8, dupPrepHits * 2);
            ingScore -= Math.Min(8, bogusQtyHits * 4);
            ingScore -= Math.Min(6, pluralUnitHits * 2);
        }

        // 2. Step Fidelity & Token Scalability (40 pts)
        if (recipe.RenderedSteps.Count == 0)
        {
            stepScore = 0;
            deductions.Add("No extracted steps (-40)");
        }
        else
        {
            var droppedWords = CountDroppedStepWords(
                normalizedSourceText,
                recipe.RenderedSteps,
                recipe.Description
            );
            if (droppedWords.Count >= 3)
            {
                var pen = Math.Min(16, droppedWords.Count * 2);
                stepScore -= pen;
                deductions.Add(
                    $"Dropped step prose words ({droppedWords.Count}: {string.Join(", ", droppedWords.Take(5))}) (-{pen})"
                );
            }

            if (recipe.UntokenizedQuantityHits > 0)
            {
                var pen = Math.Min(15, recipe.UntokenizedQuantityHits * 3);
                stepScore -= pen;
                deductions.Add(
                    $"Untokenized step measurements left as plain text ({recipe.UntokenizedQuantityHits}) (-{pen})"
                );
            }

            if (recipe.TokenStructuralWarnings > 0)
            {
                var pen = Math.Min(12, recipe.TokenStructuralWarnings * 3);
                stepScore -= pen;
                deductions.Add(
                    $"Step token structural/spacing warnings ({recipe.TokenStructuralWarnings}) (-{pen})"
                );
            }
        }

        // 3. Catalog Resolution, Description & Tags (20 pts)
        var resWarnings = recipe.Ingredients.Count(i => i.HasValidationWarning);
        if (resWarnings > 0)
        {
            var pen = Math.Min(10, resWarnings * 4);
            resScore -= pen;
            deductions.Add(
                $"Catalog resolution warnings/hallucinated IDs ({resWarnings}) (-{pen})"
            );
        }

        // Check for missed exact DB recipe or food matches when exact catalog item exists
        var missedExactMatches = 0;
        foreach (var ing in recipe.Ingredients)
        {
            var normName = RecipeUnitAndSectionHelpers.NormalizeLookupKey(ing.Name);
            if (string.IsNullOrWhiteSpace(normName))
            {
                continue;
            }

            var exactFood = catalog.FoodItems.FirstOrDefault(f =>
                string.Equals(f.NormalizedName, normName, StringComparison.OrdinalIgnoreCase)
            );
            if (
                exactFood is not null
                && ing.MatchedFoodId != exactFood.Id
                && ing.MatchedRecipeId is null
            )
            {
                // Penalize matching generic "mustard" when exact "Dijon mustard" is in DB, or marking existing item as new
                if (
                    ing.MatchedFoodId.HasValue
                    && !string.Equals(
                        ing.MatchedFoodName,
                        exactFood.Name,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    missedExactMatches++;
                    deductions.Add(
                        $"Matched '{ing.Name}' to broader '{ing.MatchedFoodName}' instead of exact DB '{exactFood.Name}' (-3)"
                    );
                }
            }

            if (
                ing.MatchedRecipeId.HasValue
                && !string.Equals(ing.Unit, "recipe", StringComparison.OrdinalIgnoreCase)
                && !ing.Name.Contains("recipe", StringComparison.OrdinalIgnoreCase)
                && (
                    exactFood is not null
                    || normName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2
                )
            )
            {
                missedExactMatches += 2;
                deductions.Add(
                    $"False sub-recipe match '{ing.Name}' -> recipe '{ing.MatchedRecipeTitle}' (-6)"
                );
            }

            if (
                string.Equals(
                    ing.MatchType,
                    "unresolved_recipe",
                    StringComparison.OrdinalIgnoreCase
                )
                && !string.Equals(ing.Unit, "recipe", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ing.Unit, "batch", StringComparison.OrdinalIgnoreCase)
                && !ing.Name.Contains("recipe", StringComparison.OrdinalIgnoreCase)
                && !(ing.PrepNote?.Contains("recipe", StringComparison.OrdinalIgnoreCase) ?? false)
            )
            {
                missedExactMatches++;
                deductions.Add(
                    $"Plain ingredient '{ing.Name}' misclassified as unresolved_recipe (-3)"
                );
            }

            if (
                string.Equals(ing.Unit, "recipe", StringComparison.OrdinalIgnoreCase)
                && ing.MatchedRecipeId is null
                && catalog.Recipes.Any(r =>
                    ing.Name.Contains(r.Title, StringComparison.OrdinalIgnoreCase)
                    || r.Title.Contains(ing.Name, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                missedExactMatches++;
                deductions.Add($"Missed existing sub-recipe match for '{ing.Name}' (-3)");
            }
        }
        resScore -= Math.Min(9, missedExactMatches * 3);

        if (
            string.IsNullOrWhiteSpace(recipe.Description)
            && normalizedSourceText.Contains("Description", StringComparison.OrdinalIgnoreCase)
        )
        {
            resScore -= 3;
            deductions.Add("Empty description despite source description (-3)");
        }
        else if (recipe.Description?.Length > 2000)
        {
            resScore -= 4;
            deductions.Add($"Description exceeds 2000 chars ({recipe.Description.Length}) (-4)");
        }

        var badTags = recipe.Tags.Where(t => InvalidMetaTagHeadings.Contains(t.Trim())).ToList();
        var isCondimentOrDip =
            recipe.Title.Contains("Hummus", StringComparison.OrdinalIgnoreCase)
            || recipe.Title.Contains("Guacamole", StringComparison.OrdinalIgnoreCase)
            || recipe.Title.Contains("Dip", StringComparison.OrdinalIgnoreCase)
            || recipe.Title.Contains("Salsa", StringComparison.OrdinalIgnoreCase)
            || recipe.Title.Contains("Mayonnaise", StringComparison.OrdinalIgnoreCase)
            || recipe.Title.Contains("Topping", StringComparison.OrdinalIgnoreCase)
            || recipe.Title.Contains("Dressing", StringComparison.OrdinalIgnoreCase);
        if (isCondimentOrDip)
        {
            badTags.AddRange(
                recipe.Tags.Where(t =>
                    string.Equals(t.Trim(), "Dinner", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.Trim(), "Main Course", StringComparison.OrdinalIgnoreCase)
                )
            );
        }
        if (badTags.Count > 0)
        {
            var pen = Math.Min(6, badTags.Count * 2);
            resScore -= pen;
            deductions.Add($"Invalid/mismatched tags ({string.Join(", ", badTags)}) (-{pen})");
        }

        // 4. Timing Faithfulness (10 pts)
        var hasExplicitPrepInSource = Regex.IsMatch(
            normalizedSourceText,
            @"\b(?:prep\s*time|hands[- ]on|active\s*time|prep\s*:)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );
        if (!hasExplicitPrepInSource && recipe.PrepMinutes is > 0)
        {
            timingScore -= 3;
            deductions.Add($"Fabricated prepTime={recipe.PrepMinutes}m not stated in source (-3)");
        }
        if (
            recipe.TotalMinutes.HasValue
            && recipe.CookMinutes.HasValue
            && recipe.TotalMinutes.Value < recipe.CookMinutes.Value
        )
        {
            timingScore -= 3;
            deductions.Add(
                $"Inconsistent timing total={recipe.TotalMinutes} < cook={recipe.CookMinutes} (-3)"
            );
        }

        ingScore = Math.Max(0, ingScore);
        stepScore = Math.Max(0, stepScore);
        resScore = Math.Max(0, resScore);
        timingScore = Math.Max(0, timingScore);
        var total = ingScore + stepScore + resScore + timingScore;

        return new RecipeQualityScorecard(
            total,
            ingScore,
            stepScore,
            resScore,
            timingScore,
            deductions
        );
    }

    public static RecipeQualityScorecard? TryScoreProductionDraft(
        string? rawNormalizedSource,
        string? extractedDraftJson,
        CatalogSnapshot catalog
    )
    {
        if (
            string.IsNullOrWhiteSpace(rawNormalizedSource)
            || string.IsNullOrWhiteSpace(extractedDraftJson)
        )
        {
            return null;
        }

        using var doc = JsonDocument.Parse(extractedDraftJson);
        var root = doc.RootElement;

        var title = GetStr(root, "Title") ?? "Untitled";
        var desc = GetStr(root, "Description");
        var prep = GetNullableInt(root, "PrepTimeMinutes");
        var cook = GetNullableInt(root, "CookTimeMinutes");
        var total = GetNullableInt(root, "TotalTimeMinutes");

        var ings = new List<ScoreableIngredient>();
        if (
            root.TryGetProperty("Ingredients", out var ingArr)
            && ingArr.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var item in ingArr.EnumerateArray())
            {
                var name = GetStr(item, "DisplayName") ?? string.Empty;
                var qty = GetNullableDecimal(item, "Quantity");
                var maxQty = GetNullableDecimal(item, "MaxQuantity");
                var unit = GetStr(item, "CustomUnitName") ?? GetStr(item, "Unit");
                var prepNote = GetStr(item, "PreparationNote");
                var foodId = GetNullableGuid(item, "MatchedFoodItemId");
                var foodName = GetStr(item, "MatchedFoodItemName");
                var recId = GetNullableGuid(item, "MatchedRecipeId");
                var recTitle = GetStr(item, "MatchedRecipeTitle");
                var isNew = GetBool(item, "IsNewFoodItem");
                var isRecRef = GetBool(item, "IsRecipeReference");
                var badFoodLink =
                    foodId.HasValue && !catalog.FoodItems.Any(f => f.Id == foodId.Value);

                ings.Add(
                    new ScoreableIngredient(
                        name,
                        qty,
                        maxQty,
                        unit,
                        prepNote,
                        recId.HasValue ? "existing_recipe"
                            : isRecRef ? "unresolved_recipe"
                            : foodId.HasValue ? "existing_ingredient"
                            : isNew ? "new_ingredient"
                            : "unknown",
                        foodId,
                        foodName,
                        recId,
                        recTitle,
                        badFoodLink
                    )
                );
            }
        }

        var renderedSteps = new List<string>();
        var untokHits = 0;
        var structWarn = 0;
        if (
            root.TryGetProperty("Steps", out var stepArr)
            && stepArr.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var st in stepArr.EnumerateArray())
            {
                var instr = GetStr(st, "Instruction") ?? string.Empty;
                var plainOutsideTokens = ProdInlineTokenRegex().Replace(instr, " ");
                untokHits += UntokenizedMeasurementRegex().Matches(plainOutsideTokens).Count;

                // Strip token wrappers cleanly into readable text for prose retention scoring
                var readable = Regex.Replace(
                    instr,
                    @"\|\|(\{[\s\S]*?\})\|\|",
                    m => RenderProdTokenForScore(m.Groups[1].Value)
                );
                renderedSteps.Add(readable);
            }
        }

        if (
            root.TryGetProperty("ValidationIssues", out var vi)
            && vi.ValueKind == JsonValueKind.Array
        )
        {
            structWarn += vi.GetArrayLength();
        }

        return ScoreRecipe(
            rawNormalizedSource,
            new ScoreableRecipe(
                title,
                desc,
                prep,
                cook,
                total,
                [],
                ings,
                renderedSteps,
                untokHits,
                structWarn
            ),
            catalog
        );
    }

    private static string RenderProdTokenForScore(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.TryGetProperty("ingredient", out var ing))
            {
                return ing.GetString() ?? string.Empty;
            }
            if (r.TryGetProperty("seconds", out var sec))
            {
                return $"{sec.GetInt32()} seconds";
            }
            if (r.TryGetProperty("label", out var lbl))
            {
                return lbl.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // ignore
        }
        return string.Empty;
    }

    private static List<string> ExtractSourceIngredientLines(string normalizedSource)
    {
        var lines = normalizedSource.Replace("\r\n", "\n").Split('\n');
        var inIng = false;
        var list = new List<string>();
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.Equals("Ingredients:", StringComparison.OrdinalIgnoreCase))
            {
                inIng = true;
                continue;
            }
            if (t.Equals("Instructions:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (inIng && t.StartsWith("- ", StringComparison.Ordinal))
            {
                list.Add(t[2..].Trim());
            }
        }
        return list;
    }

    private static List<string> CountDroppedStepWords(
        string normalizedSource,
        IReadOnlyList<string> renderedSteps,
        string? description
    )
    {
        var match = CanonicalInstructionsBlockRegex().Match(normalizedSource.Replace("\r\n", "\n"));
        if (!match.Success)
        {
            return [];
        }

        var sourceInstr = Regex.Replace(match.Groups[1].Value, @"\[[^\]]+\]", " ");
        var srcWords = WordTokenRegex()
            .Matches(sourceInstr)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !IsUnitOrStopWord(w))
            .ToHashSet();

        var combinedRendered =
            string.Join(" ", renderedSteps) + " " + (description ?? string.Empty);
        var rendWords = WordTokenRegex()
            .Matches(combinedRendered)
            .Select(m => m.Value.ToLowerInvariant())
            .ToHashSet();

        return srcWords.Where(w => !rendWords.Contains(w)).Take(12).ToList();
    }

    private static bool HasDuplicatedPrepWords(string name, string prepNote)
    {
        var nameWords = WordTokenRegex()
            .Matches(name)
            .Select(m => m.Value.ToLowerInvariant())
            .ToHashSet();
        var prepWords = WordTokenRegex()
            .Matches(prepNote)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w =>
                w
                    is "chopped"
                        or "minced"
                        or "diced"
                        or "sliced"
                        or "grated"
                        or "melted"
                        or "crushed"
                        or "shredded"
                        or "toasted"
                        or "peeled"
                        or "halved"
                        or "pitted"
                        or "seeded"
                        or "stemmed"
                        or "trimmed"
                        or "softened"
            )
            .ToList();
        return prepWords.Any(w => nameWords.Contains(w));
    }

    private static bool IsUnitOrStopWord(string w) =>
        w
            is "teaspoon"
                or "teaspoons"
                or "tablespoon"
                or "tablespoons"
                or "ounce"
                or "ounces"
                or "pound"
                or "pounds"
                or "inch"
                or "inches"
                or "minute"
                or "minutes"
                or "second"
                or "seconds"
                or "hour"
                or "hours"
                or "degree"
                or "degrees"
                or "fahrenheit"
                or "celsius"
                or "about";

    private static string? GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static int? GetNullableInt(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p)
        && p.ValueKind == JsonValueKind.Number
        && p.TryGetInt32(out var v)
            ? v
            : null;

    private static decimal? GetNullableDecimal(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p)
        && p.ValueKind == JsonValueKind.Number
        && p.TryGetDecimal(out var v)
            ? v
            : null;

    private static Guid? GetNullableGuid(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p)
        && p.ValueKind == JsonValueKind.String
        && Guid.TryParse(p.GetString(), out var g)
            ? g
            : null;

    private static bool GetBool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.True;
}
