using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Recipes;

namespace App.API.IntegrationTests;

internal sealed record LlmExtractionContext(string SourceContent);

internal sealed record RecipeQualityAuditDto(
    bool IsPlausibleRecipe,
    IReadOnlyList<string> Warnings
);

internal sealed record ExtractedStepIngredientToken(
    string TokenKey,
    string IngredientRefId,
    decimal? StepQuantity = null,
    string? StepUnit = null,
    string? FallbackDisplay = null
);

internal sealed record ExtractedStepTimerToken(string TokenKey, int DurationSeconds, string Label);

internal sealed record ExtractedStepTemperatureToken(
    string TokenKey,
    decimal? Fahrenheit = null,
    decimal? Celsius = null
);

internal sealed record ExtractedStepDto(
    int StepNumber,
    string TemplateText,
    IReadOnlyList<ExtractedStepIngredientToken>? IngredientTokens = null,
    IReadOnlyList<ExtractedStepTimerToken>? TimerTokens = null,
    IReadOnlyList<ExtractedStepTemperatureToken>? TemperatureTokens = null,
    string? SectionName = null
);

internal sealed record ExtractedIngredientDto(
    string RefId,
    string? CanonicalFoodItem = null,
    string DisplayName = "",
    string? Category = null,
    decimal? Quantity = null,
    decimal? MaxQuantity = null,
    string? Unit = null,
    decimal? AltQuantity = null,
    string? AltUnit = null,
    string? PreparationNotes = null,
    bool IsOptional = false,
    bool IsRecipeReference = false,
    string? ReferencedRecipeTitle = null,
    string? RawText = null,
    string? SectionName = null,
    IReadOnlyList<Stage1ReplacementDto>? Replacements = null
);

internal sealed record ExtractedRecipeDto(
    string Title,
    string? Description = null,
    decimal? Servings = null,
    string? YieldDisplay = null,
    int? PrepTimeMinutes = null,
    int? CookTimeMinutes = null,
    int? TotalTimeMinutes = null,
    string? Difficulty = null,
    IReadOnlyList<string>? ProposedTags = null,
    IReadOnlyList<ExtractedIngredientDto>? Ingredients = null,
    IReadOnlyList<ExtractedStepDto>? Steps = null,
    RecipeQualityAuditDto? QualityAudit = null,
    int? ForceOutOfRangeStepIndex = null,
    bool ForceZeroQuantityTokenInStep1 = false
);

internal sealed partial class TestStagePipelineResponder(
    Func<LlmExtractionContext, ExtractedRecipeDto> fixtureFactory
)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private ExtractedRecipeDto? _activeFixture;
    private Dictionary<int, JsonArray> _stage4TokensByStep = new();
    private Dictionary<string, int> _stepNumberByInstruction = new(StringComparer.Ordinal);

    public LlmStructuredStageResult<T> Respond<T>(
        EffectiveLlmConnectionConfig config,
        string stageName,
        IReadOnlyList<LlmStageChatMessage> messages
    )
    {
        var userPrompt =
            messages
                .FirstOrDefault(m =>
                    string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                )
                ?.Content
            ?? string.Empty;

        var rawJson = stageName switch
        {
            "stage1_extract_recipe" => BuildStage1Json(userPrompt),
            "stage2_shorten_description" => JsonSerializer.Serialize(
                new { description = _activeFixture?.Description ?? string.Empty }
            ),
            "stage3a_ingredient_search_terms" => BuildStage3aJson(userPrompt),
            "stage3c_match_batch" => BuildStage3cJson(userPrompt),
            "stage3b_match_ingredient" => BuildStage3bJson(userPrompt),
            "stage4b_tokenize_batch" => BuildStage4BatchJson(userPrompt),
            "stage4_tokenize_step" => BuildStage4SingleJson(userPrompt),
            "stage5b_polish_targeted" =>
                """{"title":null,"description":null,"servings":null,"yield":null,"timing":null,"tags":null,"ingredientPatches":[],"stepPatches":[],"polishNotes":[]}""",
            _ => throw new NotSupportedException(
                $"Unhandled stage '{stageName}' in test responder."
            ),
        };

        var parsed = JsonSerializer.Deserialize<T>(rawJson, JsonOptions);
        return new LlmStructuredStageResult<T>(
            parsed,
            rawJson,
            config.TextModel ?? "test-model",
            10
        );
    }

    private string BuildStage1Json(string sourcePrompt)
    {
        _activeFixture = fixtureFactory(new LlmExtractionContext(sourcePrompt));
        _stage4TokensByStep = new Dictionary<int, JsonArray>();
        _stepNumberByInstruction = new Dictionary<string, int>(StringComparer.Ordinal);

        var f = _activeFixture;
        if ((f.Ingredients?.Count ?? 0) == 0 && (f.Steps?.Count ?? 0) == 0)
        {
            f = f with
            {
                Ingredients =
                [
                    new(
                        RefId: "ing_1",
                        CanonicalFoodItem: "flour",
                        DisplayName: "flour",
                        Quantity: 1m,
                        Unit: "cup",
                        RawText: "1 cup flour"
                    ),
                ],
                Steps =
                [
                    new(
                        StepNumber: 1,
                        TemplateText: "Mix {{ing_1_a}} and bake.",
                        IngredientTokens: [new("ing_1_a", "ing_1")]
                    ),
                ],
            };
        }

        var flatIngredients = (f.Ingredients ?? []).ToList();
        var sectionOrder = new List<string?>();
        var ingredientsBySection = new Dictionary<string, List<ExtractedIngredientDto>>(
            StringComparer.OrdinalIgnoreCase
        );
        var instructionsBySection = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var ing in flatIngredients)
        {
            var secKey = ing.SectionName?.Trim() ?? string.Empty;
            if (!ingredientsBySection.ContainsKey(secKey))
            {
                sectionOrder.Add(ing.SectionName);
                ingredientsBySection[secKey] = [];
                instructionsBySection[secKey] = [];
            }
            ingredientsBySection[secKey].Add(ing);
        }

        foreach (var step in f.Steps ?? [])
        {
            var (prose, tokens) = CompileStepProseAndStage4Tokens(step, flatIngredients, f);
            _stage4TokensByStep[step.StepNumber] = tokens;
            _stepNumberByInstruction[prose] = step.StepNumber;

            var secKey = step.SectionName?.Trim() ?? string.Empty;
            if (!instructionsBySection.ContainsKey(secKey))
            {
                if (!ingredientsBySection.ContainsKey(secKey))
                {
                    sectionOrder.Add(step.SectionName);
                    ingredientsBySection[secKey] = [];
                }
                instructionsBySection[secKey] = [];
            }
            instructionsBySection[secKey].Add(prose);
        }

        if (sectionOrder.Count == 0 && (flatIngredients.Count > 0 || (f.Steps?.Count ?? 0) > 0))
        {
            sectionOrder.Add(null);
            ingredientsBySection[string.Empty] = flatIngredients;
            instructionsBySection[string.Empty] = [];
        }

        var sections = sectionOrder
            .Select(secName =>
            {
                var secKey = secName?.Trim() ?? string.Empty;
                var secIngs = ingredientsBySection.GetValueOrDefault(secKey) ?? [];
                var secSteps = instructionsBySection.GetValueOrDefault(secKey) ?? [];
                return new
                {
                    name = secName,
                    ingredients = secIngs
                        .Select(ing =>
                        {
                            var name = !string.IsNullOrWhiteSpace(ing.DisplayName)
                                ? ing.DisplayName
                                : ing.CanonicalFoodItem ?? "ingredient";
                            var prepNote = ing.PreparationNotes;
                            if (
                                string.IsNullOrWhiteSpace(prepNote)
                                && !string.IsNullOrWhiteSpace(ing.RawText)
                                && ing.RawText.Contains(
                                    "recipe follows",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                prepNote = "recipe follows";
                            }
                            var alts =
                                ing.AltQuantity is > 0m && !string.IsNullOrWhiteSpace(ing.AltUnit)
                                    ? new[]
                                    {
                                        new
                                        {
                                            quantity = ing.AltQuantity.Value,
                                            unit = (string?)ing.AltUnit,
                                        },
                                    }
                                    : Array.Empty<object>();
                            var replacements = (ing.Replacements ?? [])
                                .Select(r => new
                                {
                                    name = r.Name,
                                    quantity = r.Quantity,
                                    unit = r.Unit,
                                    prepNote = r.PrepNote,
                                })
                                .ToArray();
                            return new
                            {
                                name,
                                quantity = ing.Quantity,
                                maxQuantity = ing.MaxQuantity,
                                unit = ing.Unit,
                                prepNote,
                                isOptional = ing.IsOptional,
                                replacements,
                                alternateMeasurements = alts,
                            };
                        })
                        .ToArray(),
                    instructions = secSteps.ToArray(),
                };
            })
            .ToArray();

        return JsonSerializer.Serialize(
            new
            {
                title = f.Title,
                description = f.Description,
                servings = f.Servings,
                yield = f.YieldDisplay,
                sections,
                timing = new
                {
                    prep = f.PrepTimeMinutes,
                    cook = f.CookTimeMinutes,
                    total = f.TotalTimeMinutes,
                },
                tags = f.ProposedTags ?? [],
            }
        );
    }

    private static (string Prose, JsonArray Tokens) CompileStepProseAndStage4Tokens(
        ExtractedStepDto step,
        IReadOnlyList<ExtractedIngredientDto> flatIngredients,
        ExtractedRecipeDto fixture
    )
    {
        var prose = new StringBuilder();
        var tokens = new JsonArray();
        var template = step.TemplateText ?? string.Empty;
        var pos = 0;

        void AppendText(string segment)
        {
            if (string.IsNullOrEmpty(segment))
            {
                return;
            }
            prose.Append(segment);
            if (
                tokens.Count > 0
                && tokens[^1] is JsonValue val
                && val.TryGetValue<string>(out var prev)
            )
            {
                tokens[^1] = JsonValue.Create(prev + segment);
            }
            else
            {
                tokens.Add(JsonValue.Create(segment));
            }
        }

        foreach (Match match in PlaceholderRegex().Matches(template))
        {
            if (match.Index > pos)
            {
                AppendText(template[pos..match.Index]);
            }

            var key = match.Groups["key"].Value;
            var ingTok = step.IngredientTokens?.FirstOrDefault(t =>
                string.Equals(t.TokenKey, key, StringComparison.OrdinalIgnoreCase)
            );
            var timerTok = step.TimerTokens?.FirstOrDefault(t =>
                string.Equals(t.TokenKey, key, StringComparison.OrdinalIgnoreCase)
            );
            var tempTok = step.TemperatureTokens?.FirstOrDefault(t =>
                string.Equals(t.TokenKey, key, StringComparison.OrdinalIgnoreCase)
            );

            if (ingTok is not null)
            {
                var ingIdx = -1;
                for (var i = 0; i < flatIngredients.Count; i++)
                {
                    if (
                        string.Equals(
                            flatIngredients[i].RefId,
                            ingTok.IngredientRefId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        ingIdx = i;
                        break;
                    }
                }

                var ing = ingIdx >= 0 ? flatIngredients[ingIdx] : null;
                var displayName =
                    !string.IsNullOrWhiteSpace(ingTok.FallbackDisplay) ? ingTok.FallbackDisplay!
                    : !string.IsNullOrWhiteSpace(ing?.DisplayName) ? ing.DisplayName
                    : ing?.CanonicalFoodItem ?? key;

                if (ingTok.StepQuantity is > 0m && ingIdx >= 0)
                {
                    var rawUnit = ingTok.StepUnit ?? ing?.Unit;
                    var singularUnit = ToSingularStage4Unit(rawUnit);
                    var qty = ingTok.StepQuantity.Value;
                    var rendered = RenderPlainIngredient(qty, singularUnit, displayName);
                    prose.Append(rendered);

                    var emitIdx =
                        fixture.ForceOutOfRangeStepIndex.HasValue && step.StepNumber == 1
                            ? fixture.ForceOutOfRangeStepIndex.Value
                            : ingIdx;
                    var emitQty =
                        fixture.ForceZeroQuantityTokenInStep1 && step.StepNumber == 1 ? 0m : qty;

                    tokens.Add(
                        new JsonObject
                        {
                            ["type"] = "ingredient",
                            ["ingredientIndex"] = emitIdx,
                            ["name"] = displayName,
                            ["quantity"] = emitQty,
                            ["maxQuantity"] = (JsonNode?)null,
                            ["unit"] = singularUnit,
                        }
                    );
                }
                else
                {
                    AppendText(displayName);
                }
            }
            else if (timerTok is not null)
            {
                var secs = Math.Max(1, timerTok.DurationSeconds);
                decimal amt;
                string unit;
                if (secs >= 3600 && secs % 3600 == 0)
                {
                    amt = secs / 3600m;
                    unit = "hour";
                }
                else if (secs >= 60 && secs % 60 == 0)
                {
                    amt = secs / 60m;
                    unit = "minute";
                }
                else
                {
                    amt = secs;
                    unit = "second";
                }

                var plain =
                    $"{amt.ToString("0.##", CultureInfo.InvariantCulture)} {(amt == 1m ? unit : unit + "s")}";
                prose.Append(plain);
                tokens.Add(
                    new JsonObject
                    {
                        ["type"] = "timer",
                        ["amount"] = amt,
                        ["maxAmount"] = (JsonNode?)null,
                        ["unit"] = unit,
                        ["label"] = string.IsNullOrWhiteSpace(timerTok.Label)
                            ? plain
                            : timerTok.Label,
                    }
                );
            }
            else if (
                tempTok is not null
                && (tempTok.Fahrenheit.HasValue || tempTok.Celsius.HasValue)
            )
            {
                var isCelsius = !tempTok.Fahrenheit.HasValue;
                var val = tempTok.Fahrenheit ?? tempTok.Celsius!.Value;
                var suffix = isCelsius ? "°C" : "°F";
                prose.Append($"{val:0.##}{suffix}");
                tokens.Add(
                    new JsonObject
                    {
                        ["type"] = "temperature",
                        ["amount"] = val,
                        ["maxAmount"] = (JsonNode?)null,
                        ["unit"] = isCelsius ? "celsius" : "fahrenheit",
                    }
                );
            }
            else
            {
                AppendText(match.Value);
            }

            pos = match.Index + match.Length;
        }

        if (pos < template.Length)
        {
            AppendText(template[pos..]);
        }

        return (prose.ToString(), tokens);
    }

    private static string BuildStage3aJson(string userPrompt)
    {
        try
        {
            using var doc = JsonDocument.Parse(userPrompt);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var norm = FoodNameSanitizer.NormalizeCanonicalFoodName(name);
            var terms = new[] { name.Trim(), norm }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return JsonSerializer.Serialize(new { searchTerms = terms });
        }
        catch
        {
            return """{"searchTerms":[]}""";
        }
    }

    private string BuildStage3cJson(string userPrompt)
    {
        using var doc = JsonDocument.Parse(userPrompt);
        var items = doc.RootElement.GetProperty("items").EnumerateArray();
        var matches = new List<object>();

        foreach (var item in items)
        {
            var idx = item.GetProperty("ingredientIndex").GetInt32();
            matches.Add(BuildSingleMatchDecision(item, includeIndex: true, idx));
        }

        return JsonSerializer.Serialize(new { matches });
    }

    private string BuildStage3bJson(string userPrompt)
    {
        using var doc = JsonDocument.Parse(userPrompt);
        return JsonSerializer.Serialize(
            BuildSingleMatchDecision(doc.RootElement, includeIndex: false, 0)
        );
    }

    private object BuildSingleMatchDecision(JsonElement item, bool includeIndex, int idx)
    {
        var rawName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var prepNote =
            item.TryGetProperty("prepNote", out var pn) && pn.ValueKind == JsonValueKind.String
                ? pn.GetString()
                : null;
        var unit =
            item.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;

        var fixtureIng = (_activeFixture?.Ingredients ?? []).FirstOrDefault(ing =>
            string.Equals(ing.DisplayName, rawName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ing.ReferencedRecipeTitle, rawName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ing.CanonicalFoodItem, rawName, StringComparison.OrdinalIgnoreCase)
        );

        var isRecipeRef =
            fixtureIng?.IsRecipeReference == true
            || string.Equals(unit, "recipe", StringComparison.OrdinalIgnoreCase);

        if (isRecipeRef)
        {
            var refTitle = !string.IsNullOrWhiteSpace(fixtureIng?.ReferencedRecipeTitle)
                ? fixtureIng!.ReferencedRecipeTitle!
                : rawName;
            var altCombined = !string.IsNullOrWhiteSpace(prepNote)
                ? $"{refTitle} {prepNote}".Trim()
                : refTitle;

            if (item.TryGetProperty("existingRecipes", out var recipesProp))
            {
                foreach (var rec in recipesProp.EnumerateArray())
                {
                    var candTitle = rec.GetProperty("title").GetString() ?? "";
                    if (
                        string.Equals(candTitle, refTitle, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candTitle, altCombined, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        var idStr = rec.GetProperty("id").GetString();
                        return includeIndex
                            ? (object)
                                new
                                {
                                    ingredientIndex = idx,
                                    matchType = "existing_recipe",
                                    id = idStr,
                                    name = candTitle,
                                    category = (string?)null,
                                }
                            : new
                            {
                                matchType = "existing_recipe",
                                id = idStr,
                                name = candTitle,
                                category = (string?)null,
                            };
                    }
                }
            }

            return includeIndex
                ? (object)
                    new
                    {
                        ingredientIndex = idx,
                        matchType = "unresolved_recipe",
                        id = (string?)null,
                        name = refTitle,
                        category = (string?)null,
                    }
                : new
                {
                    matchType = "unresolved_recipe",
                    id = (string?)null,
                    name = refTitle,
                    category = (string?)null,
                };
        }

        var canonical = !string.IsNullOrWhiteSpace(fixtureIng?.CanonicalFoodItem)
            ? fixtureIng!.CanonicalFoodItem!
            : rawName;
        var normCanonical = FoodNameSanitizer.NormalizeCanonicalFoodName(canonical);

        if (item.TryGetProperty("existingIngredients", out var foodsProp))
        {
            foreach (var food in foodsProp.EnumerateArray())
            {
                var candName = food.GetProperty("name").GetString() ?? "";
                var normCand = FoodNameSanitizer.NormalizeCanonicalFoodName(candName);
                if (
                    string.Equals(candName, canonical, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candName, rawName, StringComparison.OrdinalIgnoreCase)
                    || (
                        !string.IsNullOrEmpty(normCand)
                        && string.Equals(
                            normCand,
                            normCanonical,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                )
                {
                    var idStr = food.GetProperty("id").GetString();
                    var catStr = food.TryGetProperty("category", out var cp)
                        ? cp.GetString()
                        : null;
                    return includeIndex
                        ? (object)
                            new
                            {
                                ingredientIndex = idx,
                                matchType = "existing_ingredient",
                                id = idStr,
                                name = candName,
                                category = catStr,
                            }
                        : new
                        {
                            matchType = "existing_ingredient",
                            id = idStr,
                            name = candName,
                            category = catStr,
                        };
                }
            }
        }

        var parsedCat = FoodCategories.Parse(fixtureIng?.Category);
        var categoryName = (parsedCat ?? FoodCategoryClassifier.Classify(canonical)).ToString();
        return includeIndex
            ? (object)
                new
                {
                    ingredientIndex = idx,
                    matchType = "new_ingredient",
                    id = (string?)null,
                    name = canonical,
                    category = (string?)categoryName,
                }
            : new
            {
                matchType = "new_ingredient",
                id = (string?)null,
                name = canonical,
                category = (string?)categoryName,
            };
    }

    private string BuildStage4BatchJson(string userPrompt)
    {
        using var doc = JsonDocument.Parse(userPrompt);
        var stepNumbers = doc
            .RootElement.GetProperty("steps")
            .EnumerateArray()
            .Select(s => s.GetProperty("stepNumber").GetInt32())
            .ToList();

        var outSteps = new JsonArray();
        foreach (var num in stepNumbers)
        {
            var tokens = _stage4TokensByStep.TryGetValue(num, out var found)
                ? (JsonNode)found.DeepClone()
                : new JsonArray { "" };
            outSteps.Add(new JsonObject { ["stepNumber"] = num, ["tokens"] = tokens });
        }

        return new JsonObject { ["steps"] = outSteps }.ToJsonString();
    }

    private string BuildStage4SingleJson(string userPrompt)
    {
        using var doc = JsonDocument.Parse(userPrompt);
        var instruction = doc.RootElement.TryGetProperty("instruction", out var instrProp)
            ? instrProp.GetString() ?? ""
            : "";
        if (!_stepNumberByInstruction.TryGetValue(instruction, out var num))
        {
            num = doc.RootElement.TryGetProperty("priorSteps", out var priorProp)
                ? priorProp.GetArrayLength() + 1
                : 1;
        }
        var tokens = _stage4TokensByStep.TryGetValue(num, out var found)
            ? (JsonNode)found.DeepClone()
            : new JsonArray { "" };
        return new JsonObject { ["tokens"] = tokens }.ToJsonString();
    }

    private static string? ToSingularStage4Unit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return null;
        }

        return unit.Trim().ToLowerInvariant() switch
        {
            "whole" or "entire" or "count" or "piece" or "pieces" or "item" or "items" or "each" =>
                null,
            "tbsp" or "tbsps" or "tablespoon" or "tablespoons" => "tablespoon",
            "tsp" or "tsps" or "teaspoon" or "teaspoons" => "teaspoon",
            "c" or "cup" or "cups" => "cup",
            "fl oz" or "fluid ounce" or "fluid ounces" => "fluid ounce",
            "pt" or "pint" or "pints" => "pint",
            "qt" or "quart" or "quarts" => "quart",
            "gal" or "gallon" or "gallons" => "gallon",
            "oz" or "ounce" or "ounces" => "ounce",
            "lb" or "lbs" or "pound" or "pounds" => "pound",
            "g" or "gram" or "grams" => "gram",
            "kg" or "kilogram" or "kilograms" => "kilogram",
            "ml" or "milliliter" or "milliliters" => "milliliter",
            "l" or "liter" or "liters" => "liter",
            "pinches" => "pinch",
            "dashes" => "dash",
            "cloves" => "clove",
            "cans" => "can",
            "slices" => "slice",
            "bunches" => "bunch",
            "sticks" => "stick",
            "sprigs" => "sprig",
            "leaves" => "leaf",
            "recipes" => "recipe",
            "batches" => "batch",
            var other => other.EndsWith('s') && other.Length > 3 ? other[..^1] : other,
        };
    }

    private static string RenderPlainIngredient(
        decimal qty,
        string? singularUnit,
        string displayName
    )
    {
        var qtyText = CulinaryMath.FormatNumber(qty, isMetric: false);
        if (string.IsNullOrWhiteSpace(singularUnit))
        {
            return $"{qtyText} {displayName}";
        }

        var unitText =
            qty <= 1.05m
                ? singularUnit
                : singularUnit switch
                {
                    "pinch" => "pinches",
                    "dash" => "dashes",
                    "bunch" => "bunches",
                    "box" => "boxes",
                    "pouch" => "pouches",
                    "leaf" => "leaves",
                    _ => singularUnit.EndsWith('s') ? singularUnit : singularUnit + "s",
                };

        return $"{qtyText} {unitText} {displayName}";
    }

    [GeneratedRegex(@"\{\{(?<key>[A-Za-z0-9_]+)\}\}")]
    private static partial Regex PlaceholderRegex();
}
