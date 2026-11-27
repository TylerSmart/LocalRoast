import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { of, Subject } from 'rxjs';
import { RecipeSummaryDto } from '../../../core/models/recipe.models';
import { CollectionDto } from '../../../core/models/taxonomy.models';
import { KitchenReferenceService } from '../../../core/services/kitchen-reference/kitchen-reference.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { GlobalSearchDialogComponent } from './global-search-dialog.component';

describe('GlobalSearchDialogComponent', () => {
  let component: GlobalSearchDialogComponent;
  let fixture: ComponentFixture<GlobalSearchDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };
  let mockRouter: { navigateByUrl: ReturnType<typeof vi.fn>; navigate: ReturnType<typeof vi.fn> };
  let mockKitchenReferenceService: { openDialog: ReturnType<typeof vi.fn> };

  const mockCollections: CollectionDto[] = [
    {
      id: 'c1',
      name: 'Weeknight Dinners',
      slug: 'weeknight-dinners',
      description: null,
      sortOrder: 1,
      isSystemSeeded: true,
      recipeCount: 5,
    },
  ];

  const mockRecipes: RecipeSummaryDto[] = [
    {
      id: 'r1',
      title: 'Chicken Soup',
      slug: 'chicken-soup',
      description: 'Warm soup',
      status: 'Published',
      baseServings: 4,
      yieldDisplay: null,
      prepTimeMinutes: 10,
      cookTimeMinutes: 30,
      totalTimeMinutes: 40,
      difficulty: 'Easy',
      sourceName: null,
      thumbnailImagePath: null,
      collections: [],
      tags: [],
      ingredientCount: 4,
      stepCount: 2,
      timesCooked: 1,
      averageRating: 5,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    },
  ];

  const mockRecipeService = {
    getRecipesPaged: vi.fn().mockReturnValue(
      of({
        items: mockRecipes,
        totalCount: null,
        page: 1,
        pageSize: 20,
        hasMore: false,
        isFuzzySearch: false,
      }),
    ),
  };

  const mockTaxonomyService = {
    getCollections: vi.fn().mockReturnValue(of(mockCollections)),
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };
    mockRouter = { navigateByUrl: vi.fn(), navigate: vi.fn() };
    mockKitchenReferenceService = { openDialog: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [GlobalSearchDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: Router, useValue: mockRouter },
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
        { provide: KitchenReferenceService, useValue: mockKitchenReferenceService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(GlobalSearchDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders quick actions and seeded collections by default', () => {
    expect(component).toBeTruthy();
    const results = component['searchResults']();
    expect(results.some((r) => r.id === 'action-new-recipe')).toBe(true);
    expect(results.some((r) => r.id === 'action-matcher')).toBe(true);
    expect(results.some((r) => r.id === 'col-c1')).toBe(true);
  });

  it('closes from the visible close control without selecting a result', () => {
    const closeButton: HTMLButtonElement = fixture.nativeElement.querySelector(
      'button[aria-label="Close search"]',
    );
    closeButton.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    closeButton.click();

    expect(mockDialogRef.close).toHaveBeenCalledTimes(1);
    expect(mockRouter.navigateByUrl).not.toHaveBeenCalled();
    expect(mockRouter.navigate).not.toHaveBeenCalled();
  });

  it('navigates to selected action and closes dialog', () => {
    const results = component['searchResults']();
    const matcherAction = results.find((r) => r.id === 'action-matcher')!;
    component.selectResult(matcherAction);

    expect(mockDialogRef.close).toHaveBeenCalled();
    expect(mockRouter.navigateByUrl).toHaveBeenCalledWith('/matcher');
  });

  it('navigates to collection query and closes dialog', () => {
    const results = component['searchResults']();
    const colAction = results.find((r) => r.id === 'col-c1')!;
    component.selectResult(colAction);

    expect(mockDialogRef.close).toHaveBeenCalled();
    expect(mockRouter.navigate).toHaveBeenCalledWith(['/recipes'], {
      queryParams: { collectionId: 'c1' },
    });
  });

  it('clears the active query and resets active index when clearSearch is called', () => {
    component['searchControl'].setValue('chicken');
    component['activeIndex'].set(2);

    component.clearSearch();

    expect(component['searchControl'].value).toBe('');
    expect(component['searchQuery']()).toBe('');
    expect(component['activeIndex']()).toBe(0);
  });

  it('includes Kitchen Reference quick action and matching reference entries in search', () => {
    const defaultResults = component['searchResults']();
    expect(defaultResults.some((r) => r.id === 'action-kitchen-reference')).toBe(true);

    component['searchQuery'].set('julienne');
    const results = component['searchResults']();
    const julienneHit = results.find((r) => r.type === 'reference' && r.title === 'Julienne');
    expect(julienneHit).toBeDefined();

    component.selectResult(julienneHit!);
    expect(mockDialogRef.close).toHaveBeenCalled();
    expect(mockKitchenReferenceService.openDialog).toHaveBeenCalledWith({
      tab: 'glossary',
      query: 'Julienne',
    });
  });

  it('uses paged recipe search and cancels stale in-flight searches via switchMap', async () => {
    vi.useFakeTimers();
    try {
      const first$ = new Subject<{
        items: RecipeSummaryDto[];
        totalCount: number | null;
        page: number;
        pageSize: number;
        hasMore: boolean;
        isFuzzySearch: boolean;
      }>();
      const second$ = new Subject<{
        items: RecipeSummaryDto[];
        totalCount: number | null;
        page: number;
        pageSize: number;
        hasMore: boolean;
        isFuzzySearch: boolean;
      }>();
      mockRecipeService.getRecipesPaged
        .mockReset()
        .mockReturnValueOnce(first$)
        .mockReturnValueOnce(second$);

      component['searchControl'].setValue('blu');
      await vi.advanceTimersByTimeAsync(250);
      expect(mockRecipeService.getRecipesPaged).toHaveBeenCalledWith({
        search: 'blu',
        page: 1,
        pageSize: 20,
        includeTotalCount: false,
      });

      component['searchControl'].setValue('blueberry');
      await vi.advanceTimersByTimeAsync(250);

      // Emit newer search first, then stale search — stale observable was unsubscribed by switchMap
      second$.next({
        items: mockRecipes,
        totalCount: null,
        page: 1,
        pageSize: 20,
        hasMore: false,
        isFuzzySearch: false,
      });
      first$.next({
        items: [],
        totalCount: null,
        page: 1,
        pageSize: 20,
        hasMore: false,
        isFuzzySearch: false,
      });

      expect(component['recipes']()).toEqual(mockRecipes);
      expect(component['isSearching']()).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });
});
