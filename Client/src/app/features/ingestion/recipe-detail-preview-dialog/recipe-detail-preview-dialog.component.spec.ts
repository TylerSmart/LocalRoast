import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { RecipePreviewData } from '../../../core/models/recipe.models';
import {
  RecipeDetailPreviewDialogComponent,
  RecipeDetailPreviewDialogData,
} from './recipe-detail-preview-dialog.component';

describe('RecipeDetailPreviewDialogComponent', () => {
  let component: RecipeDetailPreviewDialogComponent;
  let fixture: ComponentFixture<RecipeDetailPreviewDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  const mockRecipe: RecipePreviewData = {
    title: 'Dialog Preview Soup',
    description: 'A comforting hearty vegetable soup.',
    baseServings: 4,
    yieldDisplay: '4 servings',
    prepTimeMinutes: 20,
    cookTimeMinutes: 30,
    totalTimeMinutes: 50,
    difficulty: 'Easy',
    sourceName: 'My Family Cookbook',
    sourceBookTitle: null,
    sourceBookPage: null,
    sourceUrl: null,
    coverImagePath: null,
    extractedModel: 'local-test-model',
    collections: [],
    tags: [],
    ingredients: [
      {
        id: 'ing-0',
        index: 0,
        sectionName: null,
        quantity: 1,
        maxQuantity: null,
        unit: 'Bunch',
        customUnitName: null,
        displayName: 'Carrots',
        preparationNote: 'sliced',
        isOptional: false,
        isPantryStaple: true,
        matchedFoodItemName: 'Carrots',
      },
    ],
    steps: [
      {
        id: 'step-0',
        stepNumber: 1,
        sectionName: null,
        instruction: 'Simmer carrots until tender.',
        imagePath: null,
        ingredientIndices: [0],
        timers: [],
      },
    ],
  };

  const dialogData: RecipeDetailPreviewDialogData = {
    recipe: mockRecipe,
    canPublish: true,
    isPublishing: false,
  };

  beforeEach(async () => {
    mockDialogRef = {
      close: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [RecipeDetailPreviewDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeDetailPreviewDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders dialog title and recipe title in preview', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.dialog-title')?.textContent).toContain('Recipe Detail Preview');
    expect(el.querySelector('.recipe-title')?.textContent).toContain('Dialog Preview Soup');
  });

  it('closes dialog with close action on close()', () => {
    component.close();
    expect(mockDialogRef.close).toHaveBeenCalledWith({ action: 'close' });
  });

  it('closes dialog with publish action on publish()', () => {
    component.publish();
    expect(mockDialogRef.close).toHaveBeenCalledWith({ action: 'publish' });
  });
});
