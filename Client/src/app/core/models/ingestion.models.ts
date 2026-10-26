import { MeasurementUnit, RecipeDifficulty, RecipeStepTimerInput } from './recipe.models';

export type LlmProvider = 'Ollama' | 'OpenAiCompatible';

export type IngestionChannel = 'WebUrl' | 'RawText' | 'Images' | 'Pdf';

export type IngestionJobStatus =
  | 'Queued'
  | 'Preprocessing'
  | 'Extracting'
  | 'WaitingForLlmHost'
  | 'ReadyForReview'
  | 'Published'
  | 'Failed'
  | 'Discarded';

export interface LlmSettingsDto {
  provider: LlmProvider;
  baseUrl: string;
  hasApiKey: boolean;
  apiKeyMasked: string | null;
  textModel: string | null;
  visionModel: string | null;
  ollamaKeepAlive: string;
  requestTimeoutSeconds: number;
  autoPublishHighConfidenceText: boolean;
  modifiedAt: string | null;
}

export interface UpdateLlmSettingsRequest {
  provider?: LlmProvider;
  baseUrl?: string;
  apiKey?: string | null;
  clearApiKey?: boolean;
  textModel?: string | null;
  visionModel?: string | null;
  ollamaKeepAlive?: string;
  requestTimeoutSeconds?: number;
  autoPublishHighConfidenceText?: boolean;
}

export interface TestLlmConnectionRequest {
  provider?: LlmProvider | null;
  baseUrl?: string | null;
  apiKey?: string | null;
  clearApiKey?: boolean;
  timeoutSeconds?: number | null;
}

export interface LlmConnectionTestResultDto {
  isReachable: boolean;
  provider: LlmProvider;
  normalizedBaseUrl: string;
  latencyMs: number;
  models: string[];
  errorMessage: string | null;
}

export interface ScrapedWebRecipePayload {
  sourceUrl: string;
  pageTitle: string | null;
  siteName: string | null;
  author: string | null;
  primaryImageUrl: string | null;
  hasJsonLdRecipe: boolean;
  rawIngredientLines: string[];
  rawInstructionLines: string[];
  yieldText: string | null;
  prepTimeIso: string | null;
  cookTimeIso: string | null;
  totalTimeIso: string | null;
  cleanedBodyText: string | null;
}

export interface IngestionDraftIngredientDto {
  sortOrder: number;
  sectionName: string | null;
  originalRawText: string | null;
  quantity: number | null;
  maxQuantity: number | null;
  unit: MeasurementUnit | null;
  customUnitName: string | null;
  displayName: string;
  preparationNote: string | null;
  isOptional: boolean;
  matchedFoodItemId: string | null;
  matchedFoodItemName: string | null;
  isNewFoodItem: boolean;
  proposedFoodItemName: string | null;
  suggestedStoreAisleId: string | null;
  suggestedIsPantryStaple: boolean;
}

export interface IngestionDraftStepDto {
  stepNumber: number;
  sectionName: string | null;
  instruction: string;
  ingredientIndices: number[];
  timers: RecipeStepTimerInput[];
  imagePath: string | null;
}

export interface IngestionExtractedDraftDto {
  title: string;
  description: string | null;
  baseServings: number;
  yieldDisplay: string | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  totalTimeMinutes: number | null;
  difficulty: RecipeDifficulty | null;
  sourceUrl: string | null;
  sourceName: string | null;
  sourceAuthor: string | null;
  coverImagePath: string | null;
  thumbnailImagePath: string | null;
  suggestedCollectionIds: string[] | null;
  suggestedTagIds: string[] | null;
  ingredients: IngestionDraftIngredientDto[];
  steps: IngestionDraftStepDto[];
  confidenceScore: number;
}

export interface EnqueueWebUrlIngestionRequest {
  urls: string[];
}

export interface EnqueueRawTextIngestionRequest {
  rawText: string;
  titleHint?: string | null;
}

export interface IngestionQueueSummaryDto {
  readyForReviewCount: number;
  activeProcessingCount: number;
  waitingForLlmHostCount: number;
  failedCount: number;
}

export interface IngestionJobSummaryDto {
  id: string;
  channel: IngestionChannel;
  status: IngestionJobStatus;
  displayTitle: string;
  sourceUrl: string | null;
  thumbnailImagePath: string | null;
  confidenceScore: number | null;
  statusDetail: string | null;
  attemptCount: number;
  nextRetryAt: string | null;
  resultingRecipeId: string | null;
  resultingRecipeSlug: string | null;
  submittedByUsername: string | null;
  createdAt: string;
  modifiedAt: string;
}

export interface IngestionJobDetailDto {
  id: string;
  channel: IngestionChannel;
  status: IngestionJobStatus;
  displayTitle: string;
  sourceUrl: string | null;
  rawText: string | null;
  scrapedPage: ScrapedWebRecipePayload | null;
  normalizedSourceText: string | null;
  downloadedCoverImagePath: string | null;
  downloadedThumbnailImagePath: string | null;
  extractedDraft: IngestionExtractedDraftDto | null;
  confidenceScore: number | null;
  statusDetail: string | null;
  attemptCount: number;
  nextRetryAt: string | null;
  processedAt: string | null;
  resultingRecipeId: string | null;
  resultingRecipeSlug: string | null;
  submittedByUsername: string | null;
  createdAt: string;
  modifiedAt: string;
}

export interface RetryWaitingJobsResultDto {
  requeuedCount: number;
}
