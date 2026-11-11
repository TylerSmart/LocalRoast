import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { RecipeDetailDto } from '../../../core/models/recipe.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { RecipeShareDialogComponent } from '../recipe-share-dialog/recipe-share-dialog.component';
import { KitchenQuestionDialogComponent } from '../kitchen-question-dialog/kitchen-question-dialog.component';
import { RecipeDetailComponent } from './recipe-detail.component';

describe('RecipeDetailComponent', () => {
  let component: RecipeDetailComponent;
  let fixture: ComponentFixture<RecipeDetailComponent>;

  const sampleRecipe: RecipeDetailDto = {
    id: 'r-1',
    title: 'Homemade Chicken Noodle Soup',
    slug: 'homemade-chicken-noodle-soup',
    description: 'Comfort in a bowl',
    status: 'Published',
    baseServings: 4,
    yieldDisplay: '4 large bowls',
    prepTimeMinutes: 15,
    cookTimeMinutes: 30,
    totalTimeMinutes: 45,
    difficulty: 'Easy',
    sourceUrl: 'https://example.com/soup',
    sourceName: 'Family Tradition',
    sourceAuthor: 'Grandma',
    sourceBookTitle: null,
    sourceBookPage: null,
    extractedModel: 'llama3.2-vision',
    ingestionJobId: 'job-100',
    coverImagePath: null,
    thumbnailImagePath: null,
    timesCooked: 2,
    lastCookedAt: null,
    averageRating: null,
    ingredients: [
      {
        id: 'i-1',
        sortOrder: 0,
        sectionName: null,
        quantity: 2,
        maxQuantity: null,
        unit: 'Cup',
        customUnitName: null,
        foodItemId: null,
        foodItemName: null,
        isPantryStaple: false,
        linkedRecipeId: null,
        linkedRecipeTitle: null,
        linkedRecipeSlug: null,
        displayName: 'Egg Noodles',
        preparationNote: 'uncooked',
        isOptional: false,
        originalRawText: null,
      },
      {
        id: 'i-2',
        sortOrder: 1,
        sectionName: null,
        quantity: 4,
        maxQuantity: null,
        unit: 'Cup',
        customUnitName: null,
        foodItemId: null,
        foodItemName: null,
        isPantryStaple: true,
        linkedRecipeId: null,
        linkedRecipeTitle: null,
        linkedRecipeSlug: null,
        displayName: 'Chicken Broth',
        preparationNote: null,
        isOptional: false,
        originalRawText: null,
      },
    ],
    steps: [
      {
        id: 's-1',
        stepNumber: 1,
        sectionName: null,
        instruction: 'Bring broth to a boil and stir in noodles.',
        imagePath: null,
        ingredientIds: ['i-1', 'i-2'],
        timers: [
          {
            id: 't-1',
            sortOrder: 0,
            durationSeconds: 480,
            label: 'Boil noodles until tender',
          },
        ],
      },
    ],
    linkedSubRecipes: [],
    collections: [{ id: 'c1', name: 'Weeknight Staples', slug: 'weeknight-staples' }],
    tags: [{ id: 't1', name: 'Sheet Pan', slug: 'equipment-sheet-pan', category: 'Equipment' }],
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'test-user',
    modifiedAt: '2026-01-01T00:00:00Z',
    modifiedBy: 'test-user',
  };

  const mockIngestionService = {
    retryJob: vi.fn().mockReturnValue(of({ id: 'job-100', status: 'Queued' })),
  };

  const mockRecipeService = {
    getRecipeById: vi.fn().mockReturnValue(of(sampleRecipe)),
    deleteRecipe: vi.fn(),
  };

  const mockDialog = {
    open: vi.fn().mockReturnValue({ afterClosed: () => of(null) }),
  };

  const mockSnackBar = {
    open: vi.fn(),
  };

  const mockAiHelperService = {
    checkStatus: vi
      .fn()
      .mockReturnValue(of({ isAvailable: true, provider: 'Ollama', latencyMs: 25 })),
    status: signal({ isAvailable: true, provider: 'Ollama', latencyMs: 25 }),
    isAvailable: signal(true),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RecipeDetailComponent],
      providers: [
        provideRouter([]),
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: AiKitchenHelperService, useValue: mockAiHelperService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeDetailComponent);
    component = fixture.componentInstance;
    component.loadRecipe('r-1');
    fixture.detectChanges();
  });

  it('opens Kitchen AI with the selected ingredient, step, or recipe', () => {
    mockDialog.open.mockClear();
    const ingredientButton: HTMLButtonElement =
      fixture.nativeElement.querySelector('.explain-ing-btn');
    ingredientButton.click();
    expect(mockDialog.open).toHaveBeenLastCalledWith(
      KitchenQuestionDialogComponent,
      expect.objectContaining({
        data: expect.objectContaining({
          recipeId: 'r-1',
          recipeIngredientId: 'i-1',
          ingredientName: 'Egg Noodles',
        }),
      }),
    );

    const stepButton: HTMLButtonElement = fixture.nativeElement.querySelector('.explain-step-btn');
    stepButton.click();
    expect(mockDialog.open).toHaveBeenLastCalledWith(
      KitchenQuestionDialogComponent,
      expect.objectContaining({
        data: expect.objectContaining({ recipeId: 'r-1', recipeStepId: 's-1', stepNumber: 1 }),
      }),
    );

    component.openKitchenQuestion();
    expect(mockDialog.open).toHaveBeenLastCalledWith(
      KitchenQuestionDialogComponent,
      expect.objectContaining({
        data: { recipeId: 'r-1', recipeTitle: sampleRecipe.title },
      }),
    );
  });

  it('renders recipe title, collections, tags, ingredients, and step instructions', () => {
    expect(component).toBeTruthy();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Homemade Chicken Noodle Soup');
    expect(text).toContain('Weeknight Staples');
    expect(text).toContain('Sheet Pan');
    expect(text).toContain('Egg Noodles');
    expect(text).toContain('Chicken Broth');
    expect(text).toContain('Bring broth to a boil and stir in noodles.');
    expect(text).toContain('8 min');
  });

  it('identifies an unresolved recipe component and explains how to link it', async () => {
    mockRecipeService.getRecipeById.mockReturnValueOnce(
      of({
        ...sampleRecipe,
        ingredients: [
          ...sampleRecipe.ingredients,
          {
            id: 'i-3',
            sortOrder: 2,
            sectionName: null,
            quantity: 1,
            maxQuantity: null,
            unit: 'Custom',
            customUnitName: 'recipe',
            foodItemId: null,
            foodItemName: null,
            isPantryStaple: false,
            linkedRecipeId: null,
            linkedRecipeTitle: null,
            linkedRecipeSlug: null,
            displayName: 'Foolproof All-Butter Dough',
            preparationNote: null,
            isOptional: false,
            originalRawText: '1 recipe Foolproof All-Butter Dough',
            unresolvedRecipeTitle: 'Foolproof All-Butter Dough',
          },
        ],
      }),
    );

    component.loadRecipe('r-1');
    fixture.detectChanges();

    const warning = fixture.nativeElement.querySelector('.unresolved-recipe-reference');
    expect(warning?.textContent).toContain('Foolproof All-Butter Dough');
    expect(warning?.textContent).toContain('Edit this recipe to link it');
  });

  it('opens organize dialog on openOrganizeDialog()', () => {
    component.openOrganizeDialog();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('opens share dialog on openShareDialog()', () => {
    component.openShareDialog();
    expect(mockDialog.open).toHaveBeenCalledWith(
      RecipeShareDialogComponent,
      expect.objectContaining({
        width: '560px',
        data: {
          recipeId: 'r-1',
          recipeTitle: 'Homemade Chicken Noodle Soup',
          recipeStatus: 'Published',
        },
      }),
    );
  });

  it('triggers globalThis.print on printRecipe()', () => {
    const printSpy = vi.spyOn(globalThis, 'print').mockImplementation(() => undefined);
    component.printRecipe();
    expect(printSpy).toHaveBeenCalled();
    printSpy.mockRestore();
  });

  it('scales quantities when 2x multiplier is selected', () => {
    component.setMultiplier(2);
    fixture.detectChanges();
    expect(component['scaleFactor']()).toBe(2);

    const map = component['scaledIngredientsMap']();
    const noodles = map.get('i-1');
    expect(noodles?.scaled.quantity).toBe(4);
    expect(noodles?.displayQuantityAndUnit).toBe('4 cups');
  });

  it('uses explicit step portions and switches an ingredient equivalent without changing others', () => {
    const ingredient = {
      ...sampleRecipe.ingredients[0],
      quantity: 5,
      unit: 'Cup' as const,
      alternativeMeasurement: { quantity: 25, unit: 'Oz' as const },
      preferAlternativeMeasurement: true,
    };
    const step = {
      ...sampleRecipe.steps[0],
      ingredientIds: [ingredient.id],
      ingredientAmounts: [
        { ingredientId: ingredient.id, measurement: { quantity: 1, unit: 'Cup' as const } },
      ],
    };
    component['recipe'].set({
      ...sampleRecipe,
      ingredients: [ingredient, sampleRecipe.ingredients[1]],
      steps: [step],
    });
    component['targetSystem'].set('Metric');
    expect(component['groupedSteps']()[0].items[0].ingredientCallouts[0].label).toBe(
      `142 g ${ingredient.displayName}`,
    );
    component.setMultiplier(0.5);
    expect(component['groupedSteps']()[0].items[0].ingredientCallouts[0].label).toBe(
      `71 g ${ingredient.displayName}`,
    );
    component.toggleIngredientMeasurement(ingredient);
    expect(component['groupedSteps']()[0].items[0].ingredientCallouts[0].label).toBe(
      `118 ml ${ingredient.displayName}`,
    );
    component.toggleIngredientMeasurement(ingredient);
    expect(component['groupedSteps']()[0].items[0].ingredientCallouts[0].label).toBe(
      `71 g ${ingredient.displayName}`,
    );
    expect(
      component['scaledIngredientsMap']().get(sampleRecipe.ingredients[1].id)?.ingredient
        .preferAlternativeMeasurement,
    ).toBeUndefined();
    component['recipe'].set({
      ...sampleRecipe,
      ingredients: [ingredient],
      steps: [{ ...step, ingredientAmounts: [] }],
    });
    expect(component['groupedSteps']()[0].items[0].ingredientCallouts[0].label).toBe(
      ingredient.displayName,
    );
  });

  it('renders inline step tokens as scaled chips with a divided breakdown', () => {
    const ingredient = sampleRecipe.ingredients[0];
    component['recipe'].set({
      ...sampleRecipe,
      steps: [
        {
          ...sampleRecipe.steps[0],
          id: 's-a',
          stepNumber: 1,
          instruction:
            'Whisk ||{"ingredient":"Egg Noodles","amount":1,"unit":"cup"}||, then reduce to 1 cup.',
          ingredientIds: [ingredient.id],
          ingredientAmounts: [],
        },
        {
          ...sampleRecipe.steps[0],
          id: 's-b',
          stepNumber: 2,
          instruction:
            'Stir in the remaining ||{"ingredient":"Egg Noodles","amount":1,"unit":"cup"}||.',
          ingredientIds: [ingredient.id],
          ingredientAmounts: [],
        },
      ],
    });
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const instructions = () =>
      Array.from(el.querySelectorAll('.step-instruction-text')).map((p) => p.textContent?.trim());
    expect(instructions()).toEqual([
      'Whisk 1 cup Egg Noodles, then reduce to 1 cup.',
      'Stir in the remaining 1 cup Egg Noodles.',
    ]);
    expect(component['groupedSteps']()[0].items[0].ingredientCallouts).toEqual([
      { id: ingredient.id, label: '1 cup Egg Noodles' },
    ]);

    component.setMultiplier(2);
    fixture.detectChanges();
    expect(instructions()).toEqual([
      'Whisk 2 cups Egg Noodles, then reduce to 1 cup.',
      'Stir in the remaining 2 cups Egg Noodles.',
    ]);
    expect(el.querySelectorAll('.inline-amount.is-scaled').length).toBe(2);
    expect(el.querySelector('.ingredient-uses')?.textContent).toBe(
      'Divided: 2 cups Egg Noodles (step 1) · 2 cups Egg Noodles (step 2)',
    );
  });

  it('scales quantities when 1.5x multiplier is selected', () => {
    component.setMultiplier(1.5);
    fixture.detectChanges();
    expect(component['targetServings']()).toBe(6);
    expect(component['scaleFactor']()).toBe(1.5);
  });

  it('switches units to metric when metric is selected', () => {
    component['targetSystem'].set('Metric');
    fixture.detectChanges();

    const map = component['scaledIngredientsMap']();
    const noodles = map.get('i-1');
    // 2 cups ~ 473 ml
    expect(noodles?.scaled.unit).toBe('Ml');
    expect(noodles?.scaled.unitLabel).toBe('ml');
  });

  it('opens meal plan entry dialog on openPlanMealDialog()', () => {
    component.openPlanMealDialog();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('opens add to shopping list dialog on openAddToShoppingListDialog()', () => {
    component.openAddToShoppingListDialog();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('navigates to Cook Mode on startCooking()', () => {
    const navSpy = vi.spyOn(component['router'], 'navigate').mockResolvedValue(true);
    component.startCooking();
    expect(navSpy).toHaveBeenCalledWith(['/recipes', 'r-1', 'cook'], {
      queryParams: {
        servings: 4,
        units: 'Original',
        from: 'recipe',
      },
    });
  });

  it('opens cook log dialog on openCookLogDialog()', () => {
    component.openCookLogDialog();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('opens and closes photo lightbox', () => {
    component.openPhotoLightbox('/api/uploads/cook-logs/dish.webp');
    expect(component['previewPhotoUrl']()).toBe('/api/uploads/cook-logs/dish.webp');

    component.closePhotoLightbox();
    expect(component['previewPhotoUrl']()).toBeNull();
  });
  it('displays extracted model pill in stats strip when recipe was ingested and links to ingestion review', () => {
    const modelPill = fixture.nativeElement.querySelector('.stat-pill.model-stat');
    expect(modelPill).toBeTruthy();
    expect(modelPill.textContent).toContain('llama3.2-vision');
    expect(modelPill.getAttribute('href')).toContain('/ingest/review/job-100');
  });

  it('reruns ingestion and navigates to /ingest when rerun button is clicked', () => {
    const navSpy = vi.spyOn(component['router'], 'navigate').mockResolvedValue(true);
    component.rerunIngestion();
    expect(mockIngestionService.retryJob).toHaveBeenCalledWith('job-100');
    expect(navSpy).toHaveBeenCalledWith(['/ingest']);
  });
});
