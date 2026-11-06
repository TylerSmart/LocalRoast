export type FoodCategory =
  | 'Produce'
  | 'MeatAndSeafood'
  | 'DairyAndEggs'
  | 'Refrigerated'
  | 'Bakery'
  | 'PantryDryGoods'
  | 'CannedAndJarred'
  | 'CondimentsAndSauces'
  | 'SpicesAndSeasonings'
  | 'Baking'
  | 'Snacks'
  | 'Beverages'
  | 'Frozen'
  | 'Other';

/** Store-walk order, matching the server's FoodCategory declaration order. */
export const FOOD_CATEGORY_OPTIONS: readonly { value: FoodCategory; label: string }[] = [
  { value: 'Produce', label: 'Produce' },
  { value: 'MeatAndSeafood', label: 'Meat & Seafood' },
  { value: 'DairyAndEggs', label: 'Dairy & Eggs' },
  { value: 'Refrigerated', label: 'Refrigerated' },
  { value: 'Bakery', label: 'Bakery & Bread' },
  { value: 'PantryDryGoods', label: 'Pantry & Dry Goods' },
  { value: 'CannedAndJarred', label: 'Canned & Jarred' },
  { value: 'CondimentsAndSauces', label: 'Condiments & Sauces' },
  { value: 'SpicesAndSeasonings', label: 'Spices & Seasonings' },
  { value: 'Baking', label: 'Baking' },
  { value: 'Snacks', label: 'Snacks' },
  { value: 'Beverages', label: 'Beverages' },
  { value: 'Frozen', label: 'Frozen' },
  { value: 'Other', label: 'Other' },
];

export function foodCategoryLabel(category: FoodCategory | null | undefined): string {
  return FOOD_CATEGORY_OPTIONS.find((o) => o.value === category)?.label ?? 'Other';
}
export interface FoodItemDto {
  id: string;
  name: string;
  isPantryStaple: boolean;
  aliases: string[];
  referencedRecipeCount: number;
  referencedShoppingListItemCount?: number;
  category?: FoodCategory;
  createdAt: string;
  modifiedAt: string;
}

export interface FoodItemQueryParameters {
  search?: string | null;
  isPantryStaple?: boolean | null;
  category?: FoodCategory | null;
  page?: number;
  pageSize?: number;
}

export interface CreateFoodItemRequest {
  name: string;
  isPantryStaple?: boolean;
  aliases?: string[] | null;
  category?: FoodCategory | null;
}

export interface UpdateFoodItemRequest {
  name: string;
  isPantryStaple?: boolean;
  aliases?: string[] | null;
  category?: FoodCategory | null;
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

export interface FoodItemRecipeUsageDto {
  recipeId: string;
  recipeTitle: string;
  recipeSlug: string;
  thumbnailImagePath?: string | null;
  status: string;
  ingredientDisplayNames: string[];
}

export interface UnlinkFoodItemRecipesResultDto {
  foodItemId: string;
  unlinkedRecipeCount: number;
  unlinkedIngredientCount: number;
}
