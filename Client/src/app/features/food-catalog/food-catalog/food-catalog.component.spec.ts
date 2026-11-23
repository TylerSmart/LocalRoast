import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';
import { FoodItemDto, FoodItemQueryParameters } from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { FoodCatalogComponent } from './food-catalog.component';

describe('FoodCatalogComponent', () => {
  let component: FoodCatalogComponent;
  let fixture: ComponentFixture<FoodCatalogComponent>;

  const sampleItems: FoodItemDto[] = [
    {
      id: 'f-1',
      name: 'Olive Oil',
      isPantryStaple: true,
      aliases: ['EVOO'],
      referencedRecipeCount: 3,
      referencedShoppingListItemCount: 2,
      category: 'CondimentsAndSauces',
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
    {
      id: 'f-2',
      name: 'Garlic',
      isPantryStaple: false,
      aliases: [],
      referencedRecipeCount: 5,
      referencedShoppingListItemCount: 0,
      category: 'Produce',
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
  ];

  const mockCatalogService = {
    getFoodItems: vi.fn().mockReturnValue(of(sampleItems)),
    getFoodItemsPaged: vi.fn().mockImplementation((query?: FoodItemQueryParameters) => {
      let filtered = sampleItems;
      if (query?.isPantryStaple) {
        filtered = filtered.filter((i) => i.isPantryStaple);
      }
      if (query?.category) {
        filtered = filtered.filter((i) => i.category === query.category);
      }
      if (query?.search) {
        const s = query.search.toLowerCase();
        filtered = filtered.filter(
          (i) =>
            i.name.toLowerCase().includes(s) || i.aliases.some((a) => a.toLowerCase().includes(s)),
        );
      }
      return of({
        items: filtered,
        totalCount: filtered.length,
        pageNumber: 1,
        pageSize: 30,
        totalPages: 1,
        hasPreviousPage: false,
        hasNextPage: false,
      });
    }),
    updateFoodItem: vi.fn(),
    deleteFoodItem: vi.fn(),
    bulkDeleteFoodItems: vi.fn(),
  };

  const mockDialog = {
    open: vi.fn(),
  };

  const mockSnackBar = {
    open: vi.fn(),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FoodCatalogComponent],
      providers: [
        provideRouter([]),
        { provide: FoodCatalogService, useValue: mockCatalogService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(FoodCatalogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders loaded items and badges', () => {
    expect(component).toBeTruthy();
    expect(component['items']().length).toBe(2);
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Olive Oil');
    expect(text).toContain('Garlic');
    expect(text).toContain('2 list items');
    const filledStars = fixture.nativeElement.querySelectorAll(
      'mat-icon.staple-active.icon-filled',
    );
    expect(filledStars.length).toBe(1);

    const menuButtons = fixture.nativeElement.querySelectorAll('.food-item-menu-btn');
    expect(menuButtons.length).toBe(2);
    (menuButtons[0] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(document.querySelector('.merge-item-btn')).not.toBeNull();
    expect(document.querySelector('.edit-food-item-btn')).not.toBeNull();
    expect(document.querySelector('.delete-food-item-btn')).not.toBeNull();
  });

  it('filters items by search query', () => {
    component.onSearchChange('evoo');
    component.loadItems();
    fixture.detectChanges();
    expect(component['filteredItems']().length).toBe(1);
    expect(component['filteredItems']()[0].name).toBe('Olive Oil');
  });

  it('filters items by pantry staples toggle', () => {
    component.onStaplesToggleChange(true);
    fixture.detectChanges();
    expect(component['filteredItems']().length).toBe(1);
    expect(component['filteredItems']()[0].name).toBe('Olive Oil');
  });

  it('shows category labels and filters by category', () => {
    const labels = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="food-item-category"]'),
    ).map((el) => el.textContent?.trim());
    expect(labels).toContain('Produce');
    expect(labels).toContain('Condiments & Sauces');

    component.onCategoryFilterChange('Produce');
    fixture.detectChanges();
    expect(mockCatalogService.getFoodItemsPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({ category: 'Produce' }),
    );
    expect(component['filteredItems']().map((i) => i.name)).toEqual(['Garlic']);
  });

  it('opens merge dialog when openMergeDialog is called', () => {
    mockDialog.open.mockReturnValue({
      afterClosed: () => of(null),
    });

    component.openMergeDialog();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('opens FoodItemRecipesDialogComponent and updates count when unlinked', () => {
    mockDialog.open.mockReturnValue({
      afterClosed: () => of({ unlinked: true, unlinkedRecipeCount: 3 }),
    });

    component.viewReferencedRecipes(sampleItems[0]);
    expect(mockDialog.open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: { foodItem: sampleItems[0] },
      }),
    );
    const updated = component['items']().find((i) => i.id === 'f-1');
    expect(updated?.referencedRecipeCount).toBe(0);
  });

  it('prompts to unlink and delete when item has referenced recipes', () => {
    mockDialog.open.mockReturnValue({
      afterClosed: () => of(true),
    });
    mockCatalogService.deleteFoodItem.mockReturnValue(of(undefined));

    component.deleteItem(sampleItems[0]);
    expect(mockDialog.open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: expect.objectContaining({
          confirmText: 'Unlink & Delete',
        }),
      }),
    );
    expect(mockCatalogService.deleteFoodItem).toHaveBeenCalledWith('f-1', true);
  });

  it('prompts standard delete when item has no referenced recipes', () => {
    const itemWithoutRecipes: FoodItemDto = {
      ...sampleItems[1],
      id: 'f-3',
      referencedRecipeCount: 0,
    };
    mockDialog.open.mockReturnValue({
      afterClosed: () => of(true),
    });
    mockCatalogService.deleteFoodItem.mockReturnValue(of(undefined));

    component.deleteItem(itemWithoutRecipes);
    expect(mockDialog.open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: expect.objectContaining({
          confirmText: 'Delete',
        }),
      }),
    );
    expect(mockCatalogService.deleteFoodItem).toHaveBeenCalledWith('f-3', false);
  });

  it('restores search, filters, and pagination from query params and syncs changes back to URL', async () => {
    const queryParamMap$ = new BehaviorSubject(
      convertToParamMap({
        q: 'garlic',
        category: 'Produce',
        staples: '1',
        page: '2',
        pageSize: '60',
      }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [FoodCatalogComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: queryParamMap$.asObservable(),
            snapshot: { queryParamMap: queryParamMap$.value },
          },
        },
        { provide: FoodCatalogService, useValue: mockCatalogService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    const router = TestBed.inject(Router);
    const navSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    const restoredFixture = TestBed.createComponent(FoodCatalogComponent);
    const restoredComp = restoredFixture.componentInstance;
    restoredFixture.detectChanges();

    expect(restoredComp['searchQuery']()).toBe('garlic');
    expect(restoredComp['categoryFilter']()).toBe('Produce');
    expect(restoredComp['onlyPantryStaples']()).toBe(true);
    expect(restoredComp['sortBy']()).toBe('Name');
    expect(restoredComp['sortDirection']()).toBe('Asc');
    expect(restoredComp['pageIndex']()).toBe(1);
    expect(restoredComp['pageSize']()).toBe(60);

    restoredComp.onPageChange({ pageIndex: 2, pageSize: 60, length: 200 });
    expect(navSpy).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: expect.objectContaining({
          q: 'garlic',
          category: 'Produce',
          staples: '1',
          sort: null,
          dir: null,
          page: 3,
          pageSize: 60,
        }),
        replaceUrl: true,
      }),
    );
  });

  it('renders sort selector and direction toggle and reloads on change', () => {
    const sortField = fixture.nativeElement.querySelector('.filter-row .sort-form-field');
    const directionBtn = fixture.nativeElement.querySelector(
      '.filter-row .sort-direction-btn',
    ) as HTMLButtonElement;
    expect(sortField).toBeTruthy();
    expect(directionBtn).toBeTruthy();

    expect(component['sortBy']()).toBe('Name');
    expect(component['sortDirection']()).toBe('Asc');
    expect(mockCatalogService.getFoodItemsPaged).toHaveBeenCalledWith(
      expect.objectContaining({
        sortBy: 'Name',
        sortDirection: 'Asc',
      }),
    );

    component['pageIndex'].set(2);
    component.onSortByChange('RecipeCount');
    fixture.detectChanges();

    expect(component['sortBy']()).toBe('RecipeCount');
    expect(component['sortDirection']()).toBe('Asc');
    expect(component['pageIndex']()).toBe(0);
    expect(mockCatalogService.getFoodItemsPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({
        page: 1,
        sortBy: 'RecipeCount',
        sortDirection: 'Asc',
      }),
    );

    directionBtn.click();
    fixture.detectChanges();

    expect(component['sortDirection']()).toBe('Desc');
    expect(component['pageIndex']()).toBe(0);
    expect(mockCatalogService.getFoodItemsPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({
        page: 1,
        sortBy: 'RecipeCount',
        sortDirection: 'Desc',
      }),
    );
  });

  it('restores and syncs sort and direction query params', async () => {
    const queryParamMap$ = new BehaviorSubject(
      convertToParamMap({
        sort: 'RecipeCount',
        dir: 'Desc',
      }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [FoodCatalogComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: queryParamMap$.asObservable(),
            snapshot: { queryParamMap: queryParamMap$.value },
          },
        },
        { provide: FoodCatalogService, useValue: mockCatalogService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    const router = TestBed.inject(Router);
    const navSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    const restoredFixture = TestBed.createComponent(FoodCatalogComponent);
    const restoredComp = restoredFixture.componentInstance;
    restoredFixture.detectChanges();

    expect(restoredComp['sortBy']()).toBe('RecipeCount');
    expect(restoredComp['sortDirection']()).toBe('Desc');
    expect(mockCatalogService.getFoodItemsPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({
        sortBy: 'RecipeCount',
        sortDirection: 'Desc',
      }),
    );

    restoredComp.onSortByChange('ModifiedAt');
    expect(navSpy).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: expect.objectContaining({
          sort: 'ModifiedAt',
          dir: 'Desc',
        }),
        replaceUrl: true,
      }),
    );

    restoredComp.toggleSortDirection();
    expect(navSpy).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: expect.objectContaining({
          sort: 'ModifiedAt',
          dir: null,
        }),
        replaceUrl: true,
      }),
    );
  });

  it('supports individual, select-all, and select-unused selection and bulk delete flows', () => {
    const mixedItems: FoodItemDto[] = [
      { ...sampleItems[0], id: 'u-1', name: 'Unused A', referencedRecipeCount: 0 },
      { ...sampleItems[1], id: 'r-1', name: 'Used B', referencedRecipeCount: 2 },
      { ...sampleItems[0], id: 'u-2', name: 'Unused C', referencedRecipeCount: 0 },
    ];
    component['items'].set(mixedItems);
    component['totalCount'].set(3);
    fixture.detectChanges();

    // Select unused selects only items with 0 recipes
    expect(component['unusedOnPageCount']()).toBe(2);
    component.selectUnusedOnPage();
    fixture.detectChanges();
    expect(component['selectedCount']()).toBe(2);
    expect(component.isSelected('u-1')).toBe(true);
    expect(component.isSelected('r-1')).toBe(false);
    expect(component.isSelected('u-2')).toBe(true);

    // Bulk delete unreferenced items passes unlinkFromRecipes: false
    mockDialog.open.mockReturnValue({ afterClosed: () => of(true) });
    mockCatalogService.bulkDeleteFoodItems.mockReturnValue(
      of({
        deletedCount: 2,
        unlinkedRecipeCount: 0,
        unlinkedIngredientCount: 0,
        unlinkedShoppingListItemCount: 0,
      }),
    );

    component.deleteSelectedItems();
    expect(mockDialog.open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: expect.objectContaining({
          confirmText: 'Delete 2 Items',
        }),
      }),
    );
    expect(mockCatalogService.bulkDeleteFoodItems).toHaveBeenCalledWith({
      foodItemIds: ['u-1', 'u-2'],
      unlinkFromRecipes: false,
    });
    expect(component['selectedCount']()).toBe(0);

    // Select all including referenced item prompts Unlink & Delete
    component['items'].set(mixedItems);
    component.toggleSelectAllOnPage(true);
    expect(component['allVisibleSelected']()).toBe(true);

    component.deleteSelectedItems();
    expect(mockDialog.open).toHaveBeenLastCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: expect.objectContaining({
          confirmText: 'Unlink & Delete (3)',
        }),
      }),
    );
    expect(mockCatalogService.bulkDeleteFoodItems).toHaveBeenLastCalledWith({
      foodItemIds: ['u-1', 'r-1', 'u-2'],
      unlinkFromRecipes: true,
    });

    // Clear selection & real-DOM Shift+Click range selection (checkbox + card body + reverse + uncheck)
    const fourItems: FoodItemDto[] = [
      { ...sampleItems[0], id: 'u-1', name: 'Item 1', referencedRecipeCount: 0 },
      { ...sampleItems[1], id: 'u-2', name: 'Item 2', referencedRecipeCount: 0 },
      { ...sampleItems[0], id: 'u-3', name: 'Item 3', referencedRecipeCount: 0 },
      { ...sampleItems[1], id: 'r-4', name: 'Item 4', referencedRecipeCount: 2 },
    ];
    component['items'].set(fourItems);
    fixture.detectChanges();

    const checkboxes = () =>
      Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>(
          '.item-select-checkbox input[type="checkbox"]',
        ),
      );
    const cards = () =>
      Array.from(
        (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.food-item-card'),
      );

    // 1. Click checkbox 0 normally, then Shift+Click checkbox 2 -> selects 0, 1, 2
    checkboxes()[0].click();
    fixture.detectChanges();
    expect(component['selectedCount']()).toBe(1);

    checkboxes()[2].dispatchEvent(
      new MouseEvent('click', { bubbles: true, cancelable: true, shiftKey: true }),
    );
    fixture.detectChanges();
    expect(component['selectedCount']()).toBe(3);
    expect(component.isSelected('u-1')).toBe(true);
    expect(component.isSelected('u-2')).toBe(true);
    expect(component.isSelected('u-3')).toBe(true);
    expect(component.isSelected('r-4')).toBe(false);

    // 2. Shift+Click checked checkbox 1 -> unchecks range [1..2]
    checkboxes()[1].dispatchEvent(
      new MouseEvent('click', { bubbles: true, cancelable: true, shiftKey: true }),
    );
    fixture.detectChanges();
    expect(component.isSelected('u-1')).toBe(true);
    expect(component.isSelected('u-2')).toBe(false);
    expect(component.isSelected('u-3')).toBe(false);

    // 3. Reverse Shift+Click on card body: click checkbox 3, then Shift+Click card 1 body -> selects 1, 2, 3
    component.clearSelection();
    fixture.detectChanges();
    checkboxes()[3].click();
    fixture.detectChanges();
    cards()[1]
      .querySelector('.item-name')!
      .dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, shiftKey: true }));
    fixture.detectChanges();
    expect(component['selectedCount']()).toBe(3);
    expect(component.isSelected('u-1')).toBe(false);
    expect(component.isSelected('u-2')).toBe(true);
    expect(component.isSelected('u-3')).toBe(true);
    expect(component.isSelected('r-4')).toBe(true);

    component.clearSelection();
    expect(component['selectedCount']()).toBe(0);
  });

  it('clamps pageIndex when bulk delete removes all items on the last page', () => {
    const pageItems: FoodItemDto[] = [
      { ...sampleItems[0], id: 'p-1', name: 'Last Page Item', referencedRecipeCount: 0 },
    ];
    component['items'].set(pageItems);
    component['pageIndex'].set(1);
    component['pageSize'].set(30);
    component['totalCount'].set(31);
    component.toggleSelectAllOnPage(true);

    mockDialog.open.mockReturnValue({ afterClosed: () => of(true) });
    mockCatalogService.bulkDeleteFoodItems.mockReturnValue(
      of({
        deletedCount: 1,
        unlinkedRecipeCount: 0,
        unlinkedIngredientCount: 0,
        unlinkedShoppingListItemCount: 0,
      }),
    );

    component.deleteSelectedItems();
    expect(component['pageIndex']()).toBe(0);
  });
});
