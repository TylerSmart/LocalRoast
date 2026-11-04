import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { FoodItemDto, MergeFoodItemsResultDto } from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import {
  MergeFoodItemsDialogComponent,
  MergeFoodItemsDialogData,
} from './merge-food-items-dialog.component';

describe('MergeFoodItemsDialogComponent', () => {
  let component: MergeFoodItemsDialogComponent;
  let fixture: ComponentFixture<MergeFoodItemsDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  const items: FoodItemDto[] = [
    {
      id: 'target-1',
      name: 'Yellow Onion',
      isPantryStaple: true,
      aliases: ['Spanish Onion'],
      referencedRecipeCount: 3,
      referencedShoppingListItemCount: 1,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
    {
      id: 'source-1',
      name: 'Brown Onion',
      isPantryStaple: false,
      aliases: [],
      referencedRecipeCount: 2,
      referencedShoppingListItemCount: 1,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
  ];

  const mockCatalogService = {
    getFoodItems: vi.fn().mockReturnValue(of(items)),
    mergeFoodItems: vi.fn(),
  };

  const dialogData: MergeFoodItemsDialogData = {
    targetItem: items[0],
    sourceItems: [items[1]],
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [MergeFoodItemsDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: FoodCatalogService, useValue: mockCatalogService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(MergeFoodItemsDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('initializes with target and source items and computes impact', () => {
    expect(component).toBeTruthy();
    expect(component.targetItemId()).toBe('target-1');
    expect(component.selectedSourceIds()).toEqual(['source-1']);
    expect(component.affectedRecipesCount()).toBe(2);
    expect(component.affectedShoppingItemsCount()).toBe(1);
    expect(component.isValid()).toBe(true);
    expect(component.canSwap()).toBe(true);
  });

  it('allows swapping target and source', () => {
    component.swapTargetAndSource();
    expect(component.targetItemId()).toBe('source-1');
    expect(component.selectedSourceIds()).toEqual(['target-1']);
  });

  it('submits merge request and closes with result', () => {
    const result: MergeFoodItemsResultDto = {
      targetFoodItem: items[0],
      mergedSourceCount: 1,
      repointedRecipeIngredientCount: 2,
      repointedShoppingListItemCount: 1,
      consolidatedShoppingListItemCount: 1,
      addedAliases: ['Brown Onion'],
    };
    mockCatalogService.mergeFoodItems.mockReturnValue(of(result));

    component.onConfirmMerge();

    expect(mockCatalogService.mergeFoodItems).toHaveBeenCalledWith({
      targetFoodItemId: 'target-1',
      sourceFoodItemIds: ['source-1'],
      keepSourceNamesAsAliases: true,
    });
    expect(mockDialogRef.close).toHaveBeenCalledWith(result);
  });
});
