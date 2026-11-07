import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import type { IngestionExtractedDraftDto } from '../../../core/models/ingestion.models';
import {
  IngestionRepairDialogComponent,
  IngestionRepairDialogData,
} from './ingestion-repair-dialog.component';

describe('IngestionRepairDialogComponent', () => {
  let component: IngestionRepairDialogComponent;
  let fixture: ComponentFixture<IngestionRepairDialogComponent>;
  const close = vi.fn();
  const before: IngestionExtractedDraftDto = {
    title: 'Beans',
    description: null,
    baseServings: 4,
    yieldDisplay: null,
    prepTimeMinutes: null,
    cookTimeMinutes: null,
    totalTimeMinutes: null,
    difficulty: null,
    sourceUrl: null,
    sourceName: null,
    sourceAuthor: null,
    coverImagePath: null,
    thumbnailImagePath: null,
    suggestedCollectionIds: ['dinners'],
    suggestedTagIds: [],
    confidenceScore: 0.8,
    ingredients: [
      {
        sortOrder: 0,
        sectionName: null,
        originalRawText: '2 tbsp oil',
        quantity: 2,
        maxQuantity: null,
        unit: 'Tbsp',
        customUnitName: null,
        displayName: 'oil',
        preparationNote: null,
        isOptional: false,
        matchedFoodItemId: null,
        matchedFoodItemName: null,
        isNewFoodItem: true,
        proposedFoodItemName: 'oil',
        suggestedIsPantryStaple: false,
      },
    ],
    steps: [
      {
        stepNumber: 1,
        sectionName: null,
        instruction: 'Cook the oil.',
        ingredientIndices: [],
        ingredientAmounts: [],
        timers: [{ label: 'Timer', durationSeconds: 60 }],
        imagePath: null,
      },
    ],
  };
  const after: IngestionExtractedDraftDto = {
    ...before,
    title: 'Beans and oil',
    suggestedCollectionIds: [],
    ingredients: [
      {
        ...before.ingredients[0],
        displayName: 'olive oil',
        quantity: 1,
        alternativeMeasurement: { quantity: 15, unit: 'Ml' },
      },
    ],
    steps: [
      {
        ...before.steps[0],
        instruction: 'Warm the olive oil.',
        ingredientIndices: [0],
        ingredientAmounts: [{ ingredientIndex: 0, measurement: { quantity: 1, unit: 'Tbsp' } }],
        inlineAmounts: [
          {
            start: 9,
            length: 9,
            kind: 'Ingredient',
            ingredientIndex: 0,
            measurement: { quantity: 1, unit: 'Tbsp' },
          },
        ],
        timers: [{ label: 'Warm oil', durationSeconds: 90 }],
      },
    ],
  };
  const data: IngestionRepairDialogData = {
    before,
    preview: {
      draft: after,
      before: { validationIssues: ['Oil is unlinked'], qualityWarnings: ['Vague timer'] },
      after: { validationIssues: [], qualityWarnings: [] },
      notices: ['Verify the portions.'],
    },
    collections: [{ id: 'dinners', name: 'Dinners' }],
    tags: [],
  };

  beforeEach(async () => {
    close.mockReset();
    await TestBed.configureTestingModule({
      imports: [IngestionRepairDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: { close } },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(IngestionRepairDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('shows readable changes to quantities, links, portions, timers and metadata', () => {
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Recipe: Title');
    expect(text).toContain('Dinners');
    expect(text).toContain('Ingredient 1: Quantity');
    expect(text).toContain('Ingredient 1: Alternative measurement');
    expect(text).toContain('15 Ml');
    expect(text).toContain('Step 1: Linked ingredients');
    expect(text).toContain('1. olive oil: 1 Tbsp');
    expect(text).toContain('Warm oil: 90s');
    expect(text).toContain('Verify the portions.');
    expect(text).toContain('Review AI re-extraction');
    expect(text).toContain('Step 1: Tagged amounts');
    expect(text).toContain('"olive oil" → 1. olive oil');
    expect(text).toContain('Oil is unlinked');
    expect(component.changes.length).toBeGreaterThan(0);
  });

  it('returns an explicit decision without saving anything', () => {
    component.close(false);
    expect(close).toHaveBeenLastCalledWith(false);
    component.close(true);
    expect(close).toHaveBeenLastCalledWith(true);
  });
});
