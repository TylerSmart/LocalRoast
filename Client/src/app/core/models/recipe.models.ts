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
}

export interface RecipeQueryParameters {
  search?: string | null;
  status?: RecipeStatus | null;
  difficulty?: RecipeDifficulty | null;
  maxTotalTimeMinutes?: number | null;
  foodItemId?: string | null;
  collectionId?: string | null;
  tagIds?: string[] | null;
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
