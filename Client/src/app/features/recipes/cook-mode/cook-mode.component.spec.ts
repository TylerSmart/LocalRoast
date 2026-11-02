import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RecipeDetailDto } from '../../../core/models/recipe.models';
import { KitchenTimerService } from '../../../core/services/kitchen-timer/kitchen-timer.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { WakeLockService } from '../../../core/services/wake-lock/wake-lock.service';
import { CookModeComponent } from './cook-mode.component';

describe('CookModeComponent', () => {
  let component: CookModeComponent;
  let fixture: ComponentFixture<CookModeComponent>;

  let mockRecipeService: {
    getRecipeById: ReturnType<typeof vi.fn>;
  };
  let mockWakeLockService: {
    acquire: ReturnType<typeof vi.fn>;
    release: ReturnType<typeof vi.fn>;
    status: ReturnType<typeof vi.fn>;
  };
  let mockKitchenTimerService: {
    startTimer: ReturnType<typeof vi.fn>;
    pauseTimer: ReturnType<typeof vi.fn>;
    resumeTimer: ReturnType<typeof vi.fn>;
    addSeconds: ReturnType<typeof vi.fn>;
    resetTimer: ReturnType<typeof vi.fn>;
    dismissTimer: ReturnType<typeof vi.fn>;
    timers: ReturnType<typeof vi.fn>;
  };
  let mockDialog: {
    open: ReturnType<typeof vi.fn>;
  };
  let mockRouter: {
    navigate: ReturnType<typeof vi.fn>;
  };

  const mockRecipe: RecipeDetailDto = {
    id: 'rec-1',
    title: 'Beef Chili',
    slug: 'beef-chili',
    description: 'Hearty chili',
    status: 'Published',
    baseServings: 4,
    yieldDisplay: '4 bowls',
    prepTimeMinutes: 15,
    cookTimeMinutes: 45,
    totalTimeMinutes: 60,
    difficulty: 'Easy',
    sourceUrl: null,
    sourceName: null,
    sourceAuthor: null,
    sourceBookTitle: null,
    sourceBookPage: null,
    coverImagePath: null,
    thumbnailImagePath: null,
    timesCooked: 2,
    lastCookedAt: '2026-10-15T12:00:00Z',
    averageRating: 4.5,
    ratingCount: 2,
    cookLogs: [
      {
        id: 'cl-1',
        recipeId: 'rec-1',
        cookedByUserId: 'u1',
        cookedByDisplayName: 'Tyler',
        cookedAt: '2026-10-15T12:00:00Z',
        servingsMade: 4,
        rating: 5,
        notes: 'Added extra cumin, loved it!',
        photoImagePath: null,
        photoThumbnailPath: null,
        createdAt: '2026-10-15T12:00:00Z',
        modifiedAt: '2026-10-15T12:00:00Z',
      },
    ],
    ingredients: [
      {
        id: 'ing-1',
        sortOrder: 1,
        sectionName: 'Meat',
        quantity: 2,
        maxQuantity: null,
        unit: 'Lb',
        customUnitName: null,
        foodItemId: null,
        foodItemName: null,
        isPantryStaple: false,
        linkedRecipeId: null,
        linkedRecipeTitle: null,
        linkedRecipeSlug: null,
        displayName: 'Ground Beef',
        preparationNote: null,
        isOptional: false,
        originalRawText: null,
      },
      {
        id: 'ing-2',
        sortOrder: 2,
        sectionName: 'Seasoning',
        quantity: 2,
        maxQuantity: null,
        unit: 'Tbsp',
        customUnitName: null,
        foodItemId: null,
        foodItemName: null,
        isPantryStaple: true,
        linkedRecipeId: null,
        linkedRecipeTitle: null,
        linkedRecipeSlug: null,
        displayName: 'Chili Powder',
        preparationNote: null,
        isOptional: false,
        originalRawText: null,
      },
    ],
    steps: [
      {
        id: 'st-1',
        stepNumber: 1,
        sectionName: 'Brown Meat',
        instruction: 'Brown ground beef in a large pot over medium heat.',
        imagePath: null,
        ingredientIds: ['ing-1'],
        timers: [{ id: 't-1', sortOrder: 1, durationSeconds: 600, label: 'Brown Beef' }],
      },
      {
        id: 'st-2',
        stepNumber: 2,
        sectionName: 'Simmer',
        instruction: 'Stir in chili powder and simmer gently.',
        imagePath: null,
        ingredientIds: ['ing-2'],
        timers: [{ id: 't-2', sortOrder: 1, durationSeconds: 1800, label: 'Simmer Chili' }],
      },
    ],
    linkedSubRecipes: [],
    collections: [],
    tags: [],
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'system',
    modifiedAt: '2026-01-01T00:00:00Z',
    modifiedBy: 'system',
  };

  beforeEach(async () => {
    mockRecipeService = {
      getRecipeById: vi.fn().mockReturnValue(of(mockRecipe)),
    };
    mockWakeLockService = {
      acquire: vi.fn().mockResolvedValue(undefined),
      release: vi.fn().mockResolvedValue(undefined),
      status: vi.fn().mockReturnValue('active'),
    };
    mockKitchenTimerService = {
      startTimer: vi.fn().mockReturnValue('t-123'),
      pauseTimer: vi.fn(),
      resumeTimer: vi.fn(),
      addSeconds: vi.fn(),
      resetTimer: vi.fn(),
      dismissTimer: vi.fn(),
      timers: vi.fn().mockReturnValue([]),
    };
    mockDialog = {
      open: vi.fn().mockReturnValue({ afterClosed: () => of(null) }),
    };
    mockRouter = {
      navigate: vi.fn().mockResolvedValue(true),
    };

    await TestBed.configureTestingModule({
      imports: [CookModeComponent],
      providers: [
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: WakeLockService, useValue: mockWakeLockService },
        { provide: KitchenTimerService, useValue: mockKitchenTimerService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: Router, useValue: mockRouter },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: { get: () => 'rec-1' },
              queryParamMap: {
                has: (k: string) => k === 'servings',
                get: (k: string) => (k === 'servings' ? '8' : null),
              },
            },
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CookModeComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads recipe, acquires wake lock, and applies servings query param', () => {
    expect(mockRecipeService.getRecipeById).toHaveBeenCalledWith('rec-1');
    expect(mockWakeLockService.acquire).toHaveBeenCalled();
    expect(component['recipe']()?.title).toBe('Beef Chili');
    expect(component['targetServings']()).toBe(8);
    expect(component['scaleFactor']()).toBe(2);
  });

  it('navigates through steps using nextStep and prevStep', () => {
    expect(component['activeStepIndex']()).toBe(0);

    component['nextStep']();
    expect(component['activeStepIndex']()).toBe(1);

    component['prevStep']();
    expect(component['activeStepIndex']()).toBe(0);
  });

  it('responds to keyboard ArrowRight and ArrowLeft', () => {
    component['onWindowKeydown'](new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    expect(component['activeStepIndex']()).toBe(1);

    component['onWindowKeydown'](new KeyboardEvent('keydown', { key: 'ArrowLeft' }));
    expect(component['activeStepIndex']()).toBe(0);
  });

  it('launches a step timer via KitchenTimerService', () => {
    const timer = mockRecipe.steps[0].timers[0];
    component['startStepTimer'](timer, 1);

    expect(mockKitchenTimerService.startTimer).toHaveBeenCalledWith(
      'rec-1',
      'Beef Chili',
      'Brown Beef',
      600,
      1,
    );
  });

  it('opens CookLogDialog when clicking Finish & Log Cook on last step', () => {
    component['goToStep'](1);
    component['nextStep']();

    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('releases wake lock on component destroy', () => {
    fixture.destroy();
    expect(mockWakeLockService.release).toHaveBeenCalled();
  });
});
