import { ThemeConfig } from '../services/theme/theme.models';

export interface AccountDto {
  id: string;
  email: string;
  username: string;
  hasPassword: boolean;
  externalProvider: string | null;
  themeSettings?: string | null;
}

export interface UpdateProfileRequest {
  username: string;
  email: string;
  currentPassword?: string | null;
}

export interface ChangePasswordRequest {
  currentPassword?: string | null;
  newPassword: string;
}

export interface UpdateThemeSettingsRequest {
  settings: ThemeConfig;
}

export interface ResetAppDataResultDto {
  deletedRecipesCount: number;
  deletedMealPlansCount: number;
  deletedShoppingListsCount: number;
  deletedIngestionJobsCount: number;
  deletedFoodItemsCount: number;
  deletedTagsCount: number;
  deletedCollectionsCount: number;
  resetAt: string;
}
