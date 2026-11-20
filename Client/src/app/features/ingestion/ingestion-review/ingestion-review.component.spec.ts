import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import type {
  IngestionJobDetailDto,
  IngestionReviewDiagnosticsDto,
} from '../../../core/models/ingestion.models';
import { RecipeAutocompleteItemDto, RecipeDetailDto } from '../../../core/models/recipe.models';
import { CollectionDto, TagDto } from '../../../core/models/taxonomy.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { signal } from '@angular/core';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { IngestionReviewComponent } from './ingestion-review.component';

describe('IngestionReviewComponent', () => {
  let component: IngestionReviewComponent;
  let fixture: ComponentFixture<IngestionReviewComponent>;
  let router: Router;

  it('preserves alternatives and warnings in review and preview round trips', () => {
    component.ingredientsArray.at(0).patchValue({
      alternativeMeasurement: { quantity: 100, unit: 'G' },
      preferAlternativeMeasurement: true,
    });
    component.measurementWarnings.set(['Verify the source amount.']);
    const payload = component.buildDraftPayload();
    expect(payload.ingredients[0].alternativeMeasurement).toEqual({ quantity: 100, unit: 'G' });
    expect(payload.measurementWarnings).toEqual(['Verify the source amount.']);
    component['refreshPreviewData']();
    expect(component.previewData()?.ingredients[0].alternativeMeasurement?.quantity).toBe(100);
  });

  let mockIngestionService: {
    getJobById: ReturnType<typeof vi.fn>;
    saveDraft: ReturnType<typeof vi.fn>;
    approveAndPublish: ReturnType<typeof vi.fn>;
    discardJob: ReturnType<typeof vi.fn>;
    retryJob: ReturnType<typeof vi.fn>;
    refineJobNow: ReturnType<typeof vi.fn>;
    getReviewDiagnostics: ReturnType<typeof vi.fn>;
  };
  let mockFoodCatalogService: {
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
          suggestedIsPantryStaple: true,
          suggestedCategory: 'CondimentsAndSauces',
        },
      ],
      steps: [
        {
          stepNumber: 1,
          sectionName: null,
          instruction: 'Simmer tofu in sauce for 10 minutes.',
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
    extractedModel: 'gpt-4o',
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

  const sampleFoodItems: FoodItemDto[] = [
    {
      id: 'food-1',
      name: 'Tofu',
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
      retryJob: vi.fn().mockReturnValue(of(sampleJob)),
      refineJobNow: vi.fn().mockReturnValue(of({ ...sampleJob, refinementRunNowRequested: true })),
      getReviewDiagnostics: vi
        .fn()
        .mockReturnValue(of({ validationIssues: [], qualityWarnings: [] })),
    };

    mockFoodCatalogService = {
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
        {
          provide: AiKitchenHelperService,
          useValue: {
            checkStatus: vi.fn().mockReturnValue(
              of({
                isAvailable: true,
                provider: 'Ollama',
                latencyMs: 10,
                refineModel: 'gemma4:26b-a4b',
              }),
            ),
            refineModel: signal<string | null>('gemma4:26b-a4b'),
            hasRefinementModel: signal<boolean>(true),
          },
        },
        { provide: FoodCatalogService, useValue: mockFoodCatalogService },
        {
          provide: RecipeService,
          useValue: {
            autocompleteRecipes: vi.fn().mockReturnValue(of([] as RecipeAutocompleteItemDto[])),
          },
        },
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
    expect(ing1.get('suggestedCategory')?.value).toBe('CondimentsAndSauces');

    ing1.get('suggestedCategory')?.setValue('Refrigerated');
    expect(component.buildDraftPayload().ingredients?.[1].suggestedCategory).toBe('Refrigerated');
  });

  it('checks the edited draft and shows validation issues and advisory warnings', () => {
    mockIngestionService.getReviewDiagnostics.mockReturnValue(
      of({
        validationIssues: ['Butter is not linked.'],
        qualityWarnings: ['Step 1 has a vague timer label.'],
      }),
    );
    component.form.controls.title.setValue('Unsaved title');
    component.recheckReviewIssues();
    fixture.detectChanges();

    expect(mockIngestionService.getReviewDiagnostics).toHaveBeenLastCalledWith(
      'job-123',
      expect.objectContaining({ title: 'Unsaved title' }),
    );
    expect(fixture.nativeElement.textContent).toContain('Butter is not linked.');
    expect(fixture.nativeElement.textContent).toContain('Step 1 has a vague timer label.');
    expect(component.diagnosticsStale()).toBe(false);
    component.form.controls.description.setValue('An edit after checking');
    expect(component.diagnosticsStale()).toBe(true);
  });

  it('marks diagnostics stale when the draft changes before a check completes', () => {
    const response = new Subject<IngestionReviewDiagnosticsDto>();
    mockIngestionService.getReviewDiagnostics.mockReturnValue(response);
    component.recheckReviewIssues();
    component.form.controls.title.setValue('Changed while checking');
    response.next({ validationIssues: [], qualityWarnings: [] });
    expect(component.diagnosticsStale()).toBe(true);
  });

  it('inserts, previews and builds payload with inline step tokens', () => {
    component.stepsArray.at(0).patchValue({
      instruction:
        'Simmer ||{"ingredient":"tofu","amount":200,"unit":"g"}|| for ||{"type":"timer","seconds":600,"label":"Simmer"}||.',
    });
    fixture.detectChanges();

    const chip = fixture.nativeElement.querySelector(
      'app-step-instruction-editor .inline-step-chip.is-ingredient',
    );
    expect(chip?.textContent).toContain('200 g tofu');
    expect(fixture.nativeElement.textContent).toContain('10m (Simmer)');

    const payload = component.buildDraftPayload();
    expect(payload.steps[0].instruction).toBe(
      'Simmer ||{"ingredient":"tofu","amount":200,"unit":"g"}|| for ||{"type":"timer","seconds":600,"label":"Simmer"}||.',
    );

    component['refreshPreviewData']();
    expect(component.previewData()?.steps[0].instruction).toContain('tofu');
  });

  it('toggles mobile panes cleanly', () => {
    expect(component.mobilePane()).toBe('draft');
    component.setMobilePane('source');
    expect(component.mobilePane()).toBe('source');
  });

  it('adds and removes ingredients', () => {
    expect(component['availableIngredientLabels']()).toHaveLength(2);
    component.addIngredient({ displayName: 'Scallions' });
    expect(component.ingredientsArray.length).toBe(3);
    expect(component['availableIngredientLabels']()[2].label).toBe('3. Scallions');

    component.removeIngredient(2);
    expect(component.ingredientsArray.length).toBe(2);
    expect(component['availableIngredientLabels']()).toHaveLength(2);
  });

  it('updates food-creation behavior when changing a catalog or recipe reference', () => {
    const ingredient = component.ingredientsArray.at(0);
    ingredient.get('matchedFoodItemId')?.setValue(null);
    expect(component.buildDraftPayload().ingredients[0].createNewFoodItem).toBe(true);

    ingredient.get('isRecipeReference')?.setValue(true);
    component.onRecipeReferenceToggled(0, true);
    expect(component.buildDraftPayload().ingredients[0].createNewFoodItem).toBe(false);
    ingredient.get('isRecipeReference')?.setValue(false);
    component.onRecipeReferenceToggled(0, false);
    expect(component.buildDraftPayload().ingredients[0].createNewFoodItem).toBe(true);
  });

  it('preserves recipe references in the draft without food-item materialization', () => {
    component.addIngredient({
      displayName: '1 recipe Foolproof All-Butter Dough (this page)',
      isRecipeReference: true,
      referencedRecipeTitle: 'Foolproof All-Butter Dough',
    });

    const ingredient = component.buildDraftPayload().ingredients?.[2];
    expect(ingredient).toMatchObject({
      isRecipeReference: true,
      referencedRecipeTitle: 'Foolproof All-Butter Dough',
      displayName: 'Foolproof All-Butter Dough',
      matchedFoodItemId: null,
      matchedFoodItemName: null,
      isNewFoodItem: false,
      proposedFoodItemName: null,
      suggestedIsPantryStaple: false,
    });
  });

  it('preserves matched recipe references and includes them in preview data', () => {
    const matchedRecipeId = '00000000-0000-0000-0000-000000000001';
    component.addIngredient({
      displayName: '1 recipe Foolproof All-Butter Dough',
      isRecipeReference: true,
      referencedRecipeTitle: 'Foolproof All-Butter Dough',
      matchedRecipeId,
      matchedRecipeTitle: 'Foolproof All-Butter Dough',
      matchedRecipeSlug: 'foolproof-all-butter-dough',
    });

    const ingredient = component.buildDraftPayload().ingredients?.[2];
    expect(ingredient).toMatchObject({
      isRecipeReference: true,
      referencedRecipeTitle: 'Foolproof All-Butter Dough',
      matchedRecipeId,
      matchedRecipeTitle: 'Foolproof All-Butter Dough',
      matchedRecipeSlug: 'foolproof-all-butter-dough',
    });

    component.refreshPreviewData();
    const preview = component.previewData();
    expect(preview).not.toBeNull();
    const previewIng = preview!.ingredients[2];
    expect(previewIng.linkedRecipeId).toBe(matchedRecipeId);
    expect(previewIng.linkedRecipeTitle).toBe('Foolproof All-Butter Dough');
    expect(previewIng.unresolvedRecipeTitle).toBeNull();
  });

  it('links and unlinks referenced recipes via autocomplete handlers', () => {
    component.addIngredient({
      displayName: '',
      isRecipeReference: true,
      referencedRecipeTitle: '',
    });

    const newIndex = component.ingredientsArray.length - 1;
    component.onReferencedRecipeSelected(newIndex, {
      id: 'matched-sub-1',
      title: 'Ultracreamy Hummus',
      slug: 'ultracreamy-hummus',
      status: 'Published',
      baseServings: 6,
      yieldDisplay: '2 cups',
    });

    const linkedPayload = component.buildDraftPayload().ingredients[newIndex];
    expect(linkedPayload).toMatchObject({
      isRecipeReference: true,
      referencedRecipeTitle: 'Ultracreamy Hummus',
      displayName: 'Ultracreamy Hummus',
      matchedRecipeId: 'matched-sub-1',
      matchedRecipeTitle: 'Ultracreamy Hummus',
      matchedRecipeSlug: 'ultracreamy-hummus',
    });

    component.onReferencedRecipeTextChanged(newIndex, 'Homemade Pita Chips');

    const editedPayload = component.buildDraftPayload().ingredients[newIndex];
    expect(editedPayload).toMatchObject({
      isRecipeReference: true,
      referencedRecipeTitle: 'Homemade Pita Chips',
      displayName: 'Homemade Pita Chips',
      matchedRecipeId: null,
      matchedRecipeTitle: null,
      matchedRecipeSlug: null,
    });
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
  it('reruns extraction and navigates to /ingest', () => {
    component.rerunExtraction();
    expect(mockIngestionService.retryJob).toHaveBeenCalledWith('job-123');
    expect(router.navigate).toHaveBeenCalledWith(['/ingest']);
  });

  it('displays extracted model badge in review header', () => {
    const badge = fixture.nativeElement.querySelector('.model-badge');
    expect(badge).toBeTruthy();
    expect(badge.textContent).toContain('gpt-4o');
  });

  it.each([
    ['Images', 'Photos'],
    ['Pdf', 'PDF'],
  ] as const)('labels %s source jobs accurately', (channel, label) => {
    mockIngestionService.getJobById.mockReturnValue(of({ ...sampleJob, channel }));
    component.ngOnInit();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.channel-badge').textContent).toContain(label);
  });

  it('displays and toggles proposed new tags even when tag catalog is empty', () => {
    mockTaxonomyService.getTags.mockReturnValue(of([]));
    const jobWithProposedTags: IngestionJobDetailDto = {
      ...sampleJob,
      extractedDraft: {
        ...sampleJob.extractedDraft!,
        suggestedTagIds: [],
        proposedTagNames: ['Italian', 'Weeknight'],
      },
    };
    mockIngestionService.getJobById.mockReturnValue(of(jobWithProposedTags));

    component.ngOnInit();
    fixture.detectChanges();

    const proposedButtons = fixture.nativeElement.querySelectorAll('.proposed-tag-toggle');
    expect(proposedButtons.length).toBe(2);
    expect(proposedButtons[0].textContent).toContain('+ Italian');

    component.toggleProposedTag('Weeknight');
    const payload = component.buildDraftPayload();
    expect(payload.proposedTagNames).toEqual(['Italian']);
  });

  it('toggles between edit mode and inline recipe preview mode', () => {
    expect(component.rightPaneMode()).toBe('edit');

    component.setRightPaneMode('preview');
    fixture.detectChanges();

    expect(component.rightPaneMode()).toBe('preview');
    expect(component.previewData()).not.toBeNull();
    expect(component.previewData()?.title).toBe('Crispy Sesame Tofu');

    const previewEl = fixture.nativeElement.querySelector('app-recipe-detail-preview');
    expect(previewEl).toBeTruthy();

    component.setRightPaneMode('edit');
    fixture.detectChanges();
    expect(component.rightPaneMode()).toBe('edit');
    expect(fixture.nativeElement.querySelector('.draft-form')).toBeTruthy();
  });

  it('toggles mobile pane between source, draft, and preview', () => {
    component.setMobilePane('preview');
    expect(component.mobilePane()).toBe('preview');
    expect(component.rightPaneMode()).toBe('preview');

    component.setMobilePane('source');
    expect(component.mobilePane()).toBe('source');

    component.setMobilePane('draft');
    expect(component.mobilePane()).toBe('draft');
    expect(component.rightPaneMode()).toBe('edit');
  });

  it('opens recipe preview dialog from header button and handles publish action', () => {
    mockDialog.open.mockReturnValue({
      afterClosed: () => of({ action: 'publish' }),
    });

    component.openPreviewDialog();
    expect(mockDialog.open).toHaveBeenCalled();
    expect(mockIngestionService.approveAndPublish).toHaveBeenCalled();
  });

  it('maps targeted diagnostics to ingredient and step cards and generates jump targets', () => {
    component.diagnostics.set({
      validationIssues: ['Draft title is missing'],
      qualityWarnings: ['Step 1 mentions unlinked ingredient tofu'],
      targetedDiagnostics: [
        {
          ingredientIndex: 0,
          stepNumber: null,
          severity: 'warning',
          message: 'Ingredient 1 missing food item match',
        },
        {
          ingredientIndex: null,
          stepNumber: 1,
          severity: 'error',
          message: 'Step 1 mentions unlinked ingredient tofu',
        },
        {
          ingredientIndex: null,
          stepNumber: null,
          severity: 'error',
          message: 'Draft title is missing',
        },
      ],
    });
    fixture.detectChanges();

    expect(component.ingredientIssuesMap().get(0)?.length).toBe(1);
    expect(component.ingredientIssuesMap().get(0)?.[0].message).toBe(
      'Ingredient 1 missing food item match',
    );

    expect(component.stepIssuesMap().get(1)?.length).toBe(1);
    expect(component.stepIssuesMap().get(1)?.[0].message).toBe(
      'Step 1 mentions unlinked ingredient tofu',
    );

    expect(component.generalDiagnostics().length).toBe(1);
    expect(component.generalDiagnostics()[0].message).toBe('Draft title is missing');

    const jumpTargets = component.diagnosticJumpTargets();
    expect(jumpTargets.length).toBe(2);
    expect(jumpTargets[0].label).toContain('firm tofu');
    expect(jumpTargets[0].severity).toBe('warning');
    expect(jumpTargets[1].label).toBe('Step 1');
    expect(jumpTargets[1].severity).toBe('error');

    const banners = fixture.nativeElement.querySelectorAll('.inline-diagnostic-banner');
    expect(banners.length).toBeGreaterThanOrEqual(2);

    const targetButtons: HTMLButtonElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.diagnostic-target'),
    );
    expect(targetButtons.length).toBe(2);
    expect(targetButtons[0].classList).toContain('severity-warning');
    expect(targetButtons[0].textContent).toContain('Ingredient 1 missing food item match');
    expect(targetButtons[1].classList).toContain('severity-error');
    expect(targetButtons[1].textContent).toContain('Step 1 mentions unlinked ingredient tofu');

    vi.useFakeTimers();
    try {
      const stepCard: HTMLElement = fixture.nativeElement.querySelector('#step-card-1');
      const ingredientCard: HTMLElement = fixture.nativeElement.querySelector('#ingredient-card-0');
      stepCard.scrollIntoView = vi.fn();
      ingredientCard.scrollIntoView = vi.fn();
      document.body.appendChild(fixture.nativeElement);

      targetButtons[1].click();
      expect(stepCard.scrollIntoView).toHaveBeenCalled();
      expect(stepCard.classList).toContain('highlight-error');

      targetButtons[0].click();
      expect(stepCard.classList).not.toContain('highlight-error');
      expect(ingredientCard.classList).toContain('highlight-warning');

      vi.advanceTimersByTime(2500);
      expect(ingredientCard.classList).not.toContain('highlight-warning');
    } finally {
      fixture.nativeElement.remove();
      vi.useRealTimers();
    }
  });

  it('allows published jobs to be edited and approved, displaying Update Published Recipe', () => {
    const publishedJob: IngestionJobDetailDto = {
      ...sampleJob,
      status: 'Published',
      resultingRecipeId: 'rec-123',
      resultingRecipeSlug: 'crispy-sesame-tofu',
    };
    mockIngestionService.getJobById.mockReturnValue(of(publishedJob));
    const publishedFixture = TestBed.createComponent(IngestionReviewComponent);
    const pubComp = publishedFixture.componentInstance;
    publishedFixture.detectChanges();

    expect(pubComp.canEditReview()).toBe(true);

    const publishBtn = publishedFixture.nativeElement.querySelector('.publish-btn');
    expect(publishBtn).toBeTruthy();
    expect(publishBtn.textContent).toContain('Update Published Recipe');

    const menuBtn = publishedFixture.nativeElement.querySelector(
      '.review-actions-menu-btn',
    ) as HTMLButtonElement;
    expect(menuBtn).toBeTruthy();
    menuBtn.click();
    publishedFixture.detectChanges();

    const openRecipeLink = document.querySelector('.open-recipe-btn');
    expect(openRecipeLink).toBeTruthy();

    pubComp.approveAndPublish();
    expect(mockIngestionService.approveAndPublish).toHaveBeenCalledWith(
      'job-123',
      expect.any(Object),
    );
    expect(router.navigate).toHaveBeenCalledWith(['/recipes', 'rec-1']);
  });

  it('derives timers automatically from inline step tokens in buildDraftPayload', () => {
    component.ingredientsArray.at(0).patchValue({
      displayName: 'Sesame Oil',
      quantity: 2,
      unit: 'Tbsp',
    });
    component.stepsArray.at(0).patchValue({
      instruction:
        'Heat ||{"ingredient":"Sesame Oil","amount":1,"unit":"tbsp"}|| and fry for ||{"type":"timer","seconds":180,"label":"Sear"}||.',
    });

    const payload = component.buildDraftPayload();
    expect(payload.steps[0].timers).toEqual([{ durationSeconds: 180, label: 'Sear' }]);
  });

  it('round-trips ingredient replacements in draft payload and preview data', () => {
    component.addIngredientReplacement(0, {
      displayName: 'margarine',
      quantity: 2,
      maxQuantity: null,
      unit: 'Tbsp',
      customUnitName: null,
      preparationNote: 'melted',
    });
    expect(component.getReplacementsArray(0).length).toBe(1);

    const payload = component.buildDraftPayload();
    expect(payload.ingredients[0].replacements).toEqual([
      expect.objectContaining({
        displayName: 'margarine',
        quantity: 2,
        unit: 'Tbsp',
        preparationNote: 'melted',
      }),
    ]);

    component['refreshPreviewData']();
    expect(component.previewData()?.ingredients[0].replacements?.[0].displayName).toBe('margarine');

    component.removeIngredientReplacement(0, 0);
    expect(component.getReplacementsArray(0).length).toBe(0);
  });

  it('displays ingested and refined model badges and carries refinedModel through draft and preview', () => {
    mockIngestionService.getJobById.mockReturnValueOnce(
      of({
        ...sampleJob,
        refinedModel: 'gemma4:26b-a4b',
        refinedAt: '2026-10-26T12:15:00Z',
        extractedDraft: {
          ...sampleJob.extractedDraft!,
          refinedModel: 'gemma4:26b-a4b',
        },
      }),
    );

    component['loadJob']();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="review-extracted-model-badge"]')?.textContent).toContain(
      'Ingested: gpt-4o',
    );
    expect(el.querySelector('[data-testid="review-refined-model-badge"]')?.textContent).toContain(
      'Refined: gemma4:26b-a4b',
    );

    const payload = component.buildDraftPayload();
    expect(payload.refinedModel).toBe('gemma4:26b-a4b');

    component['refreshPreviewData']();
    expect(component.previewData()?.refinedModel).toBe('gemma4:26b-a4b');
  });

  it('queues immediate refinement from review header menu and navigates to /ingest', () => {
    const el = fixture.nativeElement as HTMLElement;
    const menuBtn = el.querySelector<HTMLButtonElement>('.review-actions-menu-btn');
    expect(menuBtn).toBeTruthy();
    menuBtn?.click();
    fixture.detectChanges();

    const refineBtn = document.querySelector<HTMLButtonElement>(
      '[data-testid="review-refine-now-btn"]',
    );
    expect(refineBtn).toBeTruthy();
    expect(refineBtn?.textContent).toContain('Refine Now');

    refineBtn?.click();
    fixture.detectChanges();

    expect(mockIngestionService.refineJobNow).toHaveBeenCalledWith('job-123');
    expect(router.navigate).toHaveBeenCalledWith(['/ingest']);
  });
});
