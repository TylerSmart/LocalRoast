import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { FoodItemDto, StoreAisleDto } from '../../../core/models/food-catalog.models';
import type { IngestionJobDetailDto } from '../../../core/models/ingestion.models';
import { RecipeDetailDto } from '../../../core/models/recipe.models';
import { CollectionDto, TagDto } from '../../../core/models/taxonomy.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { IngestionReviewComponent } from './ingestion-review.component';

describe('IngestionReviewComponent', () => {
  let component: IngestionReviewComponent;
  let fixture: ComponentFixture<IngestionReviewComponent>;
  let router: Router;

  let mockIngestionService: {
    getJobById: ReturnType<typeof vi.fn>;
    saveDraft: ReturnType<typeof vi.fn>;
    approveAndPublish: ReturnType<typeof vi.fn>;
    discardJob: ReturnType<typeof vi.fn>;
  };
  let mockFoodCatalogService: {
    getStoreAisles: ReturnType<typeof vi.fn>;
    getFoodItems: ReturnType<typeof vi.fn>;
  };
  let mockTaxonomyService: {
    getCollections: ReturnType<typeof vi.fn>;
    getTags: ReturnType<typeof vi.fn>;
  };
  let mockDialog: {
    open: ReturnType<typeof vi.fn>;
  };
  let mockSnackBar: {
    open: ReturnType<typeof vi.fn>;
  };

  const sampleJob: IngestionJobDetailDto = {
    id: 'job-123',
    channel: 'WebUrl',
    status: 'ReadyForReview',
    displayTitle: 'Crispy Sesame Tofu',
    sourceUrl: 'https://example.com/tofu',
    rawText: null,
    scrapedPage: {
      sourceUrl: 'https://example.com/tofu',
      pageTitle: 'Crispy Sesame Tofu',
      siteName: 'Example Recipes',
      author: 'Chef Alice',
      primaryImageUrl: 'https://example.com/tofu.png',
      hasJsonLdRecipe: true,
      rawIngredientLines: ['1 block firm tofu', '2 tbsp sesame oil'],
      rawInstructionLines: ['Cube tofu and fry', 'Simmer for 10 minutes'],
      yieldText: '4 servings',
      prepTimeIso: 'PT15M',
      cookTimeIso: 'PT10M',
      totalTimeIso: 'PT25M',
      cleanedBodyText: 'Page content...',
    },
    normalizedSourceText: 'Cleaned recipe source content',
    downloadedCoverImagePath: '/api/uploads/tofu.webp',
    downloadedThumbnailImagePath: '/api/uploads/tofu_thumb.webp',
    extractedDraft: {
      title: 'Crispy Sesame Tofu',
      description: 'Quick and easy weeknight tofu',
      baseServings: 4,
      yieldDisplay: '4 servings',
      prepTimeMinutes: 15,
      cookTimeMinutes: 10,
      totalTimeMinutes: 25,
      difficulty: 'Easy',
      sourceUrl: 'https://example.com/tofu',
      sourceName: 'Example Recipes',
      sourceAuthor: 'Chef Alice',
      coverImagePath: '/api/uploads/tofu.webp',
      thumbnailImagePath: '/api/uploads/tofu_thumb.webp',
      suggestedCollectionIds: ['col-1'],
      suggestedTagIds: ['tag-1'],
      ingredients: [
        {
          sortOrder: 0,
          sectionName: null,
          originalRawText: '1 block firm tofu',
          quantity: 1,
          maxQuantity: null,
          unit: 'Custom',
          customUnitName: 'block',
          displayName: 'firm tofu, pressed and cubed',
          preparationNote: 'pressed and cubed',
          isOptional: false,
          matchedFoodItemId: 'food-1',
          matchedFoodItemName: 'Tofu',
          isNewFoodItem: false,
          proposedFoodItemName: 'Tofu',
          suggestedStoreAisleId: 'aisle-1',
          suggestedIsPantryStaple: false,
        },
        {
          sortOrder: 1,
          sectionName: null,
          originalRawText: '2 tbsp toasted sesame oil',
          quantity: 2,
          maxQuantity: null,
          unit: 'Tbsp',
          customUnitName: null,
          displayName: 'toasted sesame oil',
          preparationNote: null,
          isOptional: false,
          matchedFoodItemId: null,
          matchedFoodItemName: null,
          isNewFoodItem: true,
          proposedFoodItemName: 'Toasted Sesame Oil',
          suggestedStoreAisleId: 'aisle-2',
          suggestedIsPantryStaple: true,
        },
      ],
      steps: [
        {
          stepNumber: 1,
          sectionName: null,
          instruction: 'Simmer tofu in sauce for 10 minutes.',
          ingredientIndices: [0, 1],
          timers: [
            {
              durationSeconds: 600,
              label: 'Simmer',
            },
          ],
          imagePath: null,
        },
      ],
      confidenceScore: 0.94,
    },
    confidenceScore: 0.94,
    statusDetail: 'Ready for side-by-side review.',
    attemptCount: 1,
    nextRetryAt: null,
    processedAt: '2026-10-26T12:00:00Z',
    resultingRecipeId: null,
    resultingRecipeSlug: null,
    submittedByUsername: 'alice',
    createdAt: '2026-10-26T12:00:00Z',
    modifiedAt: '2026-10-26T12:00:00Z',
  };

  const sampleAisles: StoreAisleDto[] = [
    { id: 'aisle-1', name: 'Produce', sortOrder: 1, isSystemSeeded: true },
    { id: 'aisle-2', name: 'Spices & Baking', sortOrder: 2, isSystemSeeded: false },
  ];

  const sampleFoodItems: FoodItemDto[] = [
    {
      id: 'food-1',
      name: 'Tofu',
      defaultStoreAisleId: 'aisle-1',
      defaultStoreAisleName: 'Produce',
      defaultStoreAisleSortOrder: 1,
      isPantryStaple: false,
      referencedRecipeCount: 2,
      aliases: [],
      createdAt: '2026-10-26T12:00:00Z',
      modifiedAt: '2026-10-26T12:00:00Z',
    },
  ];

  const sampleCollections: CollectionDto[] = [
    {
      id: 'col-1',
      name: 'Weeknight Dinners',
      slug: 'weeknight-dinners',
      description: null,
      sortOrder: 1,
      isSystemSeeded: false,
      recipeCount: 3,
    },
  ];

  const sampleTags: TagDto[] = [
    {
      id: 'tag-1',
      name: 'Vegetarian',
      slug: 'vegetarian',
      category: 'Dietary',
      sortOrder: 1,
      isSystemSeeded: false,
      recipeCount: 4,
    },
  ];

  beforeEach(async () => {
    mockIngestionService = {
      getJobById: vi.fn().mockReturnValue(of(sampleJob)),
      saveDraft: vi.fn().mockReturnValue(of(sampleJob)),
      approveAndPublish: vi.fn().mockReturnValue(
        of({
          id: 'rec-1',
          title: 'Crispy Sesame Tofu',
          slug: 'crispy-sesame-tofu',
        } as RecipeDetailDto),
      ),
      discardJob: vi.fn().mockReturnValue(of(undefined)),
    };

    mockFoodCatalogService = {
      getStoreAisles: vi.fn().mockReturnValue(of(sampleAisles)),
      getFoodItems: vi.fn().mockReturnValue(of(sampleFoodItems)),
    };

    mockTaxonomyService = {
      getCollections: vi.fn().mockReturnValue(of(sampleCollections)),
      getTags: vi.fn().mockReturnValue(of(sampleTags)),
    };

    mockDialog = {
      open: vi.fn(),
    };

    mockSnackBar = {
      open: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [IngestionReviewComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: {
                get: (key: string) => (key === 'id' ? 'job-123' : null),
              },
            },
          },
        },
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: FoodCatalogService, useValue: mockFoodCatalogService },
        { provide: TaxonomyService, useValue: mockTaxonomyService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: MatSnackBar, useValue: mockSnackBar },
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);

    fixture = TestBed.createComponent(IngestionReviewComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads job detail and populates draft form', () => {
    expect(mockIngestionService.getJobById).toHaveBeenCalledWith('job-123');
    expect(component.form.controls.title.value).toBe('Crispy Sesame Tofu');
    expect(component.ingredientsArray.length).toBe(2);
    expect(component.stepsArray.length).toBe(1);

    // Verify first ingredient (matched)
    const ing0 = component.ingredientsArray.at(0);
    expect(ing0.get('matchedFoodItemId')?.value).toBe('food-1');
    expect(ing0.get('isNewFoodItem')?.value).toBe(false);

    // Verify second ingredient (new food item)
    const ing1 = component.ingredientsArray.at(1);
    expect(ing1.get('matchedFoodItemId')?.value).toBeNull();
    expect(ing1.get('isNewFoodItem')?.value).toBe(true);
    expect(ing1.get('proposedFoodItemName')?.value).toBe('Toasted Sesame Oil');
  });

  it('toggles mobile panes cleanly', () => {
    expect(component.mobilePane()).toBe('draft');
    component.setMobilePane('source');
    expect(component.mobilePane()).toBe('source');
  });

  it('adds and removes ingredients', () => {
    component.addIngredient({ displayName: 'Scallions' });
    expect(component.ingredientsArray.length).toBe(3);

    component.removeIngredient(2);
    expect(component.ingredientsArray.length).toBe(2);
  });

  it('adds and removes steps and timers', () => {
    component.addStep({ instruction: 'Serve warm over rice.' });
    expect(component.stepsArray.length).toBe(2);

    component.addStepTimer(0);
    expect(component.getStepTimersArray(0).length).toBe(2);

    component.removeStepTimer(0, 1);
    expect(component.getStepTimersArray(0).length).toBe(1);

    component.removeStep(1);
    expect(component.stepsArray.length).toBe(1);
  });

  it('saves draft successfully', () => {
    component.saveDraft();
    expect(mockIngestionService.saveDraft).toHaveBeenCalledWith(
      'job-123',
      expect.objectContaining({
        title: 'Crispy Sesame Tofu',
      }),
    );
    expect(mockSnackBar.open).toHaveBeenCalledWith(
      'Review draft saved.',
      'Close',
      expect.any(Object),
    );
  });

  it('approves and publishes recipe, navigating to /recipes/:id', () => {
    component.approveAndPublish();
    expect(mockIngestionService.approveAndPublish).toHaveBeenCalledWith(
      'job-123',
      expect.objectContaining({
        title: 'Crispy Sesame Tofu',
      }),
    );
    expect(router.navigate).toHaveBeenCalledWith(['/recipes', 'rec-1']);
  });

  it('discards job after confirmation', () => {
    mockDialog.open.mockReturnValue({
      afterClosed: () => of(true),
    });

    component.discardJob();
    expect(mockDialog.open).toHaveBeenCalled();
    expect(mockIngestionService.discardJob).toHaveBeenCalledWith('job-123');
    expect(router.navigate).toHaveBeenCalledWith(['/ingest']);
  });
});
