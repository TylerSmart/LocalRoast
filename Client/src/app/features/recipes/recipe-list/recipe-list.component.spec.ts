import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { RecipeQueryParameters, RecipeSummaryDto } from '../../../core/models/recipe.models';
import { CollectionDto, TagDto } from '../../../core/models/taxonomy.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { RecipeListComponent } from './recipe-list.component';

describe('RecipeListComponent', () => {
  let component: RecipeListComponent;
  let fixture: ComponentFixture<RecipeListComponent>;

  const sampleRecipes: RecipeSummaryDto[] = [
    {
      id: 'r-1',
      title: 'Classic Bolognese',
      slug: 'classic-bolognese',
      description: 'Slow-simmered rich meat sauce',
      status: 'Published',
      baseServings: 6,
      yieldDisplay: '6 servings',
      prepTimeMinutes: 20,
      cookTimeMinutes: 180,
      totalTimeMinutes: 200,
      difficulty: 'Medium',
      sourceName: 'Marcella Hazan',
      thumbnailImagePath: null,
      collections: [{ id: 'c1', name: 'Weeknight Staples', slug: 'weeknight-staples' }],
      tags: [{ id: 't1', name: 'Italian', slug: 'cuisine-italian', category: 'Cuisine' }],
      ingredientCount: 10,
      stepCount: 6,
      timesCooked: 4,
      averageRating: 5,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
  ];

  const mockRecipeService = {
    getRecipes: vi.fn().mockReturnValue(of(sampleRecipes)),
    getRecipesPaged: vi.fn().mockImplementation((query?: RecipeQueryParameters) => {
      let filtered = sampleRecipes;
      if (query?.search) {
        const s = query.search.toLowerCase();
        filtered = filtered.filter(
          (r) =>
            r.title.toLowerCase().includes(s) ||
            r.tags.some((t) => t.name.toLowerCase().includes(s)) ||
            r.collections.some((c) => c.name.toLowerCase().includes(s)),
        );
      }
      return of({
        items: filtered,
        totalCount: filtered.length,
        pageNumber: query?.page || 1,
        pageSize: query?.pageSize || 24,
        totalPages: 1,
        hasPreviousPage: false,
        hasNextPage: false,
      });
    }),
    deleteRecipe: vi.fn(),
  };

  const mockTaxonomyService = {
    getCollections: vi.fn().mockReturnValue(
      of([
        {
          id: 'c1',
          name: 'Weeknight Staples',
          slug: 'weeknight-staples',
          description: null,
          sortOrder: 1,
          isSystemSeeded: true,
          recipeCount: 1,
        },
      ] as CollectionDto[]),
    ),
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
      imports: [RecipeListComponent],
      providers: [
        provideRouter([]),
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders loaded recipe cards and collections', () => {
    expect(component).toBeTruthy();
    expect(component['recipes']().length).toBe(1);
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Classic Bolognese');
    expect(text).toContain('Marcella Hazan');
    expect(text).toContain('Weeknight Staples');
  });

  it('filters recipes by search query matching tags or collections', () => {
    component.onSearchChange('Italian');
    component.loadRecipes();
    fixture.detectChanges();
    expect(component['filteredRecipes']().length).toBe(1);

    component.onSearchChange('NonExistent');
    component.loadRecipes();
    fixture.detectChanges();
    expect(component['filteredRecipes']().length).toBe(0);
  });

  it('resets filters on clearFilters()', () => {
    component['searchQuery'].set('Italian');
    component['selectedDifficulty'].set('Easy');
    expect(component['hasActiveFilters']()).toBe(true);

    component.clearFilters();
    expect(component['searchQuery']()).toBe('');
    expect(component['selectedDifficulty']()).toBe('All');
    expect(component['hasActiveFilters']()).toBe(false);
  });

  it('toggles forgotten favorites and applies min rating', () => {
    component.toggleForgottenFavorites();
    expect(component['forgottenFavoritesOnly']()).toBe(true);
    expect(mockRecipeService.getRecipesPaged).toHaveBeenCalledWith(
      expect.objectContaining({ forgottenFavoritesOnly: true }),
    );

    component.onMinRatingChange(4);
    expect(component['minRating']()).toBe(4);
    expect(mockRecipeService.getRecipesPaged).toHaveBeenCalledWith(
      expect.objectContaining({ minRating: 4 }),
    );
  });
});
