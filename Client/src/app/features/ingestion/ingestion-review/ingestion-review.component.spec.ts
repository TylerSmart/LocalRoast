import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import type {
  IngestionJobDetailDto,
  IngestionRepairPreviewDto,
  IngestionReviewDiagnosticsDto,
} from '../../../core/models/ingestion.models';
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

  it('preserves alternatives, explicit portions, and warnings in review and preview round trips', () => {
    component.ingredientsArray.at(0).patchValue({
      alternativeMeasurement: { quantity: 100, unit: 'G' },
      preferAlternativeMeasurement: true,
    });
    component.stepsArray.at(0).patchValue({ ingredientIndices: [0] });
    component.syncStepAmounts(0);
    component
      .getStepAmountsArray(0)
      .at(0)
      .controls.measurement.setValue({ quantity: 0.5, unit: 'Custom', customUnitName: 'block' });
    component.measurementWarnings.set(['Verify the source amount.']);
    const payload = component.buildDraftPayload();
    expect(payload.ingredients[0].alternativeMeasurement).toEqual({ quantity: 100, unit: 'G' });
    expect(payload.steps[0].ingredientAmounts).toEqual([
      {
        ingredientIndex: 0,
        measurement: { quantity: 0.5, unit: 'Custom', customUnitName: 'block' },
      },
    ]);
    expect(payload.measurementWarnings).toEqual(['Verify the source amount.']);
    component['refreshPreviewData']();
    expect(component.previewData()?.ingredients[0].alternativeMeasurement?.quantity).toBe(100);
    expect(component.previewData()?.steps[0].ingredientAmounts?.[0].measurement.quantity).toBe(0.5);
    component.removeIngredient(0);
    expect(component.stepsArray.at(0).get('ingredientIndices')?.value).toEqual([]);
    expect(component.getStepAmountsArray(0).length).toBe(0);
  });

  let mockIngestionService: {
    getJobById: ReturnType<typeof vi.fn>;
    saveDraft: ReturnType<typeof vi.fn>;
    approveAndPublish: ReturnType<typeof vi.fn>;
    discardJob: ReturnType<typeof vi.fn>;
    retryJob: ReturnType<typeof vi.fn>;
    getReviewDiagnostics: ReturnType<typeof vi.fn>;
    previewRepair: ReturnType<typeof vi.fn>;
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
      getReviewDiagnostics: vi
        .fn()
        .mockReturnValue(of({ validationIssues: [], qualityWarnings: [] })),
      previewRepair: vi.fn(),
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

  function makePreview(): IngestionRepairPreviewDto {
    const draft = component.buildDraftPayload();
    return {
      draft: {
        ...draft,
        title: 'AI suggestion',
        suggestedCollectionIds: [],
        suggestedTagIds: [],
        proposedTagNames: [],
        ingredients: draft.ingredients.map((item, index) =>
          index === 0 ? { ...item, createNewFoodItem: false } : item,
        ),
      },
      before: { validationIssues: ['A problem'], qualityWarnings: [] },
      after: { validationIssues: [], qualityWarnings: [] },
      notices: [],
    };
  }

  it('passes the current edits and hint to AI and leaves a cancelled preview unsaved', () => {
    component.form.controls.title.setValue('My unsaved title');
    component.repairHint.setValue('  Fix the timer  ');
    const preview = makePreview();
    mockIngestionService.previewRepair.mockReturnValue(of(preview));
    mockDialog.open.mockReturnValue({ afterClosed: () => of(false) });

    component.fixWithAi();

    expect(mockIngestionService.previewRepair).toHaveBeenCalledWith(
      'job-123',
      expect.objectContaining({ title: 'My unsaved title' }),
      'Fix the timer',
    );
    expect(component.form.controls.title.value).toBe('My unsaved title');
    expect(mockIngestionService.saveDraft).not.toHaveBeenCalled();
  });

  it('applies a preview only to the editor and preserves cleared fields and food settings', () => {
    component.form.controls.sourceUrl.setValue('');
    component.form.controls.difficulty.setValue(null);
    component.proposedTagNames.set(['Previously unselected']);
    const preview = makePreview();
    mockIngestionService.previewRepair.mockReturnValue(of(preview));
    const dialogResult = new Subject<boolean>();
    mockDialog.open.mockReturnValue({ afterClosed: () => dialogResult });

    component.fixWithAi();
    expect(component.form.controls.title.value).toBe('Crispy Sesame Tofu');
    dialogResult.next(true);

    expect(component.form.controls.title.value).toBe('AI suggestion');
    expect(component.form.controls.sourceUrl.value).toBe('');
    expect(component.form.controls.difficulty.value).toBeNull();
    expect(component.selectedCollectionIds().size).toBe(0);
    expect(component.selectedTagIds().size).toBe(0);
    expect(component.proposedTagNames()).toContain('Previously unselected');
    expect(component.buildDraftPayload().ingredients[0].createNewFoodItem).toBe(false);
    expect(component.form.dirty).toBe(true);
    expect(mockIngestionService.saveDraft).not.toHaveBeenCalled();
  });

  it('does not overwrite edits made during inference or while the preview is open', () => {
    const response = new Subject<IngestionRepairPreviewDto>();
    const preview = makePreview();
    const dialogResult = new Subject<boolean>();
    mockIngestionService.previewRepair.mockReturnValue(response);
    mockDialog.open.mockReturnValue({ afterClosed: () => dialogResult });

    component.fixWithAi();
    component.form.controls.title.setValue('Edited while waiting');
    response.next(preview);
    component.form.controls.description.setValue('Edited while previewing');
    dialogResult.next(true);

    expect(component.form.controls.title.value).toBe('Edited while waiting');
    expect(component.form.controls.description.value).toBe('Edited while previewing');
    expect(component.reviewError()).toContain('Your edits were kept');
    expect(mockIngestionService.saveDraft).not.toHaveBeenCalled();
  });

  it('refuses to apply a preview if another session changed the saved job', () => {
    mockIngestionService.previewRepair.mockReturnValue(of(makePreview()));
    const dialogResult = new Subject<boolean>();
    mockDialog.open.mockReturnValue({ afterClosed: () => dialogResult });
    component.fixWithAi();
    mockIngestionService.getJobById.mockReturnValue(
      of({ ...sampleJob, modifiedAt: '2026-10-27T12:00:00Z' }),
    );
    dialogResult.next(true);
    expect(component.form.controls.title.value).toBe('Crispy Sesame Tofu');
    expect(component.reviewError()).toContain('job changed');
    expect(mockIngestionService.saveDraft).not.toHaveBeenCalled();
  });

  it('keeps unsaved edits accessible when AI repair fails', () => {
    component.form.controls.title.setValue('Still editing');
    mockIngestionService.previewRepair.mockReturnValue(
      throwError(() => ({ error: { detail: 'The model is offline.' } })),
    );
    component.fixWithAi();
    fixture.detectChanges();

    expect(component.reviewError()).toBe('The model is offline.');
    expect(component.errorMessage()).toBeNull();
    expect(component.form.controls.title.value).toBe('Still editing');
    expect(fixture.nativeElement.querySelector('.draft-form')).toBeTruthy();
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
});
