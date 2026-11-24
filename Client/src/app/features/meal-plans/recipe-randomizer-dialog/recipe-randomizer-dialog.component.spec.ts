import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { of, Subject } from 'rxjs';
import { MealPlanEntryDto } from '../../../core/models/meal-plan.models';
import { RecipeDetailDto } from '../../../core/models/recipe.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import {
  RecipeRandomizerDialogComponent,
  RecipeRandomizerDialogData,
} from './recipe-randomizer-dialog.component';

function makeRecipe(
  id: string,
  title: string,
  overrides: Partial<RecipeDetailDto> = {},
): RecipeDetailDto {
  return {
    id,
    title,
    slug: title.toLowerCase().replace(/\s+/g, '-'),
    description:
      'A rich and comforting dish slow-simmered with herbs, roasted garlic, olive oil, and crusty bread on the side for dipping until every last drop is gone.',
    status: 'Published',
    baseServings: 4,
    yieldDisplay: '4 servings',
    prepTimeMinutes: 15,
    cookTimeMinutes: 30,
    totalTimeMinutes: 45,
    difficulty: 'Medium',
    sourceUrl: null,
    sourceName: null,
    sourceAuthor: null,
    sourceBookTitle: null,
    sourceBookPage: null,
    coverImagePath: `/api/uploads/${id}-cover.jpg`,
    thumbnailImagePath: `/api/uploads/${id}-thumb.webp`,
    timesCooked: 1,
    lastCookedAt: null,
    averageRating: 4.5,
    ingredients: Array.from({ length: 7 }, (_, i) => ({
      id: `${id}-ing-${i + 1}`,
      sortOrder: i,
      sectionName: null,
      quantity: i + 1,
      maxQuantity: null,
      unit: 'Cup' as const,
      customUnitName: null,
      foodItemId: null,
      foodItemName: null,
      isPantryStaple: false,
      linkedRecipeId: null,
      linkedRecipeTitle: null,
      linkedRecipeSlug: null,
      displayName: `Ingredient ${i + 1}`,
      preparationNote: i === 0 ? '(diced)' : null,
      isOptional: false,
      originalRawText: null,
    })),
    steps: [
      {
        id: `${id}-step-1`,
        stepNumber: 1,
        sectionName: null,
        instruction: 'Prep all aromatics and heat oil in a heavy skillet over medium heat.',
        imagePath: null,
        timers: [],
      },
      {
        id: `${id}-step-2`,
        stepNumber: 2,
        sectionName: null,
        instruction: 'Saute onions and garlic until fragrant and translucent.',
        imagePath: null,
        timers: [],
      },
      {
        id: `${id}-step-3`,
        stepNumber: 3,
        sectionName: null,
        instruction: 'Simmer gently and serve warm with fresh herbs.',
        imagePath: null,
        timers: [],
      },
    ],
    linkedSubRecipes: [],
    collections: [],
    tags: [],
    createdAt: '2026-11-01T00:00:00Z',
    createdBy: 'chef',
    modifiedAt: '2026-11-01T00:00:00Z',
    modifiedBy: 'chef',
    ...overrides,
  };
}

function dispatchPointerEvent(
  target: HTMLElement,
  type: 'pointerdown' | 'pointermove' | 'pointerup',
  coords: { clientX: number; clientY: number },
  pointerType = 'touch',
): void {
  const evt = new Event(type, { bubbles: true, cancelable: true }) as Event & {
    clientX: number;
    clientY: number;
    pointerId: number;
    button: number;
    pointerType: string;
  };
  Object.defineProperties(evt, {
    clientX: { value: coords.clientX },
    clientY: { value: coords.clientY },
    pointerId: { value: 1 },
    button: { value: 0 },
    pointerType: { value: pointerType },
  });
  target.dispatchEvent(evt);
}

describe('RecipeRandomizerDialogComponent', () => {
  let component: RecipeRandomizerDialogComponent;
  let fixture: ComponentFixture<RecipeRandomizerDialogComponent>;

  const recipeOne = makeRecipe('r-1', 'Smoky Chili');
  const recipeTwo = makeRecipe('r-2', 'Lemon Herb Pasta', {
    coverImagePath: null,
    thumbnailImagePath: null,
    baseServings: 6,
  });

  const mockDialogRef = {
    close: vi.fn(),
  };

  const mockSnackBar = {
    open: vi.fn(),
  };

  const mockOnMealAdded = vi.fn();

  const mockDialogData: RecipeRandomizerDialogData = {
    initialDate: '2026-11-24',
    initialSlot: 'Dinner',
    initialWeekStartDate: '2026-11-22',
    onMealAdded: mockOnMealAdded,
  };

  const mockRecipeService = {
    getRandomRecipes: vi.fn(),
  };

  const mockMealPlanService = {
    createEntry: vi.fn(),
  };

  async function setupComponent(initialDeck: RecipeDetailDto[] = [recipeOne, recipeTwo]) {
    mockRecipeService.getRandomRecipes.mockReturnValue(of(initialDeck));
    mockMealPlanService.createEntry.mockReturnValue(
      of({ id: 'entry-1', displayTitle: 'Smoky Chili' } as MealPlanEntryDto),
    );

    await TestBed.configureTestingModule({
      imports: [RecipeRandomizerDialogComponent],
      providers: [
        provideRouter([]),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockDialogData },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: MealPlanService, useValue: mockMealPlanService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeRandomizerDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  beforeEach(() => {
    vi.clearAllMocks();
    mockRecipeService.getRandomRecipes.mockReset();
    mockMealPlanService.createEntry.mockReset();
  });

  it('loads random recipes on init and renders image, shortened description, ingredients, and steps', async () => {
    await setupComponent();

    expect(mockRecipeService.getRandomRecipes).toHaveBeenCalledWith({ count: 10 });

    const el = fixture.nativeElement as HTMLElement;
    const img = el.querySelector('.randomizer-hero-image') as HTMLImageElement;
    expect(img).toBeTruthy();
    expect(img.getAttribute('src')).toBe('/api/uploads/r-1-cover.jpg');

    const desc = el.querySelector('[data-testid="randomizer-description"]') as HTMLElement;
    expect(desc.classList.contains('is-clamped')).toBe(true);

    expect(el.querySelectorAll('[data-testid="randomizer-ingredients-list"] li')).toHaveLength(5);
    expect(el.querySelectorAll('[data-testid="randomizer-steps-list"] li')).toHaveLength(2);
  });

  it('expands and collapses description, ingredients, and steps, and resets state when swiping to next recipe', async () => {
    await setupComponent();
    const el = fixture.nativeElement as HTMLElement;

    // Expand all three sections
    (el.querySelector('[data-testid="toggle-description-btn"]') as HTMLButtonElement).click();
    (el.querySelector('[data-testid="toggle-ingredients-btn"]') as HTMLButtonElement).click();
    (el.querySelector('[data-testid="toggle-steps-btn"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(
      el.querySelector('[data-testid="randomizer-description"]')?.classList.contains('is-clamped'),
    ).toBe(false);
    expect(el.querySelectorAll('[data-testid="randomizer-ingredients-list"] li')).toHaveLength(7);
    expect(el.querySelectorAll('[data-testid="randomizer-steps-list"] li')).toHaveLength(3);

    // Move to next recipe -> expansions reset & servings update to recipeTwo.baseServings (6)
    (el.querySelector('[data-testid="next-random-recipe-btn"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(el.querySelector('.randomizer-recipe-title')?.textContent?.trim()).toBe(
      'Lemon Herb Pasta',
    );
    expect(el.querySelector('.randomizer-image-placeholder')).toBeTruthy();
    expect(
      el.querySelector('[data-testid="randomizer-description"]')?.classList.contains('is-clamped'),
    ).toBe(true);
    expect(el.querySelectorAll('[data-testid="randomizer-ingredients-list"] li')).toHaveLength(5);
    expect(el.querySelectorAll('[data-testid="randomizer-steps-list"] li')).toHaveLength(2);
    expect(el.querySelector('[data-testid="randomizer-servings-value"]')?.textContent?.trim()).toBe(
      '6 srv',
    );
  });

  it('supports touch/pointer swipe left & right and keyboard ArrowRight & ArrowLeft navigation', async () => {
    await setupComponent();
    const el = fixture.nativeElement as HTMLElement;
    const card = el.querySelector('.randomizer-recipe-card') as HTMLElement;

    // Swipe left (dx < -60) -> Next
    dispatchPointerEvent(card, 'pointerdown', { clientX: 240, clientY: 120 });
    dispatchPointerEvent(card, 'pointerup', { clientX: 140, clientY: 125 });
    fixture.detectChanges();
    expect(el.querySelector('.randomizer-recipe-title')?.textContent?.trim()).toBe(
      'Lemon Herb Pasta',
    );

    // Swipe right (dx > 60) -> Previous
    dispatchPointerEvent(card, 'pointerdown', { clientX: 100, clientY: 120 });
    dispatchPointerEvent(card, 'pointerup', { clientX: 210, clientY: 125 });
    fixture.detectChanges();
    expect(el.querySelector('.randomizer-recipe-title')?.textContent?.trim()).toBe('Smoky Chili');

    // Keyboard ArrowRight / ArrowLeft
    component['onKeydown'](new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    fixture.detectChanges();
    expect(el.querySelector('.randomizer-recipe-title')?.textContent?.trim()).toBe(
      'Lemon Herb Pasta',
    );

    component['onKeydown'](new KeyboardEvent('keydown', { key: 'ArrowLeft' }));
    fixture.detectChanges();
    expect(el.querySelector('.randomizer-recipe-title')?.textContent?.trim()).toBe('Smoky Chili');
  });

  it('wraps around cleanly when unseen recipes are exhausted', async () => {
    const initialFullBatch = Array.from({ length: 10 }, (_, idx) =>
      makeRecipe(`r-${idx + 1}`, `Batch Recipe ${idx + 1}`),
    );
    const wrappedRecipe = makeRecipe('r-1', 'Batch Recipe 1');
    mockRecipeService.getRandomRecipes
      .mockReturnValueOnce(of(initialFullBatch))
      .mockReturnValueOnce(of([]))
      .mockReturnValueOnce(of([wrappedRecipe]));

    mockMealPlanService.createEntry.mockReturnValue(of({ id: 'entry-1' } as MealPlanEntryDto));

    await TestBed.configureTestingModule({
      imports: [RecipeRandomizerDialogComponent],
      providers: [
        provideRouter([]),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockDialogData },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: MealPlanService, useValue: mockMealPlanService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeRandomizerDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    // Advance to the end of the 10-item deck (at index 7 background prefetch sees [] -> sets allUnseenLoaded)
    for (let i = 0; i < 9; i++) {
      component['nextRecipe']();
    }
    expect(component['currentRecipe']()?.id).toBe('r-10');

    // Advancing past the end wraps around excluding currentId ('r-10')
    component['nextRecipe']();
    fixture.detectChanges();

    expect(mockRecipeService.getRandomRecipes).toHaveBeenCalledTimes(3);
    expect(mockRecipeService.getRandomRecipes).toHaveBeenLastCalledWith({
      count: 10,
      excludeIds: ['r-10'],
    });
    expect(component['currentRecipe']()?.title).toBe('Batch Recipe 1');
  });

  it('adds current recipe as On-Deck or Scheduled and advances to next recipe', async () => {
    await setupComponent();
    const el = fixture.nativeElement as HTMLElement;

    // 1. Default On-Deck placement
    (el.querySelector('[data-testid="add-random-recipe-btn"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(mockMealPlanService.createEntry).toHaveBeenCalledWith({
      entryType: 'Recipe',
      isOnDeck: true,
      weekStartDate: '2026-11-22',
      scheduledDate: null,
      slot: null,
      recipeId: 'r-1',
      targetServings: 4,
    });
    expect(mockOnMealAdded).toHaveBeenCalledTimes(1);
    expect(el.querySelector('.randomizer-recipe-title')?.textContent?.trim()).toBe(
      'Lemon Herb Pasta',
    );
    expect(el.querySelector('[data-testid="randomizer-planned-count"]')?.textContent?.trim()).toBe(
      '1 planned',
    );

    // 2. Switch to Scheduled placement and adjust servings
    component['setPlacement']('scheduled');
    component['slot'].set('Lunch');
    component['adjustServings'](2);
    fixture.detectChanges();

    (el.querySelector('[data-testid="add-random-recipe-btn"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(mockMealPlanService.createEntry).toHaveBeenLastCalledWith({
      entryType: 'Recipe',
      isOnDeck: false,
      weekStartDate: '2026-11-22',
      scheduledDate: '2026-11-24',
      slot: 'Lunch',
      recipeId: 'r-2',
      targetServings: 8,
    });
    expect(mockOnMealAdded).toHaveBeenCalledTimes(2);
  });

  it('shows empty state when cookbook has no published recipes', async () => {
    await setupComponent([]);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.empty-title')?.textContent).toContain(
      'No published recipes found in your cookbook yet.',
    );
    const addBtn = el.querySelector('[data-testid="add-random-recipe-btn"]') as HTMLButtonElement;
    expect(addBtn.disabled).toBe(true);
  });

  it('shows live directional badge and rubber-bands at start while dragging horizontally, without hijacking vertical scroll', async () => {
    await setupComponent();
    const el = fixture.nativeElement as HTMLElement;
    const card = el.querySelector('.randomizer-recipe-card') as HTMLElement;

    // 1. Vertical scroll gesture (dy > dx) does not trigger horizontal drag
    dispatchPointerEvent(card, 'pointerdown', { clientX: 150, clientY: 100 });
    dispatchPointerEvent(card, 'pointermove', { clientX: 155, clientY: 160 });
    fixture.detectChanges();
    expect(component['isDraggingCard']()).toBe(false);
    expect(el.querySelector('[data-testid="randomizer-drag-badge"]')).toBeNull();
    dispatchPointerEvent(card, 'pointerup', { clientX: 155, clientY: 160 });

    // 2. Dragging right at index 0 rubber-bands and displays First Recipe boundary pill
    dispatchPointerEvent(card, 'pointerdown', { clientX: 100, clientY: 120 });
    dispatchPointerEvent(card, 'pointermove', { clientX: 200, clientY: 122 });
    fixture.detectChanges();
    expect(component['isDraggingCard']()).toBe(true);
    expect(component['dragOffsetX']()).toBe(25);
    const boundaryBadge = el.querySelector('[data-testid="randomizer-drag-badge"]') as HTMLElement;
    expect(boundaryBadge?.getAttribute('data-drag-dir')).toBe('boundary');
    expect(boundaryBadge?.textContent).toContain('First Recipe');
    expect(boundaryBadge?.classList.contains('is-committed')).toBe(false);
    dispatchPointerEvent(card, 'pointerup', { clientX: 200, clientY: 122 });
    fixture.detectChanges();
    expect(component['currentRecipe']()?.id).toBe('r-1');

    // 3. Dragging left >= 60px marks Next Recipe committed and advances on release
    dispatchPointerEvent(card, 'pointerdown', { clientX: 240, clientY: 120 });
    dispatchPointerEvent(card, 'pointermove', { clientX: 155, clientY: 122 });
    fixture.detectChanges();
    const nextBadge = el.querySelector('[data-testid="randomizer-drag-badge"]') as HTMLElement;
    expect(nextBadge?.getAttribute('data-drag-dir')).toBe('next');
    expect(nextBadge?.textContent).toContain('Next Recipe');
    expect(nextBadge?.classList.contains('is-committed')).toBe(true);
    dispatchPointerEvent(card, 'pointerup', { clientX: 155, clientY: 122 });
    fixture.detectChanges();
    expect(component['currentRecipe']()?.id).toBe('r-2');

    // 4. Dragging right from index 1 marks Previous committed and returns on release
    dispatchPointerEvent(card, 'pointerdown', { clientX: 100, clientY: 120 });
    dispatchPointerEvent(card, 'pointermove', { clientX: 185, clientY: 122 });
    fixture.detectChanges();
    const prevBadge = el.querySelector('[data-testid="randomizer-drag-badge"]') as HTMLElement;
    expect(prevBadge?.getAttribute('data-drag-dir')).toBe('prev');
    expect(prevBadge?.textContent).toContain('Previous');
    expect(prevBadge?.classList.contains('is-committed')).toBe(true);
    dispatchPointerEvent(card, 'pointerup', { clientX: 185, clientY: 122 });
    fixture.detectChanges();
    expect(component['currentRecipe']()?.id).toBe('r-1');
  });

  it('applies directional slide classes and renders outgoing ghost overlay until animationend', async () => {
    await setupComponent();
    const el = fixture.nativeElement as HTMLElement;

    // Next -> slide-in-right-a + outgoing ghost (next)
    (el.querySelector('[data-testid="next-random-recipe-btn"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component['lastMoveDirection']()).toBe('next');
    expect(el.querySelector('.randomizer-recipe-card')?.className).toContain('slide-in-right-a');
    let ghost = el.querySelector('[data-testid="randomizer-outgoing-ghost"]') as HTMLElement;
    expect(ghost).toBeTruthy();
    expect(ghost.getAttribute('data-direction')).toBe('next');
    expect(ghost.textContent).toContain('Next Recipe');

    ghost.dispatchEvent(new Event('animationend'));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="randomizer-outgoing-ghost"]')).toBeNull();

    // Previous -> slide-in-left-b + outgoing ghost (prev)
    (el.querySelector('[data-testid="prev-random-recipe-btn"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component['lastMoveDirection']()).toBe('prev');
    expect(el.querySelector('.randomizer-recipe-card')?.className).toContain('slide-in-left-b');
    ghost = el.querySelector('[data-testid="randomizer-outgoing-ghost"]') as HTMLElement;
    expect(ghost?.getAttribute('data-direction')).toBe('prev');
    expect(ghost?.textContent).toContain('Previous');

    ghost.dispatchEvent(new Event('animationend'));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="randomizer-outgoing-ghost"]')).toBeNull();

    // Add to On-Deck -> slide-in-right-a + outgoing ghost (added: Planned!)
    (el.querySelector('[data-testid="add-random-recipe-btn"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(component['lastMoveDirection']()).toBe('added');
    expect(el.querySelector('.randomizer-recipe-card')?.className).toContain('slide-in-right-a');
    ghost = el.querySelector('[data-testid="randomizer-outgoing-ghost"]') as HTMLElement;
    expect(ghost?.getAttribute('data-direction')).toBe('added');
    expect(ghost?.textContent).toContain('Planned!');
  });

  it('continues handling document ArrowRight/ArrowLeft across 12+ presses even when focus is on body or background prefetch is in flight', async () => {
    const batchOne = Array.from({ length: 10 }, (_, idx) =>
      makeRecipe(`k-${idx + 1}`, `Keyboard Recipe ${idx + 1}`),
    );
    const batchTwo = Array.from({ length: 10 }, (_, idx) =>
      makeRecipe(`k-${idx + 11}`, `Keyboard Recipe ${idx + 11}`),
    );
    const prefetchSubject = new Subject<RecipeDetailDto[]>();

    mockRecipeService.getRandomRecipes
      .mockReturnValueOnce(of(batchOne))
      .mockReturnValueOnce(prefetchSubject.asObservable());
    mockMealPlanService.createEntry.mockReturnValue(of({ id: 'entry-1' } as MealPlanEntryDto));

    await TestBed.configureTestingModule({
      imports: [RecipeRandomizerDialogComponent],
      providers: [
        provideRouter([]),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockDialogData },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: MealPlanService, useValue: mockMealPlanService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeRandomizerDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const nextBtn = el.querySelector('[data-testid="next-random-recipe-btn"]') as HTMLButtonElement;
    expect(document.activeElement).toBe(document.body);

    // 7 presses advance to index 7, triggering silent background prefetch
    for (let i = 0; i < 7; i++) {
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
      fixture.detectChanges();
    }
    expect(component['currentRecipe']()?.id).toBe('k-8');
    expect(mockRecipeService.getRandomRecipes).toHaveBeenCalledTimes(2);
    expect(component['isFetchingMore']()).toBe(false);
    expect(nextBtn.disabled).toBe(false);

    // Back-and-forth presses continue working while background prefetch is in flight
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    fixture.detectChanges();
    expect(component['currentRecipe']()?.id).toBe('k-7');

    for (let i = 0; i < 3; i++) {
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
      fixture.detectChanges();
    }
    expect(component['currentRecipe']()?.id).toBe('k-10');

    // Complete background prefetch and continue past 12+ total presses
    prefetchSubject.next(batchTwo);
    prefetchSubject.complete();
    fixture.detectChanges();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    fixture.detectChanges();
    expect(component['currentRecipe']()?.id).toBe('k-12');
  });

  it('sets valid ghost CSS rotation custom properties from drag release and separates snap-back from card entrance', async () => {
    await setupComponent();
    const el = fixture.nativeElement as HTMLElement;
    const card = el.querySelector('.randomizer-recipe-card') as HTMLElement;

    // Complete left drag (-110px)
    dispatchPointerEvent(card, 'pointerdown', { clientX: 250, clientY: 120 });
    dispatchPointerEvent(card, 'pointermove', { clientX: 210, clientY: 120 });
    dispatchPointerEvent(card, 'pointermove', { clientX: 140, clientY: 120 });
    dispatchPointerEvent(card, 'pointerup', { clientX: 140, clientY: 120 });
    fixture.detectChanges();

    expect(component['currentRecipe']()?.id).toBe('r-2');
    expect(component['isSnappingBack']()).toBe(false);
    expect(card.className).toContain('slide-in-right-a');

    const ghost = el.querySelector('[data-testid="randomizer-outgoing-ghost"]') as HTMLElement;
    expect(ghost.classList.contains('is-from-drag')).toBe(true);
    expect(ghost.style.getPropertyValue('--ghost-start-x')).toBe('-110px');
    expect(ghost.style.getPropertyValue('--ghost-start-rot')).toBe('-3.85deg');

    // Card entrance class clears once animation finishes
    card.dispatchEvent(new Event('animationend'));
    fixture.detectChanges();
    expect(card.className).not.toContain('slide-in-');

    // Incomplete drag (< 60px) snaps back without re-playing entrance animation
    dispatchPointerEvent(card, 'pointerdown', { clientX: 200, clientY: 120 });
    dispatchPointerEvent(card, 'pointermove', { clientX: 165, clientY: 120 });
    dispatchPointerEvent(card, 'pointerup', { clientX: 165, clientY: 120 });
    fixture.detectChanges();

    expect(component['currentRecipe']()?.id).toBe('r-2');
    expect(component['isSnappingBack']()).toBe(true);
    expect(card.className).not.toContain('slide-in-');
  });

  it('shows desktop arrow-key instructions on desktop devices', async () => {
    await setupComponent();
    const el = fixture.nativeElement as HTMLElement;
    const navHint = el.querySelector('[data-testid="randomizer-nav-hint"]');
    expect(navHint?.textContent).toContain('Use ← or → arrow keys');
  });

  it('shows swipe instructions on coarse-pointer touchscreen devices', async () => {
    const originalDescriptor = Object.getOwnPropertyDescriptor(window, 'matchMedia');
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      writable: true,
      value: (query: string) => ({
        matches: query === '(pointer: coarse)',
        media: query,
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        dispatchEvent: vi.fn(),
      }),
    });

    try {
      await setupComponent();
      const el = fixture.nativeElement as HTMLElement;
      const navHint = el.querySelector('[data-testid="randomizer-nav-hint"]');
      expect(navHint?.textContent).toContain('Swipe left or right');
    } finally {
      if (originalDescriptor) {
        Object.defineProperty(window, 'matchMedia', originalDescriptor);
      } else {
        Reflect.deleteProperty(window, 'matchMedia');
      }
    }
  });

  it('displays recipe collections and tags when present and hides row when empty', async () => {
    const taggedFirst = makeRecipe('r-1', 'Smoky Chili', {
      collections: [{ id: 'col-1', name: 'Weeknight Dinners', slug: 'weeknight-dinners' }],
      tags: [
        { id: 'tag-1', name: 'Vegetarian', slug: 'vegetarian', category: 'Dietary' },
        { id: 'tag-2', name: 'Mexican', slug: 'mexican', category: 'Cuisine' },
      ],
    });
    const untaggedSecond = makeRecipe('r-2', 'Lemon Herb Pasta', {
      collections: [],
      tags: [],
    });

    await setupComponent([taggedFirst, untaggedSecond]);
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('[data-testid="randomizer-taxonomy-row"]')).toBeTruthy();
    const collectionBadges = el.querySelectorAll('[data-testid="randomizer-collection-badge"]');
    expect(collectionBadges).toHaveLength(1);
    expect(collectionBadges[0].textContent).toContain('Weeknight Dinners');

    const tagBadges = el.querySelectorAll('[data-testid="randomizer-tag-badge"]');
    expect(tagBadges).toHaveLength(2);
    expect(tagBadges[0].textContent?.trim()).toBe('Vegetarian');
    expect(tagBadges[0].getAttribute('data-category')).toBe('dietary');
    expect(tagBadges[1].textContent?.trim()).toBe('Mexican');
    expect(tagBadges[1].getAttribute('data-category')).toBe('cuisine');

    (el.querySelector('[data-testid="next-random-recipe-btn"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(el.querySelector('[data-testid="randomizer-taxonomy-row"]')).toBeNull();
  });
});
