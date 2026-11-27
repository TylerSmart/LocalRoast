import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject, of } from 'rxjs';
import {
  RecipePagedResultDto,
  RecipeQueryParameters,
  RecipeSummaryDto,
} from '../../../core/models/recipe.models';
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
        hasMore: false,
        isFuzzySearch: false,
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

    const cardLink = fixture.nativeElement.querySelector(
      '.recipe-card a.recipe-card-link',
    ) as HTMLAnchorElement | null;
    expect(cardLink).not.toBeNull();
    expect(cardLink?.getAttribute('href')).toBe('/recipes/r-1');
    expect(fixture.nativeElement.querySelector('.recipe-card .card-actions-quick')).toBeNull();
    expect(fixture.nativeElement.querySelector('.header-actions .import-btn')).toBeNull();
    expect(fixture.nativeElement.querySelector('.header-actions .matcher-btn')).toBeNull();
    expect(fixture.nativeElement.querySelector('.header-actions .new-recipe-btn')).not.toBeNull();

    const collectionMenuBtn = fixture.nativeElement.querySelector(
      '.collection-pill-group .collection-menu-btn',
    ) as HTMLButtonElement | null;
    expect(collectionMenuBtn).not.toBeNull();
    collectionMenuBtn?.click();
    fixture.detectChanges();
    expect(document.querySelector('.edit-collection-btn')).not.toBeNull();
    expect(document.querySelector('.delete-collection-btn')).not.toBeNull();
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

  it('cancels stale recipe requests when a new search starts', () => {
    const staleResponse = new Subject<RecipePagedResultDto>();
    const latestResponse = new Subject<RecipePagedResultDto>();
    mockRecipeService.getRecipesPaged
      .mockReturnValueOnce(staleResponse)
      .mockReturnValueOnce(latestResponse);

    component.loadRecipes();
    component.loadRecipes();

    expect(staleResponse.observers).toHaveLength(0);

    latestResponse.next({
      items: sampleRecipes,
      totalCount: 1,
      pageNumber: 1,
      pageSize: 24,
      totalPages: 1,
      hasPreviousPage: false,
      hasNextPage: false,
      hasMore: false,
      isFuzzySearch: false,
    });
    expect(component['recipes']()).toEqual(sampleRecipes);
  });

  it('loads subsequent pages without requesting another total count', () => {
    component['recipes'].set(sampleRecipes);
    component['totalCount'].set(25);
    component['hasMore'].set(true);

    component.loadMoreRecipes();

    expect(mockRecipeService.getRecipesPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({ page: 2, includeTotalCount: false }),
    );
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

  it('reloads recipes when difficulty or max time filters change', () => {
    mockRecipeService.getRecipesPaged.mockClear();

    component.onDifficultyChange('Project');
    expect(mockRecipeService.getRecipesPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({ difficulty: 'Project', page: 1 }),
    );

    component.onMaxTimeChange(30);
    expect(mockRecipeService.getRecipesPaged).toHaveBeenLastCalledWith(
      expect.objectContaining({ difficulty: 'Project', maxTotalTimeMinutes: 30 }),
    );

    component.onDifficultyChange('All');
    expect(mockRecipeService.getRecipesPaged).toHaveBeenLastCalledWith(
      expect.not.objectContaining({ difficulty: expect.anything() }),
    );
  });

  it('exposes dismissible activeFilterChips for active drawer filters', () => {
    component.onDifficultyChange('Easy');
    component.onMaxTimeChange(30);

    const chips = component['activeFilterChips']();
    expect(chips.map((c) => c.label)).toEqual(['Difficulty: Easy', 'Under 30 min']);

    chips[0].remove();
    expect(component['selectedDifficulty']()).toBe('All');
    expect(component['activeFilterChips']().map((c) => c.label)).toEqual(['Under 30 min']);
  });

  it('syncs search, filters, and loaded page count to route query params', () => {
    const router = TestBed.inject(Router);
    const navSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    component.onDifficultyChange('Medium');
    component.onMaxTimeChange(45);
    expect(navSpy).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: expect.objectContaining({
          difficulty: 'Medium',
          maxTime: 45,
          status: null,
          page: null,
        }),
        replaceUrl: true,
      }),
    );

    const activatedRoute = TestBed.inject(ActivatedRoute);
    vi.spyOn(activatedRoute.snapshot, 'queryParamMap', 'get').mockReturnValue(
      convertToParamMap({ difficulty: 'Medium', maxTime: '45' }),
    );
    component.clearFilters();
    expect(navSpy).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: expect.objectContaining({
          q: null,
          status: null,
          difficulty: null,
          maxTime: null,
          minRating: null,
          collectionId: null,
          tags: null,
          forgotten: null,
          page: null,
        }),
        replaceUrl: true,
      }),
    );
  });

  it('restores filters and multi-page scroll depth from query params on init', async () => {
    const page1: RecipePagedResultDto = {
      items: sampleRecipes,
      totalCount: 2,
      pageNumber: 1,
      pageSize: 24,
      totalPages: 2,
      hasPreviousPage: false,
      hasNextPage: true,
      hasMore: true,
      isFuzzySearch: false,
    };
    const page2: RecipePagedResultDto = {
      items: [{ ...sampleRecipes[0], id: 'r-2', title: 'Page Two Pasta' }],
      totalCount: 2,
      pageNumber: 2,
      pageSize: 24,
      totalPages: 2,
      hasPreviousPage: true,
      hasNextPage: false,
      hasMore: false,
      isFuzzySearch: false,
    };
    mockRecipeService.getRecipesPaged.mockImplementation((q?: RecipeQueryParameters) =>
      of(q?.page === 2 ? page2 : page1),
    );

    const queryParamMap$ = new BehaviorSubject(
      convertToParamMap({
        q: 'pasta',
        difficulty: 'Easy',
        maxTime: '60',
        forgotten: '1',
        page: '2',
      }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [RecipeListComponent],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: queryParamMap$.asObservable(),
            snapshot: { queryParamMap: queryParamMap$.value },
          },
        },
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    const restoredFixture = TestBed.createComponent(RecipeListComponent);
    const restoredComp = restoredFixture.componentInstance;
    restoredFixture.detectChanges();

    expect(restoredComp['searchQuery']()).toBe('pasta');
    expect(restoredComp['selectedDifficulty']()).toBe('Easy');
    expect(restoredComp['maxTimeMinutes']()).toBe(60);
    expect(restoredComp['forgottenFavoritesOnly']()).toBe(true);
    expect(restoredComp['currentPage']()).toBe(2);
    expect(restoredComp['recipes']().map((r) => r.id)).toEqual(['r-1', 'r-2']);
  });

  it('debounces rapid multi-tag selection and protects active search typing from route param clobbering', async () => {
    vi.useFakeTimers();
    try {
      const queryParamMap$ = new BehaviorSubject(convertToParamMap({}));

      TestBed.resetTestingModule();
      await TestBed.configureTestingModule({
        imports: [RecipeListComponent],
        providers: [
          provideRouter([]),
          {
            provide: ActivatedRoute,
            useValue: {
              queryParamMap: queryParamMap$.asObservable(),
              snapshot: { queryParamMap: queryParamMap$.value },
            },
          },
          { provide: RecipeService, useValue: mockRecipeService },
          { provide: TaxonomyService, useValue: mockTaxonomyService },
          { provide: MatDialog, useValue: mockDialog },
          { provide: MatSnackBar, useValue: mockSnackBar },
        ],
      }).compileComponents();

      mockRecipeService.getRecipesPaged.mockClear();
      const raceFixture = TestBed.createComponent(RecipeListComponent);
      const raceComp = raceFixture.componentInstance;
      raceFixture.detectChanges();
      mockRecipeService.getRecipesPaged.mockClear();

      // Rapidly select 3 tags -> should coalesce into 1 request after 250ms debounce
      raceComp.onTagSelectionChange(['t1']);
      raceComp.onTagSelectionChange(['t1', 't2']);
      raceComp.onTagSelectionChange(['t1', 't2', 't3']);
      expect(mockRecipeService.getRecipesPaged).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(250);
      expect(mockRecipeService.getRecipesPaged).toHaveBeenCalledTimes(1);
      expect(mockRecipeService.getRecipesPaged).toHaveBeenLastCalledWith(
        expect.objectContaining({ tagIds: ['t1', 't2', 't3'] }),
      );

      // While search timer is pending, emitting stale route query params must NOT overwrite active typing
      raceComp.onSearchChange('blueb');
      queryParamMap$.next(convertToParamMap({ q: 'blu' }));
      expect(raceComp['searchQuery']()).toBe('blueb');

      await vi.advanceTimersByTimeAsync(300);
      expect(mockRecipeService.getRecipesPaged).toHaveBeenLastCalledWith(
        expect.objectContaining({ search: 'blueb' }),
      );
    } finally {
      vi.useRealTimers();
    }
  });
});
