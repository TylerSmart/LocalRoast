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
