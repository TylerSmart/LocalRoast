namespace App.Core.Ingestion;

/// <summary>
/// Measured recommended settings for one model. Values come from docs/llm-model-settings.md.
/// </summary>
public sealed record LlmModelProfileDto(
    string Id,
    string DisplayName,
    string ModelTag,
    IReadOnlyList<string> MatchPrefixes,
    string Summary,
    bool EnableThinking,
    double? Temperature,
    double? TopP,
    int? TopK,
    int ContextWindowTokens,
    int RequestTimeoutSeconds,
    bool RecommendedAsRefineModel,
    IReadOnlyList<string> Notes,
    bool IsBuiltInDefault = true,
    string? DailyQuotaResetTimezone = null,
    string? DailyQuotaResetTime = null
);

public static class LlmModelProfiles
{
    public static IReadOnlyList<LlmModelProfileDto> All { get; } =
    [
        new(
            Id: "gemma4-e4b",
            DisplayName: "Gemma 4 E4B",
            ModelTag: "gemma4:e4b",
            MatchPrefixes: ["gemma4:e4b"],
            Summary: "Default choice: the most accurate model that fits a 16 GB GPU.",
            EnableThinking: true,
            Temperature: null,
            TopP: null,
            TopK: null,
            ContextWindowTokens: 32768,
            RequestTimeoutSeconds: 600,
            RecommendedAsRefineModel: false,
            Notes: ["Review divided ingredients, separated eggs and long steps."]
        ),
        new(
            Id: "gemma4-e2b",
            DisplayName: "Gemma 4 E2B",
            ModelTag: "gemma4:e2b",
            MatchPrefixes: ["gemma4:e2b"],
            Summary: "Fast drafts with three to four times as many findings as E4B; review every recipe.",
            EnableThinking: true,
            Temperature: null,
            TopP: null,
            TopK: null,
            ContextWindowTokens: 32768,
            RequestTimeoutSeconds: 300,
            RecommendedAsRefineModel: false,
            Notes: []
        ),
        new(
            Id: "gemma4-12b",
            DisplayName: "Gemma 4 12B",
            ModelTag: "gemma4:12b",
            MatchPrefixes: ["gemma4:12b"],
            Summary: "Works with thinking off, but is less accurate than E4B.",
            EnableThinking: false,
            Temperature: null,
            TopP: null,
            TopK: null,
            ContextWindowTokens: 32768,
            RequestTimeoutSeconds: 300,
            RecommendedAsRefineModel: false,
            Notes: []
        ),
        new(
            Id: "gemma4-12b-thinking",
            DisplayName: "Gemma 4 12B with thinking",
            ModelTag: "gemma4:12b",
            MatchPrefixes: [],
            Summary: "Experimental: best on the hardest cases, worse on the rest, and about 4 minutes per recipe.",
            EnableThinking: true,
            Temperature: 1,
            TopP: 0.95,
            TopK: 64,
            ContextWindowTokens: 32768,
            RequestTimeoutSeconds: 900,
            RecommendedAsRefineModel: true,
            Notes:
            [
                "Needs Temperature 1, Top P 0.95 and Top K 64; at temperature 0 it repeats its reasoning until the output limit.",
                "Output varies from run to run.",
            ]
        ),
        new(
            Id: "gemma4-26b-a4b",
            DisplayName: "Gemma 4 26B A4B",
            ModelTag: "gemma4:26b-a4b",
            MatchPrefixes: ["gemma4:26b"],
            Summary: "Slow second pass for drafts that still have issues; needs partial GPU offload on 16 GB.",
            EnableThinking: true,
            Temperature: 1,
            TopP: 0.95,
            TopK: 64,
            ContextWindowTokens: 32768,
            RequestTimeoutSeconds: 1200,
            RecommendedAsRefineModel: true,
            Notes:
            [
                "Does not fit in 16 GB of VRAM; create a derived model with PARAMETER num_gpu 24.",
                "About 5 minutes per recipe with thinking, and up to 20 minutes for very long recipes.",
            ]
        ),
        new(
            Id: "gemini-flash-lite",
            DisplayName: "Gemini Flash Lite",
            ModelTag: "gemini-2.5-flash-lite",
            MatchPrefixes:
            [
                "gemini-2.5-flash-lite",
                "gemini-2.0-flash-lite",
                "gemini-flash-lite",
                "google/gemini-2.5-flash-lite",
                "google/gemini-2.0-flash-lite",
                "google/gemini-flash-lite",
            ],
            Summary: "Fast and lightweight Google cloud model; highly economical for high-volume recipe ingestion.",
            EnableThinking: false,
            Temperature: null,
            TopP: null,
            TopK: null,
            ContextWindowTokens: 32768,
            RequestTimeoutSeconds: 120,
            RecommendedAsRefineModel: false,
            Notes:
            [
                "Connect via OpenAI-Compatible provider (Base URL: https://generativelanguage.googleapis.com/v1beta/openai).",
                "Works with Google AI Studio free and pay-as-you-go API keys.",
                "Supports vision for cookbook photos and scanned PDFs.",
            ],
            DailyQuotaResetTimezone: LlmQuotaSchedule.PacificTimezone,
            DailyQuotaResetTime: LlmQuotaSchedule.DefaultResetTime
        ),
        new(
            Id: "gemini-flash",
            DisplayName: "Gemini Flash",
            ModelTag: "gemini-2.5-flash",
            MatchPrefixes:
            [
                "gemini-2.5-flash",
                "gemini-2.0-flash",
                "gemini-1.5-flash",
                "gemini-flash",
                "google/gemini-2.5-flash",
                "google/gemini-2.0-flash",
                "google/gemini-flash",
            ],
            Summary: "High-accuracy, high-speed Google cloud model; ideal balance of extraction quality and latency.",
            EnableThinking: false,
            Temperature: null,
            TopP: null,
            TopK: null,
            ContextWindowTokens: 32768,
            RequestTimeoutSeconds: 180,
            RecommendedAsRefineModel: true,
            Notes:
            [
                "Connect via OpenAI-Compatible provider (Base URL: https://generativelanguage.googleapis.com/v1beta/openai).",
                "Recommended as both a primary Text Model and a background Refine Model.",
                "Supports vision for cookbook photos and scanned PDFs.",
            ],
            DailyQuotaResetTimezone: LlmQuotaSchedule.PacificTimezone,
            DailyQuotaResetTime: LlmQuotaSchedule.DefaultResetTime
        ),
        new(
            Id: "gemini-argon",
            DisplayName: "Gemini Argon",
            ModelTag: "gemini-4-argon",
            MatchPrefixes:
            [
                "gemini-4-argon",
                "gemini-argon",
                "gemini-2.5-pro",
                "gemini-1.5-pro",
                "gemini-pro",
                "google/gemini-4-argon",
                "google/gemini-2.5-pro",
                "google/gemini-pro",
            ],
            Summary: "Deep-reasoning frontier Google model; best for complex recipe structures and tricky method tagging.",
            EnableThinking: true,
            Temperature: null,
            TopP: null,
            TopK: null,
            ContextWindowTokens: 32768,
            RequestTimeoutSeconds: 300,
            RecommendedAsRefineModel: true,
            Notes:
            [
                "Recommended as a secondary background Refine Model for drafts needing expert reconciliation.",
                "Connect via OpenAI-Compatible provider (Base URL: https://generativelanguage.googleapis.com/v1beta/openai).",
            ],
            DailyQuotaResetTimezone: LlmQuotaSchedule.PacificTimezone,
            DailyQuotaResetTime: LlmQuotaSchedule.DefaultResetTime
        ),
    ];

    /// <summary>Returns the profile whose longest match prefix starts the model tag, or null.</summary>
    public static LlmModelProfileDto? Match(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var tag = model.Trim();
        return All.SelectMany(profile => profile.MatchPrefixes.Select(prefix => (profile, prefix)))
            .Where(match => tag.StartsWith(match.prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(match => match.prefix.Length)
            .Select(match => match.profile)
            .FirstOrDefault();
    }
}
