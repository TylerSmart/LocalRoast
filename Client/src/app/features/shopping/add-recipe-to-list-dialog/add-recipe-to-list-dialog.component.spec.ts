import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { of } from 'rxjs';
import {
  ShoppingListDetailDto,
  ShoppingListGenerationResultDto,
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import {
  AddRecipeToListDialogComponent,
  AddRecipeToListDialogData,
} from './add-recipe-to-list-dialog.component';

describe('AddRecipeToListDialogComponent', () => {
  let component: AddRecipeToListDialogComponent;
  let fixture: ComponentFixture<AddRecipeToListDialogComponent>;

  const mockShoppingService = {
    getShoppingLists: vi.fn(),
    addRecipeToList: vi.fn(),
  };

  const mockDialogRef = {
    close: vi.fn(),
  };

  const dialogData: AddRecipeToListDialogData = {
    recipeId: 'rec-1',
    recipeTitle: 'Chicken Tacos',
    targetServings: 6,
    hasSubRecipes: true,
  };

  const mockLists: ShoppingListSummaryDto[] = [
    {
      id: 'list-1',
      name: 'Weekly Groceries',
      isPinned: true,
      activeUncheckedCount: 0,
      checkedCount: 0,
      excludedPantryCount: 0,
      createdAt: '2026-11-02T00:00:00Z',
      modifiedAt: '2026-11-02T00:00:00Z',
    },
  ];

  beforeEach(async () => {
    mockShoppingService.getShoppingLists.mockReset().mockReturnValue(of(mockLists));
    mockShoppingService.addRecipeToList.mockReset();
    mockDialogRef.close.mockReset();

    await TestBed.configureTestingModule({
      imports: [AddRecipeToListDialogComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: ShoppingService, useValue: mockShoppingService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(AddRecipeToListDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('initializes form with data and selects default list', () => {
    expect(component).toBeTruthy();
    expect(component.form.controls.targetShoppingListId.value).toBe('list-1');
    expect(component.form.controls.targetServings.value).toBe(6);
  });

  it('submits recipe ingredients and closes dialog with result', () => {
    const mockDetail: ShoppingListDetailDto = {
      id: 'list-1',
      name: 'Weekly Groceries',
      isPinned: true,
      activeItems: [],
      checkedItems: [],
      excludedPantryItems: [],
      createdAt: '2026-11-02T00:00:00Z',
      modifiedAt: '2026-11-02T00:00:00Z',
    };
    const mockResult: ShoppingListGenerationResultDto = {
      shoppingList: mockDetail,
      importedMealsCount: 1,
      skippedDuplicateMealsCount: 0,
      ignoredLeftoversCount: 0,
      ignoredNotesCount: 0,
      activeItemsAffectedCount: 2,
      excludedPantryItemsCount: 1,
    };

    mockShoppingService.addRecipeToList.mockReturnValue(of(mockResult));

    component.onSubmit();

    expect(mockShoppingService.addRecipeToList).toHaveBeenCalledWith(
      'list-1',
      expect.objectContaining({
        recipeId: 'rec-1',
        targetServings: 6,
        includeSubRecipes: true,
        includeOptionalIngredients: true,
      }),
    );
    expect(mockDialogRef.close).toHaveBeenCalledWith(mockResult);
  });
});
