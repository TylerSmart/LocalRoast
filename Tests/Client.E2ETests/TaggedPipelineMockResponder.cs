using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace App.Client.E2ETests;

/// <summary>
/// Answers each request of the tagged ingestion pipeline (source line labels, vision
/// transcription, ingredient extraction and method tags) from one full recipe payload, so mock
/// LLM hosts can keep describing a recipe as a single JSON document.
/// </summary>
public static partial class TaggedPipelineMockResponder
{
    public static string Respond(JsonElement request, string recipeJson)
    {
        var messages = request.GetProperty("messages");
        var system = ReadText(messages[0]);
        var user = ReadText(messages[messages.GetArrayLength() - 1]);
        if (system.StartsWith("Label every line", StringComparison.Ordinal))
        {
            return LabelSourceLines(user);
        }
        if (system.StartsWith("Transcribe the recipe", StringComparison.Ordinal))
        {
            return Transcribe(recipeJson);
        }
        if (system.StartsWith("You annotate a recipe method", StringComparison.Ordinal))
        {
            return TagMethod(user, recipeJson);
        }
        return recipeJson;
    }

    public static string ReadText(JsonElement message)
    {
        var content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : content[0].GetProperty("text").GetString() ?? "";
    }

    private static string LabelSourceLines(string numberedLines)
    {
        var labels = new JsonObject();
        string? mode = null;
        var sawIngredient = false;
        foreach (var line in numberedLines.Split('\n'))
        {
            var match = NumberedLine().Match(line);
            if (!match.Success)
            {
                continue;
            }
            var number = match.Groups["number"].Value;
            var text = match.Groups["text"].Value.Trim();
            string label;
            if (IngredientsHeader().IsMatch(text))
            {
                (label, mode) = ("skip", "ingredient");
            }
            else if (InstructionsHeader().IsMatch(text))
            {
                (label, mode) = ("skip", "instruction");
            }
            else if (mode is not null)
            {
                label = mode;
            }
            else if (StepLine().IsMatch(text))
            {
                label = "instruction";
            }
            else if (QuantityLine().IsMatch(text))
            {
                label = "ingredient";
                sawIngredient = true;
            }
            else if (sawIngredient)
            {
                label = "instruction";
            }
            else
            {
                label = number == "1" ? "title" : "description";
            }
            labels[number] = label;
        }
        return labels.ToJsonString();
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
                            ?? i?["displayName"]?.GetValue<string>()
                        )
                    )
                    .ToArray()
            ),
            ["instructionParagraphs"] = new JsonArray(
                (recipe["steps"]?.AsArray() ?? [])
                    .Select(s => (JsonNode?)s?["instruction"]?.GetValue<string>())
                    .ToArray()
            ),
            ["notes"] = "",
        }.ToJsonString();
    }

    // Links each step to the payload step's ingredient rows and timer labels, and tags each
    // amount with the row whose name follows it.
    private static string TagMethod(string prompt, string recipeJson)
    {
        var payloadSteps = JsonNode.Parse(recipeJson)?["steps"]?.AsArray() ?? [];
        var labels = new List<string>();
        var amounts = new JsonObject();
        var timers = new JsonObject();
        var steps = new JsonObject();
        foreach (var line in prompt.Split('\n'))
        {
            var ingredient = IngredientLine().Match(line);
            if (ingredient.Success && !line.StartsWith("Step ", StringComparison.Ordinal))
            {
                labels.Add(ingredient.Groups["label"].Value);
                continue;
            }
            var step = StepPromptLine().Match(line);
            if (!step.Success)
            {
                continue;
            }
            var stepNumber = int.Parse(step.Groups["number"].Value);
            var text = step.Groups["text"].Value;
            var payloadStep =
                stepNumber <= payloadSteps.Count ? payloadSteps[stepNumber - 1] : null;
            var linked = (payloadStep?["ingredientIndices"]?.AsArray() ?? [])
                .Select(i => i!.GetValue<int>())
                .Where(i => i < labels.Count)
                .Select(i => labels[i])
                .ToList();
            if (payloadStep is null)
            {
                linked = labels.Where(l => Mentions(text, l)).ToList();
            }
            steps[stepNumber.ToString()] = new JsonArray(
                linked.Select(l => (JsonNode?)l).ToArray()
            );

            var payloadTimers = payloadStep?["timers"]?.AsArray() ?? [];
            var timerIndex = 0;
            foreach (Match mark in Mark().Matches(text))
            {
                var id = mark.Groups["id"].Value;
                if (id[0] == 'T')
                {
                    timers[id] =
                        timerIndex < payloadTimers.Count
                            ? payloadTimers[timerIndex]?["label"]?.GetValue<string>() ?? "Timer"
                            : "Timer";
                    timerIndex++;
                    continue;
                }
                var following = text[(mark.Index + mark.Length)..];
                var nextMark = following.IndexOf('[');
                following = nextMark >= 0 ? following[..nextMark] : following;
                amounts[id] =
                    linked
                        .Concat(labels)
                        .Select(l => (Label: l, Index: MentionIndex(following, l)))
                        .Where(m => m.Index >= 0)
                        .OrderBy(m => m.Index)
                        .Select(m => m.Label)
                        .FirstOrDefault()
                    ?? "other";
            }
        }

        var tags = new JsonObject();
        if (amounts.Count > 0)
        {
            tags["amounts"] = amounts;
        }
        if (timers.Count > 0)
        {
            tags["timers"] = timers;
        }
        tags["steps"] = steps;
        return tags.ToJsonString();
    }

    private static bool Mentions(string text, string label) => MentionIndex(text, label) >= 0;

    private static int MentionIndex(string text, string label)
    {
        var name = IngredientName().Match(label).Groups["name"].Value;
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3)
            .Select(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
    }

    [GeneratedRegex(@"^(?<number>\d+)\|(?<text>.*)$")]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"^ingredients:?$", RegexOptions.IgnoreCase)]
    private static partial Regex IngredientsHeader();

    [GeneratedRegex(@"^(instructions|directions|method|preparation):?$", RegexOptions.IgnoreCase)]
    private static partial Regex InstructionsHeader();

    [GeneratedRegex(@"^\d+[A-Za-z]?[.)]\s")]
    private static partial Regex StepLine();

    [GeneratedRegex(@"^(\d|[½¼¾⅓⅔])")]
    private static partial Regex QuantityLine();

    [GeneratedRegex(@"^(?<label>\d+ [^|]+?) \| ")]
    private static partial Regex IngredientLine();

    [GeneratedRegex(@"^Step (?<number>\d+)(?: \([^)]*\))?: (?<text>.*)$")]
    private static partial Regex StepPromptLine();

    [GeneratedRegex(@"\[(?<id>[AT]\d+): [^\]]*\]")]
    private static partial Regex Mark();

    [GeneratedRegex(@"^\d+ (?<name>[^(]+)")]
    private static partial Regex IngredientName();
}
