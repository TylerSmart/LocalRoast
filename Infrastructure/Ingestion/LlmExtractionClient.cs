using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using App.Core.Exceptions;
using App.Core.Ingestion;

namespace App.Infrastructure.Ingestion;

public sealed class LlmExtractionClient(HttpClient httpClient) : ILlmExtractionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
        EffectiveLlmConnectionConfig config,
        int timeoutSeconds = 15,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedBase = NormalizeBaseUrl(config.Provider, config.BaseUrl);
        var sw = Stopwatch.StartNew();

        try
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds))
            );
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                cts.Token
            );

            var models = new List<string>();

            if (config.Provider == LlmProvider.Ollama)
            {
                var requestUri = $"{normalizedBase}/api/tags";
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

                using var response = await httpClient.SendAsync(request, linkedCts.Token);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
                using var doc = JsonDocument.Parse(json);

                if (
                    doc.RootElement.TryGetProperty("models", out var modelsProp)
                    && modelsProp.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var m in modelsProp.EnumerateArray())
                    {
                        if (
                            m.TryGetProperty("name", out var np)
                            && np.ValueKind == JsonValueKind.String
                        )
                        {
                            var name = np.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                models.Add(name);
                            }
                        }
                    }
                }
            }
            else // OpenAiCompatible
            {
                var requestUri = $"{normalizedBase}/models";
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        config.ApiKey.Trim()
                    );
                }

                using var response = await httpClient.SendAsync(request, linkedCts.Token);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
                using var doc = JsonDocument.Parse(json);

                if (
                    doc.RootElement.TryGetProperty("data", out var dataProp)
                    && dataProp.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var m in dataProp.EnumerateArray())
                    {
                        if (
                            m.TryGetProperty("id", out var idProp)
                            && idProp.ValueKind == JsonValueKind.String
                        )
                        {
                            var id = idProp.GetString();
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                models.Add(id);
                            }
                        }
                    }
                }
            }

            sw.Stop();
            models.Sort(StringComparer.OrdinalIgnoreCase);

            return new LlmConnectionTestResultDto(
                IsReachable: true,
                Provider: config.Provider,
                NormalizedBaseUrl: normalizedBase,
                LatencyMs: (int)sw.ElapsedMilliseconds,
                Models: models,
                ErrorMessage: null
            );
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            sw.Stop();
            var msg =
                ex is TaskCanceledException && !cancellationToken.IsCancellationRequested
                    ? $"Connection timed out after {timeoutSeconds}s."
                    : ex.Message;

            return new LlmConnectionTestResultDto(
                IsReachable: false,
                Provider: config.Provider,
                NormalizedBaseUrl: normalizedBase,
                LatencyMs: (int)sw.ElapsedMilliseconds,
                Models: [],
                ErrorMessage: msg
            );
        }
    }

    public async Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
        EffectiveLlmConnectionConfig config,
        LlmExtractionContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            throw new LlmHostUnavailableException(
                "No Text LLM model is configured in LLM Settings. Please select a Text Model and retry."
            );
        }

        var normalizedBase = NormalizeBaseUrl(config.Provider, config.BaseUrl);
        var systemPrompt = BuildExtractionSystemPrompt(context);
        var timeout = TimeSpan.FromSeconds(Math.Max(15, config.RequestTimeoutSeconds));

        using var cts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            cts.Token
        );

        string responseContent;

        try
        {
            if (config.Provider == LlmProvider.Ollama)
            {
                var payload = new
                {
                    model = config.TextModel.Trim(),
                    stream = false,
                    keep_alive = string.IsNullOrWhiteSpace(config.OllamaKeepAlive)
                        ? "5m"
                        : config.OllamaKeepAlive.Trim(),
                    options = new { temperature = 0.1 },
                    format = GetRecipeExtractionJsonSchema(),
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = context.SourceContent },
                    },
                };

                var requestJson = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{normalizedBase}/api/chat"
                )
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
                };

                using var response = await httpClient.SendAsync(request, linkedCts.Token);
                CheckTransientStatus(response);
                response.EnsureSuccessStatusCode();

                var rawResponse = await response.Content.ReadAsStringAsync(linkedCts.Token);
                using var doc = JsonDocument.Parse(rawResponse);
                responseContent =
                    doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            else // OpenAiCompatible
            {
                var schemaString = JsonSerializer.Serialize(GetRecipeExtractionJsonSchema());
                var systemWithSchema =
                    $"{systemPrompt}\n\nStrict Output Rule: You MUST respond strictly in valid JSON matching this schema:\n{schemaString}";

                var payload = new
                {
                    model = config.TextModel.Trim(),
                    stream = false,
                    temperature = 0.1,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = systemWithSchema },
                        new { role = "user", content = context.SourceContent },
                    },
                };

                var requestJson = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{normalizedBase}/chat/completions"
                )
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
                };

                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        config.ApiKey.Trim()
                    );
                }

                using var response = await httpClient.SendAsync(request, linkedCts.Token);
                CheckTransientStatus(response);
                response.EnsureSuccessStatusCode();

                var rawResponse = await response.Content.ReadAsStringAsync(linkedCts.Token);
                using var doc = JsonDocument.Parse(rawResponse);
                responseContent =
                    doc.RootElement.GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString()
                    ?? "";
            }
        }
        catch (HttpRequestException ex)
        {
            throw new LlmHostUnavailableException($"LLM host connection failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmHostUnavailableException(
                $"LLM extraction request timed out after {config.RequestTimeoutSeconds}s.",
                ex
            );
        }

        var cleanedJson = CleanMarkdownJsonFences(responseContent);

        try
        {
            var result = JsonSerializer.Deserialize<LlmExtractedRecipePayload>(
                cleanedJson,
                JsonOptions
            );
            if (result is null || string.IsNullOrWhiteSpace(result.Title))
            {
                throw new DomainValidationException(
                    "Extracted recipe output was empty or missing a title."
                );
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new DomainValidationException(
                $"Failed to parse recipe extraction JSON output from LLM: {ex.Message}"
            );
        }
    }

    private static void CheckTransientStatus(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code is 408 or 429 or 502 or 503 or 504)
        {
            throw new LlmHostUnavailableException(
                $"LLM host returned temporary error HTTP {code} ({response.ReasonPhrase})."
            );
        }
    }

    private static string NormalizeBaseUrl(LlmProvider provider, string baseUrl)
    {
        if (
            string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new DomainValidationException("BaseUrl must be a valid http or https URL.");
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');

        if (provider == LlmProvider.OpenAiCompatible)
        {
            if (!trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                trimmed += "/v1";
            }
        }

        return trimmed;
    }

    private static string CleanMarkdownJsonFences(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }
        else if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed[3..].Trim();
        }

        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed[..^3].Trim();
        }

        return trimmed;
    }

    private static string BuildExtractionSystemPrompt(LlmExtractionContext context)
    {
        var aisleList =
            context.AvailableAisleNames.Count > 0
                ? string.Join(", ", context.AvailableAisleNames)
                : "Produce, Bakery, Deli, Meat & Seafood, Dairy & Eggs, Canned & Jarred, Dry Goods & Pasta, International, Spices & Baking, Frozen, Beverages, Household, Other";

        var tagList =
            context.AvailableTagNames.Count > 0
                ? string.Join(", ", context.AvailableTagNames)
                : "Main, Side, Breakfast, Lunch, Dessert, Appetizer, Vegetarian, Gluten-Free, Sheet Pan, One-Pot";

        return $$"""
You are an expert culinary recipe extractor and normalizer.
Your goal is to parse recipe text, HTML, or notes into a structured JSON recipe format.

Rules:
1. Extract Title, Description, BaseServings (numeric count, e.g. 4), YieldDisplay (e.g. "12 muffins" or "4 servings"), PrepTimeMinutes, CookTimeMinutes, TotalTimeMinutes, and Difficulty ("Easy", "Medium", or "Project").
2. SourceName & SourceAuthor: extract publication/blog name and author if present.
3. Ingredients:
   - "sectionName": optional group name (e.g. "For the Marinade", "For the Sauce").
   - "quantity": numeric decimal or null (e.g. 1.5).
   - "maxQuantity": upper bound if a range was specified (e.g. 2.0 for "1.5 to 2 tbsp"), else null.
   - "unit": normalized unit code (tsp, tbsp, fl_oz, cup, pt, qt, gal, ml, l, oz, lb, g, kg, pinch, dash, clove, can, bunch, slice, whole, or custom unit name).
   - "canonicalFoodName": the singular canonical ingredient name without preparation or quantities (e.g. "yellow onion", "garlic", "chicken thigh", "olive oil").
   - "displayName": recipe display name as written (e.g. "extra-virgin olive oil").
   - "preparationNote": preparation instructions (e.g. "finely diced", "room temperature").
   - "isOptional": boolean flag.
   - "originalRawText": verbatim line as originally written.
   - "suggestedAisleName": choose the best matching grocery aisle from: [{{aisleList}}].
4. Steps:
   - "sectionName": optional section header (e.g. "Prepare the Marinade").
   - "instruction": full clear step instruction text.
   - "ingredientIndices": 0-based array of indices of the ingredients used in this step.
   - "timers": list of timers for any duration in this step:
     - "durationSeconds": integer seconds (e.g. 15 minutes = 900, 1 hour = 3600).
     - "label": short label (e.g. "Simmer", "Bake", "Rest").
5. SuggestedTagNames: choose applicable tags from: [{{tagList}}].
6. ConfidenceScore: decimal number between 0.0 and 1.0 representing your extraction confidence.
""";
    }

    private static object GetRecipeExtractionJsonSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string" },
                description = new { type = "string" },
                baseServings = new { type = "number" },
                yieldDisplay = new { type = "string" },
                prepTimeMinutes = new { type = "integer" },
                cookTimeMinutes = new { type = "integer" },
                totalTimeMinutes = new { type = "integer" },
                difficulty = new { type = "string" },
                sourceName = new { type = "string" },
                sourceAuthor = new { type = "string" },
                confidenceScore = new { type = "number" },
                suggestedTagNames = new { type = "array", items = new { type = "string" } },
                ingredients = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            sectionName = new { type = "string" },
                            quantity = new { type = "number" },
                            maxQuantity = new { type = "number" },
                            unit = new { type = "string" },
                            canonicalFoodName = new { type = "string" },
                            displayName = new { type = "string" },
                            preparationNote = new { type = "string" },
                            isOptional = new { type = "boolean" },
                            originalRawText = new { type = "string" },
                            suggestedAisleName = new { type = "string" },
                            suggestedIsPantryStaple = new { type = "boolean" },
                        },
                        required = new[] { "displayName" },
                    },
                },
                steps = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            sectionName = new { type = "string" },
                            instruction = new { type = "string" },
                            ingredientIndices = new
                            {
                                type = "array",
                                items = new { type = "integer" },
                            },
                            timers = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        durationSeconds = new { type = "integer" },
                                        label = new { type = "string" },
                                    },
                                    required = new[] { "durationSeconds", "label" },
                                },
                            },
                        },
                        required = new[] { "instruction" },
                    },
                },
            },
            required = new[] { "title", "ingredients", "steps" },
        };
    }
}
