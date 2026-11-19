import type {
  LlmConnectionDto,
  LlmConnectionTestResultDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
  LlmProvider,
  LlmWorkflow,
} from '../../../core/models/ingestion.models';

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

const WORKFLOW_ORDER: readonly LlmWorkflow[] = ['Text', 'Vision', 'Refinement', 'Embedding'];

export function formatWorkflowList(workflows: readonly LlmWorkflow[]): string {
  const unique = WORKFLOW_ORDER.filter((workflow) => workflows.includes(workflow));
  if (unique.length === 0) {
    return '';
  }
  if (unique.length === 1) {
    return `${unique[0]} workflow`;
  }
  if (unique.length === 2) {
    return `${unique[0]} and ${unique[1]} workflows`;
  }
  return `${unique.slice(0, -1).join(', ')}, and ${unique[unique.length - 1]} workflows`;
}

/**
 * Returns a confirmation message when deleting a connection will also delete dependent saved
 * models/profiles or deselect active workflows, or null when the connection has no dependencies.
 */
export function buildDeleteConnectionConfirmMessage(
  connection: LlmConnectionDto,
  configurations: readonly LlmModelConfigurationDto[],
): string | null {
  const dependent = configurations.filter((c) => c.connectionId === connection.id);
  const modelCount = Math.max(dependent.length, connection.modelConfigurationCount);
  const workflowText = formatWorkflowList(dependent.flatMap((c) => c.assignedWorkflows));
  if (modelCount === 0 && !workflowText) {
    return null;
  }

  const modelPart = `${modelCount} saved ${modelCount === 1 ? 'model' : 'models'} and ${
    modelCount === 1 ? 'its' : 'their'
  } profiles`;
  if (workflowText) {
    return `Deleting "${connection.name}" will also delete ${modelPart}, and deselect them from the ${workflowText}. Continue?`;
  }
  return `Deleting "${connection.name}" will also delete ${modelPart}. Continue?`;
}

/**
 * Returns a confirmation message when deleting a model configuration will also delete custom
 * profiles or deselect active workflows, or null when only its unused Defaults profile exists.
 */
export function buildDeleteModelConfirmMessage(
  configuration: LlmModelConfigurationDto,
): string | null {
  const customProfileCount = Math.max(0, configuration.profileCount - 1);
  const workflowText = formatWorkflowList(configuration.assignedWorkflows);
  if (customProfileCount === 0 && !workflowText) {
    return null;
  }

  const parts: string[] = [];
  if (customProfileCount > 0) {
    parts.push(
      `delete its ${customProfileCount} custom ${customProfileCount === 1 ? 'profile' : 'profiles'}`,
    );
  }
  if (workflowText) {
    parts.push(`deselect it from the ${workflowText}`);
  }
  return `Deleting "${configuration.name}" will also ${parts.join(' and ')}. Continue?`;
}

/**
 * Returns a confirmation message when deleting a custom profile will deselect active workflows,
 * or null when it is unassigned.
 */
export function buildDeleteProfileConfirmMessage(profile: LlmModelProfile): string | null {
  const workflowText = formatWorkflowList(profile.assignedWorkflows);
  if (!workflowText) {
    return null;
  }
  return `Deleting "${profile.displayName}" will deselect it from the ${workflowText}. Continue?`;
}
