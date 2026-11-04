export interface FoodItemDto {
  id: string;
  name: string;
  isPantryStaple: boolean;
  aliases: string[];
  referencedRecipeCount: number;
  referencedShoppingListItemCount?: number;
  createdAt: string;
  modifiedAt: string;
}

export interface FoodItemQueryParameters {
  search?: string | null;
  isPantryStaple?: boolean | null;
}

export interface CreateFoodItemRequest {
  name: string;
  isPantryStaple?: boolean;
  aliases?: string[] | null;
}

export interface UpdateFoodItemRequest {
  name: string;
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
