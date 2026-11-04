import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatChipsModule } from '@angular/material/chips';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import {
  CreateMealPlanEntryRequest,
  MEAL_SLOTS,
  MealPlanEntryDto,
  MealPlanEntryType,
  MealSlot,
  UpdateMealPlanEntryRequest,
} from '../../../core/models/meal-plan.models';
import { RecipeSummaryDto } from '../../../core/models/recipe.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import {
  SearchableSelectComponent,
  SearchableSelectOption,
} from '../../../shared/components/searchable-select/searchable-select.component';
import {
  coerceToDateLocal,
  coerceToIsoDateLocal,
  getTodayIsoLocal,
  normalizeToSundayIso,
  shouldUseTouchDatepicker,
} from '../../../core/utils/meal-plan-date';

export interface MealPlanEntryDialogData {
  entry?: MealPlanEntryDto;
  initialDate?: string;
  initialSlot?: MealSlot;
  initialIsOnDeck?: boolean;
  initialWeekStartDate?: string;
  initialEntryType?: MealPlanEntryType;
  preselectedRecipe?: {
    id: string;
    title: string;
    baseServings: number;
    thumbnailImagePath?: string | null;
  };
  initialTargetServings?: number;
  weekScheduledEntries?: MealPlanEntryDto[];
}

@Component({
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatDatepickerModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonToggleModule,
    MatProgressSpinnerModule,
    MatChipsModule,
    SearchableSelectComponent,
  ],
  providers: [provideNativeDateAdapter()],
  selector: 'app-meal-plan-entry-dialog',
  templateUrl: './meal-plan-entry-dialog.component.html',
  styleUrl: './meal-plan-entry-dialog.component.scss',
})
export class MealPlanEntryDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly dialogRef = inject(MatDialogRef<MealPlanEntryDialogComponent>);
  protected readonly data: MealPlanEntryDialogData = inject(MAT_DIALOG_DATA) || {};
  private readonly mealPlanService = inject(MealPlanService);
  private readonly recipeService = inject(RecipeService);

  protected readonly mealSlots = MEAL_SLOTS;
  protected readonly useTouchDatepicker = shouldUseTouchDatepicker();
  protected readonly isEdit = !!this.data.entry;
  protected readonly isSubmitting = signal(false);
  protected readonly isLoadingRecipes = signal(false);
  protected readonly recipes = signal<RecipeSummaryDto[]>([]);
  protected readonly recipeSearchTerm = signal('');
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly recipeOptions = computed<SearchableSelectOption[]>(() =>
    this.recipes().map((r) => ({
      value: r.id,
      label: r.title,
      sublabel: `${r.baseServings} servings`,
    })),
  );

  protected readonly preselectedRecipeOption = computed<SearchableSelectOption | null>(() => {
    if (this.data.preselectedRecipe) {
      return {
        value: this.data.preselectedRecipe.id,
        label: this.data.preselectedRecipe.title,
        sublabel: `${this.data.preselectedRecipe.baseServings} servings`,
      };
    }
    if (this.data.entry?.recipe) {
      return {
        value: this.data.entry.recipe.id,
        label: this.data.entry.recipe.title,
        sublabel: `${this.data.entry.recipe.baseServings} servings`,
      };
    }
    return null;
  });

  protected readonly recipeSearchFn = (query: string): Observable<SearchableSelectOption[]> => {
    return this.recipeService.getRecipes({ search: query }).pipe(
      map((list) => {
        this.recipes.update((existing) => {
          const map = new Map(existing.map((e) => [e.id, e]));
          for (const item of list) {
            map.set(item.id, item);
          }
          return Array.from(map.values());
        });
        return list.map((r) => ({
          value: r.id,
          label: r.title,
          sublabel: `${r.baseServings} servings`,
        }));
      }),
    );
  };

  protected readonly quickNoteChips = [
    'Date Night / Eating Out',
    'Order Takeout',
    'Leftover Buffet',
    'Meal Prep Session',
    'Freezer Dinner',
  ];

  protected readonly form = this.fb.group({
    entryType: this.fb.control<MealPlanEntryType>('Recipe', { nonNullable: true }),
    placement: this.fb.control<'scheduled' | 'ondeck'>('scheduled', { nonNullable: true }),
    scheduledDate: this.fb.control<Date | string>(coerceToDateLocal(getTodayIsoLocal()), {
      nonNullable: true,
    }),
    slot: this.fb.control<MealSlot>('Dinner', { nonNullable: true }),
    weekStartDate: this.fb.control<string>(normalizeToSundayIso(getTodayIsoLocal()), {
      nonNullable: true,
    }),
    recipeId: this.fb.control<string | null>(null),
    targetServings: this.fb.control<number | null>(4, [Validators.min(0.25), Validators.max(999)]),
    sourceMealPlanEntryId: this.fb.control<string | null>(null),
    customTitle: this.fb.control<string>(''),
    notes: this.fb.control<string>(''),
  });

  ngOnInit(): void {
    this.initFormValues();
    this.loadRecipes();
  }

  protected getSelectedSlotMeta(): { value: MealSlot; label: string; icon: string } {
    const current = this.form.controls.slot.value;
    return this.mealSlots.find((s) => s.value === current) ?? this.mealSlots[2];
  }

  private initFormValues(): void {
    if (this.data.entry) {
      const e = this.data.entry;
      this.form.patchValue({
        entryType: e.entryType,
        placement: e.isOnDeck ? 'ondeck' : 'scheduled',
        scheduledDate: coerceToDateLocal(e.scheduledDate ?? getTodayIsoLocal()),
        slot: e.slot ?? 'Dinner',
        weekStartDate: e.weekStartDate,
        recipeId: e.recipe?.id ?? null,
        targetServings: e.targetServings,
        sourceMealPlanEntryId: e.sourceMealPlanEntryId,
        customTitle: e.customTitle ?? '',
        notes: e.notes ?? '',
      });
    } else {
      const initialEntryType = this.data.initialEntryType ?? 'Recipe';
      const initialPlacement = this.data.initialIsOnDeck ? 'ondeck' : 'scheduled';
      const initialDate = this.data.initialDate ?? getTodayIsoLocal();
      const initialWeekStart = this.data.initialWeekStartDate ?? normalizeToSundayIso(initialDate);

      this.form.patchValue({
        entryType: initialEntryType,
        placement: initialPlacement,
        scheduledDate: coerceToDateLocal(initialDate),
        slot: this.data.initialSlot ?? 'Dinner',
        weekStartDate: initialWeekStart,
        recipeId: this.data.preselectedRecipe?.id ?? null,
        targetServings:
          this.data.initialTargetServings ?? this.data.preselectedRecipe?.baseServings ?? 4,
      });
    }
  }

  private loadRecipes(): void {
    this.isLoadingRecipes.set(true);
    this.recipeService.getRecipes({}).subscribe({
      next: (list) => {
        this.recipes.set(list);
        this.isLoadingRecipes.set(false);
      },
      error: () => {
        this.isLoadingRecipes.set(false);
      },
    });
  }

  protected onRecipeSelected(recipeId: string | null): void {
    if (!recipeId) return;
    const found = this.recipes().find((r) => r.id === recipeId);
    if (found) {
      this.form.patchValue({ targetServings: found.baseServings });
    }
  }

  protected onLeftoverSourceSelected(sourceId: string | null): void {
    if (!sourceId) return;
    const source = this.data.weekScheduledEntries?.find((e) => e.id === sourceId);
    if (source) {
      this.form.patchValue({
        recipeId: source.recipe?.id ?? null,
        targetServings: source.targetServings,
        customTitle: `Leftovers: ${source.displayTitle}`,
      });
    }
  }

  protected selectQuickNote(chipText: string): void {
    this.form.patchValue({ customTitle: chipText });
  }

  protected adjustServings(delta: number): void {
    const current = this.form.controls.targetServings.value ?? 4;
    const next = Math.max(0.5, Math.round((current + delta) * 2) / 2);
    this.form.patchValue({ targetServings: next });
  }

  protected isFormInvalid(): boolean {
    const val = this.form.getRawValue();
    if (val.placement === 'scheduled' && !val.scheduledDate) {
      return true;
    }
    if (val.entryType === 'Recipe') {
      if (!val.recipeId) return true;
      if (val.targetServings == null || val.targetServings < 0.25 || val.targetServings > 999) {
        return true;
      }
    } else if (val.entryType === 'FreeformNote') {
      if (!val.customTitle?.trim()) return true;
    } else if (val.entryType === 'Leftovers') {
      if (!val.customTitle?.trim() && !val.recipeId) return true;
    }
    return false;
  }

  protected onSubmit(): void {
    if (this.isFormInvalid()) return;

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const val = this.form.getRawValue();
    const isOnDeck = val.placement === 'ondeck';
    const scheduledDateStr = coerceToIsoDateLocal(val.scheduledDate);
    const weekStartDate = isOnDeck
      ? normalizeToSundayIso(val.weekStartDate || scheduledDateStr)
      : normalizeToSundayIso(scheduledDateStr);

    if (this.data.entry) {
      const updateReq: UpdateMealPlanEntryRequest = {
        entryType: val.entryType,
        isOnDeck,
        weekStartDate,
        scheduledDate: isOnDeck ? null : scheduledDateStr,
        slot: isOnDeck ? null : val.slot,
        recipeId: val.entryType === 'FreeformNote' ? null : val.recipeId,
        targetServings: val.entryType === 'FreeformNote' ? null : val.targetServings,
        sourceMealPlanEntryId: val.entryType === 'Leftovers' ? val.sourceMealPlanEntryId : null,
        customTitle: val.customTitle ? val.customTitle.trim() : null,
        notes: val.notes ? val.notes.trim() : null,
      };

      this.mealPlanService.updateEntry(this.data.entry.id, updateReq).subscribe({
        next: (saved) => {
          this.isSubmitting.set(false);
          this.dialogRef.close(saved);
        },
        error: (err) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to update meal plan entry.',
          );
        },
      });
    } else {
      const createReq: CreateMealPlanEntryRequest = {
        entryType: val.entryType,
        isOnDeck,
        weekStartDate,
        scheduledDate: isOnDeck ? null : scheduledDateStr,
        slot: isOnDeck ? null : val.slot,
        recipeId: val.entryType === 'FreeformNote' ? null : val.recipeId,
        targetServings: val.entryType === 'FreeformNote' ? null : val.targetServings,
        sourceMealPlanEntryId: val.entryType === 'Leftovers' ? val.sourceMealPlanEntryId : null,
        customTitle: val.customTitle ? val.customTitle.trim() : null,
        notes: val.notes ? val.notes.trim() : null,
      };

      this.mealPlanService.createEntry(createReq).subscribe({
        next: (saved) => {
          this.isSubmitting.set(false);
          this.dialogRef.close(saved);
        },
        error: (err) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(err?.error?.detail || err?.error?.title || 'Failed to plan meal.');
        },
      });
    }
  }

  protected onCancel(): void {
    this.dialogRef.close();
  }
}
