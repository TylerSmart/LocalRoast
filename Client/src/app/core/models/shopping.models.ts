import { MeasurementUnit } from './recipe.models';

export type MeasurementDimension = 'Volume' | 'Weight' | 'Discrete' | 'Unspecified';

export type ShoppingListSourceKind = 'Manual' | 'MealPlanEntry' | 'SingleRecipe';

export type DuplicateMealPolicy = 'SkipAlreadyImported' | 'ReplaceAlreadyImported';

export interface ShoppingListSummaryDto {
  id: string;
  name: string;
  isPinned: boolean;
  activeUncheckedCount: number;
  checkedCount: number;
  excludedPantryCount: number;
  createdAt: string;
  modifiedAt: string;
}

export interface ShoppingListItemSourceDto {
  id: string;
  sourceKind: ShoppingListSourceKind;
  mealPlanEntryId?: string | null;
  recipeId?: string | null;
  sourceLabel: string;
  scheduledDate?: string | null;
  contributedQuantity?: number | null;
  contributedMaxQuantity?: number | null;
}

export interface ShoppingListItemDto {
  id: string;
  shoppingListId: string;
  foodItemId?: string | null;
  foodItemName?: string | null;
  displayName: string;
  quantity?: number | null;
  maxQuantity?: number | null;
  unit?: MeasurementUnit | null;
  customUnitName?: string | null;
  unitLabel: string;
  formattedAmount: string;
  dimension: MeasurementDimension;
  notes?: string | null;
  isPantryStapleExcluded: boolean;
  isChecked: boolean;
  checkedAt?: string | null;
  checkedByUserId?: string | null;
  checkedByDisplayName?: string | null;
  sortOrder: number;
  sources: ShoppingListItemSourceDto[];
  createdAt: string;
  modifiedAt: string;
}

export interface ShoppingListDetailDto {
  id: string;
  name: string;
  isPinned: boolean;
  activeItems: ShoppingListItemDto[];
  checkedItems: ShoppingListItemDto[];
  excludedPantryItems: ShoppingListItemDto[];
  createdAt: string;
  modifiedAt: string;
}

export interface CreateShoppingListRequest {
  name: string;
  isPinned?: boolean;
}

export interface UpdateShoppingListRequest {
  name: string;
  isPinned?: boolean;
}

export interface CreateShoppingListItemRequest {
  displayName: string;
  quantity?: number | null;
  maxQuantity?: number | null;
  unit?: MeasurementUnit | null;
  customUnitName?: string | null;
  foodItemId?: string | null;
  notes?: string | null;
  consolidateIfCompatible?: boolean;
}

export interface UpdateShoppingListItemRequest {
  displayName: string;
  quantity?: number | null;
  maxQuantity?: number | null;
  unit?: MeasurementUnit | null;
  customUnitName?: string | null;
  notes?: string | null;
  isPantryStapleExcluded?: boolean;
}

export interface SetShoppingListItemCheckedRequest {
  isChecked: boolean;
}

export interface ClearCheckedItemsResultDto {
  removedCount: number;
}

export interface AlreadyImportedMealDto {
  mealPlanEntryId: string;
  displayTitle: string;
  scheduledDate?: string | null;
  isOnDeck: boolean;
}

export interface GenerateFromMealPlanRequest {
  startDate: string;
  endDate: string;
  includeOnDeck?: boolean;
  includeSubRecipes?: boolean;
  includeOptionalIngredients?: boolean;
  duplicatePolicy?: DuplicateMealPolicy;
  promotePantryFoodItemIds?: string[] | null;
}

export interface ShoppingListCandidateItemDto {
  foodItemId?: string | null;
  displayName: string;
  isPantryStaple: boolean;
  quantity?: number | null;
  maxQuantity?: number | null;
  unit?: MeasurementUnit | null;
  customUnitName?: string | null;
  unitLabel: string;
  formattedAmount: string;
  dimension: MeasurementDimension;
  notes?: string | null;
  sourceLabels: string[];
}

export interface ShoppingListGenerationPreviewDto {
  startDate: string;
  endDate: string;
  eligibleMealsCount: number;
  newMealsToImportCount: number;
  ignoredLeftoversCount: number;
  ignoredNotesCount: number;
  alreadyImportedMeals: AlreadyImportedMealDto[];
  activeCandidateItems: ShoppingListCandidateItemDto[];
  excludedPantryCandidateItems: ShoppingListCandidateItemDto[];
}

export interface AddRecipeToShoppingListRequest {
  recipeId: string;
  targetServings?: number | null;
  includeSubRecipes?: boolean;
  includeOptionalIngredients?: boolean;
  promotePantryFoodItemIds?: string[] | null;
}

export interface ShoppingListGenerationResultDto {
  shoppingList: ShoppingListDetailDto;
  importedMealsCount: number;
  skippedDuplicateMealsCount: number;
  ignoredLeftoversCount: number;
  ignoredNotesCount: number;
  activeItemsAffectedCount: number;
  excludedPantryItemsCount: number;
}

export type RealtimeSyncState = 'disconnected' | 'connecting' | 'live' | 'reconnecting';

export type ShoppingRealtimeEventType =
  | 'ListCreated'
  | 'ListUpdated'
  | 'ListDeleted'
  | 'ItemAdded'
  | 'ItemUpdated'
  | 'ItemCheckedChanged'
  | 'ItemPromoted'
  | 'ItemDeleted'
  | 'CheckedItemsCleared'
  | 'ItemsGenerated';

export interface ShoppingListRealtimeEventDto {
  eventType: ShoppingRealtimeEventType;
  shoppingListId: string;
  shoppingList: ShoppingListDetailDto | null;
  changedItem: ShoppingListItemDto | null;
  deletedItemId: string | null;
  actorUserId: string | null;
  actorDisplayName: string | null;
  occurredAt: string;
}

export interface ShoppingListsCatalogChangedDto {
  eventType: ShoppingRealtimeEventType;
  affectedShoppingListId: string;
  lists: ShoppingListSummaryDto[];
  occurredAt: string;
}
