import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { of } from 'rxjs';
import {
  NEW_SHOPPING_LIST_ID,
  ShoppingListDetailDto,
  ShoppingListGenerationResultDto,
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import {
  buildDefaultWeeklyShoppingListName,
  getTodayIsoLocal,
  normalizeToSundayIso,
} from '../../../core/utils/meal-plan-date';
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
    createAndAddRecipeToList: vi.fn(),
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
    mockShoppingService.createAndAddRecipeToList.mockReset();
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

  it('defaults to inline new weekly list creation when user has no shopping lists', () => {
    mockShoppingService.getShoppingLists.mockReturnValue(of([]));
    const emptyFixture = TestBed.createComponent(AddRecipeToListDialogComponent);
    const emptyComponent = emptyFixture.componentInstance;
    emptyFixture.detectChanges();

    const el = emptyFixture.nativeElement as HTMLElement;
    expect(emptyComponent.form.controls.targetShoppingListId.value).toBe(NEW_SHOPPING_LIST_ID);
    expect(emptyComponent.isCreatingNewList()).toBe(true);
    expect(emptyComponent.form.controls.newListName.value).toMatch(/^Week of /);

    const submitBtn = el.querySelector('button[color="primary"]') as HTMLButtonElement;
    expect(submitBtn.textContent?.trim()).toBe('Create & Add Ingredients');

    const mockResult: ShoppingListGenerationResultDto = {
      shoppingList: {
        id: 'new-list-1',
        name: emptyComponent.form.controls.newListName.value,
        isPinned: false,
        activeItems: [],
        checkedItems: [],
        excludedPantryItems: [],
        createdAt: '2026-11-24T00:00:00Z',
        modifiedAt: '2026-11-24T00:00:00Z',
      },
      importedMealsCount: 1,
      skippedDuplicateMealsCount: 0,
      ignoredLeftoversCount: 0,
      ignoredNotesCount: 0,
      activeItemsAffectedCount: 2,
      excludedPantryItemsCount: 0,
    };
    mockShoppingService.createAndAddRecipeToList.mockReturnValue(of(mockResult));

    emptyComponent.onSubmit();

    expect(mockShoppingService.createAndAddRecipeToList).toHaveBeenCalledWith({
      name: emptyComponent.form.controls.newListName.value,
      isPinned: false,
      recipe: {
        recipeId: 'rec-1',
        targetServings: 6,
        includeSubRecipes: true,
        includeOptionalIngredients: true,
      },
    });
    expect(mockDialogRef.close).toHaveBeenCalledWith(mockResult);
  });

  it('deduplicates weekly list name when switching to create new list alongside colliding lists', () => {
    const weekStartIso = normalizeToSundayIso(getTodayIsoLocal());
    const baseWeeklyName = buildDefaultWeeklyShoppingListName(weekStartIso, []);
    const collidingLists: ShoppingListSummaryDto[] = [
      {
        id: 'list-existing',
        name: baseWeeklyName,
        isPinned: true,
        activeUncheckedCount: 0,
        checkedCount: 0,
        excludedPantryCount: 0,
        createdAt: '2026-11-24T00:00:00Z',
        modifiedAt: '2026-11-24T00:00:00Z',
      },
    ];
    mockShoppingService.getShoppingLists.mockReturnValue(of(collidingLists));

    const collideFixture = TestBed.createComponent(AddRecipeToListDialogComponent);
    const collideComponent = collideFixture.componentInstance;
    collideFixture.detectChanges();

    expect(collideComponent.form.controls.targetShoppingListId.value).toBe('list-existing');
    collideComponent.form.controls.targetShoppingListId.setValue(NEW_SHOPPING_LIST_ID);
    collideFixture.detectChanges();

    expect(collideComponent.isCreatingNewList()).toBe(true);
    expect(collideComponent.form.controls.newListName.value).toBe(`${baseWeeklyName} (2)`);

    const mockResult: ShoppingListGenerationResultDto = {
      shoppingList: {
        id: 'new-list-2',
        name: `${baseWeeklyName} (2)`,
        isPinned: false,
        activeItems: [],
        checkedItems: [],
        excludedPantryItems: [],
        createdAt: '2026-11-24T00:00:00Z',
        modifiedAt: '2026-11-24T00:00:00Z',
      },
      importedMealsCount: 1,
      skippedDuplicateMealsCount: 0,
      ignoredLeftoversCount: 0,
      ignoredNotesCount: 0,
      activeItemsAffectedCount: 1,
      excludedPantryItemsCount: 0,
    };
    mockShoppingService.createAndAddRecipeToList.mockReturnValue(of(mockResult));

    collideComponent.onSubmit();

    expect(mockShoppingService.createAndAddRecipeToList).toHaveBeenCalledWith({
      name: `${baseWeeklyName} (2)`,
      isPinned: false,
      recipe: {
        recipeId: 'rec-1',
        targetServings: 6,
        includeSubRecipes: true,
        includeOptionalIngredients: true,
      },
    });
    expect(mockDialogRef.close).toHaveBeenCalledWith(mockResult);
  });
});
