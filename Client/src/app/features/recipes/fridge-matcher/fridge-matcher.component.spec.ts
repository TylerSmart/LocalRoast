import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import { IngredientMatchResultDto } from '../../../core/models/recipe.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { FridgeMatcherComponent } from './fridge-matcher.component';

describe('FridgeMatcherComponent', () => {
  let component: FridgeMatcherComponent;
  let fixture: ComponentFixture<FridgeMatcherComponent>;

  const mockFoodItems: FoodItemDto[] = [
    {
      id: 'fi-1',
      name: 'Chicken Thighs',
      defaultStoreAisleId: null,
      defaultStoreAisleName: null,
      defaultStoreAisleSortOrder: null,
      isPantryStaple: false,
      aliases: ['Boneless Chicken Thighs'],
      referencedRecipeCount: 2,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
    {
      id: 'fi-2',
      name: 'Lime',
      defaultStoreAisleId: null,
      defaultStoreAisleName: null,
      defaultStoreAisleSortOrder: null,
      isPantryStaple: false,
      aliases: ['Lime Juice'],
      referencedRecipeCount: 3,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
    {
      id: 'fi-3',
      name: 'Kosher Salt',
      defaultStoreAisleId: null,
      defaultStoreAisleName: null,
      defaultStoreAisleSortOrder: null,
      isPantryStaple: true,
      aliases: [],
      referencedRecipeCount: 10,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
  ];

  const mockCatalogService = {
    getFoodItems: vi.fn().mockReturnValue(of(mockFoodItems)),
  };

  const mockRecipeService = {
    matchRecipesByIngredients: vi.fn(),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FridgeMatcherComponent],
      providers: [
        provideRouter([]),
        { provide: FoodCatalogService, useValue: mockCatalogService },
        { provide: RecipeService, useValue: mockRecipeService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(FridgeMatcherComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads food catalog on init', () => {
    expect(component).toBeTruthy();
    expect(component['foodItems']().length).toBe(3);
  });

  it('adds food item and triggers recipe matching', () => {
    const matchResults: IngredientMatchResultDto[] = [
      {
        recipe: {
          id: 'r1',
          title: 'Lime Chicken',
          slug: 'lime-chicken',
          description: 'Tangy chicken',
          status: 'Published',
          baseServings: 4,
          yieldDisplay: null,
          prepTimeMinutes: 10,
          cookTimeMinutes: 20,
          totalTimeMinutes: 30,
          difficulty: 'Easy',
          sourceName: null,
          thumbnailImagePath: null,
          collections: [],
          tags: [],
          ingredientCount: 3,
          stepCount: 2,
          timesCooked: 0,
          averageRating: null,
          createdAt: '2026-01-01T00:00:00Z',
          modifiedAt: '2026-01-01T00:00:00Z',
        },
        matchedCount: 2,
        selectedOnHandCount: 2,
        additionalNonStapleCount: 0,
        pantryStaplesUsedCount: 1,
        matchedIngredientNames: ['Chicken Thighs', 'Lime'],
        missingNonStapleNames: [],
      },
    ];
    mockRecipeService.matchRecipesByIngredients.mockReturnValue(of(matchResults));

    component.addFoodItem(mockFoodItems[0]);
    component.addFoodItem(mockFoodItems[1]);

    expect(component['selectedFoodItems']().length).toBe(2);
    expect(mockRecipeService.matchRecipesByIngredients).toHaveBeenCalledWith({
      foodItemIds: ['fi-1', 'fi-2'],
    });
    expect(component['matchResults']().length).toBe(1);
    expect(component['matchResults']()[0].additionalNonStapleCount).toBe(0);
  });

  it('clears all selected ingredients', () => {
    component.addFoodItem(mockFoodItems[0]);
    expect(component['selectedFoodItems']().length).toBe(1);

    component.clearSelected();
    expect(component['selectedFoodItems']().length).toBe(0);
    expect(component['matchResults']().length).toBe(0);
  });
});
