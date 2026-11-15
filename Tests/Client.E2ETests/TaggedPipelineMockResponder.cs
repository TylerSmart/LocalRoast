using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace App.Client.E2ETests;

/// <summary>
/// Answers requests for the clean-slate ingestion pipeline (vision transcription, Stage 2
/// structured extraction, and Stage 3 entity disambiguation) from a single recipe JSON document
/// so mock LLM hosts remain concise.
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

        if (
            system.Contains("disambiguate", StringComparison.OrdinalIgnoreCase)
            || (
                request.TryGetProperty("format", out var format)
                && format.GetRawText().Contains("ingredientDecisions", StringComparison.Ordinal)
            )
        )
        {
            return """{"ingredientDecisions":[],"tagDecisions":[]}""";
        }

        return ToExtractedRecipeJson(recipeJson);
    }

    public static string ReadText(JsonElement message)
    {
        var content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : content[0].GetProperty("text").GetString() ?? "";
    }

    private static string Transcribe(string recipeJson)
    {
        var recipe = JsonNode.Parse(recipeJson)!;
        return new JsonObject
        {
            ["title"] = recipe["title"]?.GetValue<string>() ?? "",
            ["introduction"] = recipe["description"]?.GetValue<string>() ?? "",
            ["ingredientLines"] = new JsonArray(
                (recipe["ingredients"]?.AsArray() ?? [])
                    .Select(i =>
                        (JsonNode?)(
                            i?["originalRawText"]?.GetValue<string>()
                            ?? i?["rawText"]?.GetValue<string>()
                            ?? i?["displayName"]?.GetValue<string>()
                        )
                    )
                    .ToArray()
            ),
            ["instructionParagraphs"] = new JsonArray(
                (recipe["steps"]?.AsArray() ?? [])
                    .Select(s =>
                        (JsonNode?)(
                            s?["instruction"]?.GetValue<string>()
                            ?? s?["templateText"]?.GetValue<string>()
                        )
                    )
                    .ToArray()
            ),
            ["notes"] = "",
        }.ToJsonString();
    }

    private static string ToExtractedRecipeJson(string recipeJson)
    {
        var root = JsonNode.Parse(recipeJson)?.AsObject();
        if (root is null || root["qualityAudit"] is not null)
        {
            return recipeJson;
        }

        var legacyIngredients = root["ingredients"]?.AsArray() ?? [];
        var extractedIngredients = new JsonArray();
        var refIds = new List<string>(legacyIngredients.Count);
        var namesByRef = new List<string>(legacyIngredients.Count);

        for (var i = 0; i < legacyIngredients.Count; i++)
        {
            var src = legacyIngredients[i]?.AsObject();
            if (src is null)
            {
                continue;
            }

            var refId = src["refId"]?.GetValue<string>() ?? $"ing_{i + 1}";
            var displayName = src["displayName"]?.GetValue<string>() ?? "ingredient";
            var canonical =
                src["canonicalFoodName"]?.GetValue<string>()
                ?? src["canonicalFoodItem"]?.GetValue<string>()
                ?? src["matchedFoodName"]?.GetValue<string>()
                ?? displayName;
            var rawText =
                src["originalRawText"]?.GetValue<string>()
                ?? src["rawText"]?.GetValue<string>()
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

            var category = src["category"]?.GetValue<string?>();
            if (
                string.IsNullOrWhiteSpace(category)
                && src["isPantryStaple"]?.GetValue<bool>() == true
                && canonical.Contains("salt", StringComparison.OrdinalIgnoreCase)
            )
            {
                category = "Spices & Seasonings";
            }

            refIds.Add(refId);
            namesByRef.Add(displayName);
            extractedIngredients.Add(
                new JsonObject
                {
                    ["refId"] = refId,
                    ["sectionName"] = src["sectionName"]?.DeepClone(),
                    ["canonicalFoodItem"] = canonical,
                    ["displayName"] = displayName,
                    ["category"] = category,
                    ["quantity"] = src["quantity"]?.DeepClone(),
                    ["maxQuantity"] = src["maxQuantity"]?.DeepClone(),
                    ["unit"] = src["unit"]?.DeepClone(),
                    ["altQuantity"] = altQty.HasValue ? JsonValue.Create(altQty.Value) : null,
                    ["altUnit"] = altUnit is not null ? JsonValue.Create(altUnit) : null,
                    ["preparationNotes"] = src["preparationNote"]?.DeepClone(),
                    ["isOptional"] = src["isOptional"]?.DeepClone() ?? JsonValue.Create(false),
                    ["isRecipeReference"] =
                        src["isRecipeReference"]?.DeepClone() ?? JsonValue.Create(false),
                    ["referencedRecipeTitle"] = src["referencedRecipeTitle"]?.DeepClone(),
                    ["rawText"] = rawText,
                }
            );
        }

        var legacySteps = root["steps"]?.AsArray() ?? [];
        var extractedSteps = new JsonArray();
        for (var sIdx = 0; sIdx < legacySteps.Count; sIdx++)
        {
            var srcStep = legacySteps[sIdx]?.AsObject();
            if (srcStep is null)
            {
                continue;
            }

            var stepNum = srcStep["stepNumber"]?.GetValue<int?>() ?? (sIdx + 1);
            var instruction =
                srcStep["instruction"]?.GetValue<string>()
                ?? srcStep["templateText"]?.GetValue<string>()
                ?? "";
            var ingIndices = (srcStep["ingredientIndices"]?.AsArray() ?? [])
                .Select(n => n?.GetValue<int>() ?? -1)
                .Where(idx => idx >= 0 && idx < refIds.Count)
                .ToList();

            var ingredientTokens = new JsonArray();
            var timerTokens = new JsonArray();
            var temperatureTokens = new JsonArray();

            string templateText;
            var inlineCounter = 0;
            if (LegacyInlineToken().IsMatch(instruction))
            {
                templateText = LegacyInlineToken()
                    .Replace(
                        instruction,
                        match =>
                        {
                            var tokKey = $"ing_s{stepNum}_{inlineCounter++}";
                            var tokJson = JsonNode.Parse(match.Groups["json"].Value)?.AsObject();
                            var ingName = tokJson?["ingredient"]?.GetValue<string>();
                            var amount = tokJson?["amount"]?.DeepClone();
                            var unit = tokJson?["unit"]?.DeepClone();

                            var matchedIdx = !string.IsNullOrWhiteSpace(ingName)
                                ? namesByRef.FindIndex(n =>
                                    n.Equals(ingName, StringComparison.OrdinalIgnoreCase)
                                )
                                : -1;
                            if (matchedIdx < 0 && ingIndices.Count > 0)
                            {
                                matchedIdx = ingIndices[0];
                            }

                            var refId =
                                matchedIdx >= 0 && matchedIdx < refIds.Count
                                    ? refIds[matchedIdx]
                                    : refIds.FirstOrDefault() ?? "ing_1";

                            ingredientTokens.Add(
                                new JsonObject
                                {
                                    ["tokenKey"] = tokKey,
                                    ["ingredientRefId"] = refId,
                                    ["stepQuantity"] = amount,
                                    ["stepUnit"] = unit,
                                    ["fallbackDisplay"] = null,
                                }
                            );
                            return $"{{{{{tokKey}}}}}";
                        }
                    );
            }
            else
            {
                var appendedPlaceholders = new List<string>();
                foreach (var idx in ingIndices)
                {
                    var tokKey = $"ing_{idx + 1}_s{stepNum}";
                    ingredientTokens.Add(
                        new JsonObject
                        {
                            ["tokenKey"] = tokKey,
                            ["ingredientRefId"] = refIds[idx],
                            ["stepQuantity"] = null,
                            ["stepUnit"] = null,
                            ["fallbackDisplay"] = null,
                        }
                    );
                    appendedPlaceholders.Add($"{{{{{tokKey}}}}}");
                }

                var timers = srcStep["timers"]?.AsArray() ?? [];
                for (var tIdx = 0; tIdx < timers.Count; tIdx++)
                {
                    var timerObj = timers[tIdx]?.AsObject();
                    if (timerObj is null)
                    {
                        continue;
                    }

                    var tokKey = $"timer_{stepNum}_{tIdx + 1}";
                    timerTokens.Add(
                        new JsonObject
                        {
                            ["tokenKey"] = tokKey,
                            ["durationSeconds"] =
                                timerObj["durationSeconds"]?.DeepClone() ?? JsonValue.Create(60),
                            ["label"] = timerObj["label"]?.DeepClone() ?? JsonValue.Create("Timer"),
                        }
                    );
                    appendedPlaceholders.Add($"{{{{{tokKey}}}}}");
                }

                templateText =
                    appendedPlaceholders.Count > 0
                        ? $"{instruction} {string.Join(" ", appendedPlaceholders)}".Trim()
                        : instruction;
            }

            extractedSteps.Add(
                new JsonObject
                {
                    ["stepNumber"] = stepNum,
                    ["sectionName"] = srcStep["sectionName"]?.DeepClone(),
                    ["templateText"] = templateText,
                    ["ingredientTokens"] = ingredientTokens,
                    ["timerTokens"] = timerTokens,
                    ["temperatureTokens"] = temperatureTokens,
                }
            );
        }

        var proposedTags =
            root["proposedTags"]?.DeepClone()
            ?? root["suggestedTagNames"]?.DeepClone()
            ?? new JsonArray();

        var isPlausible = (root["confidenceScore"]?.GetValue<decimal?>() ?? 0.90m) >= 0.85m;
        var extracted = new JsonObject
        {
            ["title"] = root["title"]?.DeepClone(),
            ["description"] = root["description"]?.DeepClone(),
            ["servings"] = root["servings"]?.DeepClone() ?? root["baseServings"]?.DeepClone(),
            ["yieldDisplay"] = root["yieldDisplay"]?.DeepClone(),
            ["prepTimeMinutes"] = root["prepTimeMinutes"]?.DeepClone(),
            ["cookTimeMinutes"] = root["cookTimeMinutes"]?.DeepClone(),
            ["totalTimeMinutes"] = root["totalTimeMinutes"]?.DeepClone(),
            ["difficulty"] = root["difficulty"]?.DeepClone(),
            ["sourceName"] = root["sourceName"]?.DeepClone(),
            ["sourceAuthor"] = root["sourceAuthor"]?.DeepClone(),
            ["proposedTags"] = proposedTags,
            ["ingredients"] = extractedIngredients,
            ["steps"] = extractedSteps,
            ["qualityAudit"] = new JsonObject
            {
                ["isPlausibleRecipe"] = isPlausible,
                ["warnings"] = new JsonArray(),
            },
        };

        return extracted.ToJsonString();
    }

    [GeneratedRegex(@"\((?<qty>\d+(?:\.\d+)?)\s+(?<unit>[a-zA-Z]+)\)")]
    private static partial Regex AltInParens();

    [GeneratedRegex(@"\|\|(?<json>\{.*?\})\|\|")]
    private static partial Regex LegacyInlineToken();
}
