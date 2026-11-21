import { CdkDragDrop, DragDropModule } from '@angular/cdk/drag-drop';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormArray, FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Title } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import {
  MeasurementUnit,
  RecipeAutocompleteItemDto,
  RecipeDetailDto,
  RecipeDifficulty,
  RecipeIngredientInput,
  RecipeIngredientReplacementDto,
  RecipeIngredientReplacementInput,
  RecipeMeasurement,
  RecipeStatus,
  RecipeStepInput,
  UpsertRecipeRequest,
} from '../../../core/models/recipe.models';
import { StepDialogIngredientOption } from '../../../core/models/step-token.models';
import {
  AllTagCategories,
  CollectionDto,
  TagCategory,
  TagCategoryLabels,
  TagDto,
} from '../../../core/models/taxonomy.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { MediaService } from '../../../core/services/media/media.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { parseIngredientLines } from '../../../core/utils/ingredient-line-parser';
import { stripOuterParentheses } from '../../../core/utils/culinary-math';
import { ingredientMeasurementValidator } from '../../../core/utils/recipe-measurement-forms';
import {
  StepTokenOriginFlags,
  deriveStepMetadataFromInstruction,
  resolveStepMetadataForPayload,
} from '../../../core/utils/step-token-parser';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { MeasurementEditorComponent } from '../../../shared/components/measurement-editor/measurement-editor.component';
import { RecipeAutocompleteComponent } from '../../../shared/components/recipe-autocomplete/recipe-autocomplete.component';
import { StepInstructionEditorComponent } from '../../../shared/components/step-instruction-editor/step-instruction-editor.component';
import { FoodItemDialogComponent } from '../../food-catalog/food-item-dialog/food-item-dialog.component';
import {
  CollectionDialogComponent,
  CollectionDialogData,
} from '../collection-dialog/collection-dialog.component';
import { TagDialogComponent, TagDialogData } from '../tag-dialog/tag-dialog.component';

interface IngredientReplacementFormValue {
  displayName: string;
  quantity?: number | string | null;
  maxQuantity?: number | string | null;
  unit?: MeasurementUnit | null;
  customUnitName?: string | null;
  foodItemId?: string | null;
  preparationNote?: string | null;
}

interface IngredientFormValue {
  id?: string | null;
  isSubRecipe?: boolean;
  sectionName?: string | null;
  quantity?: number | string | null;
  maxQuantity?: number | string | null;
  unit?: MeasurementUnit | null;
  customUnitName?: string | null;
  foodItemId?: string | null;
  linkedRecipeId?: string | null;
  linkedRecipeTitle?: string | null;
  unresolvedRecipeTitle?: string | null;
  displayName: string;
  preparationNote?: string | null;
  isOptional?: boolean;
  originalRawText?: string | null;
  alternativeMeasurement?: RecipeMeasurement | null;
  preferAlternativeMeasurement?: boolean;
  replacements?: IngredientReplacementFormValue[];
}

interface TimerFormValue {
  minutes?: number | string | null;
  seconds?: number | string | null;
  label: string;
}

interface StepFormValue {
  id?: string | null;
  sectionName?: string | null;
  instruction: string;
  imagePath?: string | null;
  timers?: TimerFormValue[];
}

export interface ValidationIssue {
  message: string;
  targetId: string;
}

export const MEASUREMENT_UNITS: { value: MeasurementUnit; label: string; shortLabel: string }[] = [
  { value: 'Tsp', label: 'Teaspoon (tsp)', shortLabel: 'tsp' },
  { value: 'Tbsp', label: 'Tablespoon (tbsp)', shortLabel: 'tbsp' },
  { value: 'FlOz', label: 'Fluid Ounce (fl oz)', shortLabel: 'fl oz' },
  { value: 'Cup', label: 'Cup (cup)', shortLabel: 'cup' },
  { value: 'Pt', label: 'Pint (pt)', shortLabel: 'pt' },
  { value: 'Qt', label: 'Quart (qt)', shortLabel: 'qt' },
  { value: 'Gal', label: 'Gallon (gal)', shortLabel: 'gal' },
  { value: 'Ml', label: 'Milliliter (ml)', shortLabel: 'ml' },
  { value: 'L', label: 'Liter (l)', shortLabel: 'L' },
  { value: 'Oz', label: 'Ounce (oz)', shortLabel: 'oz' },
  { value: 'Lb', label: 'Pound (lb)', shortLabel: 'lb' },
  { value: 'G', label: 'Gram (g)', shortLabel: 'g' },
  { value: 'Kg', label: 'Kilogram (kg)', shortLabel: 'kg' },
  { value: 'Pinch', label: 'Pinch', shortLabel: 'pinch' },
  { value: 'Dash', label: 'Dash', shortLabel: 'dash' },
  { value: 'Clove', label: 'Clove', shortLabel: 'clove' },
  { value: 'Can', label: 'Can', shortLabel: 'can' },
  { value: 'Bunch', label: 'Bunch', shortLabel: 'bunch' },
  { value: 'Slice', label: 'Slice', shortLabel: 'slice' },
  { value: 'Whole', label: 'Whole', shortLabel: 'whole' },
  { value: 'Custom', label: 'Custom Unit...', shortLabel: 'custom' },
];

const CREATE_FOOD_SENTINEL = '__create_food_item__';
const MAX_FOOD_SUGGESTIONS = 25;

@Component({
  imports: [
    ReactiveFormsModule,
    RouterLink,
    DragDropModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
    MatChipsModule,
    MatDividerModule,
    MatExpansionModule,
    MatAutocompleteModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    RecipeAutocompleteComponent,
    MeasurementEditorComponent,
    StepInstructionEditorComponent,
  ],
  selector: 'app-recipe-editor',
  templateUrl: './recipe-editor.component.html',
  styleUrl: './recipe-editor.component.scss',
  host: {
    '(document:keydown)': 'onDocumentKeyDown($event)',
  },
})
export class RecipeEditorComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly recipeService = inject(RecipeService);
  private readonly catalogService = inject(FoodCatalogService);
  private readonly mediaService = inject(MediaService);
  private readonly taxonomyService = inject(TaxonomyService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly title = inject(Title);

  protected readonly measurementUnits = MEASUREMENT_UNITS;
  protected readonly createFoodSentinel = CREATE_FOOD_SENTINEL;
  protected readonly isEdit = signal<boolean>(false);
  protected readonly recipeId = signal<string | null>(null);
  protected readonly isLoading = signal<boolean>(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly isSaving = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly hasAttemptedSave = signal<boolean>(false);
  protected readonly validationIssues = signal<ValidationIssue[]>([]);
  private readonly loadedStepTokenOrigins = new WeakMap<FormGroup, StepTokenOriginFlags>();

  protected readonly foodItems = signal<FoodItemDto[]>([]);

  // Media signals
  protected readonly coverImagePath = signal<string | null>(null);
  protected readonly thumbnailImagePath = signal<string | null>(null);
  protected readonly isUploadingCover = signal<boolean>(false);
  protected readonly isCoverDragOver = signal<boolean>(false);
  protected readonly stepUploadingMap = signal<Record<number, boolean>>({});

  // Taxonomy signals & UI state
  protected readonly collections = signal<CollectionDto[]>([]);
  protected readonly tags = signal<TagDto[]>([]);
  protected readonly selectedCollectionIds = signal<Set<string>>(new Set());
  protected readonly selectedTagIds = signal<Set<string>>(new Set());
  protected readonly tagSearchQuery = signal<string>('');
  protected readonly activeTagCategory = signal<TagCategory | 'All'>('All');
  protected readonly isManagingTaxonomy = signal<boolean>(false);
  protected readonly isTagListExpanded = signal<boolean>(false);

  // Progressive disclosure per-ingredient state (tracked by FormGroup identity so reordering stays 100% synced)
  private readonly expandedRangeGroups = signal<Set<FormGroup>>(new Set());
  private readonly expandedAltGroups = signal<Set<FormGroup>>(new Set());

  // Bulk ingredient paste UI state
  protected readonly showPasteIngredients = signal<boolean>(false);
  protected readonly pasteIngredientsText = signal<string>('');

  // Track external (non-form) dirty changes (cover photo, collections, tags)
  private readonly metaDirty = signal<boolean>(false);

  protected readonly tagCategories: TagCategory[] = AllTagCategories;

  protected categoryLabel(cat: TagCategory): string {
    return TagCategoryLabels[cat] ?? cat;
  }

  // Main Form
  protected readonly form = this.fb.group({
    title: ['', [Validators.required, Validators.maxLength(200)]],
    status: ['Published' as RecipeStatus, Validators.required],
    description: ['', Validators.maxLength(2000)],
    baseServings: [4, [Validators.required, Validators.min(0.25), Validators.max(999)]],
    yieldDisplay: ['', Validators.maxLength(100)],
    prepTimeMinutes: [null as number | null, [Validators.min(0), Validators.max(10080)]],
    cookTimeMinutes: [null as number | null, [Validators.min(0), Validators.max(10080)]],
    totalTimeMinutes: [null as number | null, [Validators.min(0), Validators.max(10080)]],
    difficulty: ['Medium' as RecipeDifficulty | null],
    sourceName: ['', Validators.maxLength(200)],
    sourceAuthor: ['', Validators.maxLength(200)],
    sourceUrl: ['', Validators.maxLength(2048)],
    sourceBookTitle: ['', Validators.maxLength(200)],
    sourceBookPage: [null as number | null, [Validators.min(1), Validators.max(10000)]],
    ingredients: this.fb.array<FormGroup>([]),
    steps: this.fb.array<FormGroup>([]),
  });

  private readonly formRevision = signal(0);

  constructor() {
    this.form.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.formRevision.update((revision) => revision + 1);
      if (this.hasAttemptedSave()) {
        const issues = this.collectValidationIssues();
        this.validationIssues.set(issues);
        if (issues.length === 0) {
          this.errorMessage.set(null);
        }
      }
    });
  }

  get ingredientsArray(): FormArray<FormGroup> {
    return this.form.get('ingredients') as FormArray<FormGroup>;
  }

  get stepsArray(): FormArray<FormGroup> {
    return this.form.get('steps') as FormArray<FormGroup>;
  }

  protected readonly hasUnsavedChanges = computed(() => {
    this.formRevision();
    return this.form.dirty || this.metaDirty();
  });

  protected readonly liveTitlePreview = computed(() => {
    this.formRevision();
    return (this.form.controls.title.value || '').trim();
  });

  protected readonly computedTotalTime = computed(() => {
    this.formRevision();
    const explicit = this.toPositiveNumber(this.form.controls.totalTimeMinutes.value);
    if (explicit !== null) return explicit;
    const prep = this.toPositiveNumber(this.form.controls.prepTimeMinutes.value) ?? 0;
    const cook = this.toPositiveNumber(this.form.controls.cookTimeMinutes.value) ?? 0;
    const sum = prep + cook;
    return sum > 0 ? sum : null;
  });

  protected readonly totalTimeHint = computed(() => {
    this.formRevision();
    const explicit = this.toPositiveNumber(this.form.controls.totalTimeMinutes.value);
    if (explicit !== null) return 'Custom total time';
    const prep = this.toPositiveNumber(this.form.controls.prepTimeMinutes.value) ?? 0;
    const cook = this.toPositiveNumber(this.form.controls.cookTimeMinutes.value) ?? 0;
    const sum = prep + cook;
    return sum > 0 ? `Auto: ${sum} min` : 'Auto-summed if blank';
  });

  protected readonly sourceSummary = computed(() => {
    this.formRevision();
    const parts = [
      this.form.controls.sourceName.value?.trim(),
      this.form.controls.sourceBookTitle.value?.trim(),
      this.form.controls.sourceAuthor.value?.trim(),
    ].filter((part): part is string => !!part);
    if (parts.length > 0) {
      return parts.slice(0, 2).join(' · ');
    }
    const url = this.form.controls.sourceUrl.value?.trim();
    if (url) {
      try {
        return new URL(url).hostname.replace(/^www\./, '');
      } catch {
        return url;
      }
    }
    return '';
  });

  protected readonly selectedCollections = computed(() => {
    const selected = this.selectedCollectionIds();
    return this.collections().filter((c) => selected.has(c.id));
  });

  protected readonly selectedTags = computed(() => {
    const selected = this.selectedTagIds();
    return this.tags().filter((t) => selected.has(t.id));
  });

  protected readonly tagsByCategory = computed(() => {
    const map: Record<TagCategory, TagDto[]> = {
      Course: [],
      Cuisine: [],
      Dietary: [],
      Equipment: [],
      Protein: [],
      Occasion: [],
      MealPrep: [],
      Custom: [],
    };
    for (const t of this.tags()) {
      if (map[t.category]) {
        map[t.category].push(t);
      }
    }
    return map;
  });

  protected readonly nonEmptyTagCategories = computed(() => {
    const byCat = this.tagsByCategory();
    return this.tagCategories.filter((cat) => byCat[cat].length > 0);
  });

  protected readonly filteredTagsByCategory = computed(() => {
    const query = this.tagSearchQuery().trim().toLowerCase();
    const activeCat = this.activeTagCategory();
    const byCat = this.tagsByCategory();
    const result: { category: TagCategory; label: string; tags: TagDto[] }[] = [];

    for (const cat of this.tagCategories) {
      if (activeCat !== 'All' && cat !== activeCat) continue;
      const list = byCat[cat];
      if (!list.length) continue;
      const filtered = query ? list.filter((t) => t.name.toLowerCase().includes(query)) : list;
      if (filtered.length > 0) {
        result.push({ category: cat, label: this.categoryLabel(cat), tags: filtered });
      }
    }
    return result;
  });

  protected readonly existingIngredientSections = computed(() => {
    this.formRevision();
    const seen = new Set<string>();
    const sections: string[] = [];
    for (const ctrl of this.ingredientsArray.controls) {
      const name = (ctrl.get('sectionName')?.value || '').trim();
      if (name && !seen.has(name.toLowerCase())) {
        seen.add(name.toLowerCase());
        sections.push(name);
      }
    }
    return sections;
  });

  protected readonly existingStepSections = computed(() => {
    this.formRevision();
    const seen = new Set<string>();
    const sections: string[] = [];
    for (const ctrl of this.stepsArray.controls) {
      const name = (ctrl.get('sectionName')?.value || '').trim();
      if (name && !seen.has(name.toLowerCase())) {
        seen.add(name.toLowerCase());
        sections.push(name);
      }
    }
    return sections;
  });

  protected readonly lastIngredientSection = computed(() => {
    this.formRevision();
    if (this.ingredientsArray.length === 0) return '';
    return (
      this.ingredientsArray.at(this.ingredientsArray.length - 1).get('sectionName')?.value || ''
    ).trim();
  });

  protected readonly lastStepSection = computed(() => {
    this.formRevision();
    if (this.stepsArray.length === 0) return '';
    return (this.stepsArray.at(this.stepsArray.length - 1).get('sectionName')?.value || '').trim();
  });

  protected readonly parsedPasteCount = computed(() => {
    const raw = this.pasteIngredientsText().trim();
    return raw ? parseIngredientLines(raw).length : 0;
  });

  protected readonly foodItemNameById = computed(() => {
    const map = new Map<string, string>();
    for (const item of this.foodItems()) {
      map.set(item.id, item.name);
    }
    return map;
  });

  ngOnInit(): void {
    this.loadCatalogItems();
    this.loadTaxonomy();

    this.route.paramMap.subscribe((params) => {
      const id = params.get('id');
      if (id) {
        this.isEdit.set(true);
        this.recipeId.set(id);
        this.loadRecipe(id);
      } else {
        this.isEdit.set(false);
        this.recipeId.set(null);
        this.loadError.set(null);
        if (this.ingredientsArray.length === 0) {
          this.addIngredient();
        }
        if (this.stepsArray.length === 0) {
          this.addStep();
        }
      }
    });
  }

  protected onDocumentKeyDown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
      if (this.dialog.openDialogs.length > 0) return;
      event.preventDefault();
      if (!this.isSaving() && !this.isLoading() && !this.loadError()) {
        this.onSave();
      }
    }
  }

  loadCatalogItems(): void {
    this.catalogService.getFoodItems().subscribe({
      next: (items) => this.foodItems.set(items),
    });
  }

  loadTaxonomy(): void {
    this.taxonomyService.getCollections().subscribe({
      next: (colls) => this.collections.set(colls),
    });
    this.taxonomyService.getTags().subscribe({
      next: (tgs) => this.tags.set(tgs),
    });
  }

  loadRecipe(id: string): void {
    this.isLoading.set(true);
    this.loadError.set(null);
    this.errorMessage.set(null);

    this.recipeService.getRecipeById(id).subscribe({
      next: (detail) => {
        this.title.setTitle(`LocalRoast | Edit ${detail.title}`);
        this.populateForm(detail);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        const msg =
          err?.status === 404
            ? 'This recipe could not be found. It may have been deleted.'
            : err?.error?.detail || err?.error?.title || 'Failed to load recipe.';
        this.loadError.set(msg);
      },
    });
  }

  protected retryLoad(): void {
    const id = this.recipeId();
    if (id) {
      this.loadRecipe(id);
    }
  }

  populateForm(detail: RecipeDetailDto): void {
    this.coverImagePath.set(detail.coverImagePath ?? null);
    this.thumbnailImagePath.set(detail.thumbnailImagePath ?? null);
    this.selectedCollectionIds.set(new Set(detail.collections.map((c) => c.id)));
    this.selectedTagIds.set(new Set(detail.tags.map((t) => t.id)));
    this.expandedRangeGroups.set(new Set());
    this.expandedAltGroups.set(new Set());
    this.hasAttemptedSave.set(false);
    this.validationIssues.set([]);
    this.errorMessage.set(null);

    this.form.patchValue({
      title: detail.title,
      status: detail.status,
      description: detail.description ?? '',
      baseServings: detail.baseServings,
      yieldDisplay: detail.yieldDisplay ?? '',
      prepTimeMinutes: detail.prepTimeMinutes,
      cookTimeMinutes: detail.cookTimeMinutes,
      totalTimeMinutes: detail.totalTimeMinutes,
      difficulty: detail.difficulty,
      sourceName: detail.sourceName ?? '',
      sourceAuthor: detail.sourceAuthor ?? '',
      sourceUrl: detail.sourceUrl ?? '',
      sourceBookTitle: detail.sourceBookTitle ?? '',
      sourceBookPage: detail.sourceBookPage,
    });

    // Populate Ingredients
    this.ingredientsArray.clear();
    const rangeGroups = new Set<FormGroup>();
    const altGroups = new Set<FormGroup>();

    detail.ingredients.forEach((ing) => {
      const isSubRecipe = !!ing.linkedRecipeId;
      const group = this.fb.group(
        {
          id: [ing.id],
          isSubRecipe: [isSubRecipe || !!ing.unresolvedRecipeTitle],
          sectionName: [ing.sectionName ?? '', Validators.maxLength(150)],
          quantity: [ing.quantity],
          maxQuantity: [ing.maxQuantity],
          unit: [ing.unit ?? null],
          customUnitName: [ing.customUnitName ?? '', Validators.maxLength(50)],
          foodItemId: [ing.foodItemId],
          linkedRecipeId: [ing.linkedRecipeId],
          linkedRecipeTitle: [ing.linkedRecipeTitle ?? null],
          unresolvedRecipeTitle: [ing.unresolvedRecipeTitle ?? null],
          displayName: [ing.displayName, [Validators.required, Validators.maxLength(200)]],
          preparationNote: [ing.preparationNote ?? '', Validators.maxLength(200)],
          isOptional: [ing.isOptional],
          originalRawText: [ing.originalRawText],
          alternativeMeasurement: this.fb.control<RecipeMeasurement | null>(
            ing.alternativeMeasurement ?? null,
          ),
          preferAlternativeMeasurement: [ing.preferAlternativeMeasurement ?? false],
          replacements: this.fb.array<FormGroup>(
            (ing.replacements ?? []).map((rep) => this.createReplacementGroup(rep)),
          ),
        },
        { validators: ingredientMeasurementValidator },
      );

      if (ing.maxQuantity !== null && ing.maxQuantity !== undefined) {
        rangeGroups.add(group);
      }
      if (ing.alternativeMeasurement) {
        altGroups.add(group);
      }

      this.ingredientsArray.push(group);
    });

    this.expandedRangeGroups.set(rangeGroups);
    this.expandedAltGroups.set(altGroups);

    // Populate Steps
    this.stepsArray.clear();
    detail.steps.forEach((step) => {
      const timersArray = this.fb.array<FormGroup>([]);
      const seenTimers = new Set<string>();
      step.timers.forEach((t) => {
        const key = `${t.durationSeconds}_${(t.label || '').trim().toLowerCase()}`;
        if (!seenTimers.has(key)) {
          seenTimers.add(key);
          timersArray.push(
            this.fb.group({
              minutes: [Math.floor(t.durationSeconds / 60)],
              seconds: [t.durationSeconds % 60],
              label: [t.label, [Validators.required, Validators.maxLength(150)]],
            }),
          );
        }
      });

      const stepGroup = this.fb.group({
        id: [step.id],
        sectionName: [step.sectionName ?? '', Validators.maxLength(150)],
        instruction: [step.instruction, [Validators.required, Validators.maxLength(4000)]],
        imagePath: [step.imagePath ?? null],
        timers: timersArray,
      });

      const originMeta = deriveStepMetadataFromInstruction(step.instruction);
      this.loadedStepTokenOrigins.set(stepGroup, {
        hasTimerTokens: originMeta.hasTimerTokens,
      });

      this.stepsArray.push(stepGroup);
    });

    this.form.markAsPristine();
    this.metaDirty.set(false);
    this.formRevision.update((r) => r + 1);
  }

  protected scrollToSection(elementId: string): void {
    const el = document.getElementById(elementId);
    if (!el) return;
    el.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
    const focusable = el.querySelector<HTMLElement>(
      'input:not([type=hidden]), textarea, select, [contenteditable="true"], button',
    );
    focusable?.focus?.({ preventScroll: true });
  }

  // --- Cover Photo Methods ---
  onCoverFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.uploadCoverFile(file);
  }

  protected onCoverDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isCoverDragOver.set(true);
  }

  protected onCoverDragLeave(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isCoverDragOver.set(false);
  }

  protected onCoverDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.isCoverDragOver.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file && file.type.startsWith('image/')) {
      this.uploadCoverFile(file);
    }
  }

  private uploadCoverFile(file: File): void {
    this.isUploadingCover.set(true);
    this.mediaService.uploadRecipeCover(file).subscribe({
      next: (dto) => {
        this.coverImagePath.set(dto.coverImagePath);
        this.thumbnailImagePath.set(dto.thumbnailImagePath);
        this.isUploadingCover.set(false);
        this.metaDirty.set(true);
        this.snackBar.open('Cover photo uploaded!', 'Close', { duration: 2500 });
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isUploadingCover.set(false);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to upload cover photo.',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  removeCoverPhoto(): void {
    this.coverImagePath.set(null);
    this.thumbnailImagePath.set(null);
    this.metaDirty.set(true);
  }

  // --- Step Photo Methods ---
  onStepFileSelected(stepIndex: number, event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    this.stepUploadingMap.update((m) => ({ ...m, [stepIndex]: true }));
    this.mediaService.uploadStepImage(file).subscribe({
      next: (dto) => {
        this.stepsArray.at(stepIndex).patchValue({ imagePath: dto.imagePath });
        this.stepUploadingMap.update((m) => ({ ...m, [stepIndex]: false }));
        this.metaDirty.set(true);
        this.snackBar.open('Step photo uploaded!', 'Close', { duration: 2500 });
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.stepUploadingMap.update((m) => ({ ...m, [stepIndex]: false }));
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to upload step photo.',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  removeStepPhoto(stepIndex: number): void {
    this.stepsArray.at(stepIndex).patchValue({ imagePath: null });
    this.metaDirty.set(true);
  }

  // --- Taxonomy Methods ---
  toggleCollection(id: string): void {
    const next = new Set(this.selectedCollectionIds());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.selectedCollectionIds.set(next);
    this.metaDirty.set(true);
  }

  toggleTag(id: string): void {
    const next = new Set(this.selectedTagIds());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.selectedTagIds.set(next);
    this.metaDirty.set(true);
  }

  protected onTagSearchInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.tagSearchQuery.set(value);
  }

  protected clearTagSearch(): void {
    this.tagSearchQuery.set('');
  }

  protected selectTagCategory(category: TagCategory | 'All'): void {
    this.activeTagCategory.set(category);
  }

  protected toggleManageTaxonomy(): void {
    this.isManagingTaxonomy.update((v) => !v);
  }

  protected toggleTagListExpanded(): void {
    this.isTagListExpanded.update((v) => !v);
  }

  openNewCollectionDialog(): void {
    const ref = this.dialog.open<CollectionDialogComponent, CollectionDialogData, CollectionDto>(
      CollectionDialogComponent,
      { width: '420px', data: {} },
    );
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.collections.update((prev) => [...prev, created]);
        this.toggleCollection(created.id);
      }
    });
  }

  openEditCollectionDialog(collection: CollectionDto, event: Event): void {
    event.stopPropagation();
    const ref = this.dialog.open<CollectionDialogComponent, CollectionDialogData, CollectionDto>(
      CollectionDialogComponent,
      { width: '420px', data: { collection } },
    );
    ref.afterClosed().subscribe((updated) => {
      if (updated) {
        this.collections.update((prev) => prev.map((c) => (c.id === updated.id ? updated : c)));
      }
    });
  }

  deleteCollection(collection: CollectionDto, event: Event): void {
    event.stopPropagation();
    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Collection',
        message: `Are you sure you want to delete "${collection.name}"? Recipes in this collection will not be deleted.`,
        confirmText: 'Delete',
        isDestructive: true,
      },
    });
    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) return;
      this.taxonomyService.deleteCollection(collection.id).subscribe({
        next: () => {
          this.collections.update((prev) => prev.filter((c) => c.id !== collection.id));
          const nextSelected = new Set(this.selectedCollectionIds());
          nextSelected.delete(collection.id);
          this.selectedCollectionIds.set(nextSelected);
          this.snackBar.open(`Deleted collection "${collection.name}"`, 'Close', {
            duration: 3000,
          });
        },
        error: (err: { error?: { detail?: string; title?: string } }) => {
          this.snackBar.open(
            err?.error?.detail || err?.error?.title || 'Failed to delete collection.',
            'Close',
            { duration: 4000 },
          );
        },
      });
    });
  }

  openNewTagDialog(defaultCategory: TagCategory = 'Custom'): void {
    const active = this.activeTagCategory();
    const effectiveDefault = active !== 'All' ? active : defaultCategory;
    const ref = this.dialog.open<TagDialogComponent, TagDialogData, TagDto>(TagDialogComponent, {
      width: '420px',
      data: { defaultCategory: effectiveDefault },
    });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.tags.update((prev) => [...prev, created]);
        this.toggleTag(created.id);
      }
    });
  }

  openEditTagDialog(tag: TagDto, event: Event): void {
    event.stopPropagation();
    const ref = this.dialog.open<TagDialogComponent, TagDialogData, TagDto>(TagDialogComponent, {
      width: '420px',
      data: { tag },
    });
    ref.afterClosed().subscribe((updated) => {
      if (updated) {
        this.tags.update((prev) => prev.map((t) => (t.id === updated.id ? updated : t)));
      }
    });
  }

  deleteTag(tag: TagDto, event: Event): void {
    event.stopPropagation();
    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Tag',
        message: `Are you sure you want to delete the tag "${tag.name}"? It will be removed from all recipes.`,
        confirmText: 'Delete',
        isDestructive: true,
      },
    });
    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) return;
      this.taxonomyService.deleteTag(tag.id).subscribe({
        next: () => {
          this.tags.update((prev) => prev.filter((t) => t.id !== tag.id));
          const nextSelected = new Set(this.selectedTagIds());
          nextSelected.delete(tag.id);
          this.selectedTagIds.set(nextSelected);
          this.snackBar.open(`Deleted tag "${tag.name}"`, 'Close', { duration: 3000 });
        },
        error: (err: { error?: { detail?: string; title?: string } }) => {
          this.snackBar.open(
            err?.error?.detail || err?.error?.title || 'Failed to delete tag.',
            'Close',
            { duration: 4000 },
          );
        },
      });
    });
  }

  // --- Food Catalog Filtering & Linking ---
  protected filterFoodItems(query: string | null | undefined): FoodItemDto[] {
    const items = this.foodItems();
    const q = (query ?? '').trim().toLowerCase();
    if (!q) {
      return items.slice(0, MAX_FOOD_SUGGESTIONS);
    }

    const startsWith: FoodItemDto[] = [];
    const contains: FoodItemDto[] = [];
    const aliasMatches: FoodItemDto[] = [];

    for (const item of items) {
      const lower = item.name.toLowerCase();
      if (lower.startsWith(q)) {
        startsWith.push(item);
      } else if (lower.includes(q)) {
        contains.push(item);
      } else if (item.aliases.some((alias) => alias.toLowerCase().includes(q))) {
        aliasMatches.push(item);
      }
    }

    return [...startsWith, ...contains, ...aliasMatches].slice(0, MAX_FOOD_SUGGESTIONS);
  }

  protected getFilteredFoodItemsForIngredient(index: number): FoodItemDto[] {
    this.formRevision();
    const query = this.ingredientsArray.at(index)?.get('displayName')?.value as string | undefined;
    return this.filterFoodItems(query);
  }

  protected getFilteredFoodItemsForReplacement(ingIndex: number, repIndex: number): FoodItemDto[] {
    this.formRevision();
    const query = this.getReplacementsArray(ingIndex).at(repIndex)?.get('displayName')?.value as
      string | undefined;
    return this.filterFoodItems(query);
  }

  protected hasExactFoodItemMatch(query: string | null | undefined): boolean {
    const q = (query ?? '').trim().toLowerCase();
    if (!q) return true;
    return this.foodItems().some((item) => item.name.trim().toLowerCase() === q);
  }

  protected getTrimmedIngredientName(index: number): string {
    this.formRevision();
    return ((this.ingredientsArray.at(index)?.get('displayName')?.value as string) || '').trim();
  }

  protected getFoodItemName(foodItemId: string | null | undefined): string {
    if (!foodItemId) return '';
    return this.foodItemNameById().get(foodItemId) ?? '';
  }

  protected onIngredientDisplayNameInput(index: number): void {
    const ingGroup = this.ingredientsArray.at(index);
    if (!ingGroup) return;
    const linkedId = ingGroup.get('foodItemId')?.value as string | null;
    if (!linkedId) return;
    const linkedName = this.getFoodItemName(linkedId);
    const current = (ingGroup.get('displayName')?.value || '').trim();
    if (linkedName && current.toLowerCase() !== linkedName.toLowerCase()) {
      ingGroup.patchValue({ foodItemId: null }, { emitEvent: false });
    }
  }

  protected onFoodOptionSelected(index: number, value: string): void {
    if (value === CREATE_FOOD_SENTINEL) {
      const ingGroup = this.ingredientsArray.at(index);
      const previousTyped = this.lastTypedByGroup.get(ingGroup) ?? '';
      ingGroup.patchValue({ displayName: previousTyped });
      this.openCreateFoodItemForIngredient(index, previousTyped);
      return;
    }
    this.onFoodItemSelected(index, value);
  }

  private readonly lastTypedByGroup = new WeakMap<FormGroup, string>();

  protected captureIngredientTypedName(index: number): void {
    const ingGroup = this.ingredientsArray.at(index);
    if (!ingGroup) return;
    const current = String(ingGroup.get('displayName')?.value || '');
    if (current !== CREATE_FOOD_SENTINEL) {
      this.lastTypedByGroup.set(ingGroup, current);
    }
  }

  protected unlinkFoodItem(index: number): void {
    this.ingredientsArray.at(index)?.patchValue({ foodItemId: null });
    this.form.markAsDirty();
  }

  protected onReplacementFoodSelected(
    ingIndex: number,
    repIndex: number,
    foodItemId: string,
  ): void {
    const item = this.foodItems().find((f) => f.id === foodItemId);
    const repGroup = this.getReplacementsArray(ingIndex).at(repIndex);
    if (item && repGroup) {
      repGroup.patchValue({
        foodItemId: item.id,
        displayName: item.name,
      });
    }
  }

  protected onReplacementDisplayNameInput(ingIndex: number, repIndex: number): void {
    const repGroup = this.getReplacementsArray(ingIndex).at(repIndex);
    if (!repGroup) return;
    const linkedId = repGroup.get('foodItemId')?.value as string | null;
    if (!linkedId) return;
    const linkedName = this.getFoodItemName(linkedId);
    const current = (repGroup.get('displayName')?.value || '').trim();
    if (linkedName && current.toLowerCase() !== linkedName.toLowerCase()) {
      repGroup.patchValue({ foodItemId: null }, { emitEvent: false });
    }
  }

  // --- Replacement Methods ---
  private createReplacementGroup(rep?: Partial<RecipeIngredientReplacementDto>): FormGroup {
    return this.fb.group({
      displayName: [rep?.displayName ?? '', [Validators.required, Validators.maxLength(200)]],
      quantity: [rep?.quantity ?? null],
      maxQuantity: [rep?.maxQuantity ?? null],
      unit: [rep?.unit ?? null],
      customUnitName: [rep?.customUnitName ?? '', Validators.maxLength(64)],
      foodItemId: [rep?.foodItemId ?? null],
      preparationNote: [rep?.preparationNote ?? '', Validators.maxLength(300)],
    });
  }

  getReplacementsArray(ingredientIndex: number): FormArray<FormGroup> {
    return this.ingredientsArray.at(ingredientIndex).get('replacements') as FormArray<FormGroup>;
  }

  addIngredientReplacement(
    ingredientIndex: number,
    rep?: Partial<RecipeIngredientReplacementDto>,
  ): void {
    this.getReplacementsArray(ingredientIndex).push(this.createReplacementGroup(rep));
    this.form.markAsDirty();
  }

  removeIngredientReplacement(ingredientIndex: number, repIndex: number): void {
    this.getReplacementsArray(ingredientIndex).removeAt(repIndex);
    this.form.markAsDirty();
  }

  // --- Progressive Disclosure Helpers ---
  protected isRangeExpanded(ingGroup: FormGroup): boolean {
    this.formRevision();
    const maxVal = ingGroup.get('maxQuantity')?.value;
    return (
      this.expandedRangeGroups().has(ingGroup) ||
      (maxVal !== null && maxVal !== undefined && maxVal !== '')
    );
  }

  protected toggleRange(ingGroup: FormGroup): void {
    const next = new Set(this.expandedRangeGroups());
    if (this.isRangeExpanded(ingGroup)) {
      next.delete(ingGroup);
      ingGroup.patchValue({ maxQuantity: null });
    } else {
      next.add(ingGroup);
    }
    this.expandedRangeGroups.set(next);
  }

  protected isAltMeasurementExpanded(ingGroup: FormGroup): boolean {
    this.formRevision();
    return (
      this.expandedAltGroups().has(ingGroup) ||
      ingGroup.get('alternativeMeasurement')?.value != null
    );
  }

  protected toggleAltMeasurement(ingGroup: FormGroup): void {
    const next = new Set(this.expandedAltGroups());
    if (this.isAltMeasurementExpanded(ingGroup)) {
      next.delete(ingGroup);
      ingGroup.patchValue({
        alternativeMeasurement: null,
        preferAlternativeMeasurement: false,
      });
    } else {
      next.add(ingGroup);
    }
    this.expandedAltGroups.set(next);
  }

  protected formatAltMeasurementBadge(ingGroup: FormGroup): string {
    this.formRevision();
    const alt = ingGroup.get('alternativeMeasurement')?.value as RecipeMeasurement | null;
    if (!alt || alt.quantity == null || !alt.unit) return '';
    const unitMeta = this.measurementUnits.find((u) => u.value === alt.unit);
    const unitLabel =
      alt.unit === 'Custom'
        ? alt.customUnitName?.trim() || 'custom'
        : unitMeta?.shortLabel || alt.unit;
    const qtyStr =
      alt.maxQuantity != null ? `${alt.quantity}–${alt.maxQuantity}` : `${alt.quantity}`;
    return `${qtyStr} ${unitLabel}`;
  }

  // --- Section Helpers ---
  protected shouldShowIngredientSectionHeader(index: number): boolean {
    this.formRevision();
    const current = (this.ingredientsArray.at(index)?.get('sectionName')?.value || '').trim();
    if (!current) return false;
    if (index === 0) return true;
    const prev = (this.ingredientsArray.at(index - 1)?.get('sectionName')?.value || '').trim();
    return current.toLowerCase() !== prev.toLowerCase();
  }

  protected shouldShowStepSectionHeader(index: number): boolean {
    this.formRevision();
    const current = (this.stepsArray.at(index)?.get('sectionName')?.value || '').trim();
    if (!current) return false;
    if (index === 0) return true;
    const prev = (this.stepsArray.at(index - 1)?.get('sectionName')?.value || '').trim();
    return current.toLowerCase() !== prev.toLowerCase();
  }

  // --- Bulk Paste Ingredients ---
  protected togglePasteIngredients(): void {
    this.showPasteIngredients.update((v) => !v);
  }

  protected onPasteTextInput(event: Event): void {
    this.pasteIngredientsText.set((event.target as HTMLTextAreaElement).value);
  }

  protected applyPastedIngredients(): void {
    const parsed = parseIngredientLines(this.pasteIngredientsText());
    if (parsed.length === 0) return;

    // Replace a single untouched blank starter row instead of leaving an empty #1 row behind
    if (this.ingredientsArray.length === 1) {
      const first = this.ingredientsArray.at(0);
      const isBlankStarter =
        !(first.get('displayName')?.value || '').trim() &&
        first.get('quantity')?.value == null &&
        !(first.get('sectionName')?.value || '').trim() &&
        !first.get('isSubRecipe')?.value;
      if (isBlankStarter) {
        this.ingredientsArray.removeAt(0);
      }
    }

    const items = this.foodItems();
    const nextRangeGroups = new Set(this.expandedRangeGroups());

    for (const line of parsed) {
      const lower = line.displayName.toLowerCase();
      const catalogMatch = items.find(
        (item) =>
          item.name.toLowerCase() === lower ||
          item.aliases.some((alias) => alias.toLowerCase() === lower),
      );

      const group = this.createIngredientGroup({
        sectionName: line.sectionName ?? '',
        quantity: line.quantity,
        maxQuantity: line.maxQuantity,
        unit: line.unit,
        customUnitName: line.customUnitName ?? '',
        foodItemId: catalogMatch?.id ?? null,
        displayName: line.displayName,
        preparationNote: line.preparationNote ?? '',
        isOptional: line.isOptional,
        originalRawText: line.originalRawText,
      });

      if (line.maxQuantity !== null) {
        nextRangeGroups.add(group);
      }

      this.ingredientsArray.push(group);
    }

    this.expandedRangeGroups.set(nextRangeGroups);
    this.pasteIngredientsText.set('');
    this.showPasteIngredients.set(false);
    this.form.markAsDirty();
    this.snackBar.open(
      `Added ${parsed.length} ingredient${parsed.length === 1 ? '' : 's'}.`,
      'Close',
      { duration: 2500 },
    );
  }

  // --- Ingredient Methods ---
  private createIngredientGroup(initial?: Partial<IngredientFormValue>): FormGroup {
    return this.fb.group(
      {
        id: [initial?.id ?? null],
        isSubRecipe: [initial?.isSubRecipe ?? false],
        sectionName: [initial?.sectionName ?? '', Validators.maxLength(150)],
        quantity: [initial?.quantity ?? null],
        maxQuantity: [initial?.maxQuantity ?? null],
        unit: [initial?.unit ?? null],
        customUnitName: [initial?.customUnitName ?? '', Validators.maxLength(50)],
        foodItemId: [initial?.foodItemId ?? null],
        linkedRecipeId: [initial?.linkedRecipeId ?? null],
        linkedRecipeTitle: [initial?.linkedRecipeTitle ?? null],
        unresolvedRecipeTitle: [initial?.unresolvedRecipeTitle ?? null],
        displayName: [initial?.displayName ?? '', [Validators.required, Validators.maxLength(200)]],
        preparationNote: [initial?.preparationNote ?? '', Validators.maxLength(200)],
        isOptional: [initial?.isOptional ?? false],
        originalRawText: [initial?.originalRawText ?? null],
        alternativeMeasurement: this.fb.control<RecipeMeasurement | null>(
          initial?.alternativeMeasurement ?? null,
        ),
        preferAlternativeMeasurement: [initial?.preferAlternativeMeasurement ?? false],
        replacements: this.fb.array<FormGroup>([]),
      },
      { validators: ingredientMeasurementValidator },
    );
  }

  addIngredient(sectionName = ''): void {
    this.ingredientsArray.push(this.createIngredientGroup({ sectionName }));
  }

  removeIngredient(index: number): void {
    const group = this.ingredientsArray.at(index);
    this.ingredientsArray.removeAt(index);
    if (group) {
      const nextRanges = new Set(this.expandedRangeGroups());
      nextRanges.delete(group);
      this.expandedRangeGroups.set(nextRanges);

      const nextAlts = new Set(this.expandedAltGroups());
      nextAlts.delete(group);
      this.expandedAltGroups.set(nextAlts);
    }
    this.form.markAsDirty();
  }

  moveIngredientUp(index: number): void {
    if (index > 0) {
      const item = this.ingredientsArray.at(index);
      this.ingredientsArray.removeAt(index);
      this.ingredientsArray.insert(index - 1, item);
      this.form.markAsDirty();
    }
  }

  moveIngredientDown(index: number): void {
    if (index < this.ingredientsArray.length - 1) {
      const item = this.ingredientsArray.at(index);
      this.ingredientsArray.removeAt(index);
      this.ingredientsArray.insert(index + 1, item);
      this.form.markAsDirty();
    }
  }

  protected onIngredientDropped(event: CdkDragDrop<FormGroup[]>): void {
    if (event.previousIndex === event.currentIndex) return;
    const item = this.ingredientsArray.at(event.previousIndex);
    this.ingredientsArray.removeAt(event.previousIndex);
    this.ingredientsArray.insert(event.currentIndex, item);
    this.form.markAsDirty();
  }

  onFoodItemSelected(index: number, foodItemId: string): void {
    const item = this.foodItems().find((f) => f.id === foodItemId);
    const ingGroup = this.ingredientsArray.at(index);
    if (item && ingGroup) {
      ingGroup.patchValue({
        foodItemId: item.id,
        displayName: item.name,
        linkedRecipeId: null,
        linkedRecipeTitle: null,
        unresolvedRecipeTitle: null,
        isSubRecipe: false,
      });
    }
  }

  onSubRecipeToggled(index: number, isSubRecipe: boolean): void {
    const ingGroup = this.ingredientsArray.at(index);
    if (!ingGroup) return;

    if (isSubRecipe) {
      const existingDisplay = (ingGroup.get('displayName')?.value || '').trim();
      const hasLinked = !!ingGroup.get('linkedRecipeId')?.value;
      ingGroup.patchValue({
        foodItemId: null,
        unresolvedRecipeTitle: hasLinked
          ? null
          : ingGroup.get('unresolvedRecipeTitle')?.value || existingDisplay || null,
      });
      this.getReplacementsArray(index).clear();
    } else {
      ingGroup.patchValue({
        linkedRecipeId: null,
        linkedRecipeTitle: null,
        unresolvedRecipeTitle: null,
      });
    }
  }

  onSubRecipeSelected(index: number, recipe: RecipeAutocompleteItemDto): void {
    const ingGroup = this.ingredientsArray.at(index);
    if (recipe && ingGroup) {
      ingGroup.patchValue({
        linkedRecipeId: recipe.id,
        linkedRecipeTitle: recipe.title,
        unresolvedRecipeTitle: null,
        displayName: recipe.title,
        foodItemId: null,
        isSubRecipe: true,
      });
    }
  }

  onSubRecipeTextChanged(index: number, text: string): void {
    const ingGroup = this.ingredientsArray.at(index);
    if (!ingGroup) return;

    const trimmed = text.trim();
    ingGroup.patchValue({
      displayName: text,
      linkedRecipeId: null,
      linkedRecipeTitle: null,
      unresolvedRecipeTitle: trimmed || null,
      foodItemId: null,
    });
  }

  onSubRecipeCleared(index: number): void {
    const ingGroup = this.ingredientsArray.at(index);
    if (!ingGroup) return;

    ingGroup.patchValue({
      linkedRecipeId: null,
      linkedRecipeTitle: null,
      unresolvedRecipeTitle: null,
    });
  }

  openCreateFoodItemForIngredient(index: number, initialText: string): void {
    const dialogRef = this.dialog.open(FoodItemDialogComponent, {
      width: '460px',
      data: { initialName: initialText.trim() },
    });

    dialogRef.afterClosed().subscribe((created: FoodItemDto | null) => {
      if (created) {
        this.foodItems.update((items) => [created, ...items]);
        this.onFoodItemSelected(index, created.id);
        this.snackBar.open(`Created and linked "${created.name}"`, 'Close', { duration: 3000 });
      }
    });
  }

  // --- Step Methods ---
  addStep(sectionName = ''): void {
    const stepGroup = this.fb.group({
      id: [null],
      sectionName: [sectionName, Validators.maxLength(150)],
      instruction: ['', [Validators.required, Validators.maxLength(4000)]],
      imagePath: [null as string | null],
      timers: this.fb.array<FormGroup>([]),
    });
    this.stepsArray.push(stepGroup);
  }

  removeStep(index: number): void {
    this.stepsArray.removeAt(index);
    this.form.markAsDirty();
  }

  moveStepUp(index: number): void {
    if (index > 0) {
      const item = this.stepsArray.at(index);
      this.stepsArray.removeAt(index);
      this.stepsArray.insert(index - 1, item);
      this.form.markAsDirty();
    }
  }

  moveStepDown(index: number): void {
    if (index < this.stepsArray.length - 1) {
      const item = this.stepsArray.at(index);
      this.stepsArray.removeAt(index);
      this.stepsArray.insert(index + 1, item);
      this.form.markAsDirty();
    }
  }

  protected onStepDropped(event: CdkDragDrop<FormGroup[]>): void {
    if (event.previousIndex === event.currentIndex) return;
    const item = this.stepsArray.at(event.previousIndex);
    this.stepsArray.removeAt(event.previousIndex);
    this.stepsArray.insert(event.currentIndex, item);
    this.form.markAsDirty();
  }

  getTimersArray(stepIndex: number): FormArray<FormGroup> {
    return this.stepsArray.at(stepIndex).get('timers') as FormArray<FormGroup>;
  }

  addTimerToStep(stepIndex: number): void {
    const timers = this.getTimersArray(stepIndex);
    timers.push(
      this.fb.group({
        minutes: [5],
        seconds: [0],
        label: ['Simmer', [Validators.required, Validators.maxLength(150)]],
      }),
    );
  }

  removeTimerFromStep(stepIndex: number, timerIndex: number): void {
    const timers = this.getTimersArray(stepIndex);
    timers.removeAt(timerIndex);
  }

  getStepDialogIngredients(): StepDialogIngredientOption[] {
    return this.ingredientsArray.controls
      .map((ctrl, index) => ({
        index,
        id: ctrl.get('id')?.value ?? null,
        displayName: (ctrl.get('displayName')?.value || '').trim(),
        sectionName: ctrl.get('sectionName')?.value?.trim() || null,
        quantity:
          ctrl.get('quantity')?.value !== null &&
          ctrl.get('quantity')?.value !== undefined &&
          ctrl.get('quantity')?.value !== ''
            ? Number(ctrl.get('quantity')?.value)
            : null,
        unit: ctrl.get('unit')?.value ?? null,
        customUnitName: ctrl.get('customUnitName')?.value?.trim() || null,
      }))
      .filter((item) => !!item.displayName);
  }

  // --- Validation & Save / Submit ---
  private collectValidationIssues(): ValidationIssue[] {
    const issues: ValidationIssue[] = [];

    if (this.form.controls.title.invalid) {
      issues.push({ message: 'Recipe title is required', targetId: 'field-recipe-title' });
    }
    if (this.form.controls.baseServings.invalid) {
      issues.push({
        message: 'Base servings must be between 0.25 and 999',
        targetId: 'field-base-servings',
      });
    }

    this.ingredientsArray.controls.forEach((group, idx) => {
      const targetId = `ingredient-card-${idx}`;
      if (group.get('displayName')?.invalid) {
        issues.push({ message: `Ingredient #${idx + 1}: name is required`, targetId });
      }
      if (group.hasError('primaryMeasurement')) {
        issues.push({
          message: `Ingredient #${idx + 1}: primary quantity & unit required for alternative measurement`,
          targetId,
        });
      }
      if (group.get('alternativeMeasurement')?.invalid) {
        issues.push({
          message: `Ingredient #${idx + 1}: complete or remove alternative measurement`,
          targetId,
        });
      }
      const reps = group.get('replacements') as FormArray<FormGroup>;
      reps?.controls.forEach((repGroup, repIdx) => {
        if (repGroup.get('displayName')?.invalid) {
          issues.push({
            message: `Ingredient #${idx + 1}: replacement #${repIdx + 1} needs a name`,
            targetId,
          });
        }
      });
    });

    this.stepsArray.controls.forEach((group, idx) => {
      if (group.get('instruction')?.invalid) {
        issues.push({
          message: `Step ${idx + 1}: instruction is required`,
          targetId: `step-card-${idx}`,
        });
      }
    });

    return issues;
  }

  onSave(): void {
    this.hasAttemptedSave.set(true);

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      const issues = this.collectValidationIssues();
      this.validationIssues.set(issues);
      this.errorMessage.set('Please fix the highlighted fields before saving.');

      const toastMsg =
        issues.length === 1
          ? issues[0].message
          : `Please fix ${issues.length || 'the'} required fields before saving.`;
      this.snackBar
        .open(toastMsg, 'Show', { duration: 4000 })
        ?.onAction?.()
        .subscribe(() => {
          if (issues[0]) {
            this.scrollToSection(issues[0].targetId);
          }
        });

      if (issues[0]) {
        this.scrollToSection(issues[0].targetId);
      }
      return;
    }

    const val = this.form.getRawValue();

    // Map ingredients
    const rawIngredients = (val.ingredients || []) as unknown as IngredientFormValue[];
    const mappedIngredients: RecipeIngredientInput[] = rawIngredients.map((ing) => ({
      sectionName: ing.sectionName?.trim() || null,
      quantity:
        ing.quantity !== null && ing.quantity !== undefined && ing.quantity !== ''
          ? Number(ing.quantity)
          : null,
      maxQuantity:
        ing.maxQuantity !== null && ing.maxQuantity !== undefined && ing.maxQuantity !== ''
          ? Number(ing.maxQuantity)
          : null,
      unit: ing.unit || null,
      customUnitName: ing.unit === 'Custom' ? ing.customUnitName?.trim() || null : null,
      foodItemId: !ing.isSubRecipe && ing.foodItemId ? ing.foodItemId : null,
      linkedRecipeId: ing.isSubRecipe && ing.linkedRecipeId ? ing.linkedRecipeId : null,
      unresolvedRecipeTitle:
        ing.isSubRecipe && !ing.linkedRecipeId
          ? ing.unresolvedRecipeTitle?.trim() || ing.displayName.trim()
          : null,
      displayName: ing.displayName.trim(),
      preparationNote: stripOuterParentheses(ing.preparationNote),
      isOptional: !!ing.isOptional,
      originalRawText: ing.originalRawText ?? null,
      alternativeMeasurement: ing.alternativeMeasurement ?? null,
      preferAlternativeMeasurement:
        !!ing.alternativeMeasurement && !!ing.preferAlternativeMeasurement,
      replacements:
        !ing.isSubRecipe && ing.replacements?.length
          ? ing.replacements
              .filter((r) => !!r.displayName?.trim())
              .map((r): RecipeIngredientReplacementInput => ({
                displayName: r.displayName.trim(),
                quantity:
                  r.quantity !== null && r.quantity !== undefined && r.quantity !== ''
                    ? Number(r.quantity)
                    : null,
                maxQuantity:
                  r.maxQuantity !== null && r.maxQuantity !== undefined && r.maxQuantity !== ''
                    ? Number(r.maxQuantity)
                    : null,
                unit: r.unit || null,
                customUnitName: r.unit === 'Custom' ? r.customUnitName?.trim() || null : null,
                foodItemId: r.foodItemId || null,
                preparationNote: stripOuterParentheses(r.preparationNote),
              }))
          : null,
    }));

    // Map steps
    const rawSteps = (val.steps || []) as unknown as StepFormValue[];
    const mappedSteps: RecipeStepInput[] = rawSteps.map((s, index) => {
      const stepGroup = this.stepsArray.at(index);
      const fallbackTimers = (s.timers || []).map((t) => {
        const mins = Number(t.minutes) || 0;
        const secs = Number(t.seconds) || 0;
        const totalSecs = Math.max(1, mins * 60 + secs);
        return {
          durationSeconds: totalSecs,
          label: t.label.trim(),
        };
      });

      const resolved = resolveStepMetadataForPayload(
        s.instruction,
        this.loadedStepTokenOrigins.get(stepGroup),
        fallbackTimers,
      );

      return {
        sectionName: s.sectionName?.trim() || null,
        instruction: (s.instruction || '').trim(),
        imagePath: s.imagePath?.trim() || null,
        timers: resolved.timers,
      };
    });

    const request: UpsertRecipeRequest = {
      title: (val.title || '').trim(),
      status: (val.status as RecipeStatus) || 'Published',
      description: val.description?.trim() || null,
      baseServings: Number(val.baseServings) || 4,
      yieldDisplay: val.yieldDisplay?.trim() || null,
      prepTimeMinutes: val.prepTimeMinutes != null ? Number(val.prepTimeMinutes) : null,
      cookTimeMinutes: val.cookTimeMinutes != null ? Number(val.cookTimeMinutes) : null,
      totalTimeMinutes: val.totalTimeMinutes != null ? Number(val.totalTimeMinutes) : null,
      difficulty: val.difficulty || null,
      sourceName: val.sourceName?.trim() || null,
      sourceAuthor: val.sourceAuthor?.trim() || null,
      sourceUrl: val.sourceUrl?.trim() || null,
      sourceBookTitle: val.sourceBookTitle?.trim() || null,
      sourceBookPage: val.sourceBookPage != null ? Number(val.sourceBookPage) : null,
      coverImagePath: this.coverImagePath(),
      thumbnailImagePath: this.thumbnailImagePath(),
      collectionIds: Array.from(this.selectedCollectionIds()),
      tagIds: Array.from(this.selectedTagIds()),
      ingredients: mappedIngredients,
      steps: mappedSteps,
    };

    this.isSaving.set(true);
    this.errorMessage.set(null);
    this.validationIssues.set([]);

    const obs$ =
      this.isEdit() && this.recipeId()
        ? this.recipeService.updateRecipe(this.recipeId()!, request)
        : this.recipeService.createRecipe(request);

    obs$.subscribe({
      next: (saved) => {
        this.isSaving.set(false);
        this.form.markAsPristine();
        this.metaDirty.set(false);
        this.snackBar.open(`Recipe "${saved.title}" saved!`, 'Close', { duration: 3000 });
        this.router.navigate(['/recipes', saved.id]);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isSaving.set(false);
        const msg = err?.error?.detail || err?.error?.title || 'Failed to save recipe.';
        this.errorMessage.set(msg);
        this.snackBar.open(msg, 'Close', { duration: 4000 });
      },
    });
  }

  private toPositiveNumber(val: number | string | null | undefined): number | null {
    if (val === null || val === undefined || val === '') return null;
    const num = Number(val);
    return Number.isFinite(num) && num >= 0 ? num : null;
  }
}
