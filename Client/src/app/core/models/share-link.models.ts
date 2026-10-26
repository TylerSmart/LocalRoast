import { MeasurementUnit, RecipeDifficulty, RecipeStepDto } from './recipe.models';
import { TagCategory } from './taxonomy.models';

export interface ShareLinkDto {
  id: string;
  recipeId: string;
  token: string;
  label: string | null;
  includeCookLogs: boolean;
  expiresAt: string | null;
  revokedAt: string | null;
  isActive: boolean;
  viewCount: number;
  lastAccessedAt: string | null;
  createdAt: string;
  createdBy: string;
}

export interface CreateShareLinkRequest {
  label?: string | null;
  includeCookLogs?: boolean;
  expiresAt?: string | null;
}

export interface PublicRecipeTagBadgeDto {
  name: string;
  category: TagCategory;
}

export interface PublicRecipeIngredientDto {
  id: string;
  sortOrder: number;
  sectionName: string | null;
  quantity: number | null;
  maxQuantity: number | null;
  unit: MeasurementUnit | null;
  customUnitName: string | null;
  isPantryStaple: boolean;
  linkedSubRecipeId: string | null;
  linkedSubRecipeTitle: string | null;
  displayName: string;
  preparationNote: string | null;
  isOptional: boolean;
}

export interface PublicSubRecipeDto {
  id: string;
  title: string;
  baseServings: number;
  yieldDisplay: string | null;
  ingredients: PublicRecipeIngredientDto[];
  steps: RecipeStepDto[];
}

export interface PublicSharedRecipeDto {
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
  sourceBookTitle: string | null;
  sourceBookPage: number | null;
  coverImagePath: string | null;
  tags: PublicRecipeTagBadgeDto[];
  ingredients: PublicRecipeIngredientDto[];
  steps: RecipeStepDto[];
  linkedSubRecipes: PublicSubRecipeDto[];
  includeCookLogs: boolean;
  expiresAt: string | null;
}
