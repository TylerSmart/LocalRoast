using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using App.Core.Ingestion;

namespace IngestionPoc.Services;

public enum EndpointStyle
{
    Native,
    OpenAi,
}

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
    string OllamaKeepAlive = "5m",
    EndpointStyle EndpointStyle = EndpointStyle.Native,
    string? EmbedModel = "embeddinggemma-2:270m"
);

public sealed record EmbeddingCallResult(
    IReadOnlyList<float[]> Vectors,
    long ElapsedMs,
    int? PromptTokens,
    string? Error
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

            if (
                config.Provider == LlmProvider.Ollama
                && config.EndpointStyle == EndpointStyle.Native
            )
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
                    top_k = config.EndpointStyle == EndpointStyle.OpenAi ? null : config.TopK,
                    max_tokens = config.EndpointStyle == EndpointStyle.OpenAi ? (int?)null : 16384,
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
        string? reasoning = null;
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
            if (first.TryGetProperty("message", out var msg))
            {
                if (
                    msg.TryGetProperty("content", out var cp)
                    && cp.ValueKind == JsonValueKind.String
                )
                {
                    content = cp.GetString();
                }
                if (
                    msg.TryGetProperty("reasoning", out var rp)
                    && rp.ValueKind == JsonValueKind.String
                )
                {
                    reasoning = rp.GetString();
                }
                else if (
                    msg.TryGetProperty("reasoning_content", out var rcp)
                    && rcp.ValueKind == JsonValueKind.String
                )
                {
                    reasoning = rcp.GetString();
                }
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
            reasoning,
            promptTokens,
            completionTokens,
            finishReason,
            err
        );
    }

    public async Task<EmbeddingCallResult> GenerateEmbeddingsAsync(
        PocLlmConfig config,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default
    )
    {
        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(config.EmbedModel))
        {
            return new EmbeddingCallResult([], 0, null, null);
        }

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

            var baseUrl = NormalizeOpenAiBaseUrl(config.BaseUrl);
            var payload = new { model = config.EmbedModel.Trim(), input = inputs };
            var requestJson = JsonSerializer.Serialize(payload, RequestJsonOptions);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/embeddings")
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
                return new EmbeddingCallResult(
                    [],
                    sw.ElapsedMilliseconds,
                    null,
                    $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body, 300)}"
                );
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            int? promptTokens =
                root.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("prompt_tokens", out var pt)
                && pt.TryGetInt32(out var pti)
                    ? pti
                    : null;

            var indexed = new List<(int Index, float[] Vector)>(inputs.Count);
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    var idx =
                        entry.TryGetProperty("index", out var ip) && ip.TryGetInt32(out var iv)
                            ? iv
                            : indexed.Count;
                    if (
                        !entry.TryGetProperty("embedding", out var ep)
                        || ep.ValueKind != JsonValueKind.Array
                    )
                    {
                        continue;
                    }

                    var rawVec = new float[ep.GetArrayLength()];
                    var k = 0;
                    double normSq = 0;
                    foreach (var v in ep.EnumerateArray())
                    {
                        var fv = v.GetSingle();
                        rawVec[k++] = fv;
                        normSq += fv * fv;
                    }

                    if (normSq > 1e-12)
                    {
                        var invNorm = (float)(1.0 / Math.Sqrt(normSq));
                        for (var j = 0; j < rawVec.Length; j++)
                        {
                            rawVec[j] *= invNorm;
                        }
                    }

                    indexed.Add((idx, rawVec));
                }
            }

            var vectors = indexed.OrderBy(x => x.Index).Select(x => x.Vector).ToList();
            var err =
                vectors.Count != inputs.Count
                    ? $"Expected {inputs.Count} embeddings, received {vectors.Count}."
                    : null;

            return new EmbeddingCallResult(vectors, sw.ElapsedMilliseconds, promptTokens, err);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new EmbeddingCallResult(
                [],
                sw.ElapsedMilliseconds,
                null,
                $"Embedding request timed out after {config.RequestTimeoutSeconds}s"
            );
        }
        finally
        {
            SerialGate.Release();
        }
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
