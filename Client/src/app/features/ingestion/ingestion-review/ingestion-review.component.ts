import { SourceViewerComponent } from '../source-viewer/source-viewer.component';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormArray,
  FormBuilder,
  FormGroup,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  FOOD_CATEGORY_OPTIONS,
  FoodCategory,
  FoodItemDto,
} from '../../../core/models/food-catalog.models';
import type {
  IngestionDraftIngredientDto,
  IngestionDraftStepDto,
  IngestionExtractedDraftDto,
  IngestionJobDetailDto,
} from '../../../core/models/ingestion.models';
import {
  MEASUREMENT_UNITS,
  RecipeDifficulty,
  RecipeMeasurement,
  RecipePreviewData,
  RecipeStepTimerInput,
} from '../../../core/models/recipe.models';
import { CollectionDto, TagDto } from '../../../core/models/taxonomy.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { MeasurementEditorComponent } from '../../../shared/components/measurement-editor/measurement-editor.component';
import {
  buildStepAmounts,
  createStepAmounts,
  getStepAmounts,
  ingredientMeasurementValidator,
  remapStepIngredients,
  syncStepAmounts,
} from '../../../core/utils/recipe-measurement-forms';
import { RecipeDetailPreviewComponent } from '../recipe-detail-preview/recipe-detail-preview.component';
import {
  RecipeDetailPreviewDialogComponent,
  RecipeDetailPreviewDialogData,
  RecipeDetailPreviewDialogResult,
} from '../recipe-detail-preview-dialog/recipe-detail-preview-dialog.component';

@Component({
  selector: 'app-ingestion-review',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatCheckboxModule,
    MatChipsModule,
    MatDividerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTooltipModule,
    SourceViewerComponent,
    RecipeDetailPreviewComponent,
    MeasurementEditorComponent,
  ],
  templateUrl: './ingestion-review.component.html',
  styleUrl: './ingestion-review.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IngestionReviewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly ingestionService = inject(IngestionService);
  private readonly foodCatalogService = inject(FoodCatalogService);
  private readonly taxonomyService = inject(TaxonomyService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);

  readonly measurementUnits = MEASUREMENT_UNITS;

  readonly job = signal<IngestionJobDetailDto | null>(null);
  readonly isLoading = signal<boolean>(true);
  readonly isSaving = signal<boolean>(false);
  readonly isPublishing = signal<boolean>(false);
  readonly isRerunning = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);
  readonly mobilePane = signal<'source' | 'draft' | 'preview'>('draft');
  readonly rightPaneMode = signal<'edit' | 'preview'>('edit');
  readonly previewData = signal<RecipePreviewData | null>(null);
  readonly isPromotingCover = signal<boolean>(false);

  readonly foodItems = signal<FoodItemDto[]>([]);
  readonly collections = signal<CollectionDto[]>([]);
  readonly tags = signal<TagDto[]>([]);
  readonly proposedTagNames = signal<string[]>([]);
  readonly measurementWarnings = signal<string[]>([]);
  readonly selectedCollectionIds = signal<Set<string>>(new Set());
  readonly selectedTagIds = signal<Set<string>>(new Set());
  readonly selectedProposedTagNames = signal<Set<string>>(new Set());

  readonly form = this.fb.group({
    title: ['', [Validators.required, Validators.maxLength(200)]],
    description: ['', [Validators.maxLength(2000)]],
    baseServings: [4, [Validators.required, Validators.min(0.25), Validators.max(999)]],
    yieldDisplay: ['', [Validators.maxLength(100)]],
    prepTimeMinutes: [null as number | null, [Validators.min(0), Validators.max(10080)]],
    cookTimeMinutes: [null as number | null, [Validators.min(0), Validators.max(10080)]],
    totalTimeMinutes: [null as number | null, [Validators.min(0), Validators.max(10080)]],
    difficulty: ['Medium' as RecipeDifficulty | null],
    sourceName: ['', [Validators.maxLength(200)]],
    sourceAuthor: ['', [Validators.maxLength(200)]],
    sourceUrl: ['', [Validators.maxLength(2048)]],
    coverImagePath: [null as string | null],
    thumbnailImagePath: [null as string | null],
    sourceBookTitle: ['', [Validators.maxLength(200)]],
    sourceBookPage: [null as number | null, [Validators.min(1), Validators.max(10000)]],
    retainSourceAttachments: [true],
    ingredients: this.fb.array<FormGroup>([]),
    steps: this.fb.array<FormGroup>([]),
  });

  get ingredientsArray(): FormArray<FormGroup> {
    return this.form.get('ingredients') as FormArray<FormGroup>;
  }

  get stepsArray(): FormArray<FormGroup> {
    return this.form.get('steps') as FormArray<FormGroup>;
  }

  protected readonly foodCategoryOptions = FOOD_CATEGORY_OPTIONS;

  protected readonly availableIngredientLabels = computed(() => {
    return this.ingredientsArray.controls.map((ctrl, index) => {
      const name = ctrl.get('displayName')?.value || `Ingredient #${index + 1}`;
      return { index, label: `${index + 1}. ${name}` };
    });
  });

  ngOnInit(): void {
    this.loadCatalogData();
    this.loadJob();
  }

  private loadCatalogData(): void {
    this.foodCatalogService
      .getFoodItems()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (items) => this.foodItems.set(items) });

    this.taxonomyService
      .getCollections()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (colls) => this.collections.set(colls) });

    this.taxonomyService
      .getTags()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (tgs) => this.tags.set(tgs) });
  }

  private loadJob(): void {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.errorMessage.set('Missing job identifier.');
      this.isLoading.set(false);
      return;
    }

    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.ingestionService
      .getJobById(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (jobDetail) => {
          this.job.set(jobDetail);
          this.populateForm(jobDetail);
          this.isLoading.set(false);
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to load ingestion job.');
          this.isLoading.set(false);
        },
      });
  }

  private populateForm(jobDetail: IngestionJobDetailDto): void {
    const draft = jobDetail.extractedDraft;
    if (!draft) {
      this.form.patchValue({
        title: jobDetail.displayTitle,
        sourceUrl: jobDetail.sourceUrl ?? '',
        coverImagePath: jobDetail.downloadedCoverImagePath,
        thumbnailImagePath: jobDetail.downloadedThumbnailImagePath,
      });
      return;
    }

    this.form.patchValue({
      title: draft.title || jobDetail.displayTitle,
      description: draft.description ?? '',
      baseServings: draft.baseServings || 4,
      yieldDisplay: draft.yieldDisplay ?? '',
      prepTimeMinutes: draft.prepTimeMinutes,
      cookTimeMinutes: draft.cookTimeMinutes,
      totalTimeMinutes: draft.totalTimeMinutes,
      difficulty: draft.difficulty ?? 'Medium',
      sourceName: draft.sourceName ?? jobDetail.scrapedPage?.siteName ?? '',
      sourceAuthor: draft.sourceAuthor ?? jobDetail.scrapedPage?.author ?? '',
      sourceUrl: draft.sourceUrl ?? jobDetail.sourceUrl ?? '',
      coverImagePath: draft.coverImagePath ?? jobDetail.downloadedCoverImagePath,
      thumbnailImagePath: draft.thumbnailImagePath ?? jobDetail.downloadedThumbnailImagePath,
      sourceBookTitle: draft.sourceBookTitle ?? jobDetail.sourceBookTitle ?? '',
      sourceBookPage: draft.sourceBookPage ?? jobDetail.sourceBookPage ?? null,
      retainSourceAttachments: draft.retainSourceAttachments ?? true,
    });

    if (draft.suggestedCollectionIds && draft.suggestedCollectionIds.length > 0) {
      this.selectedCollectionIds.set(new Set(draft.suggestedCollectionIds));
    }
    if (draft.suggestedTagIds && draft.suggestedTagIds.length > 0) {
      this.selectedTagIds.set(new Set(draft.suggestedTagIds));
    } else {
      this.selectedTagIds.set(new Set());
    }
    this.measurementWarnings.set(draft.measurementWarnings ?? []);
    if (draft.proposedTagNames && draft.proposedTagNames.length > 0) {
      this.proposedTagNames.set([...draft.proposedTagNames]);
      this.selectedProposedTagNames.set(new Set(draft.proposedTagNames));
    } else {
      this.proposedTagNames.set([]);
      this.selectedProposedTagNames.set(new Set());
    }

    this.ingredientsArray.clear();
    if (draft.ingredients && draft.ingredients.length > 0) {
      for (const ing of draft.ingredients) {
        this.addIngredient(ing);
      }
    } else {
      this.addIngredient();
    }

    this.stepsArray.clear();
    if (draft.steps && draft.steps.length > 0) {
      for (const stp of draft.steps) {
        this.addStep(stp);
      }
    } else {
      this.addStep();
    }

    this.refreshPreviewData();
  }

  addIngredient(item?: Partial<IngestionDraftIngredientDto>): void {
    const fg = this.fb.group(
      {
        sortOrder: [item?.sortOrder ?? this.ingredientsArray.length],
        sectionName: [item?.sectionName ?? null],
        originalRawText: [item?.originalRawText ?? null],
        quantity: [item?.quantity ?? null],
        maxQuantity: [item?.maxQuantity ?? null],
        unit: [item?.unit ?? null],
        customUnitName: [item?.customUnitName ?? null],
        displayName: [item?.displayName ?? '', [Validators.required]],
        preparationNote: [item?.preparationNote ?? null],
        isOptional: [item?.isOptional ?? false],
        matchedFoodItemId: [item?.matchedFoodItemId ?? null],
        matchedFoodItemName: [item?.matchedFoodItemName ?? null],
        isNewFoodItem: [item?.isNewFoodItem ?? !item?.matchedFoodItemId],
        isRecipeReference: [item?.isRecipeReference ?? !!item?.referencedRecipeTitle],
        referencedRecipeTitle: [
          item?.referencedRecipeTitle ?? null,
          item?.isRecipeReference || item?.referencedRecipeTitle
            ? [Validators.required, Validators.maxLength(200)]
            : [Validators.maxLength(200)],
        ],
        proposedFoodItemName: [
          item?.proposedFoodItemName ?? item?.displayName ?? '',
          [Validators.required],
        ],
        suggestedIsPantryStaple: [item?.suggestedIsPantryStaple ?? false],
        suggestedCategory: this.fb.control<FoodCategory | null>(item?.suggestedCategory ?? null),
        alternativeMeasurement: this.fb.control<RecipeMeasurement | null>(
          item?.alternativeMeasurement ?? null,
        ),
        preferAlternativeMeasurement: [item?.preferAlternativeMeasurement ?? false],
      },
      { validators: ingredientMeasurementValidator },
    );

    // When matchedFoodItemId is changed, update isNewFoodItem accordingly
    fg.get('matchedFoodItemId')
      ?.valueChanges.pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((id) => {
        if (id) {
          fg.get('isNewFoodItem')?.setValue(false);
          const found = this.foodItems().find((f) => f.id === id);
          if (found) {
            fg.get('matchedFoodItemName')?.setValue(found.name);
          }
        } else {
          fg.get('isNewFoodItem')?.setValue(true);
          fg.get('matchedFoodItemName')?.setValue(null);
        }
      });

    this.ingredientsArray.push(fg);
  }

  removeIngredient(index: number): void {
    this.ingredientsArray.removeAt(index);
    remapStepIngredients(this.stepsArray, (i) => (i === index ? null : i > index ? i - 1 : i));
  }

  onRecipeReferenceToggled(index: number, isRecipeReference: boolean): void {
    const group = this.ingredientsArray.at(index);
    if (!group) return;

    const titleControl = group.get('referencedRecipeTitle');
    if (isRecipeReference) {
      group.patchValue(
        {
          referencedRecipeTitle: titleControl?.value || group.get('displayName')?.value || null,
          matchedFoodItemId: null,
          matchedFoodItemName: null,
          isNewFoodItem: false,
          suggestedIsPantryStaple: false,
        },
        { emitEvent: false },
      );
      titleControl?.setValidators([Validators.required, Validators.maxLength(200)]);
    } else {
      group.patchValue(
        {
          referencedRecipeTitle: null,
          isNewFoodItem: !group.get('matchedFoodItemId')?.value,
          proposedFoodItemName:
            group.get('proposedFoodItemName')?.value || group.get('displayName')?.value,
        },
        { emitEvent: false },
      );
      titleControl?.setValidators([Validators.maxLength(200)]);
    }
    titleControl?.updateValueAndValidity({ emitEvent: false });
  }

  addStep(step?: Partial<IngestionDraftStepDto>): void {
    const stepNumber = step?.stepNumber ?? this.stepsArray.length + 1;
    const timersArray = this.fb.array<FormGroup>([]);

    if (step?.timers && step.timers.length > 0) {
      for (const t of step.timers) {
        timersArray.push(this.createTimerGroup(t));
      }
    }

    const fg = this.fb.group({
      stepNumber: [stepNumber],
      sectionName: [step?.sectionName ?? null],
      instruction: [step?.instruction ?? '', [Validators.required]],
      ingredientIndices: [step?.ingredientIndices ?? []],
      ingredientAmounts: createStepAmounts(
        step?.ingredientIndices ?? [],
        step?.ingredientAmounts ?? [],
      ),
      timers: timersArray,
      imagePath: [step?.imagePath ?? null],
    });

    this.stepsArray.push(fg);
  }

  removeStep(index: number): void {
    this.stepsArray.removeAt(index);
  }

  getStepAmountsArray(stepIndex: number) {
    return getStepAmounts(this.stepsArray.at(stepIndex));
  }
  syncStepAmounts(stepIndex: number): void {
    syncStepAmounts(this.stepsArray.at(stepIndex));
  }

  getStepTimersArray(stepIndex: number): FormArray<FormGroup> {
    return this.stepsArray.at(stepIndex).get('timers') as FormArray<FormGroup>;
  }

  private createTimerGroup(timer?: RecipeStepTimerInput): FormGroup {
    const totalSec = timer?.durationSeconds ?? 0;
    const mins = Math.floor(totalSec / 60);
    const secs = totalSec % 60;

    return this.fb.group({
      durationSeconds: [totalSec],
      label: [timer?.label ?? 'Timer'],
      minutes: [mins],
      seconds: [secs],
    });
  }

  addStepTimer(stepIndex: number): void {
    this.getStepTimersArray(stepIndex).push(
      this.createTimerGroup({ durationSeconds: 300, label: 'Timer' }),
    );
  }

  removeStepTimer(stepIndex: number, timerIndex: number): void {
    this.getStepTimersArray(stepIndex).removeAt(timerIndex);
  }

  updateTimerDuration(timerGroup: FormGroup): void {
    const mins = Number(timerGroup.get('minutes')?.value) || 0;
    const secs = Number(timerGroup.get('seconds')?.value) || 0;
    timerGroup.get('durationSeconds')?.setValue(mins * 60 + secs);
  }

  toggleCollection(id: string): void {
    const next = new Set(this.selectedCollectionIds());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.selectedCollectionIds.set(next);
  }

  toggleTag(id: string): void {
    const next = new Set(this.selectedTagIds());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.selectedTagIds.set(next);
  }

  toggleProposedTag(name: string): void {
    const next = new Set(this.selectedProposedTagNames());
    if (next.has(name)) {
      next.delete(name);
    } else {
      next.add(name);
    }
    this.selectedProposedTagNames.set(next);
  }

  setRightPaneMode(mode: 'edit' | 'preview'): void {
    if (mode === 'preview') {
      this.refreshPreviewData();
    }
    this.rightPaneMode.set(mode);
  }

  setMobilePane(pane: 'source' | 'draft' | 'preview'): void {
    this.mobilePane.set(pane);
    if (pane === 'preview') {
      this.refreshPreviewData();
      this.rightPaneMode.set('preview');
    } else if (pane === 'draft') {
      this.rightPaneMode.set('edit');
    }
  }

  refreshPreviewData(): void {
    this.previewData.set(this.buildPreviewData());
  }

  buildPreviewData(): RecipePreviewData {
    const draft = this.buildDraftPayload();
    const allCollections = this.collections();
    const allTags = this.tags();

    const selectedCollections = (draft.suggestedCollectionIds || [])
      .map((id) => allCollections.find((c) => c.id === id))
      .filter((c): c is CollectionDto => !!c)
      .map((c) => ({ id: c.id, name: c.name }));

    const selectedTags = [
      ...(draft.suggestedTagIds || [])
        .map((id) => allTags.find((t) => t.id === id))
        .filter((t): t is TagDto => !!t)
        .map((t) => ({ id: t.id, name: t.name, category: t.category })),
      ...(draft.proposedTagNames || []).map((name) => ({ name, category: 'General' })),
    ];

    return {
      title: draft.title,
      description: draft.description,
      baseServings: draft.baseServings,
      yieldDisplay: draft.yieldDisplay,
      prepTimeMinutes: draft.prepTimeMinutes,
      cookTimeMinutes: draft.cookTimeMinutes,
      totalTimeMinutes: draft.totalTimeMinutes,
      difficulty: draft.difficulty,
      sourceName: draft.sourceName,
      sourceAuthor: draft.sourceAuthor,
      sourceUrl: draft.sourceUrl,
      sourceBookTitle: draft.sourceBookTitle,
      sourceBookPage: draft.sourceBookPage,
      coverImagePath: draft.coverImagePath,
      extractedModel: draft.extractedModel,
      collections: selectedCollections,
      tags: selectedTags,
      ingredients: (draft.ingredients || []).map((ing, i) => ({
        id: `ing-${i}`,
        index: i,
        sectionName: ing.sectionName,
        quantity: ing.quantity,
        maxQuantity: ing.maxQuantity,
        unit: ing.unit,
        customUnitName: ing.customUnitName,
        displayName: ing.isRecipeReference
          ? ing.referencedRecipeTitle || ing.displayName
          : ing.displayName,
        preparationNote: ing.preparationNote,
        isOptional: ing.isOptional,
        isPantryStaple: ing.suggestedIsPantryStaple,
        matchedFoodItemName: ing.matchedFoodItemName || ing.proposedFoodItemName,
        alternativeMeasurement: ing.alternativeMeasurement,
        preferAlternativeMeasurement: ing.preferAlternativeMeasurement,
        unresolvedRecipeTitle: ing.isRecipeReference ? ing.referencedRecipeTitle : null,
      })),
      steps: (draft.steps || []).map((st, i) => ({
        id: `step-${i}`,
        stepNumber: st.stepNumber,
        sectionName: st.sectionName,
        instruction: st.instruction,
        imagePath: st.imagePath,
        ingredientIndices: st.ingredientIndices,
        ingredientAmounts: st.ingredientAmounts,
        timers: st.timers,
      })),
    };
  }

  openPreviewDialog(): void {
    this.refreshPreviewData();
    const preview = this.previewData();
    if (!preview) return;

    const ref = this.dialog.open<
      RecipeDetailPreviewDialogComponent,
      RecipeDetailPreviewDialogData,
      RecipeDetailPreviewDialogResult
    >(RecipeDetailPreviewDialogComponent, {
      width: '920px',
      maxWidth: '96vw',
      maxHeight: '92vh',
      data: {
        recipe: preview,
        canPublish: !this.form.invalid,
        isPublishing: this.isPublishing(),
      },
    });

    ref.afterClosed().subscribe((res) => {
      if (res?.action === 'publish') {
        this.approveAndPublish();
      }
    });
  }

  buildDraftPayload(): IngestionExtractedDraftDto {
    const raw = this.form.getRawValue();

    const ingredients: IngestionDraftIngredientDto[] = this.ingredientsArray.controls.map(
      (ctrl, i) => {
        const val = ctrl.getRawValue();
        return {
          sortOrder: i,
          sectionName: val.sectionName?.trim() || null,
          originalRawText: val.originalRawText || null,
          quantity: val.quantity !== null && val.quantity !== '' ? Number(val.quantity) : null,
          maxQuantity:
            val.maxQuantity !== null && val.maxQuantity !== '' ? Number(val.maxQuantity) : null,
          unit: val.unit || null,
          customUnitName: val.customUnitName?.trim() || null,
          displayName: val.isRecipeReference
            ? val.referencedRecipeTitle?.trim() || val.displayName.trim()
            : val.displayName.trim(),
          preparationNote: val.preparationNote?.trim() || null,
          isOptional: Boolean(val.isOptional),
          alternativeMeasurement: val.alternativeMeasurement ?? null,
          preferAlternativeMeasurement:
            !!val.alternativeMeasurement && Boolean(val.preferAlternativeMeasurement),
          suggestedCategory: val.isRecipeReference ? null : val.suggestedCategory || null,
          isRecipeReference: Boolean(val.isRecipeReference),
          referencedRecipeTitle: val.isRecipeReference
            ? val.referencedRecipeTitle?.trim() || null
            : null,
          matchedFoodItemId: val.isRecipeReference ? null : val.matchedFoodItemId || null,
          matchedFoodItemName: val.isRecipeReference ? null : val.matchedFoodItemName || null,
          isNewFoodItem: !val.isRecipeReference && !val.matchedFoodItemId,
          proposedFoodItemName: val.isRecipeReference
            ? null
            : val.proposedFoodItemName?.trim() || val.displayName.trim(),
          suggestedIsPantryStaple: !val.isRecipeReference && Boolean(val.suggestedIsPantryStaple),
        };
      },
    );

    const steps: IngestionDraftStepDto[] = this.stepsArray.controls.map((ctrl, i) => {
      const val = ctrl.getRawValue();
      const timersArray = ctrl.get('timers') as FormArray<FormGroup>;
      const timers: RecipeStepTimerInput[] = timersArray.controls.map((tCtrl) => {
        const tVal = tCtrl.getRawValue();
        const mins = Number(tVal.minutes) || 0;
        const secs = Number(tVal.seconds) || 0;
        return {
          durationSeconds: mins * 60 + secs,
          label: tVal.label?.trim() || `Step ${i + 1} timer`,
        };
      });

      return {
        stepNumber: i + 1,
        sectionName: val.sectionName?.trim() || null,
        instruction: val.instruction.trim(),
        ingredientIndices: val.ingredientIndices || [],
        ingredientAmounts: buildStepAmounts(ctrl),
        timers,
        imagePath: val.imagePath || null,
      };
    });

    return {
      title: raw.title?.trim() || 'Untitled Recipe',
      description: raw.description?.trim() || null,
      baseServings: Number(raw.baseServings) || 4,
      yieldDisplay: raw.yieldDisplay?.trim() || null,
      prepTimeMinutes:
        raw.prepTimeMinutes !== null && raw.prepTimeMinutes !== undefined
          ? Number(raw.prepTimeMinutes)
          : null,
      cookTimeMinutes:
        raw.cookTimeMinutes !== null && raw.cookTimeMinutes !== undefined
          ? Number(raw.cookTimeMinutes)
          : null,
      totalTimeMinutes:
        raw.totalTimeMinutes !== null && raw.totalTimeMinutes !== undefined
          ? Number(raw.totalTimeMinutes)
          : null,
      difficulty: raw.difficulty || null,
      sourceUrl: raw.sourceUrl?.trim() || null,
      sourceName: raw.sourceName?.trim() || null,
      sourceAuthor: raw.sourceAuthor?.trim() || null,
      coverImagePath: raw.coverImagePath || null,
      thumbnailImagePath: raw.thumbnailImagePath || null,
      sourceBookTitle: raw.sourceBookTitle?.trim() || null,
      sourceBookPage:
        raw.sourceBookPage !== null && raw.sourceBookPage !== undefined
          ? Number(raw.sourceBookPage)
          : null,
      retainSourceAttachments: Boolean(raw.retainSourceAttachments),
      suggestedCollectionIds: Array.from(this.selectedCollectionIds()),
      suggestedTagIds: Array.from(this.selectedTagIds()),
      proposedTagNames: Array.from(this.selectedProposedTagNames()),
      measurementWarnings: this.measurementWarnings(),
      ingredients,
      steps,
      confidenceScore: this.job()?.confidenceScore ?? 0.8,
      extractedModel: this.job()?.extractedModel ?? null,
    };
  }

  onPromotePageToCover(pageNumber: number): void {
    const currentJob = this.job();
    if (!currentJob || this.isPromotingCover()) return;

    this.isPromotingCover.set(true);
    this.ingestionService
      .promotePageToCover(currentJob.id, pageNumber)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (updatedJob) => {
          this.job.set(updatedJob);
          if (updatedJob.downloadedCoverImagePath) {
            this.form.patchValue({
              coverImagePath: updatedJob.downloadedCoverImagePath,
              thumbnailImagePath: updatedJob.downloadedThumbnailImagePath,
            });
          }
          this.isPromotingCover.set(false);
          this.snackBar.open(`Page ${pageNumber} promoted to recipe cover!`, 'Close', {
            duration: 3000,
          });
        },
        error: (err) => {
          this.isPromotingCover.set(false);
          this.snackBar.open(err?.error?.detail || 'Failed to promote page to cover.', 'Close', {
            duration: 4000,
          });
        },
      });
  }

  saveDraft(): void {
    const currentJob = this.job();
    if (!currentJob || this.isSaving()) {
      return;
    }

    this.isSaving.set(true);
    this.errorMessage.set(null);

    const draft = this.buildDraftPayload();
    this.ingestionService
      .saveDraft(currentJob.id, draft)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (updatedDetail) => {
          this.job.set(updatedDetail);
          this.isSaving.set(false);
          this.snackBar.open('Review draft saved.', 'Close', { duration: 3000 });
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to save review draft.');
          this.isSaving.set(false);
        },
      });
  }

  rerunExtraction(): void {
    const currentJob = this.job();
    if (!currentJob || this.isRerunning()) return;

    this.isRerunning.set(true);
    this.ingestionService
      .retryJob(currentJob.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isRerunning.set(false);
          this.snackBar.open(
            'Re-queued extraction for "' + currentJob.displayTitle + '" with current LLM model.',
            'Close',
            { duration: 4000 },
          );
          this.router.navigate(['/ingest']);
        },
        error: (err) => {
          this.isRerunning.set(false);
          this.snackBar.open(err?.error?.detail || 'Failed to rerun extraction.', 'Close', {
            duration: 5000,
          });
        },
      });
  }

  approveAndPublish(): void {
    const currentJob = this.job();
    if (!currentJob) return;

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.rightPaneMode.set('edit');
      this.mobilePane.set('draft');
      this.snackBar.open(
        'Please fix the validation errors in the draft before publishing.',
        'Close',
        { duration: 4000 },
      );
      return;
    }

    if (this.isPublishing()) return;

    this.isPublishing.set(true);
    this.errorMessage.set(null);

    const draft = this.buildDraftPayload();
    this.ingestionService
      .approveAndPublish(currentJob.id, draft)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (createdRecipe) => {
          this.isPublishing.set(false);
          this.snackBar.open(
            currentJob.resultingRecipeId
              ? `Recipe "${createdRecipe.title}" updated!`
              : `Recipe "${createdRecipe.title}" published!`,
            'Close',
            {
              duration: 3000,
            },
          );
          this.router.navigate(['/recipes', createdRecipe.id]);
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to approve and publish recipe.');
          this.isPublishing.set(false);
        },
      });
  }

  discardJob(): void {
    const currentJob = this.job();
    if (!currentJob) {
      return;
    }

    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Discard Ingestion Job?',
        message: `Are you sure you want to discard "${currentJob.displayTitle}"? Downloaded images and extracted draft data will be permanently removed.`,
        confirmText: 'Discard Job',
        cancelText: 'Keep Job',
        isDestructive: true,
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (confirmed) {
        this.ingestionService
          .discardJob(currentJob.id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: () => {
              this.snackBar.open('Job discarded.', 'Close', { duration: 2500 });
              this.router.navigate(['/ingest']);
            },
            error: (err) => {
              this.errorMessage.set(err?.error?.detail || 'Failed to discard job.');
            },
          });
      }
    });
  }

  getFoodItemName(id: string | null): string {
    if (!id) return '';
    const item = this.foodItems().find((f) => f.id === id);
    return item ? item.name : '';
  }

  asFormGroup(control: AbstractControl): FormGroup {
    return control as FormGroup;
  }
}
