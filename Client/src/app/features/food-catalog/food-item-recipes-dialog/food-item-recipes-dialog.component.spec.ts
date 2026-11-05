import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { FoodItemDto, FoodItemRecipeUsageDto } from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import {
  FoodItemRecipesDialogComponent,
  FoodItemRecipesDialogData,
} from './food-item-recipes-dialog.component';

describe('FoodItemRecipesDialogComponent', () => {
  let component: FoodItemRecipesDialogComponent;
  let fixture: ComponentFixture<FoodItemRecipesDialogComponent>;

  const mockFoodItem: FoodItemDto = {
    id: 'food-123',
    name: 'Null',
    isPantryStaple: false,
    aliases: [],
    referencedRecipeCount: 2,
    createdAt: new Date().toISOString(),
    modifiedAt: new Date().toISOString(),
  };

  const mockRecipes: FoodItemRecipeUsageDto[] = [
    {
      recipeId: 'rec-1',
      recipeTitle: 'Crispy Salmon',
      recipeSlug: 'crispy-salmon',
      thumbnailImagePath: null,
      status: 'Published',
      ingredientDisplayNames: ['salt to taste', 'black pepper'],
    },
    {
      recipeId: 'rec-2',
      recipeTitle: 'Roast Chicken',
      recipeSlug: 'roast-chicken',
      thumbnailImagePath: '/api/uploads/chicken.jpg',
      status: 'Published',
      ingredientDisplayNames: ['kosher salt'],
    },
  ];

  const dialogData: FoodItemRecipesDialogData = {
    foodItem: mockFoodItem,
  };

  const mockCatalogService = {
    getFoodItemRecipes: vi.fn(),
    getFoodItemRecipesPaged: vi.fn().mockReturnValue(
      of({
        items: mockRecipes,
        totalCount: mockRecipes.length,
        pageNumber: 1,
        pageSize: 15,
        totalPages: 1,
        hasPreviousPage: false,
        hasNextPage: false,
      }),
    ),
    unlinkFoodItemRecipes: vi.fn(),
  };

  const mockDialogRef = {
    close: vi.fn(),
  };

  const mockRouter = {
    navigate: vi.fn(),
  };

  const mockSnackBar = {
    open: vi.fn(),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    mockCatalogService.getFoodItemRecipesPaged.mockReturnValue(
      of({
        items: mockRecipes,
        totalCount: mockRecipes.length,
        pageNumber: 1,
        pageSize: 15,
        totalPages: 1,
        hasPreviousPage: false,
        hasNextPage: false,
      }),
    );

    await TestBed.configureTestingModule({
      imports: [FoodItemRecipesDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: FoodCatalogService, useValue: mockCatalogService },
        { provide: Router, useValue: mockRouter },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(FoodItemRecipesDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads and displays referencing recipes on init', () => {
    expect(mockCatalogService.getFoodItemRecipesPaged).toHaveBeenCalledWith('food-123', 1, 15);
    expect(component.isLoading()).toBe(false);
    expect(component.recipes().length).toBe(2);
    expect(component.recipes()[0].recipeTitle).toBe('Crispy Salmon');
    expect(component.recipes()[0].ingredientDisplayNames).toContain('salt to taste');
  });

  it('navigates to recipe when clicked and closes dialog', () => {
    component.navigateToRecipe('rec-1');
    expect(mockDialogRef.close).toHaveBeenCalledWith({ unlinked: false });
    expect(mockRouter.navigate).toHaveBeenCalledWith(['/recipes', 'rec-1']);
  });

  it('unlinks all recipes and clears list on success', () => {
    mockCatalogService.unlinkFoodItemRecipes.mockReturnValue(
      of({
        foodItemId: 'food-123',
        unlinkedRecipeCount: 2,
        unlinkedIngredientCount: 3,
      }),
    );

    component.unlinkAll();

    expect(mockCatalogService.unlinkFoodItemRecipes).toHaveBeenCalledWith('food-123');
    expect(component.isUnlinking()).toBe(false);
    expect(component.recipes().length).toBe(0);
    expect(component.unlinkedResult()?.unlinkedRecipeCount).toBe(2);
    expect(mockSnackBar.open).toHaveBeenCalled();
  });

  it('handles load error gracefully', () => {
    mockCatalogService.getFoodItemRecipesPaged.mockReturnValue(
      throwError(() => ({ error: { detail: 'Server error' } })),
    );

    component.loadRecipes();

    expect(component.isLoading()).toBe(false);
    expect(component.errorMessage()).toBe('Server error');
  });
});
