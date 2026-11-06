import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { FoodItemDialogComponent, FoodItemDialogData } from './food-item-dialog.component';

describe('FoodItemDialogComponent', () => {
  let component: FoodItemDialogComponent;
  let fixture: ComponentFixture<FoodItemDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  const mockCatalogService = {
    createFoodItem: vi.fn(),
    updateFoodItem: vi.fn(),
  };

  const dialogData: FoodItemDialogData = {
    initialName: 'Brown Sugar',
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [FoodItemDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: FoodCatalogService, useValue: mockCatalogService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(FoodItemDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('prefills initial name from data', () => {
    expect(component).toBeTruthy();
    expect(component['form'].controls.name.value).toBe('Brown Sugar');
  });

  it('submits create request and closes with saved item', () => {
    const saved: FoodItemDto = {
      id: 'f-1',
      name: 'Brown Sugar',
      isPantryStaple: true,
      aliases: ['Dark Brown Sugar'],
      referencedRecipeCount: 0,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    };
    mockCatalogService.createFoodItem.mockReturnValue(of(saved));

    component['form'].controls.isPantryStaple.setValue(true);
    component['form'].controls.aliasesText.setValue('Dark Brown Sugar');
    component.onSave();

    expect(mockCatalogService.createFoodItem).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'Brown Sugar',
        isPantryStaple: true,
        aliases: ['Dark Brown Sugar'],
      }),
    );
    expect(mockDialogRef.close).toHaveBeenCalledWith(saved);
  });

  it('sends null category for auto-detect and the chosen category when selected', () => {
    mockCatalogService.createFoodItem.mockReset().mockReturnValue(of({} as FoodItemDto));

    component.onSave();
    expect(mockCatalogService.createFoodItem).toHaveBeenLastCalledWith(
      expect.objectContaining({ category: null }),
    );

    component['form'].controls.category.setValue('Baking');
    component.onSave();
    expect(mockCatalogService.createFoodItem).toHaveBeenLastCalledWith(
      expect.objectContaining({ category: 'Baking' }),
    );
  });
});
