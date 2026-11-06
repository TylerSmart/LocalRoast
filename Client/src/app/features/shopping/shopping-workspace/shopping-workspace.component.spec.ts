import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { ActivatedRoute, Router } from '@angular/router';
import { Subject, of } from 'rxjs';
import {
  RealtimeSyncState,
  ShoppingListDetailDto,
  ShoppingListItemDto,
  ShoppingListRealtimeEventDto,
  ShoppingListSummaryDto,
  ShoppingListsCatalogChangedDto,
} from '../../../core/models/shopping.models';
import { ShoppingRealtimeService } from '../../../core/services/shopping/shopping-realtime.service';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import {
  SHOPPING_GROUP_BY_CATEGORY_STORAGE_KEY,
  ShoppingItemGroup,
  ShoppingWorkspaceComponent,
} from './shopping-workspace.component';

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

  const mockRealtimeSyncState = signal<RealtimeSyncState>('live');
  const mockListUpdated$ = new Subject<ShoppingListRealtimeEventDto>();
  const mockCatalogChanged$ = new Subject<ShoppingListsCatalogChangedDto>();
  const mockReconnected$ = new Subject<void>();

  const mockRealtimeService = {
    syncState: mockRealtimeSyncState,
    listUpdated$: mockListUpdated$,
    catalogChanged$: mockCatalogChanged$,
    reconnected$: mockReconnected$,
    connect: vi.fn().mockReturnValue(Promise.resolve()),
    disconnect: vi.fn().mockReturnValue(Promise.resolve()),
    watchList: vi.fn().mockReturnValue(Promise.resolve()),
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
    mockRealtimeService.connect.mockReset().mockReturnValue(Promise.resolve());
    mockRealtimeService.disconnect.mockReset().mockReturnValue(Promise.resolve());
    mockRealtimeService.watchList.mockReset().mockReturnValue(Promise.resolve());
    mockRealtimeSyncState.set('live');
    mockDialog.open.mockReset();
    mockSnackBar.open.mockReset();
    mockRouter.navigate.mockReset();

    await TestBed.configureTestingModule({
      imports: [ShoppingWorkspaceComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: ShoppingService, useValue: mockShoppingService },
        { provide: ShoppingRealtimeService, useValue: mockRealtimeService },
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

  it('should create and load shopping lists and connect realtime', () => {
    expect(component).toBeTruthy();
    expect(mockRealtimeService.connect).toHaveBeenCalled();
    expect(mockShoppingService.getShoppingLists).toHaveBeenCalled();
    expect(mockShoppingService.getShoppingListById).toHaveBeenCalledWith('list-1');
    expect(mockRealtimeService.watchList).toHaveBeenCalledWith('list-1');
    expect(component['lists']().length).toBe(2);
    expect(component['activeList']()?.name).toBe('Weekly Groceries');

    const compiled = fixture.nativeElement as HTMLElement;
    const syncPill = compiled.querySelector('.sync-status-pill.live');
    expect(syncPill).toBeTruthy();
    expect(syncPill?.textContent).toContain('Live Sync');
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

  it('reconciles active list when realtime listUpdated event arrives', () => {
    const updatedDetail: ShoppingListDetailDto = {
      ...mockDetail,
      activeItems: [
        mockActiveItem,
        {
          id: 'item-remote',
          shoppingListId: 'list-1',
          displayName: 'Organic Strawberries',
          unitLabel: 'box',
          formattedAmount: '2',
          dimension: 'Discrete',
          isPantryStapleExcluded: false,
          isChecked: false,
          sortOrder: 3,
          sources: [],
          createdAt: '2026-11-02T02:00:00Z',
          modifiedAt: '2026-11-02T02:00:00Z',
        },
      ],
    };

    mockListUpdated$.next({
      eventType: 'ItemAdded',
      shoppingListId: 'list-1',
      shoppingList: updatedDetail,
      changedItem: updatedDetail.activeItems[1],
      deletedItemId: null,
      actorUserId: 'user-2',
      actorDisplayName: 'Alex',
      occurredAt: new Date().toISOString(),
    });

    expect(component['activeList']()?.activeItems.length).toBe(2);
    expect(component['activeList']()?.activeItems[1].displayName).toBe('Organic Strawberries');
  });

  it('updates catalog when realtime catalogChanged event arrives', () => {
    const nextCatalog: ShoppingListSummaryDto[] = [
      ...mockLists,
      {
        id: 'list-3',
        name: 'Target Trip',
        isPinned: false,
        activeUncheckedCount: 0,
        checkedCount: 0,
        excludedPantryCount: 0,
        createdAt: '2026-11-02T03:00:00Z',
        modifiedAt: '2026-11-02T03:00:00Z',
      },
    ];

    mockCatalogChanged$.next({
      eventType: 'ListCreated',
      affectedShoppingListId: 'list-3',
      lists: nextCatalog,
      occurredAt: new Date().toISOString(),
    });

    expect(component['lists']().length).toBe(3);
    expect(component['lists']()[2].name).toBe('Target Trip');
  });

  it('disconnects on component destroy', () => {
    fixture.destroy();
    expect(mockRealtimeService.disconnect).toHaveBeenCalled();
  });

  describe('category grouping', () => {
    const categorizedDetail = (): ShoppingListDetailDto => ({
      ...mockDetail,
      activeItems: [
        {
          ...mockActiveItem,
          id: 'c-1',
          displayName: 'Curry Powder',
          category: 'SpicesAndSeasonings',
        },
        { ...mockActiveItem, id: 'c-2', displayName: 'Apples', category: 'Produce' },
        { ...mockActiveItem, id: 'c-3', displayName: 'Paper Towels' },
        { ...mockActiveItem, id: 'c-4', displayName: 'Lettuce', category: 'Produce' },
      ],
    });

    afterEach(() => localStorage.removeItem(SHOPPING_GROUP_BY_CATEGORY_STORAGE_KEY));

    it('groups active items by category in store-walk order', () => {
      component['activeList'].set(categorizedDetail());
      fixture.detectChanges();

      const groups = component['activeItemGroups']();
      expect(groups.map((g: ShoppingItemGroup) => g.category)).toEqual([
        'Produce',
        'SpicesAndSeasonings',
        'Other',
      ]);
      expect(groups[0].items.map((i) => i.displayName)).toEqual(['Apples', 'Lettuce']);

      const headings = Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll('.category-heading'),
      ).map((h) => h.textContent ?? '');
      expect(headings[0]).toContain('Produce');
      expect(headings[1]).toContain('Spices & Seasonings');
    });

    it('toggles to a flat list and persists the preference', () => {
      component['activeList'].set(categorizedDetail());
      component['toggleGroupByCategory']();
      fixture.detectChanges();

      const groups = component['activeItemGroups']();
      expect(groups.length).toBe(1);
      expect(groups[0].label).toBeNull();
      expect(groups[0].items.length).toBe(4);
      expect(localStorage.getItem(SHOPPING_GROUP_BY_CATEGORY_STORAGE_KEY)).toBe('false');
      expect((fixture.nativeElement as HTMLElement).querySelector('.category-heading')).toBeNull();
    });

    it('restores the flat-list preference from storage', () => {
      localStorage.setItem(SHOPPING_GROUP_BY_CATEGORY_STORAGE_KEY, 'false');
      const restored = TestBed.createComponent(ShoppingWorkspaceComponent).componentInstance;
      expect(restored['groupByCategory']()).toBe(false);
    });
  });
});
