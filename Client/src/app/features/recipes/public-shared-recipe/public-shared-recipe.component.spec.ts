import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { PublicSharedRecipeDto } from '../../../core/models/share-link.models';
import { ShareLinkService } from '../../../core/services/share-link/share-link.service';
import { PublicSharedRecipeComponent } from './public-shared-recipe.component';

describe('PublicSharedRecipeComponent', () => {
  let component: PublicSharedRecipeComponent;
  let fixture: ComponentFixture<PublicSharedRecipeComponent>;
  let shareLinkServiceSpy: {
    getPublicSharedRecipe: ReturnType<typeof vi.fn>;
  };

  const mockPublicRecipe: PublicSharedRecipeDto = {
    title: 'Carnitas Tacos',
    description: 'Crispy braised pork',
    baseServings: 4,
    yieldDisplay: '8 tacos',
    prepTimeMinutes: 20,
    cookTimeMinutes: 120,
    totalTimeMinutes: 140,
    difficulty: 'Medium',
    sourceUrl: null,
    sourceName: 'Family Book',
    sourceAuthor: null,
    sourceBookTitle: null,
    sourceBookPage: null,
    coverImagePath: null,
    tags: [{ name: 'Mexican', category: 'Cuisine' }],
    ingredients: [
      {
        id: 'ing-1',
        sortOrder: 1,
        sectionName: 'Meat',
        quantity: 2,
        maxQuantity: null,
        unit: 'Lb',
        customUnitName: null,
        isPantryStaple: false,
        linkedSubRecipeId: 'sub-1',
        linkedSubRecipeTitle: 'Salsa',
        displayName: 'Pork Shoulder',
        preparationNote: 'cut into cubes',
        isOptional: false,
      },
    ],
    steps: [
      {
        id: 'step-1',
        stepNumber: 1,
        sectionName: null,
        instruction: 'Sear meat on high heat.',
        imagePath: null,
        ingredientIds: ['ing-1'],
        timers: [{ id: 't-1', sortOrder: 1, durationSeconds: 600, label: 'Sear' }],
      },
    ],
    linkedSubRecipes: [
      {
        id: 'sub-1',
        title: 'Salsa',
        baseServings: 4,
        yieldDisplay: null,
        ingredients: [
          {
            id: 'sub-ing-1',
            sortOrder: 1,
            sectionName: null,
            quantity: 3,
            maxQuantity: null,
            unit: 'Whole',
            customUnitName: null,
            isPantryStaple: false,
            linkedSubRecipeId: null,
            linkedSubRecipeTitle: null,
            displayName: 'Roma Tomatoes',
            preparationNote: null,
            isOptional: false,
          },
        ],
        steps: [
          {
            id: 'sub-step-1',
            stepNumber: 1,
            sectionName: null,
            instruction: 'Roast tomatoes on comal.',
            imagePath: null,
            ingredientIds: ['sub-ing-1'],
            timers: [],
          },
        ],
      },
    ],
    includeCookLogs: false,
    expiresAt: null,
  };

  beforeEach(async () => {
    shareLinkServiceSpy = {
      getPublicSharedRecipe: vi.fn().mockReturnValue(of(mockPublicRecipe)),
    };

    await TestBed.configureTestingModule({
      imports: [PublicSharedRecipeComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap({ token: 'test-token-123456789012345678' })),
          },
        },
        { provide: ShareLinkService, useValue: shareLinkServiceSpy },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(PublicSharedRecipeComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads and renders the public shared recipe', () => {
    expect(shareLinkServiceSpy.getPublicSharedRecipe).toHaveBeenCalledWith(
      'test-token-123456789012345678',
    );
    expect(component['recipe']()?.title).toBe('Carnitas Tacos');
    expect(component['isUnavailable']()).toBe(false);
  });

  it('scales servings with multipliers and steppers', () => {
    component.setMultiplier(2);
    expect(component['targetServings']()).toBe(8);
    expect(component['scaleFactor']()).toBe(2);

    component.adjustServings(-1);
    expect(component['targetServings']()).toBe(7);
  });

  it('converts units between Original, UsCustomary, and Metric', () => {
    component['targetSystem'].set('Metric');
    const map = component['scaledIngredientsMap']();
    const item = map.get('ing-1');
    expect(item).toBeDefined();
    // 2 Lb in Metric converts to ~907g
    expect(item!.scaled.unitLabel).toBe('g');
  });

  it('renders sub-recipe preview and scales sub-recipe ingredient', () => {
    const subRecipe = component.getSubRecipe('sub-1');
    expect(subRecipe).toBeDefined();
    expect(subRecipe?.title).toBe('Salsa');

    const scaledLabel = component.scaleSubRecipeIngredient(subRecipe!.ingredients[0]);
    expect(scaledLabel).toContain('Roma Tomatoes');
  });

  it('sets isUnavailable true when API returns error', () => {
    shareLinkServiceSpy.getPublicSharedRecipe.mockReturnValue(
      throwError(() => new Error('Not found')),
    );
    component.loadSharedRecipe('bad-token');
    expect(component['isUnavailable']()).toBe(true);
  });

  it('toggles ingredient checked state', () => {
    component.toggleIngredientChecked('ing-1');
    expect(component['checkedIngredients']()['ing-1']).toBe(true);
    component.toggleIngredientChecked('ing-1');
    expect(component['checkedIngredients']()['ing-1']).toBe(false);
  });

  it('printRecipe calls globalThis.print', () => {
    const printSpy = vi.spyOn(globalThis, 'print').mockImplementation(() => undefined);
    component.printRecipe();
    expect(printSpy).toHaveBeenCalled();
  });

  it('renders cook notes when includeCookLogs is true and does not display user identity', () => {
    const recipeWithCookLogs: PublicSharedRecipeDto = {
      ...mockPublicRecipe,
      includeCookLogs: true,
      averageRating: 4.5,
      timesCooked: 3,
      cookLogs: [
        {
          cookedAt: '2026-11-01T12:00:00Z',
          rating: 5,
          notes: 'Crispier carnitas with higher broil temp!',
          photoImagePath: '/media/cook-logs/photo1.webp',
          photoThumbnailPath: '/media/cook-logs/thumb1.webp',
        },
      ],
    };

    shareLinkServiceSpy.getPublicSharedRecipe.mockReturnValue(of(recipeWithCookLogs));
    component.loadSharedRecipe('test-token');
    fixture.detectChanges();

    const notesSection = fixture.nativeElement.querySelector('.public-cook-notes-section');
    expect(notesSection).toBeTruthy();
    expect(notesSection.textContent).toContain('Crispier carnitas with higher broil temp!');
    // Privacy invariant: no user names, emails, or IDs
    expect(notesSection.textContent).not.toContain('chef');
    expect(notesSection.textContent).not.toContain('UserId');

    // Photo thumbnail is displayed
    const thumb = fixture.nativeElement.querySelector('.cook-photo-thumb');
    expect(thumb).toBeTruthy();
    expect(thumb.getAttribute('src')).toBe('/media/cook-logs/thumb1.webp');
  });

  it('hides cook notes section when includeCookLogs is false', () => {
    const recipeWithoutCookLogs: PublicSharedRecipeDto = {
      ...mockPublicRecipe,
      includeCookLogs: false,
      cookLogs: [
        {
          cookedAt: '2026-11-01T12:00:00Z',
          rating: 5,
          notes: 'Hidden note',
          photoImagePath: null,
          photoThumbnailPath: null,
        },
      ],
    };

    shareLinkServiceSpy.getPublicSharedRecipe.mockReturnValue(of(recipeWithoutCookLogs));
    component.loadSharedRecipe('test-token');
    fixture.detectChanges();

    const notesSection = fixture.nativeElement.querySelector('.public-cook-notes-section');
    expect(notesSection).toBeNull();
  });

  it('opens and closes photo lightbox', () => {
    component.openPhotoLightbox('/media/cook-logs/photo1.webp');
    fixture.detectChanges();

    expect(component['previewPhotoUrl']()).toBe('/media/cook-logs/photo1.webp');
    const lightbox = fixture.nativeElement.querySelector('.photo-lightbox-backdrop');
    expect(lightbox).toBeTruthy();

    component.closePhotoLightbox();
    fixture.detectChanges();
    expect(component['previewPhotoUrl']()).toBeNull();
    expect(fixture.nativeElement.querySelector('.photo-lightbox-backdrop')).toBeNull();
  });
});
