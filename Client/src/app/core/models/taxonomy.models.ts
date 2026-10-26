export type TagCategory = 'Course' | 'Cuisine' | 'Dietary' | 'Equipment' | 'Custom';

export interface CollectionDto {
  id: string;
  name: string;
  slug: string;
  description: string | null;
  sortOrder: number;
  isSystemSeeded: boolean;
  recipeCount: number;
}

export interface UpsertCollectionRequest {
  name: string;
  description?: string | null;
  sortOrder?: number;
}

export interface TagDto {
  id: string;
  name: string;
  slug: string;
  category: TagCategory;
  sortOrder: number;
  isSystemSeeded: boolean;
  recipeCount: number;
}

export interface UpsertTagRequest {
  name: string;
  category: TagCategory;
  sortOrder?: number;
}

export interface RecipeCollectionBadgeDto {
  id: string;
  name: string;
  slug: string;
}

export interface RecipeTagBadgeDto {
  id: string;
  name: string;
  slug: string;
  category: TagCategory;
}
