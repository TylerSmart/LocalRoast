import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
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
    component['searchQuery'].set('evoo');
    fixture.detectChanges();
    expect(component['filteredItems']().length).toBe(1);
    expect(component['filteredItems']()[0].name).toBe('Olive Oil');
  });

  it('filters items by pantry staples toggle', () => {
    component['onlyPantryStaples'].set(true);
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
});
