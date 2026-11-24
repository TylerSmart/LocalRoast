import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { merge } from 'rxjs';
import {
  DuplicateMealPolicy,
  GenerateFromMealPlanRequest,
  NEW_SHOPPING_LIST_ID,
  ShoppingListGenerationPreviewDto,
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import {
  addDaysIso,
  buildDefaultWeeklyShoppingListName,
  coerceToDateLocal,
  coerceToIsoDateLocal,
  getTodayIsoLocal,
  normalizeToSundayIso,
  shouldUseTouchDatepicker,
} from '../../../core/utils/meal-plan-date';

export { NEW_SHOPPING_LIST_ID };

export interface GenerateShoppingListDialogData {
  targetShoppingListId?: string;
  startDate?: string;
  endDate?: string;
}

@Component({
  selector: 'app-generate-shopping-list-dialog',
  templateUrl: './generate-shopping-list-dialog.component.html',
  styleUrl: './generate-shopping-list-dialog.component.scss',
  providers: [provideNativeDateAdapter()],
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatDatepickerModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
    MatRadioModule,
    MatExpansionModule,
    MatProgressSpinnerModule,
    MatButtonToggleModule,
  ],
})
export class GenerateShoppingListDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly dialogRef = inject(MatDialogRef<GenerateShoppingListDialogComponent>);
  readonly data = inject<GenerateShoppingListDialogData>(MAT_DIALOG_DATA, { optional: true });
  private readonly shoppingService = inject(ShoppingService);
  private readonly destroyRef = inject(DestroyRef);

  readonly newListSentinel = NEW_SHOPPING_LIST_ID;
  readonly shoppingLists = signal<ShoppingListSummaryDto[]>([]);
  readonly useTouchDatepicker = shouldUseTouchDatepicker();
  readonly preview = signal<ShoppingListGenerationPreviewDto | null>(null);
  readonly isLoadingLists = signal(true);
  readonly isPreviewing = signal(false);
  readonly isGenerating = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly promotedStaples = signal<Set<string>>(new Set());
  readonly selectedTargetListId = signal<string>(NEW_SHOPPING_LIST_ID);
  readonly isCreatingNewList = computed(() => this.selectedTargetListId() === NEW_SHOPPING_LIST_ID);
  private customListNameEdited = false;

  readonly form = this.fb.nonNullable.group({
    targetShoppingListId: [NEW_SHOPPING_LIST_ID, [Validators.required]],
    newListName: [''],
    startDate: [coerceToDateLocal(getTodayIsoLocal()) as Date | string, [Validators.required]],
    endDate: [coerceToDateLocal(getTodayIsoLocal()) as Date | string, [Validators.required]],
    includeOnDeck: [true],
    includeSubRecipes: [true],
    includeOptionalIngredients: [true],
    duplicatePolicy: ['SkipAlreadyImported' as DuplicateMealPolicy],
  });

  ngOnInit(): void {
    const today = getTodayIsoLocal();
    const startIso = this.data?.startDate || normalizeToSundayIso(today);
    const endIso = this.data?.endDate || addDaysIso(startIso, 6);

    this.form.patchValue(
      {
        startDate: coerceToDateLocal(startIso),
        endDate: coerceToDateLocal(endIso),
        newListName: this.buildDefaultWeeklyListName(startIso, []),
      },
      { emitEvent: false },
    );

    this.loadLists();

    this.form.controls.targetShoppingListId.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((value) => {
        this.selectedTargetListId.set(value);
        if (
          value === NEW_SHOPPING_LIST_ID &&
          !this.customListNameEdited &&
          !this.form.controls.newListName.value.trim()
        ) {
          const currentStart = coerceToIsoDateLocal(this.form.controls.startDate.value);
          this.form.patchValue(
            { newListName: this.buildDefaultWeeklyListName(currentStart, this.shoppingLists()) },
            { emitEvent: false },
          );
        }
      });

    this.form.controls.startDate.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((newStart) => {
        if (this.isCreatingNewList() && !this.customListNameEdited) {
          const startStr = coerceToIsoDateLocal(newStart);
          this.form.patchValue(
            { newListName: this.buildDefaultWeeklyListName(startStr, this.shoppingLists()) },
            { emitEvent: false },
          );
        }
      });

    merge(
      this.form.controls.targetShoppingListId.valueChanges,
      this.form.controls.startDate.valueChanges,
      this.form.controls.endDate.valueChanges,
      this.form.controls.includeOnDeck.valueChanges,
      this.form.controls.includeSubRecipes.valueChanges,
      this.form.controls.includeOptionalIngredients.valueChanges,
      this.form.controls.duplicatePolicy.valueChanges,
    )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.refreshPreview();
      });
  }

  private loadLists(): void {
    this.isLoadingLists.set(true);
    this.shoppingService.getShoppingLists().subscribe({
      next: (lists) => {
        this.shoppingLists.set(lists);
        this.isLoadingLists.set(false);

        const requestedId = this.data?.targetShoppingListId;
        const initialListId =
          requestedId && lists.some((l) => l.id === requestedId)
            ? requestedId
            : NEW_SHOPPING_LIST_ID;

        const currentStart = coerceToIsoDateLocal(this.form.controls.startDate.value);
        const defaultName = this.buildDefaultWeeklyListName(currentStart, lists);

        this.selectedTargetListId.set(initialListId);
        this.form.patchValue(
          {
            targetShoppingListId: initialListId,
            newListName: this.customListNameEdited
              ? this.form.controls.newListName.value
              : defaultName,
          },
          { emitEvent: false },
        );
        this.refreshPreview();
      },
      error: () => {
        this.isLoadingLists.set(false);
        this.errorMessage.set('Failed to load shopping lists.');
      },
    });
  }

  onNewListNameInput(): void {
    this.customListNameEdited = true;
  }

  buildDefaultWeeklyListName(startIso: string, existingLists: ShoppingListSummaryDto[]): string {
    return buildDefaultWeeklyShoppingListName(startIso, existingLists);
  }

  refreshPreview(): void {
    const val = this.form.getRawValue();
    const startDateStr = coerceToIsoDateLocal(val.startDate);
    const endDateStr = coerceToIsoDateLocal(val.endDate);
    if (!val.targetShoppingListId || !startDateStr || !endDateStr) {
      return;
    }

    this.isPreviewing.set(true);
    this.errorMessage.set(null);

    const request: GenerateFromMealPlanRequest = {
      startDate: startDateStr,
      endDate: endDateStr,
      includeOnDeck: val.includeOnDeck ?? true,
      includeSubRecipes: val.includeSubRecipes ?? true,
      includeOptionalIngredients: val.includeOptionalIngredients ?? true,
      duplicatePolicy: val.duplicatePolicy ?? 'SkipAlreadyImported',
      promotePantryFoodItemIds: Array.from(this.promotedStaples()),
    };

    const previewObs =
      val.targetShoppingListId === NEW_SHOPPING_LIST_ID
        ? this.shoppingService.previewGenerateFromNewList(request)
        : this.shoppingService.previewGenerateFromMealPlan(val.targetShoppingListId, request);

    previewObs.subscribe({
      next: (res) => {
        this.preview.set(res);
        this.isPreviewing.set(false);
      },
      error: (err) => {
        this.isPreviewing.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to calculate preview.',
        );
      },
    });
  }

  togglePromoteStaple(foodItemId: string | null | undefined): void {
    if (!foodItemId) return;

    this.promotedStaples.update((set) => {
      const next = new Set(set);
      if (next.has(foodItemId)) {
        next.delete(foodItemId);
      } else {
        next.add(foodItemId);
      }
      return next;
    });

    this.refreshPreview();
  }

  onSubmit(): void {
    if (this.form.invalid || this.isGenerating()) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    const creatingNew = val.targetShoppingListId === NEW_SHOPPING_LIST_ID;
    const newListName = val.newListName.trim();
    if (creatingNew && !newListName) {
      this.errorMessage.set('Please enter a name for the new shopping list.');
      return;
    }

    this.isGenerating.set(true);
    this.errorMessage.set(null);

    const request: GenerateFromMealPlanRequest = {
      startDate: coerceToIsoDateLocal(val.startDate),
      endDate: coerceToIsoDateLocal(val.endDate),
      includeOnDeck: val.includeOnDeck ?? true,
      includeSubRecipes: val.includeSubRecipes ?? true,
      includeOptionalIngredients: val.includeOptionalIngredients ?? true,
      duplicatePolicy: val.duplicatePolicy ?? 'SkipAlreadyImported',
      promotePantryFoodItemIds: Array.from(this.promotedStaples()),
    };

    const generateObs = creatingNew
      ? this.shoppingService.createAndGenerateFromMealPlan({
          name: newListName,
          isPinned: false,
          generation: request,
        })
      : this.shoppingService.generateFromMealPlan(val.targetShoppingListId, request);

    generateObs.subscribe({
      next: (result) => {
        this.dialogRef.close(result);
      },
      error: (err) => {
        this.isGenerating.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to generate shopping list items.',
        );
      },
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
