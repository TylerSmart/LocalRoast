import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
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
});
