import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login.component').then((m) => m.LoginComponent),
    canActivate: [guestGuard],
    title: 'LocalRoast | Login',
  },
  {
    path: 'share/:token',
    loadComponent: () =>
      import('./features/recipes/public-shared-recipe/public-shared-recipe.component').then(
        (m) => m.PublicSharedRecipeComponent,
      ),
    title: 'LocalRoast | Recipe',
  },
  {
    path: 'recipes/:id/cook',
    loadComponent: () =>
      import('./features/recipes/cook-mode/cook-mode.component').then((m) => m.CookModeComponent),
    canActivate: [authGuard],
    title: 'LocalRoast | Cook Recipe',
  },
  {
    path: '',
    loadComponent: () =>
      import('./features/shell/app-shell/app-shell.component').then((m) => m.AppShellComponent),
    canActivate: [authGuard],
    children: [
      {
        path: '',
        pathMatch: 'full',
        redirectTo: 'recipes',
      },
      {
        path: 'recipes',
        loadComponent: () =>
          import('./features/recipes/recipe-list/recipe-list.component').then(
            (m) => m.RecipeListComponent,
          ),
        title: 'LocalRoast | Cookbook',
      },
      {
        path: 'recipes/new',
        loadComponent: () =>
          import('./features/recipes/recipe-editor/recipe-editor.component').then(
            (m) => m.RecipeEditorComponent,
          ),
        title: 'LocalRoast | New Recipe',
      },
      {
        path: 'recipes/:id',
        loadComponent: () =>
          import('./features/recipes/recipe-detail/recipe-detail.component').then(
            (m) => m.RecipeDetailComponent,
          ),
        title: 'LocalRoast | Recipe',
      },
      {
        path: 'recipes/:id/edit',
        loadComponent: () =>
          import('./features/recipes/recipe-editor/recipe-editor.component').then(
            (m) => m.RecipeEditorComponent,
          ),
        title: 'LocalRoast | Edit Recipe',
      },
      {
        path: 'meal-plan',
        data: { fullWidth: true },
        loadComponent: () =>
          import('./features/meal-plans/meal-plan-calendar/meal-plan-calendar.component').then(
            (m) => m.MealPlanCalendarComponent,
          ),
        title: 'LocalRoast | Meal Plan',
      },
      {
        path: 'shopping',
        loadComponent: () =>
          import('./features/shopping/shopping-workspace/shopping-workspace.component').then(
            (m) => m.ShoppingWorkspaceComponent,
          ),
        title: 'LocalRoast | Shop',
      },
      {
        path: 'ingest',
        loadComponent: () =>
          import('./features/ingestion/ingestion-hub/ingestion-hub.component').then(
            (m) => m.IngestionHubComponent,
          ),
        title: 'LocalRoast | Ingest',
      },
      {
        path: 'ingest/review/:id',
        loadComponent: () =>
          import('./features/ingestion/ingestion-review/ingestion-review.component').then(
            (m) => m.IngestionReviewComponent,
          ),
        title: 'LocalRoast | Review Recipe',
      },
      {
        path: 'matcher',
        loadComponent: () =>
          import('./features/recipes/fridge-matcher/fridge-matcher.component').then(
            (m) => m.FridgeMatcherComponent,
          ),
        title: 'LocalRoast | Matcher',
      },
      {
        path: 'catalog',
        loadComponent: () =>
          import('./features/food-catalog/food-catalog/food-catalog.component').then(
            (m) => m.FoodCatalogComponent,
          ),
        title: 'LocalRoast | Food Catalog',
      },
      {
        path: 'dashboard',
        redirectTo: 'recipes',
      },
    ],
  },
  {
    path: '**',
    redirectTo: 'recipes',
  },
];
