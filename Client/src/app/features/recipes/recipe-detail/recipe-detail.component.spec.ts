import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { RecipeDetailDto } from '../../../core/models/recipe.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
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

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RecipeDetailComponent],
      providers: [
        provideRouter([]),
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeDetailComponent);
    component = fixture.componentInstance;
    component.loadRecipe('r-1');
    fixture.detectChanges();
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

  it('opens organize dialog on openOrganizeDialog()', () => {
    component.openOrganizeDialog();
    expect(mockDialog.open).toHaveBeenCalled();
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

  it('switches units to metric when metric is selected', () => {
    component['targetSystem'].set('Metric');
    fixture.detectChanges();

    const map = component['scaledIngredientsMap']();
    const noodles = map.get('i-1');
    // 2 cups ~ 473 ml
    expect(noodles?.scaled.unit).toBe('Ml');
    expect(noodles?.scaled.unitLabel).toBe('ml');
  });
});
