import { MealSlot, MealPlanRecipeBadgeDto } from './meal-plan.models';

export interface AiHelperStatusDto {
  isAvailable: boolean;
  provider: string;
  textModel?: string | null;
  latencyMs: number;
  unavailableReason?: string | null;
}

export interface SuggestIngredientSubstitutionsRequest {
  recipeId: string;
  recipeIngredientId: string;
  targetServings?: number | null;
  targetSystem?: string | null;
  dietaryOrPantryNote?: string | null;
}

export interface IngredientSubstitutionOptionDto {
  label: string;
  ratioAndAmount: string;
  culinaryNotes: string;
  flavorOrTextureImpact: string;
}

export interface IngredientSubstitutionResponseDto {
  recipeId: string;
  recipeTitle: string;
  recipeIngredientId: string;
  ingredientDisplayName: string;
  scaledAmountDisplay: string;
  modelUsed: string;
  substitutions: IngredientSubstitutionOptionDto[];
}

export interface AskKitchenQuestionRequest {
  question: string;
  recipeId?: string;
  recipeIngredientId?: string;
  recipeStepId?: string;
}

export interface KitchenAnswerDto {
  answer: string;
  modelUsed: string;
}

export interface DraftWeeklyMealPlanRequest {
  weekStartDate?: string | null;
  prompt: string;
  mealCount?: number;
  defaultSlot?: MealSlot;
  placeOnDeck?: boolean;
}

export interface DraftMealPlanCandidateDto {
  recipeId: string;
  recipe: MealPlanRecipeBadgeDto;
  isOnDeck: boolean;
  dayOffset?: number | null;
  scheduledDate?: string | null;
  slot?: MealSlot | null;
  targetServings: number;
  rationale: string;
}

export interface DraftMealPlanResponseDto {
  weekStartDate: string;
  weekEndDate: string;
  modelUsed: string;
  consideredCookbookRecipeCount: number;
  candidates: DraftMealPlanCandidateDto[];
}
