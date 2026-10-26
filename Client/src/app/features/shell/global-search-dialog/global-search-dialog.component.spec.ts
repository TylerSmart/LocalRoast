import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { RecipeSummaryDto } from '../../../core/models/recipe.models';
import { CollectionDto } from '../../../core/models/taxonomy.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { GlobalSearchDialogComponent } from './global-search-dialog.component';

describe('GlobalSearchDialogComponent', () => {
  let component: GlobalSearchDialogComponent;
  let fixture: ComponentFixture<GlobalSearchDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };
  let mockRouter: { navigateByUrl: ReturnType<typeof vi.fn>; navigate: ReturnType<typeof vi.fn> };

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
    getRecipes: vi.fn().mockReturnValue(of(mockRecipes)),
  };

  const mockTaxonomyService = {
    getCollections: vi.fn().mockReturnValue(of(mockCollections)),
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };
    mockRouter = { navigateByUrl: vi.fn(), navigate: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [GlobalSearchDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: Router, useValue: mockRouter },
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
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
});
