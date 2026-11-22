import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import {
  RecipeAutocompleteItemDto,
  RecipeDetailDto,
  RecipeSummaryDto,
} from '../../../core/models/recipe.models';
import { CollectionDto, TagDto } from '../../../core/models/taxonomy.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { MediaService } from '../../../core/services/media/media.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { RecipeEditorComponent } from './recipe-editor.component';

describe('RecipeEditorComponent', () => {
  let component: RecipeEditorComponent;
  let fixture: ComponentFixture<RecipeEditorComponent>;

  const mockRecipeService = {
    getRecipes: vi.fn().mockReturnValue(of([] as RecipeSummaryDto[])),
    autocompleteRecipes: vi.fn().mockReturnValue(of([] as RecipeAutocompleteItemDto[])),
    getRecipeById: vi.fn(),
    createRecipe: vi.fn(),
    updateRecipe: vi.fn(),
  };

  const mockCatalogService = {
    getFoodItems: vi.fn().mockReturnValue(of([] as FoodItemDto[])),
  };

  const mockMediaService = {
    uploadRecipeCover: vi.fn(),
    uploadStepImage: vi.fn(),
  };

  const mockTaxonomyService = {
    getCollections: vi.fn().mockReturnValue(of([] as CollectionDto[])),
    getTags: vi.fn().mockReturnValue(of([] as TagDto[])),
  };

  const mockDialog = {
    open: vi.fn(),
  };

  const mockSnackBar = {
    open: vi.fn(),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RecipeEditorComponent],
      providers: [
        provideRouter([{ path: 'recipes/:id', component: class DummyComponent {} }]),
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: FoodCatalogService, useValue: mockCatalogService },
        { provide: MediaService, useValue: mockMediaService },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeEditorComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('initializes in new recipe mode with 1 blank ingredient and step', () => {
    expect(component).toBeTruthy();
    expect(component.ingredientsArray.length).toBe(1);
    expect(component.stepsArray.length).toBe(1);
    expect(component['isEdit']()).toBe(false);
  });

  it('adds and removes ingredients', () => {
    component.addIngredient();
    expect(component.ingredientsArray.length).toBe(2);

    component.removeIngredient(0);
    expect(component.ingredientsArray.length).toBe(1);
  });

  it('loads unresolved recipe references and clears them when linked to a saved recipe', () => {
    const referencedTitle = 'Foolproof All-Butter Dough';
    const recipeDetail: RecipeDetailDto = {
      id: 'parent-recipe',
      title: 'Blueberry Pie',
      slug: 'blueberry-pie',
      description: null,
      status: 'Published',
      baseServings: 8,
      yieldDisplay: null,
      prepTimeMinutes: null,
      cookTimeMinutes: null,
      totalTimeMinutes: null,
      difficulty: null,
      sourceUrl: null,
      sourceName: null,
      sourceAuthor: null,
      sourceBookTitle: null,
      sourceBookPage: null,
      coverImagePath: null,
      thumbnailImagePath: null,
      timesCooked: 0,
      lastCookedAt: null,
      averageRating: null,
      ingredients: [
        {
          id: 'component-ingredient',
          sortOrder: 0,
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
          displayName: referencedTitle,
          preparationNote: null,
          isOptional: false,
          originalRawText: null,
          unresolvedRecipeTitle: referencedTitle,
        },
      ],
      steps: [],
      linkedSubRecipes: [],
      createdAt: '2026-01-01T00:00:00Z',
      createdBy: 'test-user',
      modifiedAt: '2026-01-01T00:00:00Z',
      modifiedBy: 'test-user',
      collections: [],
      tags: [],
    };
    const savedSubRecipe: RecipeAutocompleteItemDto = {
      id: 'saved-sub-recipe',
      title: referencedTitle,
      slug: 'foolproof-all-butter-dough',
      status: 'Published',
      baseServings: 1,
      yieldDisplay: '1 double-crust pie',
    };

    component.populateForm(recipeDetail);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Not in the cookbook yet');
    expect(component.ingredientsArray.at(0).get('unresolvedRecipeTitle')?.value).toBe(
      referencedTitle,
    );

    component.onSubRecipeSelected(0, savedSubRecipe);
    fixture.detectChanges();

    expect(component.ingredientsArray.at(0).get('linkedRecipeId')?.value).toBe(savedSubRecipe.id);
    expect(component.ingredientsArray.at(0).get('linkedRecipeTitle')?.value).toBe(
      savedSubRecipe.title,
    );
    expect(component.ingredientsArray.at(0).get('unresolvedRecipeTitle')?.value).toBeNull();
  });

  it('supports unlinked sub-recipe typing and unlinks when editing away from a selected recipe', () => {
    const ing = component.ingredientsArray.at(0);
    ing.get('isSubRecipe')?.setValue(true);
    component.onSubRecipeToggled(0, true);

    component.onSubRecipeSelected(0, {
      id: 'sub-1',
      title: 'Quick Pickled Onions',
      slug: 'quick-pickled-onions',
      status: 'Published',
      baseServings: 4,
      yieldDisplay: '1 jar',
    });

    expect(ing.get('linkedRecipeId')?.value).toBe('sub-1');
    expect(ing.get('displayName')?.value).toBe('Quick Pickled Onions');

    component.onSubRecipeTextChanged(0, 'Future Sauce Recipe');

    expect(ing.get('linkedRecipeId')?.value).toBeNull();
    expect(ing.get('unresolvedRecipeTitle')?.value).toBe('Future Sauce Recipe');
    expect(ing.get('displayName')?.value).toBe('Future Sauce Recipe');
  });

  it('loads and saves inline step tokens', () => {
    const ingredient = (id: string, displayName: string) => ({
      id,
      sortOrder: 0,
      sectionName: null,
      quantity: 1,
      maxQuantity: null,
      unit: 'Cup' as const,
      customUnitName: null,
      foodItemId: null,
      foodItemName: null,
      isPantryStaple: false,
      linkedRecipeId: null,
      linkedRecipeTitle: null,
      linkedRecipeSlug: null,
      displayName,
      preparationNote: null,
      isOptional: false,
      originalRawText: null,
    });
    const detail: RecipeDetailDto = {
      id: 'r-1',
      title: 'Pancakes',
      slug: 'pancakes',
      description: null,
      status: 'Published',
      baseServings: 4,
      yieldDisplay: null,
      prepTimeMinutes: null,
      cookTimeMinutes: null,
      totalTimeMinutes: null,
      difficulty: null,
      sourceUrl: null,
      sourceName: null,
      sourceAuthor: null,
      sourceBookTitle: null,
      sourceBookPage: null,
      coverImagePath: null,
      thumbnailImagePath: null,
      timesCooked: 0,
      lastCookedAt: null,
      averageRating: null,
      ingredients: [ingredient('flour', 'flour'), ingredient('milk', 'whole milk')],
      steps: [
        {
          id: 's-1',
          stepNumber: 1,
          sectionName: null,
          instruction: 'Add ||{"ingredient":"milk","amount":3,"unit":"tbsp"}||.',
          imagePath: null,
          timers: [],
        },
      ],
      linkedSubRecipes: [],
      collections: [],
      tags: [],
      createdAt: '2026-01-01T00:00:00Z',
      createdBy: 'user',
      modifiedAt: '2026-01-01T00:00:00Z',
      modifiedBy: 'user',
    };
    component.populateForm(detail);
    fixture.detectChanges();

    const previewChip = fixture.nativeElement.querySelector(
      'app-step-instruction-editor .inline-step-chip.is-ingredient',
    );
    expect(previewChip?.textContent).toContain('3 tbsp milk');

    mockRecipeService.createRecipe.mockReturnValue(of(detail));
    component.onSave();
    expect(mockRecipeService.createRecipe).toHaveBeenLastCalledWith(
      expect.objectContaining({
        steps: [
          expect.objectContaining({
            instruction: 'Add ||{"ingredient":"milk","amount":3,"unit":"tbsp"}||.',
          }),
        ],
      }),
    );
  });

  it('submits valid recipe creation', () => {
    const savedDetail: RecipeDetailDto = {
      id: 'new-1',
      title: 'Quick Tomato Pasta',
      slug: 'quick-tomato-pasta',
      description: null,
      status: 'Published',
      baseServings: 4,
      yieldDisplay: null,
      prepTimeMinutes: 5,
      cookTimeMinutes: 15,
      totalTimeMinutes: 20,
      difficulty: 'Easy',
      sourceUrl: null,
      sourceName: null,
      sourceAuthor: null,
      sourceBookTitle: null,
      sourceBookPage: null,
      coverImagePath: null,
      thumbnailImagePath: null,
      timesCooked: 0,
      lastCookedAt: null,
      averageRating: null,
      ingredients: [],
      steps: [],
      linkedSubRecipes: [],
      collections: [],
      tags: [],
      createdAt: '2026-01-01T00:00:00Z',
      createdBy: 'user',
      modifiedAt: '2026-01-01T00:00:00Z',
      modifiedBy: 'user',
    };
    mockRecipeService.createRecipe.mockReturnValue(of(savedDetail));

    component['form'].patchValue({
      title: 'Quick Tomato Pasta',
      baseServings: 4,
    });
    component.ingredientsArray.at(0).patchValue({
      displayName: 'Spaghetti',
      quantity: 1,
      alternativeMeasurement: { quantity: 100, unit: 'G' },
      preferAlternativeMeasurement: true,
      unit: 'Cup',
    });
    component.stepsArray.at(0).patchValue({
      instruction: 'Boil water and cook pasta.',
    });

    component.onSave();

    expect(mockRecipeService.createRecipe).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Quick Tomato Pasta',
        ingredients: [
          expect.objectContaining({
            alternativeMeasurement: { quantity: 100, unit: 'G' },
            preferAlternativeMeasurement: true,
          }),
        ],
        steps: [
          expect.objectContaining({
            instruction: 'Boil water and cook pasta.',
          }),
        ],
      }),
    );
  });

  it('derives timers from inline step tokens on save', () => {
    mockRecipeService.createRecipe.mockReturnValue(
      of({
        id: 'saved-2',
        title: 'Tokenized Pasta',
        slug: 'tokenized-pasta',
        description: null,
        status: 'Published',
        baseServings: 4,
        yieldDisplay: null,
        prepTimeMinutes: null,
        cookTimeMinutes: null,
        totalTimeMinutes: null,
        difficulty: null,
        sourceUrl: null,
        sourceName: null,
        sourceAuthor: null,
        sourceBookTitle: null,
        sourceBookPage: null,
        coverImagePath: null,
        thumbnailImagePath: null,
        timesCooked: 0,
        lastCookedAt: null,
        averageRating: null,
        ingredients: [],
        steps: [],
        linkedSubRecipes: [],
        collections: [],
        tags: [],
        createdAt: '2026-01-01T00:00:00Z',
        createdBy: 'user',
        modifiedAt: '2026-01-01T00:00:00Z',
        modifiedBy: 'user',
      } as RecipeDetailDto),
    );

    component['form'].patchValue({
      title: 'Tokenized Pasta',
      baseServings: 4,
    });
    component.ingredientsArray.at(0).patchValue({
      displayName: 'Spaghetti',
      quantity: 1,
      unit: 'Lb',
    });
    component.stepsArray.at(0).patchValue({
      instruction:
        'Cook ||{"ingredient":"Spaghetti","amount":0.5,"unit":"lb"}|| for ||{"type":"timer","seconds":480,"label":"Boil"}|| at ||{"type":"temperature","fahrenheit":212}||.',
    });

    component.onSave();

    expect(mockRecipeService.createRecipe).toHaveBeenCalledWith(
      expect.objectContaining({
        steps: [
          expect.objectContaining({
            timers: [{ durationSeconds: 480, label: 'Boil' }],
          }),
        ],
      }),
    );
  });

  it('adds, removes, and saves structured ingredient replacements', () => {
    mockRecipeService.createRecipe.mockReturnValue(
      of({
        id: 'new-rep-rec',
        title: 'Tacos',
        slug: 'tacos',
        description: null,
        status: 'Published',
        baseServings: 4,
        yieldDisplay: null,
        prepTimeMinutes: null,
        cookTimeMinutes: null,
        totalTimeMinutes: null,
        difficulty: null,
        sourceUrl: null,
        sourceName: null,
        sourceAuthor: null,
        sourceBookTitle: null,
        sourceBookPage: null,
        extractedModel: null,
        ingestionJobId: null,
        coverImagePath: null,
        thumbnailImagePath: null,
        timesCooked: 0,
        lastCookedAt: null,
        averageRating: null,
        ingredients: [],
        steps: [],
        linkedSubRecipes: [],
        collections: [],
        tags: [],
        createdAt: '2026-01-01T00:00:00Z',
        createdBy: 'user',
        modifiedAt: '2026-01-01T00:00:00Z',
        modifiedBy: 'user',
      } as RecipeDetailDto),
    );

    mockRecipeService.createRecipe.mockClear();
    component['form'].patchValue({ title: 'Tacos', baseServings: 4 });
    component.ingredientsArray.at(0).patchValue({
      displayName: 'ground beef',
      quantity: 1,
      unit: 'Lb',
    });
    component.stepsArray.at(0).patchValue({ instruction: 'Brown ground beef in a skillet.' });
    component.addIngredientReplacement(0, {
      displayName: 'ground turkey',
      quantity: 1,
      maxQuantity: 1.5,
      unit: 'Lb',
      preparationNote: 'lean',
    });
    component.addIngredientReplacement(0, { displayName: 'tofu' });
    expect(component.getReplacementsArray(0).length).toBe(2);
    component.removeIngredientReplacement(0, 1);
    expect(component.getReplacementsArray(0).length).toBe(1);

    component.onSave();

    expect(mockRecipeService.createRecipe).toHaveBeenCalledWith(
      expect.objectContaining({
        ingredients: [
          expect.objectContaining({
            replacements: [
              expect.objectContaining({
                displayName: 'ground turkey',
                quantity: 1,
                maxQuantity: 1.5,
                unit: 'Lb',
                preparationNote: 'lean',
              }),
            ],
          }),
        ],
      }),
    );
  });

  it('filters food catalog autocomplete by name and alias and clears stale links on edit', () => {
    component['foodItems'].set([
      {
        id: 'f-1',
        name: 'All-Purpose Flour',
        aliases: ['AP Flour', 'plain flour'],
        category: 'Baking',
        isPantryStaple: true,
        referencedRecipeCount: 5,
        createdAt: '2026-01-01T00:00:00Z',
        modifiedAt: '2026-01-01T00:00:00Z',
      },
      {
        id: 'f-2',
        name: 'Yellow Onion',
        aliases: ['brown onion'],
        category: 'Produce',
        isPantryStaple: false,
        referencedRecipeCount: 3,
        createdAt: '2026-01-01T00:00:00Z',
        modifiedAt: '2026-01-01T00:00:00Z',
      },
    ]);

    component.ingredientsArray.at(0).patchValue({ displayName: 'plain' });
    expect(component['getFilteredFoodItemsForIngredient'](0).map((f) => f.id)).toEqual(['f-1']);

    component.onFoodItemSelected(0, 'f-1');
    expect(component.ingredientsArray.at(0).get('foodItemId')?.value).toBe('f-1');
    expect(component.ingredientsArray.at(0).get('displayName')?.value).toBe('All-Purpose Flour');

    component.ingredientsArray.at(0).patchValue({ displayName: 'Whole Wheat Flour' });
    component['onIngredientDisplayNameInput'](0);
    expect(component.ingredientsArray.at(0).get('foodItemId')?.value).toBeNull();
  });

  it('preserves alternative measurement and range disclosure across ingredient reordering', () => {
    component.addIngredient();
    const first = component.ingredientsArray.at(0);
    const second = component.ingredientsArray.at(1);
    first.patchValue({
      displayName: 'Flour',
      quantity: 2,
      unit: 'Cup',
      alternativeMeasurement: { quantity: 240, unit: 'G' },
      preferAlternativeMeasurement: true,
    });
    second.patchValue({ displayName: 'Sugar', quantity: 1, unit: 'Cup' });
    component['toggleRange'](second);

    expect(component['isAltMeasurementExpanded'](first)).toBe(true);
    expect(component['isAltMeasurementExpanded'](second)).toBe(false);
    expect(component['isRangeExpanded'](second)).toBe(true);

    component.moveIngredientUp(1);
    fixture.detectChanges();

    expect(component.ingredientsArray.at(0)).toBe(second);
    expect(component.ingredientsArray.at(1)).toBe(first);
    expect(component['isAltMeasurementExpanded'](component.ingredientsArray.at(0))).toBe(false);
    expect(component['isAltMeasurementExpanded'](component.ingredientsArray.at(1))).toBe(true);
    expect(component['isRangeExpanded'](component.ingredientsArray.at(0))).toBe(true);
  });

  it('bulk pastes multi-line ingredients with sections and replaces untouched starter row', () => {
    component['foodItems'].set([
      {
        id: 'f-salt',
        name: 'Kosher Salt',
        aliases: [],
        category: 'SpicesAndSeasonings',
        isPantryStaple: true,
        referencedRecipeCount: 1,
        createdAt: '2026-01-01T00:00:00Z',
        modifiedAt: '2026-01-01T00:00:00Z',
      },
    ]);

    component['pasteIngredientsText'].set(
      'Crust:\n2 cups all-purpose flour, sifted\n1 tsp kosher salt',
    );
    component['applyPastedIngredients']();

    expect(component.ingredientsArray.length).toBe(2);
    expect(component.ingredientsArray.at(0).get('sectionName')?.value).toBe('Crust');
    expect(component.ingredientsArray.at(0).get('displayName')?.value).toBe('all-purpose flour');
    expect(component.ingredientsArray.at(0).get('preparationNote')?.value).toBe('sifted');
    expect(component.ingredientsArray.at(1).get('foodItemId')?.value).toBe('f-salt');
  });

  it('renders dedicated load error state when loading an existing recipe fails', () => {
    mockRecipeService.getRecipeById.mockReturnValue(
      throwError(() => ({ status: 404, error: { detail: 'Recipe not found' } })),
    );

    component.loadRecipe('missing-id');
    fixture.detectChanges();

    expect(component['isLoading']()).toBe(false);
    expect(component['loadError']()).toContain('could not be found');
    expect(fixture.nativeElement.querySelector('.load-error-card')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.editor-form')).toBeNull();
  });

  it('filters tags by search query and hides destructive manage buttons by default', () => {
    component['tags'].set([
      {
        id: 't-1',
        name: 'Vegetarian',
        slug: 'vegetarian',
        category: 'Dietary',
        sortOrder: 1,
        isSystemSeeded: true,
        recipeCount: 0,
      },
      {
        id: 't-2',
        name: 'Italian',
        slug: 'italian',
        category: 'Cuisine',
        sortOrder: 2,
        isSystemSeeded: true,
        recipeCount: 0,
      },
    ]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('.chip-manage-btn').length).toBe(0);

    component['toggleManageTaxonomy']();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('.chip-manage-btn').length).toBe(4);

    component['tagSearchQuery'].set('veg');
    expect(component['filteredTagsByCategory']().length).toBe(1);
    expect(component['filteredTagsByCategory']()[0].tags.map((t) => t.name)).toEqual([
      'Vegetarian',
    ]);
  });

  it('collects actionable validation issues when saving an invalid form', () => {
    component.onSave();
    fixture.detectChanges();

    expect(component['validationIssues']().length).toBeGreaterThanOrEqual(3);
    expect(component['validationIssues']().map((i) => i.targetId)).toContain('field-recipe-title');
    expect(fixture.nativeElement.querySelectorAll('.validation-jump-chip').length).toBeGreaterThan(
      0,
    );
  });

  it('accepts recipe descriptions up to 4000 characters and rejects longer ones', () => {
    component['form'].controls.description.setValue('A'.repeat(3500));
    expect(component['form'].controls.description.valid).toBe(true);

    component['form'].controls.description.setValue('A'.repeat(4000));
    expect(component['form'].controls.description.valid).toBe(true);

    component['form'].controls.description.setValue('A'.repeat(4001));
    expect(component['form'].controls.description.hasError('maxlength')).toBe(true);
  });
});
