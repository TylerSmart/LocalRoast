using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using App.Core.Exceptions;
using App.Core.Ingestion;

namespace App.Infrastructure.Ingestion;

public sealed class LlmExtractionClient(HttpClient httpClient) : ILlmExtractionClient
{
    private readonly bool _ = ConfigureHttpClient(httpClient);

    private static bool ConfigureHttpClient(HttpClient client)
    {
        try
        {
            if (client.Timeout != Timeout.InfiniteTimeSpan)
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            }
        }
        catch (InvalidOperationException)
        {
            // Ignore if HttpClient was already started
        }
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new FlexibleNullableIntJsonConverter(),
            new FlexibleIntJsonConverter(),
            new FlexibleNullableDecimalJsonConverter(),
        },
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

        var systemPrompt = BuildExtractionSystemPrompt(context);
        var responseContent = await SendStructuredTextChatAsync(
            config,
            "recipe_extraction",
            GetRecipeExtractionJsonSchema(),
            systemPrompt,
            context.SourceContent,
            temperature: 0.1,
            cancellationToken
        );

        var cleanedJson = CleanMarkdownJsonFences(responseContent);

        try
        {
            return DeserializeRecipeWithRepair(cleanedJson);
        }
        catch (JsonException ex)
        {
            throw new DomainValidationException(
                $"Failed to parse recipe extraction JSON output from LLM: {ex.Message}"
            );
        }
    }

    public async Task<LlmSubstitutionPayload> SuggestIngredientSubstitutionsAsync(
        EffectiveLlmConnectionConfig config,
        LlmSubstitutionContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            throw new LlmHostUnavailableException(
                "No Text LLM model is configured in LLM Settings. Please select a Text Model and retry."
            );
        }

        var systemPrompt = BuildSubstitutionSystemPrompt();
        var userPrompt = BuildSubstitutionUserPrompt(context);

        var responseContent = await SendStructuredTextChatAsync(
            config,
            "ingredient_substitutions",
            GetIngredientSubstitutionJsonSchema(),
            systemPrompt,
            userPrompt,
            temperature: 0.2,
            cancellationToken
        );

        var cleanedJson = CleanMarkdownJsonFences(responseContent);

        try
        {
            var result = JsonSerializer.Deserialize<LlmSubstitutionPayload>(
                cleanedJson,
                JsonOptions
            );
            if (result is null || result.Substitutions is null || result.Substitutions.Count == 0)
            {
                throw new DomainValidationException("Ingredient substitution output was empty.");
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new DomainValidationException(
                $"Failed to parse ingredient substitution JSON output from LLM: {ex.Message}"
            );
        }
    }

    public async Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
        EffectiveLlmConnectionConfig config,
        LlmDraftMealPlanContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(config.TextModel))
        {
            throw new LlmHostUnavailableException(
                "No Text LLM model is configured in LLM Settings. Please select a Text Model and retry."
            );
        }

        var systemPrompt = BuildDraftMealPlanSystemPrompt();
        var userPrompt = BuildDraftMealPlanUserPrompt(context);

        var responseContent = await SendStructuredTextChatAsync(
            config,
            "draft_meal_plan",
            GetDraftMealPlanJsonSchema(),
            systemPrompt,
            userPrompt,
            temperature: 0.2,
            cancellationToken
        );

        var cleanedJson = CleanMarkdownJsonFences(responseContent);

        try
        {
            var result = JsonSerializer.Deserialize<LlmDraftMealPlanPayload>(
                cleanedJson,
                JsonOptions
            );
            if (result is null || result.Proposals is null || result.Proposals.Count == 0)
            {
                throw new DomainValidationException("Draft meal plan output was empty.");
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new DomainValidationException(
                $"Failed to parse draft meal plan JSON output from LLM: {ex.Message}"
            );
        }
    }

    private async Task<string> SendStructuredTextChatAsync(
        EffectiveLlmConnectionConfig config,
        string schemaName,
        object jsonSchema,
        string systemPrompt,
        string userPrompt,
        double temperature,
        CancellationToken cancellationToken
    )
    {
        var normalizedBase = NormalizeBaseUrl(config.Provider, config.BaseUrl);
        var timeout = TimeSpan.FromSeconds(Math.Max(15, config.RequestTimeoutSeconds));
        var sw = Stopwatch.StartNew();

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
                    model = config.TextModel!.Trim(),
                    stream = false,
                    think = config.EnableThinking,
                    keep_alive = string.IsNullOrWhiteSpace(config.OllamaKeepAlive)
                        ? "5m"
                        : config.OllamaKeepAlive.Trim(),
                    options = new
                    {
                        temperature,
                        num_predict = 16384,
                        repeat_penalty = 1.1,
                    },
                    format = jsonSchema,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt },
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
                await EnsureSuccessfulLlmResponseAsync(response, linkedCts.Token);

                var rawResponse = await response.Content.ReadAsStringAsync(linkedCts.Token);
                responseContent = ExtractOllamaMessageContent(rawResponse);
            }
            else // OpenAiCompatible
            {
                var schemaString = JsonSerializer.Serialize(jsonSchema);
                var systemWithSchema =
                    $"{systemPrompt}\n\nStrict Output Rule: You MUST respond strictly in valid JSON matching this schema:\n{schemaString}";

                var payload = new
                {
                    model = config.TextModel!.Trim(),
                    stream = false,
                    temperature,
                    max_tokens = 16384,
                    response_format = new
                    {
                        type = "json_schema",
                        json_schema = new
                        {
                            name = schemaName,
                            strict = true,
                            schema = jsonSchema,
                        },
                    },
                    messages = new object[]
                    {
                        new { role = "system", content = systemWithSchema },
                        new { role = "user", content = userPrompt },
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
                await EnsureSuccessfulLlmResponseAsync(response, linkedCts.Token);

                var rawResponse = await response.Content.ReadAsStringAsync(linkedCts.Token);
                responseContent = ExtractOpenAiMessageContent(rawResponse);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new LlmHostUnavailableException($"LLM host connection failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            var elapsed = Math.Max(1, (int)sw.Elapsed.TotalSeconds);
            throw new LlmHostUnavailableException(
                $"LLM extraction request timed out after {elapsed}s (configured timeout: {config.RequestTimeoutSeconds}s).",
                ex
            );
        }

        return responseContent;
    }

    public async Task<LlmExtractedRecipePayload> ExtractRecipeFromImagesAsync(
        EffectiveLlmConnectionConfig config,
        LlmVisionExtractionContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(config.VisionModel))
        {
            throw new LlmHostUnavailableException(
                "No Vision LLM model is configured in LLM Settings. Please select a Vision Model and retry."
            );
        }

        if (context.Images is null || context.Images.Count == 0)
        {
            throw new DomainValidationException("No images provided for multimodal extraction.");
        }

        var normalizedBase = NormalizeBaseUrl(config.Provider, config.BaseUrl);
        var systemPrompt = BuildVisionExtractionSystemPrompt(context);
        var timeout = TimeSpan.FromSeconds(Math.Max(20, config.RequestTimeoutSeconds));
        var sw = Stopwatch.StartNew();

        using var cts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            cts.Token
        );

        var userPromptText =
            "Transcribe and structure the culinary recipe shown across these page images into structured recipe JSON.";
        if (!string.IsNullOrWhiteSpace(context.SupplementalHintText))
        {
            userPromptText +=
                $"\nSupplemental Context/Hints: {context.SupplementalHintText.Trim()}";
        }

        string responseContent;

        try
        {
            if (config.Provider == LlmProvider.Ollama)
            {
                var payload = new
                {
                    model = config.VisionModel.Trim(),
                    stream = false,
                    think = config.EnableThinking,
                    keep_alive = string.IsNullOrWhiteSpace(config.OllamaKeepAlive)
                        ? "5m"
                        : config.OllamaKeepAlive.Trim(),
                    options = new
                    {
                        temperature = 0.1,
                        num_predict = 16384,
                        repeat_penalty = 1.1,
                    },
                    format = GetRecipeExtractionJsonSchema(),
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new
                        {
                            role = "user",
                            content = userPromptText,
                            images = context.Images.Select(i => i.Base64Data).ToArray(),
                        },
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
                await EnsureSuccessfulLlmResponseAsync(response, linkedCts.Token);

                var rawResponse = await response.Content.ReadAsStringAsync(linkedCts.Token);
                responseContent = ExtractOllamaMessageContent(rawResponse);
            }
            else // OpenAiCompatible
            {
                var schemaObj = GetRecipeExtractionJsonSchema();
                var schemaString = JsonSerializer.Serialize(schemaObj);
                var systemWithSchema =
                    $"{systemPrompt}\n\nStrict Output Rule: You MUST respond strictly in valid JSON matching this schema:\n{schemaString}";

                var contentList = new List<object> { new { type = "text", text = userPromptText } };

                foreach (var img in context.Images)
                {
                    contentList.Add(
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = $"data:{img.MimeType};base64,{img.Base64Data}",
                            },
                        }
                    );
                }

                var payload = new
                {
                    model = config.VisionModel.Trim(),
                    stream = false,
                    temperature = 0.1,
                    max_tokens = 16384,
                    response_format = new
                    {
                        type = "json_schema",
                        json_schema = new
                        {
                            name = "recipe_extraction",
                            strict = true,
                            schema = schemaObj,
                        },
                    },
                    messages = new object[]
                    {
                        new { role = "system", content = systemWithSchema },
                        new { role = "user", content = contentList },
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
                await EnsureSuccessfulLlmResponseAsync(response, linkedCts.Token);

                var rawResponse = await response.Content.ReadAsStringAsync(linkedCts.Token);
                responseContent = ExtractOpenAiMessageContent(rawResponse);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new LlmHostUnavailableException($"LLM host connection failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            var elapsed = Math.Max(1, (int)sw.Elapsed.TotalSeconds);
            throw new LlmHostUnavailableException(
                $"LLM vision extraction request timed out after {elapsed}s (configured timeout: {config.RequestTimeoutSeconds}s).",
                ex
            );
        }

        var cleanedJson = CleanMarkdownJsonFences(responseContent);

        try
        {
            return DeserializeRecipeWithRepair(cleanedJson);
        }
        catch (JsonException ex)
        {
            throw new DomainValidationException(
                $"Failed to parse recipe extraction JSON output from LLM: {ex.Message}"
            );
        }
    }

    private static async Task EnsureSuccessfulLlmResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        CheckTransientStatus(response);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = !string.IsNullOrWhiteSpace(errorBody) ? $": {errorBody}" : "";
            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}){detail}"
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

    private static string ExtractOllamaMessageContent(string rawResponse)
    {
        using var doc = JsonDocument.Parse(rawResponse);
        var root = doc.RootElement;

        string? content = null;
        string? thinking = null;

        if (
            root.TryGetProperty("message", out var msgProp)
            && msgProp.ValueKind == JsonValueKind.Object
        )
        {
            if (
                msgProp.TryGetProperty("content", out var contentProp)
                && contentProp.ValueKind == JsonValueKind.String
            )
            {
                content = contentProp.GetString();
            }

            if (
                msgProp.TryGetProperty("thinking", out var thinkingProp)
                && thinkingProp.ValueKind == JsonValueKind.String
            )
            {
                thinking = thinkingProp.GetString();
            }
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var doneReason =
            root.TryGetProperty("done_reason", out var doneReasonProp)
            && doneReasonProp.ValueKind == JsonValueKind.String
                ? doneReasonProp.GetString()
                : null;

        if (string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(
                "LLM output reached the maximum token limit before completing the structured JSON response (done_reason: length). Increase context/output limits or use an instruct/non-thinking model."
            );
        }

        if (!string.IsNullOrWhiteSpace(thinking))
        {
            throw new DomainValidationException(
                "LLM returned internal reasoning tokens without a final structured JSON response."
            );
        }

        return content ?? "";
    }

    private static string ExtractOpenAiMessageContent(string rawResponse)
    {
        using var doc = JsonDocument.Parse(rawResponse);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var content =
            choice.TryGetProperty("message", out var msgProp)
            && msgProp.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.String
                ? contentProp.GetString()
                : null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var finishReason =
            choice.TryGetProperty("finish_reason", out var finishProp)
            && finishProp.ValueKind == JsonValueKind.String
                ? finishProp.GetString()
                : null;

        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainValidationException(
                "LLM output reached the maximum token limit before completing the structured JSON response (finish_reason: length)."
            );
        }

        return content ?? "";
    }

    private static LlmExtractedRecipePayload DeserializeRecipeWithRepair(string cleanedJson)
    {
        try
        {
            var result = JsonSerializer.Deserialize<LlmExtractedRecipePayload>(
                cleanedJson,
                JsonOptions
            );
            if (result is not null && !string.IsNullOrWhiteSpace(result.Title))
            {
                return result;
            }
        }
        catch (JsonException)
        {
            // Fall through to truncation repair
        }

        if (JsonTruncationRepair.TryRepair(cleanedJson, out var repairedJson))
        {
            try
            {
                var repairedResult = JsonSerializer.Deserialize<LlmExtractedRecipePayload>(
                    repairedJson,
                    JsonOptions
                );
                if (repairedResult is not null && !string.IsNullOrWhiteSpace(repairedResult.Title))
                {
                    return repairedResult;
                }
            }
            catch
            {
                // Fall through to re-throw original JsonException
            }
        }

        var finalResult = JsonSerializer.Deserialize<LlmExtractedRecipePayload>(
            cleanedJson,
            JsonOptions
        );
        if (finalResult is null || string.IsNullOrWhiteSpace(finalResult.Title))
        {
            throw new DomainValidationException(
                "Extracted recipe output was empty or missing a title."
            );
        }
        return finalResult;
    }

    private static string CleanMarkdownJsonFences(string content)
    {
        var trimmed = System
            .Text.RegularExpressions.Regex.Replace(
                content.Trim(),
                @"<think>[\s\S]*?</think>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            )
            .Trim();
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
        var tagRule =
            context.AvailableTagNames.Count > 0
                ? $"SuggestedTagNames: assign 2 to 6 concise, reusable culinary tags (course like \"Dinner\", \"Side\", \"Dessert\", \"Breakfast\"; cuisine like \"Italian\", \"Mexican\", \"Chinese\", \"American\"; dietary like \"Vegetarian\", \"Gluten-Free\"; or method/style like \"Grill\", \"Slow Cooker\", \"Sheet Pan\", \"One-Pot\", \"Baking\", \"Weeknight\"). Reuse applicable tags from Existing Tags: [{string.Join(", ", context.AvailableTagNames)}], and propose new concise Title-Case tags when needed. Avoid overly specific single-recipe tags."
                : "SuggestedTagNames: propose 2 to 6 concise, reusable Title-Case culinary tags (course like \"Dinner\", \"Side\", \"Dessert\", \"Breakfast\"; cuisine like \"Italian\", \"Mexican\", \"Chinese\", \"American\"; dietary like \"Vegetarian\", \"Gluten-Free\"; or method/style like \"Grill\", \"Slow Cooker\", \"Sheet Pan\", \"One-Pot\", \"Baking\", \"Weeknight\"). Avoid overly specific single-recipe tags.";

        var foodCatalogSection = context.AvailableFoodNames is { Count: > 0 }
            ? $"\n\nExisting Food Catalog (reuse exact name in \"canonicalFoodName\" whenever semantically identical):\n[{string.Join(", ", context.AvailableFoodNames)}]"
            : "";

        return $$"""
You are an expert culinary recipe extractor and normalizer.
Your goal is to parse recipe text, HTML, or notes into a structured JSON recipe format.

Rules:
1. Extract Title, BaseServings (numeric count, e.g. 4), YieldDisplay (e.g. "12 muffins" or "4 servings"), PrepTimeMinutes, CookTimeMinutes, TotalTimeMinutes, and Difficulty ("Easy", "Medium", or "Project").
2. Description: Extract the recipe's intro/headnote description VERBATIM as written in the source text. Do NOT summarize, rewrite, condense, or invent your own description. Preserve the author's original headnote text and explanation verbatim. Strip out boilerplate section headers like "WHY THIS RECIPE WORKS", "Why This Recipe Works", or "Why It Works" at the start of the description so it begins directly with the headnote narrative. If no headnote or intro description exists in the source text, set description to null.
3. SourceName & SourceAuthor: extract publication/blog name and author if present. SourceBookTitle & SourceBookPage: extract cookbook title and page number if visible.
4. Ingredients:
   - "sectionName": optional group name (e.g. "For the Marinade", "For the Sauce").
   - "quantity": numeric decimal greater than 0, or null if unmeasured / to taste (e.g. 1.5). NEVER output 0 or negative numbers.
   - "maxQuantity": upper bound if a range was specified (e.g. 2.0 for "1.5 to 2 tbsp"), else null.
   - "unit": normalized unit code (tsp, tbsp, fl_oz, cup, pt, qt, gal, ml, l, oz, lb, g, kg, pinch, dash, clove, can, bunch, slice, whole, or custom unit name).
   - "canonicalFoodName": the singular canonical ingredient name without preparation, brand, or quantities (e.g. "yellow onion", "garlic", "chicken thigh", "all-purpose flour", "unsalted butter"). When an ingredient matches an item in Existing Food Catalog, reuse that exact canonical name. NEVER output "null", "none", or placeholder words (leave empty or null if unclassified).
   - "displayName": recipe display name as written without leading quantity/unit (e.g. "extra-virgin olive oil").
   - "preparationNote": preparation instructions or parenthetical weights (e.g. "finely diced", "room temperature", "7 1/2 ounces").
   - "isOptional": boolean flag.
   - "originalRawText": verbatim line as originally written.
   - "suggestedIsPantryStaple": boolean flag; true ONLY for basic everyday household staples typically kept on hand (water, salt, black pepper, cooking oils, basic vinegars, all-purpose flour, sugar, baking powder/soda, dried spices/herbs, soy sauce), false for fresh produce, meats, dairy, canned goods, and specialty items.
5. Steps:
   - "sectionName": optional section header (e.g. "Prepare the Marinade").
   - "instruction": full clear step instruction text.
   - "ingredientIndices": 0-based array of indices of the ingredients used in this step.
   - "timers": list of timers for any duration in this step:
     - "durationSeconds": integer seconds (e.g. 15 minutes = 900, 1 hour = 3600).
     - "label": short descriptive label (e.g. "Simmer", "Bake", "Rest"). If a step has multiple timers, assign distinct descriptive labels (e.g. "Initial Simmer", "Covered Simmer", "Rest") rather than duplicate names.
6. {{tagRule}}
7. ConfidenceScore: decimal number between 0.0 and 1.0 representing your extraction confidence.
8. Output ONLY a single valid JSON object matching the requested schema with no conversational text.{{foodCatalogSection}}
""";
    }

    private static string BuildVisionExtractionSystemPrompt(LlmVisionExtractionContext context)
    {
        var tagRule =
            context.AvailableTagNames.Count > 0
                ? $"SuggestedTagNames: assign 2 to 6 concise, reusable culinary tags (course like \"Dinner\", \"Side\", \"Dessert\", \"Breakfast\"; cuisine like \"Italian\", \"Mexican\", \"Chinese\", \"American\"; dietary like \"Vegetarian\", \"Gluten-Free\"; or method/style like \"Grill\", \"Slow Cooker\", \"Sheet Pan\", \"One-Pot\", \"Baking\", \"Weeknight\"). Reuse applicable tags from Existing Tags: [{string.Join(", ", context.AvailableTagNames)}], and propose new concise Title-Case tags when needed. Avoid overly specific single-recipe tags."
                : "SuggestedTagNames: propose 2 to 6 concise, reusable Title-Case culinary tags (course like \"Dinner\", \"Side\", \"Dessert\", \"Breakfast\"; cuisine like \"Italian\", \"Mexican\", \"Chinese\", \"American\"; dietary like \"Vegetarian\", \"Gluten-Free\"; or method/style like \"Grill\", \"Slow Cooker\", \"Sheet Pan\", \"One-Pot\", \"Baking\", \"Weeknight\"). Avoid overly specific single-recipe tags.";

        var foodCatalogSection = context.AvailableFoodNames is { Count: > 0 }
            ? $"\n\nExisting Food Catalog (reuse exact name in \"canonicalFoodName\" whenever semantically identical):\n[{string.Join(", ", context.AvailableFoodNames)}]"
            : "";

        return $$"""
You are an expert culinary vision OCR transcriber and recipe extractor.
Your goal is to transcribe multi-page cookbook scans, photos, or handwritten recipe cards into a clean structured JSON recipe format.

Rules:
1. Carefully transcribe the recipe across all provided page images in sequential page order.
2. Extract Title, BaseServings (numeric count, e.g. 4), YieldDisplay (e.g. "12 muffins" or "4 servings"), PrepTimeMinutes, CookTimeMinutes, TotalTimeMinutes, and Difficulty ("Easy", "Medium", or "Project").
3. Description: Transcribe the recipe's intro/headnote description VERBATIM from the page text as printed. Do NOT summarize, rewrite, condense, or invent your own description. Transcribe the author's original headnote text and explanation verbatim across all paragraphs. Strip out boilerplate section headers like "WHY THIS RECIPE WORKS", "Why This Recipe Works", or "Why It Works" at the start of the description so it begins directly with the headnote narrative. If no headnote or intro description is printed on the page(s), set description to null.
4. SourceBookTitle & SourceBookPage: if visible in headers, footers, or margins, extract the cookbook title and page number. SourceAuthor / SourceName: extract author or chef attribution if printed.
5. Ingredients:
   - "sectionName": optional group name (e.g. "For the Marinade", "For the Sauce").
   - "quantity": numeric decimal greater than 0, or null if unmeasured / to taste (e.g. 1.5). NEVER output 0 or negative numbers.
   - "maxQuantity": upper bound if a range was specified (e.g. 2.0 for "1.5 to 2 tbsp"), else null.
   - "unit": normalized unit code (tsp, tbsp, fl_oz, cup, pt, qt, gal, ml, l, oz, lb, g, kg, pinch, dash, clove, can, bunch, slice, whole, or custom unit name).
   - "canonicalFoodName": the singular canonical ingredient name without preparation, brand, or quantities (e.g. "yellow onion", "garlic", "chicken thigh", "all-purpose flour", "unsalted butter"). When an ingredient matches an item in Existing Food Catalog, reuse that exact canonical name. NEVER output "null", "none", or placeholder words (leave empty or null if unclassified).
   - "displayName": recipe display name as written without leading quantity/unit (e.g. "extra-virgin olive oil").
   - "preparationNote": preparation instructions or parenthetical weights (e.g. "finely diced", "room temperature", "7 1/2 ounces").
   - "isOptional": boolean flag.
   - "originalRawText": verbatim line as originally printed.
   - "suggestedIsPantryStaple": boolean flag; true ONLY for basic everyday household staples typically kept on hand (water, salt, black pepper, cooking oils, basic vinegars, all-purpose flour, sugar, baking powder/soda, dried spices/herbs, soy sauce), false for fresh produce, meats, dairy, canned goods, and specialty items.
6. Steps:
   - "sectionName": optional section header (e.g. "Prepare the Marinade").
   - "instruction": full clear step instruction text.
   - "ingredientIndices": 0-based array of indices of the ingredients used in this step.
   - "timers": list of timers for any duration in this step:
     - "durationSeconds": integer seconds (e.g. 15 minutes = 900, 1 hour = 3600).
     - "label": short descriptive label (e.g. "Simmer", "Bake", "Rest"). If a step has multiple timers, assign distinct descriptive labels (e.g. "Initial Simmer", "Covered Simmer", "Rest") rather than duplicate names.
7. {{tagRule}}
8. ConfidenceScore: decimal number between 0.0 and 1.0 representing your transcription/extraction confidence.
9. Output ONLY a single valid JSON object matching the requested schema with no conversational text.{{foodCatalogSection}}
""";
    }

    private static string BuildSubstitutionSystemPrompt()
    {
        return """
You are an expert culinary chef and recipe consultant.
Your goal is to suggest practical, high-quality ingredient substitutions for a specific ingredient in a home recipe.

Rules:
1. Provide 2 to 3 realistic, culinary-sound substitutions tailored specifically to how the ingredient functions in the recipe.
2. For each substitution:
   - "label": Short name of the substitute (e.g. "Lemon Juice + Milk", "Greek Yogurt", "Applesauce").
   - "ratioAndAmount": The exact scaled measurement to use for the target amount (e.g. "1 tbsp lemon juice + whole milk to measure 1 cup").
   - "culinaryNotes": How to mix, prepare, or incorporate this substitute.
   - "flavorOrTextureImpact": Any subtle change in flavor, acidity, moisture, or texture the cook should expect.
3. If dietary or pantry constraints are specified, strictly respect them.
""";
    }

    private static string BuildSubstitutionUserPrompt(LlmSubstitutionContext context)
    {
        return $$"""
Recipe: {{context.RecipeTitle}}
Description: {{context.RecipeDescription ?? "N/A"}}
Target Servings: {{context.TargetServings}}
Ingredient to Substitute: {{context.TargetIngredientName}} (Scaled Amount: {{context.ScaledAmountDisplay}}{{(
                !string.IsNullOrWhiteSpace(context.PreparationNote)
                    ? $", {context.PreparationNote}"
                    : ""
            )}})
Other Ingredients in Dish: {{(
                context.OtherRecipeIngredients.Count > 0
                    ? string.Join(", ", context.OtherRecipeIngredients)
                    : "None listed"
            )}}
Relevant Cooking Steps:
{{(
                context.RelevantStepInstructions.Count > 0
                    ? string.Join(
                        "\n",
                        context.RelevantStepInstructions.Select((s, i) => $"{i + 1}. {s}")
                    )
                    : "N/A"
            )}}
{{(
                !string.IsNullOrWhiteSpace(context.DietaryOrPantryNote)
                    ? $"Dietary / Pantry Constraint: {context.DietaryOrPantryNote.Trim()}"
                    : ""
            )}}
""";
    }

    private static string BuildDraftMealPlanSystemPrompt()
    {
        return """
You are an expert household meal planning assistant.
Your goal is to suggest a cohesive, satisfying weekly meal plan for a household strictly using recipes from their personal saved cookbook catalog.

Strict Rules:
1. You MUST ONLY propose recipes from the provided Household Cookbook Catalog using their exact integer "catalogIndex".
2. NEVER invent, hallucinate, or suggest any recipe that is not listed in the catalog.
3. Respect the user's prompt constraints (e.g. max time, vegetarian, high-rated, specific proteins, busy weeknights).
4. For each proposal:
   - "catalogIndex": The integer index of the recipe from the catalog (1-based).
   - "recipeTitle": Title of the recipe matching the catalog.
   - "dayOffset": Day of the week offset: 0 for Sunday, 1 for Monday, ..., 6 for Saturday (or null if placed on-deck).
   - "slot": Meal slot ("Breakfast", "Lunch", "Dinner", or "SnackPrep").
   - "isOnDeck": True if placed in the flexible weekly On-Deck tray, otherwise false.
   - "targetServings": Servings to cook (defaults to the recipe's base servings).
   - "rationale": Short explanation of why this recipe fits the plan and prompt.
""";
    }

    private static string BuildDraftMealPlanUserPrompt(LlmDraftMealPlanContext context)
    {
        var catalogLines = context.CookbookCatalog.Select(c =>
            $"[{c.CatalogIndex}] {c.Title} | BaseServings: {c.BaseServings} | Time: {(c.TotalTimeMinutes.HasValue ? $"{c.TotalTimeMinutes}m" : "N/A")} | Difficulty: {c.Difficulty ?? "N/A"} | Rating: {(c.AverageRating.HasValue ? $"★{c.AverageRating:F1}" : "Unrated")} | Cooked: {c.TimesCooked}x (Last: {(c.DaysSinceLastCooked.HasValue ? $"{c.DaysSinceLastCooked}d ago" : "Never")}) | Tags: {(c.Tags.Count > 0 ? string.Join(", ", c.Tags) : "None")} | Ingredients: {(c.KeyIngredients.Count > 0 ? string.Join(", ", c.KeyIngredients) : "N/A")}"
        );

        return $$"""
Target Week: Sunday {{context.WeekStartDate:yyyy-MM-dd}}
User Request / Constraints: {{context.UserPrompt}}
Requested Number of Meals: {{context.RequestedMealCount}}
Default Meal Slot: {{context.DefaultSlotName}}
Place in On-Deck Tray by Default: {{context.PlaceOnDeck}}
Existing Scheduled Meals This Week:
{{(
                context.ExistingWeekScheduleLines.Count > 0
                    ? string.Join("\n", context.ExistingWeekScheduleLines)
                    : "None scheduled yet"
            )}}

Household Cookbook Catalog:
{{string.Join("\n", catalogLines)}}
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
                description = new
                {
                    type = "string",
                    description = "Verbatim headnote or intro description from the source without rewriting or summarizing. Omit 'WHY THIS RECIPE WORKS' headers.",
                },
                baseServings = new { type = "number" },
                yieldDisplay = new { type = "string" },
                prepTimeMinutes = new { type = "integer" },
                cookTimeMinutes = new { type = "integer" },
                totalTimeMinutes = new { type = "integer" },
                difficulty = new { type = "string" },
                sourceName = new { type = "string" },
                sourceAuthor = new { type = "string" },
                sourceBookTitle = new { type = "string" },
                sourceBookPage = new { type = "integer" },
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
            required = new[]
            {
                "title",
                "ingredients",
                "steps",
                "suggestedTagNames",
                "confidenceScore",
            },
        };
    }

    private static object GetIngredientSubstitutionJsonSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                substitutions = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            label = new { type = "string" },
                            ratioAndAmount = new { type = "string" },
                            culinaryNotes = new { type = "string" },
                            flavorOrTextureImpact = new { type = "string" },
                        },
                        required = new[]
                        {
                            "label",
                            "ratioAndAmount",
                            "culinaryNotes",
                            "flavorOrTextureImpact",
                        },
                    },
                },
            },
            required = new[] { "substitutions" },
        };
    }

    private static object GetDraftMealPlanJsonSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                proposals = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            catalogIndex = new { type = "integer" },
                            recipeTitle = new { type = "string" },
                            dayOffset = new { type = "integer" },
                            slot = new { type = "string" },
                            isOnDeck = new { type = "boolean" },
                            targetServings = new { type = "number" },
                            rationale = new { type = "string" },
                        },
                        required = new[] { "catalogIndex", "rationale" },
                    },
                },
            },
            required = new[] { "proposals" },
        };
    }
}
