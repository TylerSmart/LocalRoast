import { RecipeCollectionBadgeDto, RecipeTagBadgeDto } from './taxonomy.models';

export type RecipeStatus = 'DraftReview' | 'Published' | 'Archived';

export type RecipeDifficulty = 'Easy' | 'Medium' | 'Project';

export type MeasurementUnit =
  | 'Tsp'
  | 'Tbsp'
  | 'FlOz'
  | 'Cup'
  | 'Pt'
  | 'Qt'
  | 'Gal'
  | 'Ml'
  | 'L'
  | 'Oz'
  | 'Lb'
  | 'G'
  | 'Kg'
  | 'Pinch'
  | 'Dash'
  | 'Clove'
  | 'Can'
  | 'Bunch'
  | 'Slice'
  | 'Whole'
  | 'Custom';

export const MEASUREMENT_UNITS: { value: MeasurementUnit; label: string }[] = [
  { value: 'Tsp', label: 'Teaspoon (tsp)' },
  { value: 'Tbsp', label: 'Tablespoon (tbsp)' },
  { value: 'FlOz', label: 'Fluid Ounce (fl oz)' },
  { value: 'Cup', label: 'Cup (cup)' },
  { value: 'Pt', label: 'Pint (pt)' },
  { value: 'Qt', label: 'Quart (qt)' },
  { value: 'Gal', label: 'Gallon (gal)' },
  { value: 'Ml', label: 'Milliliter (ml)' },
  { value: 'L', label: 'Liter (l)' },
  { value: 'Oz', label: 'Ounce (oz)' },
  { value: 'Lb', label: 'Pound (lb)' },
  { value: 'G', label: 'Gram (g)' },
  { value: 'Kg', label: 'Kilogram (kg)' },
  { value: 'Pinch', label: 'Pinch' },
  { value: 'Dash', label: 'Dash' },
  { value: 'Clove', label: 'Clove' },
  { value: 'Can', label: 'Can' },
  { value: 'Bunch', label: 'Bunch' },
  { value: 'Slice', label: 'Slice' },
  { value: 'Whole', label: 'Whole' },
  { value: 'Custom', label: 'Custom Unit...' },
];

export type UnitSystem = 'Original' | 'UsCustomary' | 'Metric';

export interface RecipeMeasurement {
  quantity: number | null;
  unit: MeasurementUnit | null;
  maxQuantity?: number | null;
  customUnitName?: string | null;
}

export interface RecipeStepIngredientAmountInput {
  ingredientIndex: number;
  measurement: RecipeMeasurement;
}

export interface RecipeStepIngredientAmountDto {
  ingredientId: string;
  measurement: RecipeMeasurement;
}

export type InlineStepAmountKind = 'Ingredient' | 'Mixture' | 'Fixed';

export interface RecipeStepInlineAmountDto {
  start: number;
  length: number;
  kind: InlineStepAmountKind;
  ingredientId: string | null;
  measurement: RecipeMeasurement;
  alternativeMeasurement?: RecipeMeasurement | null;
}

export interface RecipeStepInlineAmountInput {
  start: number;
  length: number;
  kind: InlineStepAmountKind;
  ingredientIndex: number | null;
  measurement: RecipeMeasurement;
  alternativeMeasurement?: RecipeMeasurement | null;
}

export interface DetectedStepAmountDto {
  start: number;
  length: number;
  text: string;
  measurement: RecipeMeasurement;
  alternativeMeasurement: RecipeMeasurement | null;
}

export interface RecipeSummaryDto {
  id: string;
  title: string;
  slug: string;
  description: string | null;
  status: RecipeStatus;
  baseServings: number;
  yieldDisplay: string | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  totalTimeMinutes: number | null;
  difficulty: RecipeDifficulty | null;
  sourceName: string | null;
  thumbnailImagePath: string | null;
  ingredientCount: number;
  stepCount: number;
  timesCooked: number;
  averageRating: number | null;
  createdAt: string;
  modifiedAt: string;
  collections: RecipeCollectionBadgeDto[];
  tags: RecipeTagBadgeDto[];
  lastCookedAt?: string | null;
  ratingCount?: number;
  latestCookNote?: string | null;
}

export interface RecipeIngredientDto {
  id: string;
  sortOrder: number;
  sectionName: string | null;
  quantity: number | null;
  maxQuantity: number | null;
  unit: MeasurementUnit | null;
  customUnitName: string | null;
  foodItemId: string | null;
  foodItemName: string | null;
  isPantryStaple: boolean;
  linkedRecipeId: string | null;
  linkedRecipeTitle: string | null;
  linkedRecipeSlug: string | null;
  displayName: string;
  preparationNote: string | null;
  isOptional: boolean;
  originalRawText: string | null;
  alternativeMeasurement?: RecipeMeasurement | null;
  preferAlternativeMeasurement?: boolean;
  unresolvedRecipeTitle?: string | null;
}

export interface RecipeStepTimerDto {
  id: string;
  sortOrder: number;
  durationSeconds: number;
  label: string;
}

export interface RecipeStepDto {
  id: string;
  stepNumber: number;
  sectionName: string | null;
  instruction: string;
  imagePath: string | null;
  ingredientIds: string[];
  timers: RecipeStepTimerDto[];
  ingredientAmounts?: RecipeStepIngredientAmountDto[] | null;
  inlineAmounts?: RecipeStepInlineAmountDto[] | null;
}

export interface SubRecipePreviewDto {
  id: string;
  title: string;
  slug: string;
  baseServings: number;
  yieldDisplay: string | null;
  ingredients: RecipeIngredientDto[];
  steps: RecipeStepDto[];
}

export interface RecipeAttachmentDto {
  id: string;
  sortOrder: number;
  fileName: string;
  mediaUrlPath: string;
  contentType: string;
  fileSizeBytes: number;
  label: string | null;
  createdAt: string;
}

export interface RecipeDetailDto {
  id: string;
  title: string;
  slug: string;
  description: string | null;
  status: RecipeStatus;
  baseServings: number;
  yieldDisplay: string | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  totalTimeMinutes: number | null;
  difficulty: RecipeDifficulty | null;
  sourceUrl: string | null;
  sourceName: string | null;
  sourceAuthor: string | null;
  sourceBookTitle: string | null;
  sourceBookPage: number | null;
  coverImagePath: string | null;
  thumbnailImagePath: string | null;
  timesCooked: number;
  lastCookedAt: string | null;
  averageRating: number | null;
  ingredients: RecipeIngredientDto[];
  steps: RecipeStepDto[];
  linkedSubRecipes: SubRecipePreviewDto[];
  createdAt: string;
  createdBy: string;
  modifiedAt: string;
  modifiedBy: string;
  collections: RecipeCollectionBadgeDto[];
  tags: RecipeTagBadgeDto[];
  attachments?: RecipeAttachmentDto[];
  ratingCount?: number;
  cookLogs?: CookLogDto[];
  extractedModel?: string | null;
  ingestionJobId?: string | null;
}

export interface RecipeQueryParameters {
  search?: string | null;
  status?: RecipeStatus | null;
  difficulty?: RecipeDifficulty | null;
  maxTotalTimeMinutes?: number | null;
  foodItemId?: string | null;
  collectionId?: string | null;
  tagIds?: string[] | null;
  minRating?: number | null;
  forgottenFavoritesOnly?: boolean;
  forgottenDaysThreshold?: number;
  page?: number;
  pageSize?: number;
  includeTotalCount?: boolean;
  fuzzySearch?: boolean;
}

export interface RecipePagedResultDto {
  items: RecipeSummaryDto[];
  totalCount: number | null;
  pageNumber: number;
  pageSize: number;
  totalPages: number | null;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
  hasMore: boolean;
  isFuzzySearch: boolean;
}

export interface RecipeIngredientInput {
  sectionName?: string | null;
  quantity?: number | null;
  maxQuantity?: number | null;
  unit?: MeasurementUnit | null;
  customUnitName?: string | null;
  foodItemId?: string | null;
  linkedRecipeId?: string | null;
  displayName: string;
  preparationNote?: string | null;
  isOptional?: boolean;
  originalRawText?: string | null;
  alternativeMeasurement?: RecipeMeasurement | null;
  preferAlternativeMeasurement?: boolean;
  unresolvedRecipeTitle?: string | null;
}

export interface RecipeStepTimerInput {
  durationSeconds: number;
  label: string;
}

export interface RecipeStepInput {
  sectionName?: string | null;
  instruction: string;
  ingredientIndices?: number[] | null;
  timers?: RecipeStepTimerInput[] | null;
  imagePath?: string | null;
  ingredientAmounts?: RecipeStepIngredientAmountInput[] | null;
  inlineAmounts?: RecipeStepInlineAmountInput[] | null;
}

export interface UpsertRecipeRequest {
  title: string;
  description?: string | null;
  status?: RecipeStatus;
  baseServings?: number;
  yieldDisplay?: string | null;
  prepTimeMinutes?: number | null;
  cookTimeMinutes?: number | null;
  totalTimeMinutes?: number | null;
  difficulty?: RecipeDifficulty | null;
  sourceUrl?: string | null;
  sourceName?: string | null;
  sourceAuthor?: string | null;
  sourceBookTitle?: string | null;
  sourceBookPage?: number | null;
  ingredients?: RecipeIngredientInput[] | null;
  steps?: RecipeStepInput[] | null;
  coverImagePath?: string | null;
  thumbnailImagePath?: string | null;
  collectionIds?: string[] | null;
  tagIds?: string[] | null;
  extractedModel?: string | null;
}

export interface UpdateRecipeOrganizationRequest {
  collectionIds?: string[] | null;
  tagIds?: string[] | null;
}

export interface IngredientMatchRequest {
  foodItemIds: string[];
}

export interface IngredientMatchResultDto {
  recipe: RecipeSummaryDto;
  matchedCount: number;
  selectedOnHandCount: number;
  additionalNonStapleCount: number;
  pantryStaplesUsedCount: number;
  matchedIngredientNames: string[];
  missingNonStapleNames: string[];
}

export interface CookLogDto {
  id: string;
  recipeId: string;
  cookedByUserId: string | null;
  cookedByDisplayName: string;
  cookedAt: string;
  servingsMade: number | null;
  rating: number | null;
  notes: string | null;
  photoImagePath: string | null;
  photoThumbnailPath: string | null;
  createdAt: string;
  modifiedAt: string;
}

export interface RecipeCookStatsSummaryDto {
  recipeId: string;
  timesCooked: number;
  lastCookedAt: string | null;
  averageRating: number | null;
  ratingCount: number;
  latestCookNote: string | null;
}

export interface CookLogMutationResultDto {
  cookLog: CookLogDto;
  recipeStats: RecipeCookStatsSummaryDto;
}

export interface UpsertCookLogRequest {
  cookedAt?: string | null;
  servingsMade?: number | null;
  rating?: number | null;
  notes?: string | null;
  photoImagePath?: string | null;
  photoThumbnailPath?: string | null;
}

export interface RecipePreviewIngredient {
  id: string;
  index: number;
  sectionName?: string | null;
  quantity?: number | null;
  maxQuantity?: number | null;
  unit?: MeasurementUnit | null;
  customUnitName?: string | null;
  displayName: string;
  preparationNote?: string | null;
  isOptional?: boolean;
  isPantryStaple?: boolean;
  matchedFoodItemName?: string | null;
  alternativeMeasurement?: RecipeMeasurement | null;
  preferAlternativeMeasurement?: boolean;
  linkedRecipeId?: string | null;
  linkedRecipeTitle?: string | null;
  linkedRecipeSlug?: string | null;
  unresolvedRecipeTitle?: string | null;
}

export interface RecipePreviewStep {
  id: string;
  stepNumber: number;
  sectionName?: string | null;
  instruction: string;
  imagePath?: string | null;
  ingredientIndices?: number[];
  timers?: { durationSeconds: number; label?: string }[];
  ingredientAmounts?: RecipeStepIngredientAmountInput[] | null;
  inlineAmounts?: RecipeStepInlineAmountInput[] | null;
}

export interface RecipePreviewData {
  title: string;
  description?: string | null;
  baseServings: number;
  yieldDisplay?: string | null;
  prepTimeMinutes?: number | null;
  cookTimeMinutes?: number | null;
  totalTimeMinutes?: number | null;
  difficulty?: RecipeDifficulty | string | null;
  sourceName?: string | null;
  sourceAuthor?: string | null;
  sourceUrl?: string | null;
  sourceBookTitle?: string | null;
  sourceBookPage?: number | null;
  coverImagePath?: string | null;
  extractedModel?: string | null;
  collections: { id?: string; name: string }[];
  tags: { id?: string; name: string; category?: string }[];
  ingredients: RecipePreviewIngredient[];
  steps: RecipePreviewStep[];
}
