export type MealSlot = 'Breakfast' | 'Lunch' | 'Dinner' | 'SnackPrep';

export interface MealSlotMeta {
  value: MealSlot;
  label: string;
  shortLabel: string;
  icon: string;
}

export const MEAL_SLOTS: MealSlotMeta[] = [
  { value: 'Breakfast', label: 'Breakfast', shortLabel: 'Bkfst', icon: 'breakfast_dining' },
  { value: 'Lunch', label: 'Lunch', shortLabel: 'Lunch', icon: 'lunch_dining' },
  { value: 'Dinner', label: 'Dinner', shortLabel: 'Dinner', icon: 'dinner_dining' },
  { value: 'SnackPrep', label: 'Snack / Prep', shortLabel: 'Prep', icon: 'cookie' },
];

export type MealPlanEntryType = 'Recipe' | 'Leftovers' | 'FreeformNote';

export type ApplyTemplateMode = 'Merge' | 'Replace';

export interface MealPlanRecipeBadgeDto {
  id: string;
  title: string;
  slug: string;
  baseServings: number;
  totalTimeMinutes: number | null;
  difficulty: string | null;
  thumbnailImagePath: string | null;
}

export interface MealPlanEntryDto {
  id: string;
  weekStartDate: string;
  isOnDeck: boolean;
  scheduledDate: string | null;
  slot: MealSlot | null;
  sortOrder: number;
  entryType: MealPlanEntryType;
  isLeftovers: boolean;
  targetServings: number | null;
  displayTitle: string;
  customTitle: string | null;
  notes: string | null;
  sourceMealPlanEntryId: string | null;
  recipe: MealPlanRecipeBadgeDto | null;
  createdAt: string;
  createdBy: string;
  modifiedAt: string;
  modifiedBy: string;
}

export interface MealPlanDayDto {
  date: string;
  dayOffset: number;
  entries: MealPlanEntryDto[];
}

export interface MealPlanWeekDto {
  weekStartDate: string;
  weekEndDate: string;
  days: MealPlanDayDto[];
  onDeckEntries: MealPlanEntryDto[];
}

export interface MealPlanRangeResponseDto {
  startDate: string;
  endDate: string;
  scheduledEntries: MealPlanEntryDto[];
  onDeckEntries: MealPlanEntryDto[];
}

export interface CreateMealPlanEntryRequest {
  entryType?: MealPlanEntryType;
  isOnDeck?: boolean;
  weekStartDate?: string | null;
  scheduledDate?: string | null;
  slot?: MealSlot | null;
  recipeId?: string | null;
  targetServings?: number | null;
  sourceMealPlanEntryId?: string | null;
  customTitle?: string | null;
  notes?: string | null;
}

export interface UpdateMealPlanEntryRequest {
  entryType?: MealPlanEntryType;
  isOnDeck?: boolean;
  weekStartDate?: string | null;
  scheduledDate?: string | null;
  slot?: MealSlot | null;
  recipeId?: string | null;
  targetServings?: number | null;
  sourceMealPlanEntryId?: string | null;
  customTitle?: string | null;
  notes?: string | null;
}

export interface MoveMealPlanEntryRequest {
  isOnDeck?: boolean;
  weekStartDate?: string | null;
  scheduledDate?: string | null;
  slot?: MealSlot | null;
  targetSortOrder?: number | null;
}

export interface CookTonightRequest {
  targetDate?: string | null;
  slot?: MealSlot;
}

export interface MealPlanTemplateItemDto {
  id: string;
  isOnDeck: boolean;
  dayOffset: number | null;
  slot: MealSlot | null;
  sortOrder: number;
  entryType: MealPlanEntryType;
  isLeftovers: boolean;
  targetServings: number | null;
  displayTitle: string;
  customTitle: string | null;
  notes: string | null;
  recipe: MealPlanRecipeBadgeDto | null;
}

export interface MealPlanTemplateSummaryDto {
  id: string;
  name: string;
  description: string | null;
  itemCount: number;
  scheduledItemCount: number;
  onDeckItemCount: number;
  createdAt: string;
  modifiedAt: string;
}

export interface MealPlanTemplateDetailDto {
  id: string;
  name: string;
  description: string | null;
  items: MealPlanTemplateItemDto[];
  createdAt: string;
  createdBy: string;
  modifiedAt: string;
  modifiedBy: string;
}

export interface MealPlanTemplateItemInput {
  isOnDeck?: boolean;
  dayOffset?: number | null;
  slot?: MealSlot | null;
  entryType?: MealPlanEntryType;
  recipeId?: string | null;
  targetServings?: number | null;
  customTitle?: string | null;
  notes?: string | null;
}

export interface UpsertMealPlanTemplateRequest {
  name: string;
  description?: string | null;
  items?: MealPlanTemplateItemInput[] | null;
}

export interface CreateTemplateFromWeekRequest {
  weekStartDate: string;
  name: string;
  description?: string | null;
  includeOnDeck?: boolean;
}

export interface ApplyMealPlanTemplateRequest {
  targetWeekStartDate: string;
  mode?: ApplyTemplateMode;
}
