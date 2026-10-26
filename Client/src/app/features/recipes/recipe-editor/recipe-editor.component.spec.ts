import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import { RecipeDetailDto, RecipeSummaryDto } from '../../../core/models/recipe.models';
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
    });
    component.stepsArray.at(0).patchValue({
      instruction: 'Boil water and cook pasta.',
    });

    component.onSave();

    expect(mockRecipeService.createRecipe).toHaveBeenCalledWith(
      expect.objectContaining({
        title: 'Quick Tomato Pasta',
      }),
    );
  });
});
