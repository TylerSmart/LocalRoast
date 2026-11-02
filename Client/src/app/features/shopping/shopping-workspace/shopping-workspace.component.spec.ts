import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import {
  ShoppingListDetailDto,
  ShoppingListItemDto,
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import { ShoppingWorkspaceComponent } from './shopping-workspace.component';

describe('ShoppingWorkspaceComponent', () => {
  let component: ShoppingWorkspaceComponent;
  let fixture: ComponentFixture<ShoppingWorkspaceComponent>;

  const mockShoppingService = {
    getShoppingLists: vi.fn(),
    getShoppingListById: vi.fn(),
    createShoppingList: vi.fn(),
    updateShoppingList: vi.fn(),
    deleteShoppingList: vi.fn(),
    addItem: vi.fn(),
    updateItem: vi.fn(),
    setItemChecked: vi.fn(),
    promotePantryStaple: vi.fn(),
    deleteItem: vi.fn(),
    clearCheckedItems: vi.fn(),
    generateFromMealPlan: vi.fn(),
    addRecipeToList: vi.fn(),
  };

  const mockDialog = {
    open: vi.fn(),
  };

  const mockSnackBar = {
    open: vi.fn(),
  };

  const mockRouter = {
    navigate: vi.fn(),
  };

  const mockLists: ShoppingListSummaryDto[] = [
    {
      id: 'list-1',
      name: 'Weekly Groceries',
      isPinned: true,
      activeUncheckedCount: 1,
      checkedCount: 1,
      excludedPantryCount: 1,
      createdAt: '2026-11-01T00:00:00Z',
      modifiedAt: '2026-11-01T00:00:00Z',
    },
    {
      id: 'list-2',
      name: 'Costco Run',
      isPinned: false,
      activeUncheckedCount: 1,
      checkedCount: 0,
      excludedPantryCount: 0,
      createdAt: '2026-11-01T00:00:00Z',
      modifiedAt: '2026-11-01T00:00:00Z',
    },
  ];

  const mockActiveItem: ShoppingListItemDto = {
    id: 'item-1',
    shoppingListId: 'list-1',
    foodItemId: 'food-1',
    displayName: 'Garlic',
    quantity: 7,
    maxQuantity: null,
    unit: 'Clove',
    customUnitName: null,
    unitLabel: 'cloves',
    formattedAmount: '7',
    dimension: 'Discrete',
    notes: null,
    isPantryStapleExcluded: false,
    isChecked: false,
    checkedAt: null,
    checkedByUserId: null,
    checkedByDisplayName: null,
    sortOrder: 0,
    sources: [
      {
        id: 'src-1',
        sourceKind: 'MealPlanEntry',
        mealPlanEntryId: 'mpe-1',
        recipeId: 'rec-1',
        sourceLabel: 'Mon: Garlic Bread',
        contributedQuantity: 4,
      },
    ],
    createdAt: '2026-11-01T00:00:00Z',
    modifiedAt: '2026-11-01T00:00:00Z',
  };

  const mockCheckedItem: ShoppingListItemDto = {
    id: 'item-2',
    shoppingListId: 'list-1',
    foodItemId: 'food-2',
    displayName: 'Milk',
    quantity: 1,
    maxQuantity: null,
    unit: 'Gal',
    customUnitName: null,
    unitLabel: 'gal',
    formattedAmount: '1',
    dimension: 'Volume',
    notes: null,
    isPantryStapleExcluded: false,
    isChecked: true,
    checkedAt: '2026-11-02T01:00:00Z',
    checkedByUserId: 'user-1',
    checkedByDisplayName: 'Tyler',
    sortOrder: 1,
    sources: [],
    createdAt: '2026-11-01T00:00:00Z',
    modifiedAt: '2026-11-01T00:00:00Z',
  };

  const mockExcludedPantryItem: ShoppingListItemDto = {
    id: 'item-3',
    shoppingListId: 'list-1',
    foodItemId: 'food-3',
    displayName: 'Olive Oil',
    quantity: 0.5,
    maxQuantity: null,
    unit: 'Cup',
    customUnitName: null,
    unitLabel: 'cup',
    formattedAmount: '1/2',
    dimension: 'Volume',
    notes: null,
    isPantryStapleExcluded: true,
    isChecked: false,
    checkedAt: null,
    checkedByUserId: null,
    checkedByDisplayName: null,
    sortOrder: 2,
    sources: [],
    createdAt: '2026-11-01T00:00:00Z',
    modifiedAt: '2026-11-01T00:00:00Z',
  };

  const mockDetail: ShoppingListDetailDto = {
    id: 'list-1',
    name: 'Weekly Groceries',
    isPinned: true,
    activeItems: [mockActiveItem],
    checkedItems: [mockCheckedItem],
    excludedPantryItems: [mockExcludedPantryItem],
    createdAt: '2026-11-01T00:00:00Z',
    modifiedAt: '2026-11-01T00:00:00Z',
  };

  beforeEach(async () => {
    mockShoppingService.getShoppingLists.mockReset().mockReturnValue(of(mockLists));
    mockShoppingService.getShoppingListById.mockReset().mockReturnValue(of(mockDetail));
    mockShoppingService.addItem.mockReset();
    mockShoppingService.setItemChecked.mockReset();
    mockShoppingService.promotePantryStaple.mockReset();
    mockShoppingService.clearCheckedItems.mockReset();
    mockShoppingService.deleteItem.mockReset();
    mockDialog.open.mockReset();
    mockSnackBar.open.mockReset();
    mockRouter.navigate.mockReset();

    await TestBed.configureTestingModule({
      imports: [ShoppingWorkspaceComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: ShoppingService, useValue: mockShoppingService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: Router, useValue: mockRouter },
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: of(new Map([['listId', 'list-1']])),
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ShoppingWorkspaceComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create and load shopping lists', () => {
    expect(component).toBeTruthy();
    expect(mockShoppingService.getShoppingLists).toHaveBeenCalled();
    expect(mockShoppingService.getShoppingListById).toHaveBeenCalledWith('list-1');
    expect(component['lists']().length).toBe(2);
    expect(component['activeList']()?.name).toBe('Weekly Groceries');
  });

  it('should quick-add an item and refresh the active list', () => {
    const newItem: ShoppingListItemDto = {
      ...mockActiveItem,
      id: 'item-new',
      displayName: 'Paper Towels',
      sources: [],
    };
    mockShoppingService.addItem.mockReturnValue(of(newItem));

    component['quickAddName'].set('Paper Towels');
    component.onQuickAddSubmit();

    expect(mockShoppingService.addItem).toHaveBeenCalledWith('list-1', {
      displayName: 'Paper Towels',
      consolidateIfCompatible: true,
    });
    expect(component['quickAddName']()).toBe('');
  });

  it('should optimistically toggle item checked and call service', () => {
    const checkedItemResult = { ...mockActiveItem, isChecked: true };
    mockShoppingService.setItemChecked.mockReturnValue(of(checkedItemResult));

    component.toggleItemChecked(mockActiveItem, true);

    // activeItems should no longer contain item-1
    expect(component['activeList']()?.activeItems.some((i) => i.id === 'item-1')).toBe(false);
    // checkedItems should now contain item-1
    expect(component['activeList']()?.checkedItems.some((i) => i.id === 'item-1')).toBe(true);
    expect(mockShoppingService.setItemChecked).toHaveBeenCalledWith('list-1', 'item-1', true);
  });

  it('should promote a pantry staple and refresh list', () => {
    const promotedItem = { ...mockExcludedPantryItem, isPantryStapleExcluded: false };
    mockShoppingService.promotePantryStaple.mockReturnValue(of(promotedItem));

    component.promotePantryStaple(mockExcludedPantryItem);

    expect(mockShoppingService.promotePantryStaple).toHaveBeenCalledWith('list-1', 'item-3');
    expect(mockSnackBar.open).toHaveBeenCalledWith(
      expect.stringMatching(/Added Olive Oil to active list/),
      expect.any(String),
      expect.any(Object),
    );
  });

  it('should clear checked items', () => {
    mockShoppingService.clearCheckedItems.mockReturnValue(of({ removedCount: 1 }));

    component.clearCheckedItems();

    expect(mockShoppingService.clearCheckedItems).toHaveBeenCalledWith('list-1');
    expect(mockSnackBar.open).toHaveBeenCalledWith(
      expect.stringMatching(/Cleared 1 checked item/),
      expect.any(String),
      expect.any(Object),
    );
  });

  it('should delete an item', () => {
    mockShoppingService.deleteItem.mockReturnValue(of(undefined));

    component.deleteItem(mockActiveItem);

    expect(mockShoppingService.deleteItem).toHaveBeenCalledWith('list-1', 'item-1');
  });
});
