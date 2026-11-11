import { FoodCategory } from './food-catalog.models';

export type PdfExtractionMode = 'DigitalText' | 'VisionRaster';

export interface IngestionSourceFileDto {
  pageNumber: number;
  originalFileName: string;
  urlPath: string;
  contentType: string;
  fileSizeBytes: number;
  width?: number | null;
  height?: number | null;
}

export interface PdfInspectionSummaryDto {
  totalPageCount: number;
  processedPageCount: number;
  totalExtractedCharacters: number;
  averageCharactersPerPage: number;
  extractionMode: PdfExtractionMode;
  renderedPageImages: IngestionSourceFileDto[];
}

export interface PromotePageToCoverRequest {
  pageNumber: number;
}

import {
  MeasurementUnit,
  RecipeDifficulty,
  RecipeMeasurement,
  RecipeStepIngredientAmountInput,
  RecipeStepTimerInput,
} from './recipe.models';

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

export interface LlmModelProfile {
  id: string;
  displayName: string;
  modelTag: string;
  matchPrefixes: string[];
  summary: string;
  enableThinking: boolean;
  temperature: number | null;
  topP: number | null;
  topK: number | null;
  contextWindowTokens: number;
  requestTimeoutSeconds: number;
  recommendedAsRefineModel: boolean;
  notes: string[];
  isBuiltInDefault?: boolean;
  dailyQuotaResetTimezone?: string;
  dailyQuotaResetTime?: string;
}

export interface UpsertLlmModelProfileRequest {
  displayName: string;
  modelTag: string;
  matchPrefixes?: string[];
  summary?: string | null;
  enableThinking?: boolean;
  temperature?: number | null;
  topP?: number | null;
  topK?: number | null;
  contextWindowTokens?: number;
  requestTimeoutSeconds?: number;
  recommendedAsRefineModel?: boolean;
  notes?: string[];
  isBuiltInDefault?: boolean;
}

export interface UpsertLlmModelProfileRequest {
  displayName: string;
  modelTag: string;
  matchPrefixes?: string[];
  summary?: string | null;
  enableThinking?: boolean;
  temperature?: number | null;
  topP?: number | null;
  topK?: number | null;
  contextWindowTokens?: number;
  requestTimeoutSeconds?: number;
  recommendedAsRefineModel?: boolean;
  notes?: string[];
}

export interface LlmSettingsDto {
  provider: LlmProvider;
  baseUrl: string;
  hasApiKey: boolean;
  apiKeyMasked: string | null;
  textModel: string | null;
  visionModel: string | null;
  ollamaKeepAlive: string;
  contextWindowTokens: number;
  requestTimeoutSeconds: number;
  autoPublishHighConfidenceText: boolean;
  enableThinking: boolean;
  enableIngestion: boolean;
  maxIngestionRetries: number;
  modifiedAt: string | null;
  ollamaCpuOnly?: boolean;
  temperature?: number | null;
  topP?: number | null;
  topK?: number | null;
  refineModel?: string | null;
  refineEnableThinking?: boolean;
  refineTemperature?: number | null;
  refineTopP?: number | null;
  refineTopK?: number | null;
  refineRequestTimeoutSeconds?: number;
  enableRateLimitSafeguards?: boolean;
  maxConcurrentRequests?: number;
  requestsPerMinute?: number | null;
  dailyRequestQuota?: number | null;
  dailyQuotaResetTimezone?: string;
  dailyQuotaResetTime?: string;
  autoResumeQueue?: boolean;
  pausedUntilUtc?: string | null;
  pauseReason?: string | null;
}

export interface UpdateLlmSettingsRequest {
  provider?: LlmProvider;
  baseUrl?: string;
  apiKey?: string | null;
  clearApiKey?: boolean;
  textModel?: string | null;
  visionModel?: string | null;
  ollamaKeepAlive?: string;
  contextWindowTokens?: number;
  requestTimeoutSeconds?: number;
  autoPublishHighConfidenceText?: boolean;
  enableThinking?: boolean;
  enableIngestion?: boolean;
  maxIngestionRetries?: number;
  ollamaCpuOnly?: boolean;
  temperature?: number | null;
  topP?: number | null;
  topK?: number | null;
  refineModel?: string | null;
  refineEnableThinking?: boolean;
  refineTemperature?: number | null;
  refineTopP?: number | null;
  refineTopK?: number | null;
  refineRequestTimeoutSeconds?: number;
  enableRateLimitSafeguards?: boolean;
  maxConcurrentRequests?: number;
  requestsPerMinute?: number | null;
  dailyRequestQuota?: number | null;
  dailyQuotaResetTimezone?: string;
  dailyQuotaResetTime?: string;
  autoResumeQueue?: boolean;
  pausedUntilUtc?: string | null;
  pauseReason?: string | null;
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
  createNewFoodItem?: boolean;
  proposedFoodItemName: string | null;
  suggestedIsPantryStaple: boolean;
  alternativeMeasurement?: RecipeMeasurement | null;
  preferAlternativeMeasurement?: boolean;
  suggestedCategory?: FoodCategory | null;
  isRecipeReference?: boolean;
  referencedRecipeTitle?: string | null;
  matchedRecipeId?: string | null;
  matchedRecipeTitle?: string | null;
  matchedRecipeSlug?: string | null;
}

export interface IngestionDraftStepDto {
  stepNumber: number;
  sectionName: string | null;
  instruction: string;
  ingredientIndices: number[];
  timers: RecipeStepTimerInput[];
  imagePath: string | null;
  ingredientAmounts?: RecipeStepIngredientAmountInput[] | null;
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
  sourceBookTitle?: string | null;
  sourceBookPage?: number | null;
  retainSourceAttachments?: boolean;
  extractedModel?: string | null;
  proposedTagNames?: string[] | null;
  measurementWarnings?: string[] | null;
}

export interface IngestionDiagnosticTargetDto {
  message: string;
  severity: 'error' | 'warning';
  ingredientIndex?: number | null;
  stepNumber?: number | null;
}

export interface IngestionReviewDiagnosticsDto {
  validationIssues: string[];
  qualityWarnings: string[];
  targetedDiagnostics?: IngestionDiagnosticTargetDto[];
}

export interface IngestionRepairPreviewDto {
  draft: IngestionExtractedDraftDto;
  before: IngestionReviewDiagnosticsDto;
  after: IngestionReviewDiagnosticsDto;
  notices: string[];
}

export type IngestionRefinementStatus =
  'Pending' | 'Running' | 'Improved' | 'NoImprovement' | 'Failed';

/** issuesBefore and issuesAfter count validation issues plus quality warnings. */
export interface IngestionRefinementDto {
  status: IngestionRefinementStatus;
  model: string;
  detail: string | null;
  issuesBefore: number | null;
  issuesAfter: number | null;
  requestedAt: string;
  completedAt: string | null;
  hasProposal: boolean;
  basedOnEarlierDraft: boolean;
}

export interface EnqueueWebUrlIngestionRequest {
  urls: string[];
}

export interface EnqueueRawTextIngestionRequest {
  rawText: string;
  titleHint?: string | null;
  sourceName?: string | null;
  sourceAuthor?: string | null;
  sourceBookTitle?: string | null;
  sourceBookPage?: number | null;
  coverImagePath?: string | null;
  thumbnailImagePath?: string | null;
  collectionIds?: string[] | null;
  autoPublish?: boolean;
}

export interface EnqueuePdfBatchMetadata {
  sourceBookTitle?: string | null;
  splitMultiPage?: boolean;
}

export interface IngestionQueueSummaryDto {
  readyForReviewCount: number;
  activeProcessingCount: number;
  waitingForLlmHostCount: number;
  failedCount: number;
  allCount?: number;
  publishedCount?: number;
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
  sourceFileCount?: number;
  extractedModel?: string | null;
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
  sourceFiles?: IngestionSourceFileDto[];
  pdfInspection?: PdfInspectionSummaryDto | null;
  sourceBookTitle?: string | null;
  sourceBookPage?: number | null;
  extractedModel?: string | null;
}

export interface RetryWaitingJobsResultDto {
  requeuedCount: number;
}

export interface RetryBatchJobsRequest {
  jobIds?: string[];
  filter?: string;
  search?: string;
}

export interface RetryBatchJobsResultDto {
  requeuedCount: number;
}
