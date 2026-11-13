import type {
  LlmConnectionTestResultDto,
  LlmModelProfile,
  LlmProvider,
} from '../../../core/models/ingestion.models';

/** Returns the profile with the longest match prefix that starts the model tag. */
export function matchModelProfile(
  profiles: readonly LlmModelProfile[],
  model: string | null | undefined,
): LlmModelProfile | null {
  const tag = model?.trim().toLowerCase() ?? '';
  if (!tag) {
    return null;
  }
  let best: LlmModelProfile | null = null;
  let bestLength = 0;
  for (const profile of profiles) {
    for (const prefix of profile.matchPrefixes) {
      if (tag.startsWith(prefix.toLowerCase()) && prefix.length > bestLength) {
        best = profile;
        bestLength = prefix.length;
      }
    }
  }
  return best;
}

/**
 * Filters discovered model names by every whitespace-separated word in the query (case-insensitive),
 * so "gemini flash" matches "google/gemini-3.8-flash". A blank query or one that exactly names a
 * model returns the full list so the user can still switch models after picking one.
 */
export function filterModels(
  models: readonly string[],
  query: string | null | undefined,
): string[] {
  const normalized = query?.trim().toLowerCase() ?? '';
  if (!normalized || models.some((model) => model.toLowerCase() === normalized)) {
    return [...models];
  }
  const terms = normalized.split(/\s+/);
  return models.filter((model) => {
    const name = model.toLowerCase();
    return terms.every((term) => name.includes(term));
  });
}

/** Default base URL for a provider, used when the user switches providers on a blank or default URL. */
export function defaultBaseUrlFor(provider: LlmProvider, currentUrl: string): string | null {
  const url = currentUrl.trim();
  if (provider === 'Ollama' && (url === '' || url.includes(':1234'))) {
    return 'http://localhost:11434';
  }
  if (provider === 'OpenAiCompatible' && (url === '' || url === 'http://localhost:11434')) {
    return 'http://localhost:1234/v1';
  }
  return null;
}

export function providerLabel(provider: LlmProvider): string {
  return provider === 'Ollama' ? 'Ollama' : 'OpenAI-compatible';
}

export const LLM_TIMEZONE_OPTIONS: readonly { value: string; label: string }[] = [
  { value: 'America/Los_Angeles', label: 'Pacific Time (PT) - Google AI Studio (Midnight PT)' },
  { value: 'UTC', label: 'Coordinated Universal Time (UTC) - Standard / OpenRouter' },
  { value: 'America/New_York', label: 'Eastern Time (ET)' },
  { value: 'America/Chicago', label: 'Central Time (CT)' },
  { value: 'America/Denver', label: 'Mountain Time (MT)' },
  { value: 'Europe/London', label: 'London / GMT' },
  { value: 'Europe/Paris', label: 'Central European Time (CET)' },
  { value: 'Asia/Tokyo', label: 'Tokyo / JST' },
  { value: 'Local', label: 'Local Server Time' },
];

export function failedTestResult(
  provider: LlmProvider,
  baseUrl: string,
  err: { error?: { detail?: string }; message?: string } | null | undefined,
): LlmConnectionTestResultDto {
  return {
    isReachable: false,
    provider,
    normalizedBaseUrl: baseUrl.trim(),
    latencyMs: 0,
    models: [],
    errorMessage: err?.error?.detail || err?.message || 'Connection test failed.',
  };
}
