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
import { FoodItemDto, StoreAisleDto } from '../../../core/models/food-catalog.models';
import type {
  IngestionDraftIngredientDto,
  IngestionDraftStepDto,
  IngestionExtractedDraftDto,
  IngestionJobDetailDto,
} from '../../../core/models/ingestion.models';
import {
  MEASUREMENT_UNITS,
  RecipeDifficulty,
  RecipeStepTimerInput,
} from '../../../core/models/recipe.models';
import { CollectionDto, TagDto } from '../../../core/models/taxonomy.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';

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
  readonly errorMessage = signal<string | null>(null);
  readonly mobilePane = signal<'source' | 'draft'>('draft');

  readonly storeAisles = signal<StoreAisleDto[]>([]);
  readonly foodItems = signal<FoodItemDto[]>([]);
  readonly collections = signal<CollectionDto[]>([]);
  readonly tags = signal<TagDto[]>([]);
  readonly selectedCollectionIds = signal<Set<string>>(new Set());
  readonly selectedTagIds = signal<Set<string>>(new Set());

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
    ingredients: this.fb.array<FormGroup>([]),
    steps: this.fb.array<FormGroup>([]),
  });

  get ingredientsArray(): FormArray<FormGroup> {
    return this.form.get('ingredients') as FormArray<FormGroup>;
  }

  get stepsArray(): FormArray<FormGroup> {
    return this.form.get('steps') as FormArray<FormGroup>;
  }

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
      .getStoreAisles()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (aisles) => this.storeAisles.set(aisles) });

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
    });

    if (draft.suggestedCollectionIds && draft.suggestedCollectionIds.length > 0) {
      this.selectedCollectionIds.set(new Set(draft.suggestedCollectionIds));
    }
    if (draft.suggestedTagIds && draft.suggestedTagIds.length > 0) {
      this.selectedTagIds.set(new Set(draft.suggestedTagIds));
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
  }

  addIngredient(item?: Partial<IngestionDraftIngredientDto>): void {
    const fg = this.fb.group({
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
      proposedFoodItemName: [
        item?.proposedFoodItemName ?? item?.displayName ?? '',
        [Validators.required],
      ],
      suggestedStoreAisleId: [item?.suggestedStoreAisleId ?? null],
      suggestedIsPantryStaple: [item?.suggestedIsPantryStaple ?? false],
    });

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
      timers: timersArray,
      imagePath: [step?.imagePath ?? null],
    });

    this.stepsArray.push(fg);
  }

  removeStep(index: number): void {
    this.stepsArray.removeAt(index);
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

  setMobilePane(pane: 'source' | 'draft'): void {
    this.mobilePane.set(pane);
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
          displayName: val.displayName.trim(),
          preparationNote: val.preparationNote?.trim() || null,
          isOptional: Boolean(val.isOptional),
          matchedFoodItemId: val.matchedFoodItemId || null,
          matchedFoodItemName: val.matchedFoodItemName || null,
          isNewFoodItem: !val.matchedFoodItemId,
          proposedFoodItemName: val.proposedFoodItemName?.trim() || val.displayName.trim(),
          suggestedStoreAisleId: val.suggestedStoreAisleId || null,
          suggestedIsPantryStaple: Boolean(val.suggestedIsPantryStaple),
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
      suggestedCollectionIds: Array.from(this.selectedCollectionIds()),
      suggestedTagIds: Array.from(this.selectedTagIds()),
      ingredients,
      steps,
      confidenceScore: this.job()?.confidenceScore ?? 0.8,
    };
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

  approveAndPublish(): void {
    const currentJob = this.job();
    if (!currentJob || this.form.invalid || this.isPublishing()) {
      this.form.markAllAsTouched();
      return;
    }

    this.isPublishing.set(true);
    this.errorMessage.set(null);

    const draft = this.buildDraftPayload();
    this.ingestionService
      .approveAndPublish(currentJob.id, draft)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (createdRecipe) => {
          this.isPublishing.set(false);
          this.snackBar.open(`Recipe "${createdRecipe.title}" published!`, 'Close', {
            duration: 3000,
          });
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
