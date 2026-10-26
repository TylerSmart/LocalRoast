import { Component, OnInit, computed, inject, signal } from '@angular/core';
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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import {
  MeasurementUnit,
  RecipeDetailDto,
  RecipeDifficulty,
  RecipeIngredientInput,
  RecipeStatus,
  RecipeStepInput,
  RecipeSummaryDto,
  UpsertRecipeRequest,
} from '../../../core/models/recipe.models';
import { CollectionDto, TagCategory, TagDto } from '../../../core/models/taxonomy.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { MediaService } from '../../../core/services/media/media.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { FoodItemDialogComponent } from '../../food-catalog/food-item-dialog/food-item-dialog.component';
import {
  CollectionDialogComponent,
  CollectionDialogData,
} from '../collection-dialog/collection-dialog.component';
import { TagDialogComponent, TagDialogData } from '../tag-dialog/tag-dialog.component';

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
  displayName: string;
  preparationNote?: string | null;
  isOptional?: boolean;
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
  ingredientIndices?: number[];
  timers?: TimerFormValue[];
}

export const MEASUREMENT_UNITS: { value: MeasurementUnit; label: string }[] = [
  { value: 'Tsp', label: 'Teaspoon (tsp)' },
  { value: 'Tbsp', label: 'Tablespoon (tbsp)' },
  { value: 'FlOz', label: 'Fluid Ounce (fl oz)' },
  { value: 'Cup', label: 'Cup (cup)' },
  { value: 'Pt', label: 'Pint (pt)' },
  { value: 'Qt', label: 'Quart (qt)' },
  { value: 'Gal', label: 'Gallon (gal)' },
  { value: 'Ml', label: 'Milliliter (ml)' },
  { value: 'L', label: 'Liter (l)' },
  { value: 'Oz', label: 'Ounce (oz)' },
  { value: 'Lb', label: 'Pound (lb)' },
  { value: 'G', label: 'Gram (g)' },
  { value: 'Kg', label: 'Kilogram (kg)' },
  { value: 'Pinch', label: 'Pinch' },
  { value: 'Dash', label: 'Dash' },
  { value: 'Clove', label: 'Clove' },
  { value: 'Can', label: 'Can' },
  { value: 'Bunch', label: 'Bunch' },
  { value: 'Slice', label: 'Slice' },
  { value: 'Whole', label: 'Whole' },
  { value: 'Custom', label: 'Custom Unit...' },
];

@Component({
  imports: [
    ReactiveFormsModule,
    RouterLink,
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
  ],
  selector: 'app-recipe-editor',
  templateUrl: './recipe-editor.component.html',
  styleUrl: './recipe-editor.component.scss',
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

  protected readonly measurementUnits = MEASUREMENT_UNITS;
  protected readonly isEdit = signal<boolean>(false);
  protected readonly recipeId = signal<string | null>(null);
  protected readonly isLoading = signal<boolean>(false);
  protected readonly isSaving = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly foodItems = signal<FoodItemDto[]>([]);
  protected readonly availableSubRecipes = signal<RecipeSummaryDto[]>([]);

  // Media signals
  protected readonly coverImagePath = signal<string | null>(null);
  protected readonly thumbnailImagePath = signal<string | null>(null);
  protected readonly isUploadingCover = signal<boolean>(false);
  protected readonly stepUploadingMap = signal<Record<number, boolean>>({});

  // Taxonomy signals
  protected readonly collections = signal<CollectionDto[]>([]);
  protected readonly tags = signal<TagDto[]>([]);
  protected readonly selectedCollectionIds = signal<Set<string>>(new Set());
  protected readonly selectedTagIds = signal<Set<string>>(new Set());

  protected readonly tagCategories: TagCategory[] = [
    'Course',
    'Cuisine',
    'Dietary',
    'Equipment',
    'Custom',
  ];

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

  get ingredientsArray(): FormArray<FormGroup> {
    return this.form.get('ingredients') as FormArray<FormGroup>;
  }

  get stepsArray(): FormArray<FormGroup> {
    return this.form.get('steps') as FormArray<FormGroup>;
  }

  ngOnInit(): void {
    this.loadCatalogItems();
    this.loadSubRecipes();
    this.loadTaxonomy();

    this.route.paramMap.subscribe((params) => {
      const id = params.get('id');
      if (id) {
        this.isEdit.set(true);
        this.recipeId.set(id);
        this.loadRecipe(id);
      } else {
        // Add initial blank ingredient and step for new recipe
        this.addIngredient();
        this.addStep();
      }
    });
  }

  loadCatalogItems(): void {
    this.catalogService.getFoodItems().subscribe({
      next: (items) => this.foodItems.set(items),
    });
  }

  loadSubRecipes(): void {
    this.recipeService.getRecipes().subscribe({
      next: (recipes) => {
        // Exclude current recipe id if in edit mode
        const currentId = this.recipeId();
        this.availableSubRecipes.set(recipes.filter((r) => r.id !== currentId));
      },
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
    this.errorMessage.set(null);

    this.recipeService.getRecipeById(id).subscribe({
      next: (detail) => {
        this.populateForm(detail);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        this.errorMessage.set(err?.error?.detail || err?.error?.title || 'Failed to load recipe.');
      },
    });
  }

  populateForm(detail: RecipeDetailDto): void {
    this.coverImagePath.set(detail.coverImagePath ?? null);
    this.thumbnailImagePath.set(detail.thumbnailImagePath ?? null);
    this.selectedCollectionIds.set(new Set(detail.collections.map((c) => c.id)));
    this.selectedTagIds.set(new Set(detail.tags.map((t) => t.id)));

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
    const ingredientIdToIndex = new Map<string, number>();

    detail.ingredients.forEach((ing, index) => {
      ingredientIdToIndex.set(ing.id, index);
      const isSubRecipe = !!ing.linkedRecipeId;
      const group = this.fb.group({
        id: [ing.id],
        isSubRecipe: [isSubRecipe],
        sectionName: [ing.sectionName ?? '', Validators.maxLength(150)],
        quantity: [ing.quantity],
        maxQuantity: [ing.maxQuantity],
        unit: [ing.unit ?? null],
        customUnitName: [ing.customUnitName ?? '', Validators.maxLength(50)],
        foodItemId: [ing.foodItemId],
        linkedRecipeId: [ing.linkedRecipeId],
        displayName: [ing.displayName, [Validators.required, Validators.maxLength(200)]],
        preparationNote: [ing.preparationNote ?? '', Validators.maxLength(200)],
        isOptional: [ing.isOptional],
      });
      this.ingredientsArray.push(group);
    });

    // Populate Steps
    this.stepsArray.clear();
    detail.steps.forEach((step) => {
      const selectedIndices = step.ingredientIds
        .map((id) => ingredientIdToIndex.get(id))
        .filter((idx): idx is number => idx !== undefined);

      const timersArray = this.fb.array<FormGroup>([]);
      step.timers.forEach((t) => {
        timersArray.push(
          this.fb.group({
            minutes: [Math.floor(t.durationSeconds / 60)],
            seconds: [t.durationSeconds % 60],
            label: [t.label, [Validators.required, Validators.maxLength(150)]],
          }),
        );
      });

      const stepGroup = this.fb.group({
        id: [step.id],
        sectionName: [step.sectionName ?? '', Validators.maxLength(150)],
        instruction: [step.instruction, [Validators.required, Validators.maxLength(4000)]],
        imagePath: [step.imagePath ?? null],
        ingredientIndices: [selectedIndices],
        timers: timersArray,
      });

      this.stepsArray.push(stepGroup);
    });
  }

  // --- Cover Photo Methods ---
  onCoverFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    this.isUploadingCover.set(true);
    this.mediaService.uploadRecipeCover(file).subscribe({
      next: (dto) => {
        this.coverImagePath.set(dto.coverImagePath);
        this.thumbnailImagePath.set(dto.thumbnailImagePath);
        this.isUploadingCover.set(false);
        this.snackBar.open('Cover photo uploaded successfully!', 'Close', { duration: 3000 });
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
  }

  // --- Step Photo Methods ---
  onStepFileSelected(stepIndex: number, event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    this.stepUploadingMap.update((m) => ({ ...m, [stepIndex]: true }));
    this.mediaService.uploadStepImage(file).subscribe({
      next: (dto) => {
        this.stepsArray.at(stepIndex).patchValue({ imagePath: dto.imagePath });
        this.stepUploadingMap.update((m) => ({ ...m, [stepIndex]: false }));
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

  protected readonly tagsByCategory = computed(() => {
    const map: Record<TagCategory, TagDto[]> = {
      Course: [],
      Cuisine: [],
      Dietary: [],
      Equipment: [],
      Custom: [],
    };
    for (const t of this.tags()) {
      if (map[t.category]) {
        map[t.category].push(t);
      }
    }
    return map;
  });

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

  openNewTagDialog(defaultCategory: TagCategory = 'Custom'): void {
    const ref = this.dialog.open<TagDialogComponent, TagDialogData, TagDto>(TagDialogComponent, {
      width: '420px',
      data: { defaultCategory },
    });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.tags.update((prev) => [...prev, created]);
        this.toggleTag(created.id);
      }
    });
  }

  // --- Ingredient Methods ---
  addIngredient(): void {
    const group = this.fb.group({
      id: [null],
      isSubRecipe: [false],
      sectionName: ['', Validators.maxLength(150)],
      quantity: [null],
      maxQuantity: [null],
      unit: [null],
      customUnitName: ['', Validators.maxLength(50)],
      foodItemId: [null],
      linkedRecipeId: [null],
      displayName: ['', [Validators.required, Validators.maxLength(200)]],
      preparationNote: ['', Validators.maxLength(200)],
      isOptional: [false],
    });
    this.ingredientsArray.push(group);
  }

  removeIngredient(index: number): void {
    this.ingredientsArray.removeAt(index);
  }

  moveIngredientUp(index: number): void {
    if (index > 0) {
      const item = this.ingredientsArray.at(index);
      this.ingredientsArray.removeAt(index);
      this.ingredientsArray.insert(index - 1, item);
    }
  }

  moveIngredientDown(index: number): void {
    if (index < this.ingredientsArray.length - 1) {
      const item = this.ingredientsArray.at(index);
      this.ingredientsArray.removeAt(index);
      this.ingredientsArray.insert(index + 1, item);
    }
  }

  onFoodItemSelected(index: number, foodItemId: string): void {
    const item = this.foodItems().find((f) => f.id === foodItemId);
    const ingGroup = this.ingredientsArray.at(index);
    if (item && ingGroup) {
      ingGroup.patchValue({
        foodItemId: item.id,
        displayName: item.name,
        linkedRecipeId: null,
      });
    }
  }

  onSubRecipeSelected(index: number, recipeId: string): void {
    const recipe = this.availableSubRecipes().find((r) => r.id === recipeId);
    const ingGroup = this.ingredientsArray.at(index);
    if (recipe && ingGroup) {
      ingGroup.patchValue({
        linkedRecipeId: recipe.id,
        displayName: recipe.title,
        foodItemId: null,
      });
    }
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
        this.snackBar.open(`Created and selected "${created.name}"`, 'Close', { duration: 3000 });
      }
    });
  }

  // --- Step Methods ---
  addStep(): void {
    const stepGroup = this.fb.group({
      id: [null],
      sectionName: ['', Validators.maxLength(150)],
      instruction: ['', [Validators.required, Validators.maxLength(4000)]],
      imagePath: [null as string | null],
      ingredientIndices: [[] as number[]],
      timers: this.fb.array<FormGroup>([]),
    });
    this.stepsArray.push(stepGroup);
  }

  removeStep(index: number): void {
    this.stepsArray.removeAt(index);
  }

  moveStepUp(index: number): void {
    if (index > 0) {
      const item = this.stepsArray.at(index);
      this.stepsArray.removeAt(index);
      this.stepsArray.insert(index - 1, item);
    }
  }

  moveStepDown(index: number): void {
    if (index < this.stepsArray.length - 1) {
      const item = this.stepsArray.at(index);
      this.stepsArray.removeAt(index);
      this.stepsArray.insert(index + 1, item);
    }
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

  toggleStepIngredientCallout(stepIndex: number, ingredientIndex: number): void {
    const step = this.stepsArray.at(stepIndex);
    const curr: number[] = step.get('ingredientIndices')?.value ?? [];
    const next = curr.includes(ingredientIndex)
      ? curr.filter((i) => i !== ingredientIndex)
      : [...curr, ingredientIndex];
    step.patchValue({ ingredientIndices: next });
  }

  // --- Save / Submit ---
  onSave(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.errorMessage.set('Please fill out all required fields before saving.');
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
      displayName: ing.displayName.trim(),
      preparationNote: ing.preparationNote?.trim() || null,
      isOptional: !!ing.isOptional,
    }));

    // Map steps
    const rawSteps = (val.steps || []) as unknown as StepFormValue[];
    const mappedSteps: RecipeStepInput[] = rawSteps.map((s) => {
      const timers = (s.timers || []).map((t) => {
        const mins = Number(t.minutes) || 0;
        const secs = Number(t.seconds) || 0;
        const totalSecs = Math.max(1, mins * 60 + secs);
        return {
          durationSeconds: totalSecs,
          label: t.label.trim(),
        };
      });

      return {
        sectionName: s.sectionName?.trim() || null,
        instruction: s.instruction.trim(),
        imagePath: s.imagePath?.trim() || null,
        ingredientIndices: s.ingredientIndices || [],
        timers,
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

    const obs$ =
      this.isEdit() && this.recipeId()
        ? this.recipeService.updateRecipe(this.recipeId()!, request)
        : this.recipeService.createRecipe(request);

    obs$.subscribe({
      next: (saved) => {
        this.isSaving.set(false);
        this.snackBar.open(`Recipe "${saved.title}" saved!`, 'Close', { duration: 3000 });
        this.router.navigate(['/recipes', saved.id]);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isSaving.set(false);
        this.errorMessage.set(err?.error?.detail || err?.error?.title || 'Failed to save recipe.');
      },
    });
  }
}
