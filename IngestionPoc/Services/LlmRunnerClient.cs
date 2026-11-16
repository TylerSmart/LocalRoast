using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using App.Core.Ingestion;

namespace IngestionPoc.Services;

public sealed record PocLlmConfig(
    LlmProvider Provider,
    string BaseUrl,
    string? ApiKey,
    string TextModel,
    string? VisionModel,
    bool EnableThinking,
    bool? AuxThinkingOverride,
    double? Temperature,
    double? TopP,
    int? TopK,
    int ContextWindowTokens,
    int RequestTimeoutSeconds,
    int? OllamaGpuLayers,
    bool OllamaCpuOnly,
    string OllamaKeepAlive = "5m"
);

public sealed record LlmExchangeRecord(
    int Attempt,
    bool ThinkingUsed,
    long ElapsedMs,
    int HttpStatus,
    JsonNode RequestPayload,
    string RawResponseBody,
    string? ExtractedContent,
    string? ExtractedThinking,
    int? PromptEvalCount,
    int? EvalCount,
    string? DoneReason,
    string? Error
);

public sealed record LlmCallResult<T>(
    T? Parsed,
    string? AssistantContent,
    long TotalElapsedMs,
    string? ParseError,
    IReadOnlyList<LlmExchangeRecord> Exchanges
);

public sealed class LlmRunnerClient(HttpClient httpClient)
{
    private static readonly SemaphoreSlim SerialGate = new(1, 1);

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

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public async Task<LlmCallResult<T>> CompleteStructuredAsync<T>(
        PocLlmConfig config,
        string schemaName,
        object jsonSchema,
        string systemPrompt,
        string userMessage,
        bool isAuxiliaryStep = false,
        Func<T, string?>? validateRetryFeedback = null,
        int maxCorrectionTurns = 0,
        CancellationToken cancellationToken = default
    )
    {
        var messages = new List<(string Role, string Content)> { ("user", userMessage) };
        return await CompleteStructuredMultiTurnAsync(
            config,
            schemaName,
            jsonSchema,
            systemPrompt,
            messages,
            isAuxiliaryStep,
            validateRetryFeedback,
            maxCorrectionTurns,
            cancellationToken
        );
    }

    public async Task<LlmCallResult<T>> CompleteStructuredMultiTurnAsync<T>(
        PocLlmConfig config,
        string schemaName,
        object jsonSchema,
        string systemPrompt,
        List<(string Role, string Content)> messages,
        bool isAuxiliaryStep = false,
        Func<T, string?>? validateRetryFeedback = null,
        int maxCorrectionTurns = 0,
        CancellationToken cancellationToken = default
    )
    {
        var exchanges = new List<LlmExchangeRecord>();
        var totalSw = Stopwatch.StartNew();
        var useThinking =
            isAuxiliaryStep && config.AuxThinkingOverride.HasValue
                ? config.AuxThinkingOverride.Value
                : config.EnableThinking;

        T? lastParsed = default;
        string? lastContent = null;
        string? lastError = null;

        for (var turn = 0; turn <= maxCorrectionTurns; turn++)
        {
            var exchange = await SendSingleTurnAsync(
                config,
                schemaName,
                jsonSchema,
                systemPrompt,
                messages,
                turn + 1,
                useThinking,
                cancellationToken
            );
            exchanges.Add(exchange);

            // Auto-retry once with thinking disabled if Ollama truncated while thinking
            if (
                exchange.Error is not null
                && string.Equals(exchange.DoneReason, "length", StringComparison.OrdinalIgnoreCase)
                && config.Provider == LlmProvider.Ollama
                && useThinking
            )
            {
                useThinking = false;
                exchange = await SendSingleTurnAsync(
                    config,
                    schemaName,
                    jsonSchema,
                    systemPrompt,
                    messages,
                    turn + 1,
                    enableThinking: false,
                    cancellationToken
                );
                exchanges.Add(exchange);
            }

            if (exchange.Error is not null || string.IsNullOrWhiteSpace(exchange.ExtractedContent))
            {
                lastError = exchange.Error ?? "Empty response content from LLM.";
                if (turn < maxCorrectionTurns)
                {
                    messages.Add(("assistant", exchange.ExtractedContent ?? string.Empty));
                    messages.Add(
                        (
                            "user",
                            $"Your previous response failed ({lastError}). Return valid JSON matching the schema."
                        )
                    );
                    continue;
                }
                break;
            }

            lastContent = CleanMarkdownJsonFences(exchange.ExtractedContent);
            try
            {
                lastParsed = JsonSerializer.Deserialize<T>(lastContent, JsonOptions);
            }
            catch (JsonException ex)
            {
                lastError = $"JSON parse error: {ex.Message}";
                if (turn < maxCorrectionTurns)
                {
                    messages.Add(("assistant", lastContent));
                    messages.Add(
                        (
                            "user",
                            $"Your response was not valid JSON ({ex.Message}). Return only valid JSON matching the schema."
                        )
                    );
                    continue;
                }
                break;
            }

            if (lastParsed is null)
            {
                lastError = "Deserialized payload was null.";
                continue;
            }

            if (validateRetryFeedback is not null)
            {
                var feedback = validateRetryFeedback(lastParsed);
                if (!string.IsNullOrWhiteSpace(feedback))
                {
                    lastError = feedback;
                    if (turn < maxCorrectionTurns)
                    {
                        messages.Add(("assistant", lastContent));
                        messages.Add(("user", feedback));
                        continue;
                    }
                    break;
                }
            }

            totalSw.Stop();
            return new LlmCallResult<T>(
                lastParsed,
                lastContent,
                totalSw.ElapsedMilliseconds,
                null,
                exchanges
            );
        }

        totalSw.Stop();
        return new LlmCallResult<T>(
            lastParsed,
            lastContent,
            totalSw.ElapsedMilliseconds,
            lastError,
            exchanges
        );
    }

    private async Task<LlmExchangeRecord> SendSingleTurnAsync(
        PocLlmConfig config,
        string schemaName,
        object jsonSchema,
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> messages,
        int attempt,
        bool enableThinking,
        CancellationToken cancellationToken
    )
    {
        // Critical hardware constraint for ThinkStationP3: strictly 1 request at a time.
        await SerialGate.WaitAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(15, config.RequestTimeoutSeconds))
            );
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                cts.Token
            );

            if (config.Provider == LlmProvider.Ollama)
            {
                var baseUrl = config.BaseUrl.Trim().TrimEnd('/');
                var fullMessages = new List<object>(messages.Count + 1)
                {
                    new { role = "system", content = systemPrompt },
                };
                foreach (var (role, content) in messages)
                {
                    fullMessages.Add(new { role, content });
                }

                var options = new Dictionary<string, object>
                {
                    ["temperature"] = config.Temperature ?? 0.0,
                    ["num_ctx"] = config.ContextWindowTokens,
                    ["num_predict"] = Math.Max(16384, config.ContextWindowTokens),
                    ["repeat_penalty"] = 1.0,
                };
                if (config.TopP is { } topP)
                {
                    options["top_p"] = topP;
                }
                if (config.TopK is { } topK)
                {
                    options["top_k"] = topK;
                }
                if (config.OllamaCpuOnly)
                {
                    options["num_gpu"] = 0;
                }
                else if (config.OllamaGpuLayers is { } gpuLayers)
                {
                    options["num_gpu"] = gpuLayers;
                }

                var payload = new
                {
                    model = config.TextModel,
                    stream = false,
                    think = enableThinking,
                    keep_alive = config.OllamaKeepAlive,
                    options,
                    format = jsonSchema,
                    messages = fullMessages,
                };

                var requestJson = JsonSerializer.Serialize(payload, RequestJsonOptions);
                var requestNode = JsonNode.Parse(requestJson)!;

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat")
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
                };
                using var resp = await httpClient.SendAsync(req, linkedCts.Token);
                var body = await resp.Content.ReadAsStringAsync(linkedCts.Token);
                sw.Stop();

                if (!resp.IsSuccessStatusCode)
                {
                    return new LlmExchangeRecord(
                        attempt,
                        enableThinking,
                        sw.ElapsedMilliseconds,
                        (int)resp.StatusCode,
                        requestNode,
                        body,
                        null,
                        null,
                        null,
                        null,
                        null,
                        $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 400)}"
                    );
                }

                return ParseOllamaExchange(
                    attempt,
                    enableThinking,
                    sw.ElapsedMilliseconds,
                    (int)resp.StatusCode,
                    requestNode,
                    body
                );
            }
            else
            {
                var baseUrl = NormalizeOpenAiBaseUrl(config.BaseUrl);
                var fullMessages = new List<object>(messages.Count + 1)
                {
                    new { role = "system", content = systemPrompt },
                };
                foreach (var (role, content) in messages)
                {
                    fullMessages.Add(new { role, content });
                }

                var payload = new
                {
                    model = config.TextModel,
                    stream = false,
                    temperature = config.Temperature ?? 0.0,
                    top_p = config.TopP,
                    top_k = config.TopK,
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
                    messages = fullMessages,
                };

                var requestJson = JsonSerializer.Serialize(payload, RequestJsonOptions);
                var requestNode = JsonNode.Parse(requestJson)!;

                using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/chat/completions"
                )
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        config.ApiKey.Trim()
                    );
                }

                using var resp = await httpClient.SendAsync(req, linkedCts.Token);
                var body = await resp.Content.ReadAsStringAsync(linkedCts.Token);
                sw.Stop();

                if (!resp.IsSuccessStatusCode)
                {
                    return new LlmExchangeRecord(
                        attempt,
                        enableThinking,
                        sw.ElapsedMilliseconds,
                        (int)resp.StatusCode,
                        requestNode,
                        body,
                        null,
                        null,
                        null,
                        null,
                        null,
                        $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 400)}"
                    );
                }

                return ParseOpenAiExchange(
                    attempt,
                    enableThinking,
                    sw.ElapsedMilliseconds,
                    (int)resp.StatusCode,
                    requestNode,
                    body
                );
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new LlmExchangeRecord(
                attempt,
                enableThinking,
                sw.ElapsedMilliseconds,
                0,
                new JsonObject(),
                string.Empty,
                null,
                null,
                null,
                null,
                "timeout",
                $"Timed out after {config.RequestTimeoutSeconds}s"
            );
        }
        finally
        {
            SerialGate.Release();
        }
    }

    private static LlmExchangeRecord ParseOllamaExchange(
        int attempt,
        bool thinkingUsed,
        long elapsedMs,
        int status,
        JsonNode requestNode,
        string rawBody
    )
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        string? content = null;
        string? thinking = null;

        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            if (msg.TryGetProperty("content", out var cp) && cp.ValueKind == JsonValueKind.String)
            {
                content = cp.GetString();
            }
            if (msg.TryGetProperty("thinking", out var tp) && tp.ValueKind == JsonValueKind.String)
            {
                thinking = tp.GetString();
            }
        }

        var doneReason =
            root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String
                ? dr.GetString()
                : null;
        int? promptEval =
            root.TryGetProperty("prompt_eval_count", out var pe)
            && pe.ValueKind == JsonValueKind.Number
                ? pe.GetInt32()
                : null;
        int? eval =
            root.TryGetProperty("eval_count", out var ec) && ec.ValueKind == JsonValueKind.Number
                ? ec.GetInt32()
                : null;

        string? err = null;
        if (string.Equals(doneReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            err = "Output truncated (done_reason: length).";
        }
        else if (string.IsNullOrWhiteSpace(content))
        {
            err = "Empty message.content from Ollama.";
        }

        return new LlmExchangeRecord(
            attempt,
            thinkingUsed,
            elapsedMs,
            status,
            requestNode,
            rawBody,
            content,
            thinking,
            promptEval,
            eval,
            doneReason,
            err
        );
    }

    private static LlmExchangeRecord ParseOpenAiExchange(
        int attempt,
        bool thinkingUsed,
        long elapsedMs,
        int status,
        JsonNode requestNode,
        string rawBody
    )
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        string? content = null;
        string? finishReason = null;
        int? promptTokens = null;
        int? completionTokens = null;

        if (
            root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
        )
        {
            var first = choices[0];
            if (
                first.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var cp)
                && cp.ValueKind == JsonValueKind.String
            )
            {
                content = cp.GetString();
            }
            if (
                first.TryGetProperty("finish_reason", out var fp)
                && fp.ValueKind == JsonValueKind.String
            )
            {
                finishReason = fp.GetString();
            }
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pti))
            {
                promptTokens = pti;
            }
            if (
                usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cti)
            )
            {
                completionTokens = cti;
            }
        }

        string? err = null;
        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            err = "Output truncated (finish_reason: length).";
        }
        else if (string.IsNullOrWhiteSpace(content))
        {
            err = "Empty choices[0].message.content from OpenAI-compatible server.";
        }

        return new LlmExchangeRecord(
            attempt,
            thinkingUsed,
            elapsedMs,
            status,
            requestNode,
            rawBody,
            content,
            null,
            promptTokens,
            completionTokens,
            finishReason,
            err
        );
    }

    private static string NormalizeOpenAiBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (
            trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/openai", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
        )
        {
            return trimmed;
        }

        return $"{trimmed}/v1";
    }

    private static string CleanMarkdownJsonFences(string content)
    {
        var trimmed = Regex
            .Replace(
                content.Trim(),
                @"<think>[\s\S]*?</think>",
                string.Empty,
                RegexOptions.IgnoreCase
            )
            .Trim();

        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[3..].Trim();
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3].Trim();
        }

        return trimmed;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
