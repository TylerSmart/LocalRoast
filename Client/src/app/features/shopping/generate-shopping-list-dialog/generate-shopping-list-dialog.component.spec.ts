import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { of } from 'rxjs';
import {
  ShoppingListDetailDto,
  ShoppingListGenerationPreviewDto,
  ShoppingListGenerationResultDto,
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import {
  GenerateShoppingListDialogComponent,
  GenerateShoppingListDialogData,
} from './generate-shopping-list-dialog.component';

describe('GenerateShoppingListDialogComponent', () => {
  let component: GenerateShoppingListDialogComponent;
  let fixture: ComponentFixture<GenerateShoppingListDialogComponent>;

  const mockShoppingService = {
    getShoppingLists: vi.fn(),
    previewGenerateFromMealPlan: vi.fn(),
    generateFromMealPlan: vi.fn(),
  };

  const mockDialogRef = {
    close: vi.fn(),
  };

  const dialogData: GenerateShoppingListDialogData = {
    targetShoppingListId: 'list-1',
    startDate: '2026-11-02',
    endDate: '2026-11-08',
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

  const mockPreview: ShoppingListGenerationPreviewDto = {
    startDate: '2026-11-02',
    endDate: '2026-11-08',
    eligibleMealsCount: 3,
    newMealsToImportCount: 3,
    ignoredLeftoversCount: 1,
    ignoredNotesCount: 0,
    alreadyImportedMeals: [],
    activeCandidateItems: [
      {
        foodItemId: null,
        displayName: 'Garlic',
        isPantryStaple: false,
        quantity: 4,
        maxQuantity: null,
        unit: 'Clove',
        customUnitName: null,
        unitLabel: 'cloves',
        formattedAmount: '4',
        dimension: 'Discrete',
        notes: null,
        sourceLabels: ['Mon: Tacos'],
      },
    ],
    excludedPantryCandidateItems: [],
  };

  beforeEach(async () => {
    mockShoppingService.getShoppingLists.mockReset().mockReturnValue(of(mockLists));
    mockShoppingService.previewGenerateFromMealPlan.mockReset().mockReturnValue(of(mockPreview));
    mockShoppingService.generateFromMealPlan.mockReset();
    mockDialogRef.close.mockReset();

    await TestBed.configureTestingModule({
      imports: [GenerateShoppingListDialogComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: ShoppingService, useValue: mockShoppingService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(GenerateShoppingListDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads lists and preview on init', () => {
    expect(component).toBeTruthy();
    expect(mockShoppingService.getShoppingLists).toHaveBeenCalled();
    expect(mockShoppingService.previewGenerateFromMealPlan).toHaveBeenCalled();
    expect(component.preview()).toEqual(mockPreview);
  });

  it('submits generation request and closes dialog', () => {
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
      importedMealsCount: 3,
      skippedDuplicateMealsCount: 0,
      ignoredLeftoversCount: 1,
      ignoredNotesCount: 0,
      activeItemsAffectedCount: 1,
      excludedPantryItemsCount: 0,
    };

    mockShoppingService.generateFromMealPlan.mockReturnValue(of(mockResult));

    component.onSubmit();

    expect(mockShoppingService.generateFromMealPlan).toHaveBeenCalledWith(
      'list-1',
      expect.objectContaining({
        startDate: '2026-11-02',
        endDate: '2026-11-08',
      }),
    );
    expect(mockDialogRef.close).toHaveBeenCalledWith(mockResult);
  });
});
