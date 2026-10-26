import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login.component').then((m) => m.LoginComponent),
    canActivate: [guestGuard],
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
      },
      {
        path: 'recipes/new',
        loadComponent: () =>
          import('./features/recipes/recipe-editor/recipe-editor.component').then(
            (m) => m.RecipeEditorComponent,
          ),
      },
      {
        path: 'recipes/:id',
        loadComponent: () =>
          import('./features/recipes/recipe-detail/recipe-detail.component').then(
            (m) => m.RecipeDetailComponent,
          ),
      },
      {
        path: 'recipes/:id/edit',
        loadComponent: () =>
          import('./features/recipes/recipe-editor/recipe-editor.component').then(
            (m) => m.RecipeEditorComponent,
          ),
      },
      {
        path: 'matcher',
        loadComponent: () =>
          import('./features/recipes/fridge-matcher/fridge-matcher.component').then(
            (m) => m.FridgeMatcherComponent,
          ),
      },
      {
        path: 'catalog',
        loadComponent: () =>
          import('./features/food-catalog/food-catalog/food-catalog.component').then(
            (m) => m.FoodCatalogComponent,
          ),
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
