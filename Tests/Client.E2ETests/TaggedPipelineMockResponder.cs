using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;
using App.Core.Recipes;

namespace App.Client.E2ETests;

/// <summary>
/// Answers multi-stage ingestion requests (vision transcription plus Stages 1–5b) and legacy
/// mock recipe JSON payloads for Playwright E2E tests.
/// </summary>
public static partial class TaggedPipelineMockResponder
{
    public static string Respond(JsonElement request, string recipeJson)
    {
        var messages = request.GetProperty("messages");
        var system = ReadText(messages[0]);
        if (system.StartsWith("Transcribe the recipe", StringComparison.Ordinal))
        {
            return Transcribe(recipeJson);
        }

        var userText = messages.GetArrayLength() > 1 ? ReadText(messages[1]) : string.Empty;
        var schema = ExtractSchemaElement(request);
        if (
            schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var props)
        )
        {
            if (props.TryGetProperty("sections", out _))
            {
                return BuildStage1Json(recipeJson);
            }

            if (props.TryGetProperty("ingredientPatches", out _))
            {
                return """{"title":null,"description":null,"servings":null,"yield":null,"timing":null,"tags":null,"ingredientPatches":[],"stepPatches":[],"polishNotes":[]}""";
            }

            if (props.TryGetProperty("description", out _))
            {
                var doc = JsonNode.Parse(recipeJson);
                var desc = doc?["description"]?.GetValue<string?>() ?? string.Empty;
                return new JsonObject { ["description"] = desc }.ToJsonString();
            }

            if (props.TryGetProperty("searchTerms", out _))
            {
                return BuildStage3aJson(userText);
            }

            if (props.TryGetProperty("matches", out _))
            {
                return BuildStage3cJson(userText, recipeJson);
            }

            if (props.TryGetProperty("matchType", out _))
            {
                return BuildStage3bJson(userText, recipeJson);
            }

            if (props.TryGetProperty("steps", out _))
            {
                return BuildStage4BatchJson(userText, recipeJson);
            }

            if (props.TryGetProperty("tokens", out _))
            {
                return BuildStage4SingleJson(userText, recipeJson);
            }
        }

        return BuildStage1Json(recipeJson);
    }

    public static string ReadText(JsonElement message)
    {
        var content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : content[0].GetProperty("text").GetString() ?? "";
    }

    private static JsonElement ExtractSchemaElement(JsonElement request)
    {
        if (request.TryGetProperty("format", out var format))
        {
            return format;
        }

        if (
            request.TryGetProperty("response_format", out var rf)
            && rf.ValueKind == JsonValueKind.Object
            && rf.TryGetProperty("json_schema", out var js)
            && js.ValueKind == JsonValueKind.Object
            && js.TryGetProperty("schema", out var s)
        )
        {
            return s;
        }

        return default;
    }

    private static string Transcribe(string recipeJson)
    {
        var recipe = JsonNode.Parse(recipeJson)!;
        var ingredients = FlattenMockIngredients(recipe);
        var steps = FlattenMockSteps(recipe);

        return new JsonObject
        {
            ["title"] = recipe["title"]?.GetValue<string>() ?? "",
            ["introduction"] = recipe["description"]?.GetValue<string>() ?? "",
            ["ingredientLines"] = new JsonArray(
                ingredients
                    .Select(i =>
                        (JsonNode?)(
                            i["originalRawText"]?.GetValue<string?>()
                            ?? i["rawText"]?.GetValue<string?>()
                            ?? i["displayName"]?.GetValue<string?>()
                            ?? i["name"]?.GetValue<string?>()
                        )
                    )
                    .ToArray()
            ),
            ["instructionParagraphs"] = new JsonArray(
                steps
                    .Select(s =>
                    {
                        var text =
                            s["instruction"]?.GetValue<string?>()
                            ?? s["templateText"]?.GetValue<string?>()
                            ?? "";
                        return (JsonNode?)StepInstructionParser.ToPlainText(text);
                    })
                    .ToArray()
            ),
            ["notes"] = "",
        }.ToJsonString();
    }

    private static string BuildStage1Json(string recipeJson)
    {
        var root = JsonNode.Parse(recipeJson)?.AsObject();
        if (root is null || root["sections"] is not null)
        {
            return recipeJson;
        }

        var legacyIngredients = root["ingredients"]?.AsArray() ?? [];
        var ingredientNames = new List<string>(legacyIngredients.Count);
        var stage1Ingredients = new JsonArray();

        for (var i = 0; i < legacyIngredients.Count; i++)
        {
            var src = legacyIngredients[i]?.AsObject();
            if (src is null)
            {
                continue;
            }

            var displayName =
                src["displayName"]?.GetValue<string?>()
                ?? src["name"]?.GetValue<string?>()
                ?? "ingredient";
            var rawText =
                src["originalRawText"]?.GetValue<string?>()
                ?? src["rawText"]?.GetValue<string?>()
                ?? displayName;

            decimal? altQty = src["altQuantity"]?.GetValue<decimal?>();
            string? altUnit = src["altUnit"]?.GetValue<string?>();
            if (!altQty.HasValue && !string.IsNullOrWhiteSpace(rawText))
            {
                var altMatch = AltInParens().Match(rawText);
                if (
                    altMatch.Success
                    && decimal.TryParse(
                        altMatch.Groups[1].Value,
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsedAltQty
                    )
                )
                {
                    altQty = parsedAltQty;
                    altUnit = altMatch.Groups[2].Value;
                }
            }

            var altsArray = new JsonArray();
            if (altQty.HasValue && !string.IsNullOrWhiteSpace(altUnit))
            {
                altsArray.Add(new JsonObject { ["quantity"] = altQty.Value, ["unit"] = altUnit });
            }

            var replacementsArray = new JsonArray();
            if (src["replacements"] is JsonArray repArr)
            {
                foreach (var repNode in repArr)
                {
                    if (repNode is JsonObject repObj)
                    {
                        replacementsArray.Add(
                            new JsonObject
                            {
                                ["name"] =
                                    repObj["name"]?.DeepClone()
                                    ?? repObj["displayName"]?.DeepClone()
                                    ?? JsonValue.Create(""),
                                ["quantity"] = repObj["quantity"]?.DeepClone(),
                                ["unit"] = repObj["unit"]?.DeepClone(),
                                ["prepNote"] =
                                    repObj["prepNote"]?.DeepClone()
                                    ?? repObj["preparationNote"]?.DeepClone(),
                            }
                        );
                    }
                }
            }

            ingredientNames.Add(displayName);
            stage1Ingredients.Add(
                new JsonObject
                {
                    ["name"] = displayName,
                    ["quantity"] = src["quantity"]?.DeepClone(),
                    ["maxQuantity"] = src["maxQuantity"]?.DeepClone(),
                    ["unit"] = src["unit"]?.DeepClone(),
                    ["prepNote"] =
                        src["prepNote"]?.DeepClone() ?? src["preparationNote"]?.DeepClone(),
                    ["isOptional"] = src["isOptional"]?.DeepClone() ?? JsonValue.Create(false),
                    ["replacements"] = replacementsArray,
                    ["alternateMeasurements"] = altsArray,
                }
            );
        }

        var compiledSteps = CompileStepsAndTokens(root, ingredientNames);
        var instructions = new JsonArray();
        foreach (var (_, prose, _) in compiledSteps)
        {
            instructions.Add(JsonValue.Create(prose));
        }

        var tags =
            root["tags"]?.DeepClone()
            ?? root["proposedTags"]?.DeepClone()
            ?? root["suggestedTagNames"]?.DeepClone()
            ?? new JsonArray();

        var timing =
            root["timing"]?.DeepClone()
            ?? new JsonObject
            {
                ["prep"] = root["prepTimeMinutes"]?.DeepClone(),
                ["cook"] = root["cookTimeMinutes"]?.DeepClone(),
                ["total"] = root["totalTimeMinutes"]?.DeepClone(),
            };

        var stage1 = new JsonObject
        {
            ["title"] = root["title"]?.DeepClone(),
            ["description"] = root["description"]?.DeepClone(),
            ["servings"] = root["servings"]?.DeepClone() ?? root["baseServings"]?.DeepClone(),
            ["yield"] = root["yield"]?.DeepClone() ?? root["yieldDisplay"]?.DeepClone(),
            ["sections"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = (JsonNode?)null,
                    ["ingredients"] = stage1Ingredients,
                    ["instructions"] = instructions,
                },
            },
            ["timing"] = timing,
            ["tags"] = tags,
        };

        return stage1.ToJsonString();
    }

    private static string BuildStage3aJson(string userText)
    {
        try
        {
            using var doc = JsonDocument.Parse(userText);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var norm = FoodNameSanitizer.NormalizeCanonicalFoodName(name);
            var terms = new JsonArray();
            if (!string.IsNullOrWhiteSpace(name))
            {
                terms.Add(name.Trim());
            }
            if (
                !string.IsNullOrWhiteSpace(norm)
                && !norm.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
            )
            {
                terms.Add(norm);
            }
            return new JsonObject { ["searchTerms"] = terms }.ToJsonString();
        }
        catch
        {
            return """{"searchTerms":[]}""";
        }
    }

    private static string BuildStage3cJson(string userText, string recipeJson)
    {
        using var userDoc = JsonDocument.Parse(userText);
        var recipe = JsonNode.Parse(recipeJson);
        var mockIngs = recipe is not null ? FlattenMockIngredients(recipe) : [];
        var matches = new JsonArray();

        foreach (var item in userDoc.RootElement.GetProperty("items").EnumerateArray())
        {
            var idx = item.GetProperty("ingredientIndex").GetInt32();
            matches.Add(BuildStage3MatchObject(item, mockIngs, includeIndex: true, idx));
        }

        return new JsonObject { ["matches"] = matches }.ToJsonString();
    }

    private static string BuildStage3bJson(string userText, string recipeJson)
    {
        using var userDoc = JsonDocument.Parse(userText);
        var recipe = JsonNode.Parse(recipeJson);
        var mockIngs = recipe is not null ? FlattenMockIngredients(recipe) : [];
        return BuildStage3MatchObject(userDoc.RootElement, mockIngs, includeIndex: false, 0)
            .ToJsonString();
    }

    private static JsonObject BuildStage3MatchObject(
        JsonElement item,
        List<JsonObject> mockIngs,
        bool includeIndex,
        int idx
    )
    {
        var rawName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var srcIng = mockIngs.FirstOrDefault(m =>
            string.Equals(
                m["displayName"]?.GetValue<string?>() ?? m["name"]?.GetValue<string?>(),
                rawName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        var preferredCanonical =
            srcIng?["canonicalFoodName"]?.GetValue<string?>()
            ?? srcIng?["matchedFoodName"]?.GetValue<string?>()
            ?? rawName;

        string? matchedId = null;
        string? matchedName = null;
        string? matchedCategory = null;

        if (item.TryGetProperty("existingIngredients", out var cands))
        {
            foreach (var c in cands.EnumerateArray())
            {
                var cname = c.GetProperty("name").GetString() ?? "";
                if (
                    string.Equals(cname, preferredCanonical, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cname, rawName, StringComparison.OrdinalIgnoreCase)
                )
                {
                    matchedId = c.GetProperty("id").GetString();
                    matchedName = cname;
                    matchedCategory = c.TryGetProperty("category", out var cp)
                        ? cp.GetString()
                        : null;
                    break;
                }
            }
        }

        var obj = new JsonObject();
        if (includeIndex)
        {
            obj["ingredientIndex"] = idx;
        }

        if (matchedId is not null)
        {
            obj["matchType"] = "existing_ingredient";
            obj["id"] = matchedId;
            obj["name"] = matchedName;
            obj["category"] = matchedCategory;
            return obj;
        }

        string? catEnum = null;
        var rawCat = srcIng?["category"]?.GetValue<string?>();
        if (!string.IsNullOrWhiteSpace(rawCat))
        {
            catEnum = FoodCategories.Parse(rawCat)?.ToString();
        }
        else if (
            srcIng?["isPantryStaple"]?.GetValue<bool>() == true
            && preferredCanonical.Contains("salt", StringComparison.OrdinalIgnoreCase)
        )
        {
            catEnum = nameof(FoodCategory.SpicesAndSeasonings);
        }
        else
        {
            catEnum = FoodCategoryClassifier.Classify(preferredCanonical).ToString();
        }

        obj["matchType"] = "new_ingredient";
        obj["id"] = (JsonNode?)null;
        obj["name"] = preferredCanonical;
        obj["category"] = catEnum;
        return obj;
    }

    private static string BuildStage4BatchJson(string userText, string recipeJson)
    {
        using var userDoc = JsonDocument.Parse(userText);
        var root = JsonNode.Parse(recipeJson)?.AsObject();
        if (root is null)
        {
            return """{"steps":[]}""";
        }

        var compiled = BuildCompiledStepsForRoot(root);
        var byNumber = compiled.ToDictionary(x => x.StepNumber, x => x.Tokens);

        var outSteps = new JsonArray();
        foreach (var reqStep in userDoc.RootElement.GetProperty("steps").EnumerateArray())
        {
            var num = reqStep.GetProperty("stepNumber").GetInt32();
            var instr = reqStep.TryGetProperty("instruction", out var ip)
                ? ip.GetString() ?? ""
                : "";
            var tokArr = byNumber.TryGetValue(num, out var found)
                ? (JsonNode)found.DeepClone()
                : new JsonArray { instr };
            outSteps.Add(new JsonObject { ["stepNumber"] = num, ["tokens"] = tokArr });
        }

        return new JsonObject { ["steps"] = outSteps }.ToJsonString();
    }

    private static string BuildStage4SingleJson(string userText, string recipeJson)
    {
        using var userDoc = JsonDocument.Parse(userText);
        var root = JsonNode.Parse(recipeJson)?.AsObject();
        if (root is null)
        {
            return """{"tokens":[]}""";
        }

        var compiled = BuildCompiledStepsForRoot(root);
        var instr = userDoc.RootElement.TryGetProperty("instruction", out var ip)
            ? ip.GetString() ?? ""
            : "";
        var match = compiled.FirstOrDefault(x =>
            string.Equals(x.Prose, instr, StringComparison.Ordinal)
        );
        if (match.Tokens is null)
        {
            var fallbackIdx = userDoc.RootElement.TryGetProperty("priorSteps", out var pp)
                ? pp.GetArrayLength()
                : 0;
            match =
                fallbackIdx >= 0 && fallbackIdx < compiled.Count
                    ? compiled[fallbackIdx]
                    : (1, instr, new JsonArray { instr });
        }

        return new JsonObject { ["tokens"] = match.Tokens.DeepClone() }.ToJsonString();
    }

    private static List<(int StepNumber, string Prose, JsonArray Tokens)> BuildCompiledStepsForRoot(
        JsonObject root
    )
    {
        var ingNodes = FlattenMockIngredients(root);
        var ingNames = ingNodes
            .Select(i =>
                i["displayName"]?.GetValue<string?>()
                ?? i["name"]?.GetValue<string?>()
                ?? "ingredient"
            )
            .ToList();
        return CompileStepsAndTokens(root, ingNames);
    }

    private static List<(int StepNumber, string Prose, JsonArray Tokens)> CompileStepsAndTokens(
        JsonObject root,
        List<string> ingredientNames
    )
    {
        var result = new List<(int, string, JsonArray)>();
        var stepObjs = FlattenMockSteps(root);
        for (var i = 0; i < stepObjs.Count; i++)
        {
            var srcStep = stepObjs[i];
            var stepNum = srcStep["stepNumber"]?.GetValue<int?>() ?? (i + 1);
            var rawInstr =
                srcStep["instruction"]?.GetValue<string?>()
                ?? srcStep["templateText"]?.GetValue<string?>()
                ?? "";
            var (prose, tokens) = ConvertInstructionAndTimers(srcStep, rawInstr, ingredientNames);
            result.Add((stepNum, prose, tokens));
        }
        return result;
    }

    private static (string Prose, JsonArray Tokens) ConvertInstructionAndTimers(
        JsonObject srcStep,
        string rawInstr,
        List<string> ingredientNames
    )
    {
        var tokens = new JsonArray();
        var prose = new StringBuilder();

        void AppendText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            prose.Append(text);
            if (
                tokens.Count > 0
                && tokens[^1] is JsonValue v
                && v.TryGetValue<string>(out var prev)
            )
            {
                tokens[^1] = JsonValue.Create(prev + text);
            }
            else
            {
                tokens.Add(JsonValue.Create(text));
            }
        }

        var segments = StepInstructionParser.ParseSegments(rawInstr);
        if (segments.Any(s => s.IsToken))
        {
            foreach (var seg in segments)
            {
                if (!seg.IsToken || seg.Token is null)
                {
                    AppendText(seg.Text);
                    continue;
                }

                switch (seg.Token)
                {
                    case StepIngredientToken ingTok when ingTok.Amount is > 0m:
                    {
                        var ingIdx = ingredientNames.FindIndex(n =>
                            n.Equals(ingTok.Ingredient, StringComparison.OrdinalIgnoreCase)
                        );
                        if (ingIdx < 0)
                        {
                            ingIdx = 0;
                        }
                        var unitStr = ToSingularStage4Unit(
                            ingTok.Unit?.ToString() ?? ingTok.CustomUnitName
                        );
                        var rendered = RenderPlainMeasurement(
                            ingTok.Amount.Value,
                            ingTok.MaxAmount,
                            unitStr,
                            false,
                            ingTok.Ingredient
                        );
                        prose.Append(rendered);
                        tokens.Add(
                            new JsonObject
                            {
                                ["type"] = "ingredient",
                                ["ingredientIndex"] = ingIdx,
                                ["name"] = ingTok.Ingredient,
                                ["quantity"] = ingTok.Amount.Value,
                                ["maxQuantity"] = ingTok.MaxAmount.HasValue
                                    ? JsonValue.Create(ingTok.MaxAmount.Value)
                                    : null,
                                ["unit"] = unitStr,
                            }
                        );
                        break;
                    }
                    case StepIngredientToken ingTok:
                        AppendText(ingTok.Ingredient);
                        break;
                    case StepMixtureToken mixTok when mixTok.Amount is > 0m:
                    {
                        var unitStr = ToSingularStage4Unit(
                            mixTok.Unit?.ToString() ?? mixTok.CustomUnitName
                        );
                        var rendered = RenderPlainMeasurement(
                            mixTok.Amount.Value,
                            mixTok.MaxAmount,
                            unitStr,
                            false,
                            mixTok.Name
                        );
                        prose.Append(rendered);
                        tokens.Add(
                            new JsonObject
                            {
                                ["type"] = "mixture",
                                ["name"] = mixTok.Name,
                                ["quantity"] = mixTok.Amount.Value,
                                ["maxQuantity"] = mixTok.MaxAmount.HasValue
                                    ? JsonValue.Create(mixTok.MaxAmount.Value)
                                    : null,
                                ["unit"] = unitStr,
                            }
                        );
                        break;
                    }
                    case StepMixtureToken mixTok:
                        AppendText(mixTok.Name);
                        break;
                    case StepTimerToken timerTok:
                    {
                        var (amt, maxAmt, unit) = DecomposeTimer(
                            timerTok.Seconds,
                            timerTok.MaxSeconds
                        );
                        var plain = RenderPlainTimer(amt, maxAmt, unit);
                        prose.Append(plain);
                        tokens.Add(
                            new JsonObject
                            {
                                ["type"] = "timer",
                                ["amount"] = amt,
                                ["maxAmount"] = maxAmt.HasValue
                                    ? JsonValue.Create(maxAmt.Value)
                                    : null,
                                ["unit"] = unit,
                                ["label"] = !string.IsNullOrWhiteSpace(timerTok.Label)
                                    ? timerTok.Label
                                    : plain,
                            }
                        );
                        break;
                    }
                    case StepTemperatureToken tempTok:
                    {
                        var isCelsius = !tempTok.Fahrenheit.HasValue && tempTok.Celsius.HasValue;
                        var val = tempTok.Fahrenheit ?? tempTok.Celsius ?? 350m;
                        var maxVal = tempTok.MaxFahrenheit ?? tempTok.MaxCelsius;
                        var suffix = isCelsius ? "°C" : "°F";
                        var plain =
                            maxVal.HasValue && maxVal.Value > val
                                ? $"{val:0.##} to {maxVal.Value:0.##}{suffix}"
                                : $"{val:0.##}{suffix}";
                        prose.Append(plain);
                        tokens.Add(
                            new JsonObject
                            {
                                ["type"] = "temperature",
                                ["amount"] = val,
                                ["maxAmount"] = maxVal.HasValue
                                    ? JsonValue.Create(maxVal.Value)
                                    : null,
                                ["unit"] = isCelsius ? "celsius" : "fahrenheit",
                            }
                        );
                        break;
                    }
                    case StepDimensionToken dimTok:
                    {
                        var unitStr = DimensionUnitToSchema(dimTok.Unit);
                        var rendered = RenderPlainMeasurement(
                            dimTok.Value,
                            dimTok.MaxValue,
                            unitStr,
                            dimTok.Hyphenated,
                            null
                        );
                        prose.Append(rendered);
                        tokens.Add(
                            new JsonObject
                            {
                                ["type"] = "dimension",
                                ["amount"] = dimTok.Value,
                                ["maxAmount"] = dimTok.MaxValue.HasValue
                                    ? JsonValue.Create(dimTok.MaxValue.Value)
                                    : null,
                                ["unit"] = unitStr,
                                ["hyphenated"] = dimTok.Hyphenated,
                            }
                        );
                        break;
                    }
                    case StepFixedToken fixTok when fixTok.Amount is > 0m:
                    {
                        var unitStr = ToSingularStage4Unit(
                            fixTok.Unit?.ToString() ?? fixTok.CustomUnitName
                        );
                        var rendered = RenderPlainMeasurement(
                            fixTok.Amount.Value,
                            fixTok.MaxAmount,
                            unitStr,
                            fixTok.Hyphenated,
                            fixTok.Name
                        );
                        prose.Append(rendered);
                        tokens.Add(
                            new JsonObject
                            {
                                ["type"] = "fixed",
                                ["name"] = fixTok.Name ?? "",
                                ["amount"] = fixTok.Amount.Value,
                                ["maxAmount"] = fixTok.MaxAmount.HasValue
                                    ? JsonValue.Create(fixTok.MaxAmount.Value)
                                    : null,
                                ["unit"] = unitStr,
                                ["hyphenated"] = fixTok.Hyphenated,
                            }
                        );
                        break;
                    }
                    case StepFixedToken fixTok:
                        AppendText(fixTok.Name ?? "");
                        break;
                }
            }

            return (prose.ToString(), tokens);
        }

        // Plain instruction without inline ||{...}|| tokens; preserve explicit timers if any.
        var timers = srcStep["timers"]?.AsArray();
        if (timers is { Count: > 0 } && timers[0] is JsonObject firstTimer)
        {
            var secs = firstTimer["durationSeconds"]?.GetValue<int?>() ?? 60;
            var label = firstTimer["label"]?.GetValue<string?>() ?? "Timer";
            var (amt, _, unit) = DecomposeTimer(secs, null);
            var timerPlain = RenderPlainTimer(amt, null, unit);
            var timerObj = new JsonObject
            {
                ["type"] = "timer",
                ["amount"] = amt,
                ["maxAmount"] = (JsonNode?)null,
                ["unit"] = unit,
                ["label"] = label,
            };

            var durationMatch = PlainDurationRegex().Match(rawInstr);
            if (durationMatch.Success)
            {
                AppendText(rawInstr[..durationMatch.Index]);
                prose.Append(timerPlain);
                tokens.Add(timerObj);
                AppendText(rawInstr[(durationMatch.Index + durationMatch.Length)..]);
                return (prose.ToString(), tokens);
            }

            var endsWithPeriod = rawInstr.EndsWith('.');
            var body = endsWithPeriod ? rawInstr[..^1].TrimEnd() : rawInstr.TrimEnd();
            AppendText($"{body} for ");
            prose.Append(timerPlain);
            tokens.Add(timerObj);
            if (endsWithPeriod)
            {
                AppendText(".");
            }
            return (prose.ToString(), tokens);
        }

        AppendText(rawInstr);
        return (prose.ToString(), tokens);
    }

    private static List<JsonObject> FlattenMockIngredients(JsonNode recipe)
    {
        var list = new List<JsonObject>();
        if (recipe["sections"] is JsonArray secArr)
        {
            foreach (var sec in secArr.OfType<JsonObject>())
            {
                if (sec["ingredients"] is JsonArray ings)
                {
                    list.AddRange(ings.OfType<JsonObject>());
                }
            }
            return list;
        }

        if (recipe["ingredients"] is JsonArray flat)
        {
            list.AddRange(flat.OfType<JsonObject>());
        }
        return list;
    }

    private static List<JsonObject> FlattenMockSteps(JsonNode recipe)
    {
        var list = new List<JsonObject>();
        if (recipe["sections"] is JsonArray secArr)
        {
            var stepNo = 1;
            foreach (var sec in secArr.OfType<JsonObject>())
            {
                if (sec["instructions"] is JsonArray instrs)
                {
                    foreach (var ins in instrs)
                    {
                        var text = ins?.GetValue<string?>() ?? "";
                        list.Add(
                            new JsonObject { ["stepNumber"] = stepNo++, ["instruction"] = text }
                        );
                    }
                }
            }
            return list;
        }

        if (recipe["steps"] is JsonArray flat)
        {
            list.AddRange(flat.OfType<JsonObject>());
        }
        return list;
    }

    private static (decimal Amt, decimal? MaxAmt, string Unit) DecomposeTimer(
        int seconds,
        int? maxSeconds
    )
    {
        if (
            seconds >= 3600
            && seconds % 3600 == 0
            && (!maxSeconds.HasValue || maxSeconds.Value % 3600 == 0)
        )
        {
            return (seconds / 3600m, maxSeconds.HasValue ? maxSeconds.Value / 3600m : null, "hour");
        }
        if (
            seconds >= 60
            && seconds % 60 == 0
            && (!maxSeconds.HasValue || maxSeconds.Value % 60 == 0)
        )
        {
            return (seconds / 60m, maxSeconds.HasValue ? maxSeconds.Value / 60m : null, "minute");
        }
        return (seconds, maxSeconds, "second");
    }

    private static string RenderPlainTimer(decimal amt, decimal? maxAmt, string unit)
    {
        var effective = maxAmt ?? amt;
        var unitText = effective <= 1.05m ? unit : unit + "s";
        return maxAmt.HasValue && maxAmt.Value > amt
            ? $"{amt:0.##} to {maxAmt.Value:0.##} {unitText}"
            : $"{amt:0.##} {unitText}";
    }

    private static string DimensionUnitToSchema(DimensionUnit unit) =>
        unit switch
        {
            DimensionUnit.Inch => "inch",
            DimensionUnit.Foot => "foot",
            DimensionUnit.Yard => "yard",
            DimensionUnit.Mm => "millimeter",
            DimensionUnit.Cm => "centimeter",
            DimensionUnit.M => "meter",
            _ => "inch",
        };

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
            "floz" or "fl oz" or "fluid ounce" or "fluid ounces" => "fluid ounce",
            "pt" or "pint" or "pints" => "pint",
            "qt" or "quart" or "quarts" => "quart",
            "gal" or "gallon" or "gallons" => "gallon",
            "oz" or "ounce" or "ounces" => "ounce",
            "lb" or "lbs" or "pound" or "pounds" => "pound",
            "g" or "gram" or "grams" => "gram",
            "kg" or "kilogram" or "kilograms" => "kilogram",
            "ml" or "milliliter" or "milliliters" => "milliliter",
            "l" or "liter" or "liters" => "liter",
            "cloves" => "clove",
            "cans" => "can",
            "slices" => "slice",
            var other => other,
        };
    }

    private static string RenderPlainMeasurement(
        decimal qty,
        decimal? maxQty,
        string? unit,
        bool hyphenated,
        string? suffix
    )
    {
        var qStr = CulinaryMath.FormatNumber(qty, isMetric: false);
        var qtyText =
            maxQty.HasValue && maxQty.Value > qty
                ? $"{qStr} to {CulinaryMath.FormatNumber(maxQty.Value, isMetric: false)}"
                : qStr;
        var effective = maxQty ?? qty;
        var unitText =
            string.IsNullOrWhiteSpace(unit) ? ""
            : hyphenated || effective <= 1.05m
                ? unit switch
                {
                    "millimeter" => "mm",
                    "centimeter" => "cm",
                    "milliliter" => "ml",
                    "gram" => "g",
                    "kilogram" => "kg",
                    _ => unit,
                }
            : unit switch
            {
                "inch" => "inches",
                "foot" => "feet",
                "pinch" => "pinches",
                "dash" => "dashes",
                "bunch" => "bunches",
                "millimeter" => "mm",
                "centimeter" => "cm",
                "milliliter" => "ml",
                "gram" => "g",
                "kilogram" => "kg",
                _ => unit.EndsWith('s') ? unit : unit + "s",
            };
        var combined =
            string.IsNullOrEmpty(unitText) ? qtyText
            : hyphenated ? $"{qtyText}-{unitText}"
            : $"{qtyText} {unitText}";
        return string.IsNullOrWhiteSpace(suffix) ? combined : $"{combined} {suffix}";
    }

    [GeneratedRegex(@"\((?<qty>\d+(?:\.\d+)?)\s+(?<unit>[a-zA-Z]+)\)")]
    private static partial Regex AltInParens();

    [GeneratedRegex(
        @"\b\d+(?:\.\d+)?\s*(?:seconds?|secs?|minutes?|mins?|hours?|hrs?)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex PlainDurationRegex();
}
