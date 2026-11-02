export interface StoreAisleDto {
  id: string;
  name: string;
  sortOrder: number;
  isSystemSeeded: boolean;
}

export interface FoodItemDto {
  id: string;
  name: string;
  defaultStoreAisleId: string | null;
  defaultStoreAisleName: string | null;
  defaultStoreAisleSortOrder: number | null;
  isPantryStaple: boolean;
  aliases: string[];
  referencedRecipeCount: number;
  referencedShoppingListItemCount?: number;
  createdAt: string;
  modifiedAt: string;
}

export interface FoodItemQueryParameters {
  search?: string | null;
  storeAisleId?: string | null;
  isPantryStaple?: boolean | null;
}

export interface CreateFoodItemRequest {
  name: string;
  defaultStoreAisleId?: string | null;
  isPantryStaple?: boolean;
  aliases?: string[] | null;
}

export interface UpdateFoodItemRequest {
  name: string;
  defaultStoreAisleId?: string | null;
  isPantryStaple?: boolean;
  aliases?: string[] | null;
}

export interface MergeFoodItemsRequest {
  targetFoodItemId: string;
  sourceFoodItemIds: string[];
  keepSourceNamesAsAliases?: boolean;
}

export interface MergeFoodItemsResultDto {
  targetFoodItem: FoodItemDto;
  mergedSourceCount: number;
  repointedRecipeIngredientCount: number;
  repointedShoppingListItemCount: number;
  consolidatedShoppingListItemCount: number;
  addedAliases: string[];
}
