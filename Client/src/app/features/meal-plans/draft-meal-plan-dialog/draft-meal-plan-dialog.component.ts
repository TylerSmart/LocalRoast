import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  DraftMealPlanResponseDto,
  DraftWeeklyMealPlanRequest,
} from '../../../core/models/ai-helper.models';
import {
  CreateMealPlanEntryRequest,
  MEAL_SLOTS,
  MealPlanRecipeBadgeDto,
  MealSlot,
} from '../../../core/models/meal-plan.models';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import {
  coerceToDateLocal,
  coerceToIsoDateLocal,
  addDaysIso,
  getTodayIsoLocal,
  shouldUseTouchDatepicker,
} from '../../../core/utils/meal-plan-date';

export interface DraftMealPlanDialogData {
  weekStartDate: string;
}

export interface DraftMealCandidateFormItem {
  id: string;
  selected: boolean;
  recipeId: string;
  recipe: MealPlanRecipeBadgeDto;
  scheduledDate: string;
  scheduledDateObj: Date;
  slot: MealSlot;
  targetServings: number;
  isOnDeck: boolean;
  rationale: string;
  leftoverDates: string[];
  servingsPerMeal: number;
  hasInvalidDate: boolean;
  hasInvalidServings: boolean;
}

@Component({
  selector: 'app-draft-meal-plan-dialog',
  providers: [provideNativeDateAdapter()],
  imports: [
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatDatepickerModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
    MatChipsModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './draft-meal-plan-dialog.component.html',
  styleUrl: './draft-meal-plan-dialog.component.scss',
})
export class DraftMealPlanDialogComponent {
  readonly dialogRef = inject(MatDialogRef<DraftMealPlanDialogComponent>);
  readonly data: DraftMealPlanDialogData = inject(MAT_DIALOG_DATA);
  private readonly aiHelper = inject(AiKitchenHelperService);
  private readonly mealPlanService = inject(MealPlanService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private lastDraftRequest: DraftWeeklyMealPlanRequest | null = null;

  readonly slots = MEAL_SLOTS;
  readonly useTouchDatepicker = shouldUseTouchDatepicker();
  readonly presets = [
    'Quick weeknight dinners (<30 min)',
    'High-protein family meals',
    'Use up fresh produce / pantry items',
    'Cozy comfort food',
  ];

  readonly prompt = signal<string>('');
  readonly mealCount = signal<number>(4);
  readonly defaultSlot = signal<MealSlot>('Dinner');
  readonly placeOnDeck = signal<boolean>(false);
  readonly servingsPerMeal = signal<number>(4);
  readonly minDate = computed(() => {
    const today = getTodayIsoLocal();
    const start = this.draftResponse()?.weekStartDate ?? today;
    return coerceToDateLocal(start > today ? start : today);
  });
  readonly retryingCandidateId = signal<string | null>(null);

  readonly isLoading = signal<boolean>(false);
  readonly isSubmitting = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);

  readonly draftResponse = signal<DraftMealPlanResponseDto | null>(null);
  readonly candidateItems = signal<DraftMealCandidateFormItem[]>([]);

  readonly selectedCount = computed(
    () => this.candidateItems().filter((item) => item.selected).length,
  );
  readonly selectedMealCount = computed(() =>
    this.candidateItems()
      .filter((item) => item.selected)
      .reduce((count, item) => count + 1 + item.leftoverDates.length, 0),
  );
  readonly hasInvalidSelection = computed(() =>
    this.candidateItems().some(
      (item) => item.selected && (item.hasInvalidDate || item.hasInvalidServings),
    ),
  );
  readonly isBusy = computed(
    () => this.isLoading() || this.isSubmitting() || this.retryingCandidateId() !== null,
  );

  selectPreset(preset: string): void {
    this.prompt.set(preset);
  }

  submitDraft(): void {
    const promptText = this.prompt().trim();
    if (!promptText || this.isBusy()) return;

    this.isLoading.set(true);
    this.errorMessage.set(null);

    const today = getTodayIsoLocal();
    const request: DraftWeeklyMealPlanRequest = {
      weekStartDate: this.data.weekStartDate < today ? today : this.data.weekStartDate,
      prompt: promptText,
      mealCount: this.mealCount(),
      defaultSlot: this.defaultSlot(),
      placeOnDeck: this.placeOnDeck(),
      servingsPerMeal: this.servingsPerMeal(),
      todayDate: today,
    };
    this.aiHelper
      .draftWeeklyMealPlan(request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.lastDraftRequest = request;
          this.draftResponse.set(response);
          const items: DraftMealCandidateFormItem[] = response.candidates.map((c, idx) => {
            const scheduledDate = c.scheduledDate ?? response.weekStartDate;
            const leftoverDates = c.leftoverDates ?? [];
            return {
              id: `${c.recipeId}-${idx}`,
              selected: true,
              recipeId: c.recipeId,
              recipe: c.recipe,
              scheduledDate,
              scheduledDateObj: coerceToDateLocal(scheduledDate),
              slot: c.slot ?? request.defaultSlot ?? 'Dinner',
              targetServings: c.targetServings,
              isOnDeck: c.isOnDeck,
              rationale: c.rationale,
              leftoverDates,
              servingsPerMeal: c.targetServings / (1 + leftoverDates.length),
              hasInvalidDate: false,
              hasInvalidServings: false,
            };
          });
          this.candidateItems.set(items);
          this.isLoading.set(false);
        },
        error: (err) => {
          this.errorMessage.set(
            err?.error?.detail || err?.message || 'Failed to generate draft meal plan.',
          );
          this.isLoading.set(false);
        },
      });
  }

  retryCandidate(id: string): void {
    const item = this.candidateItems().find((candidate) => candidate.id === id);
    if (!item || !this.lastDraftRequest || this.isBusy()) return;
    this.retryingCandidateId.set(id);
    this.errorMessage.set(null);
    this.aiHelper
      .draftWeeklyMealPlan({
        ...this.lastDraftRequest,
        todayDate: getTodayIsoLocal(),
        mealCount: 1,
        placeOnDeck: true,
        excludedRecipeIds: this.candidateItems().map((candidate) => candidate.recipeId),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          const replacement = response.candidates[0];
          if (!replacement) {
            this.errorMessage.set(
              'The AI returned no replacement. Your current suggestions are unchanged.',
            );
          } else {
            this.candidateItems.update((items) =>
              items.map((candidate) =>
                candidate.id === id
                  ? {
                      ...candidate,
                      recipeId: replacement.recipeId,
                      recipe: replacement.recipe,
                      rationale: replacement.rationale,
                    }
                  : candidate,
              ),
            );
          }
          this.retryingCandidateId.set(null);
        },
        error: (err) => {
          this.errorMessage.set(
            err?.error?.detail || err?.message || 'Failed to retry this suggestion.',
          );
          this.retryingCandidateId.set(null);
        },
      });
  }

  maxCookingDate(item: DraftMealCandidateFormItem): Date {
    return coerceToDateLocal(
      item.leftoverDates.length
        ? addDaysIso(item.leftoverDates[0], -1)
        : this.draftResponse()?.weekEndDate,
    );
  }

  toggleCandidate(id: string): void {
    if (this.isBusy()) return;
    this.candidateItems.update((items) =>
      items.map((item) => (item.id === id ? { ...item, selected: !item.selected } : item)),
    );
  }

  updateScheduledDate(id: string, date: Date | string | null): void {
    if (this.isBusy()) return;
    const iso = coerceToIsoDateLocal(date);
    const item = this.candidateItems().find((candidate) => candidate.id === id);
    if (!item) return;
    if (
      !date ||
      (date instanceof Date && Number.isNaN(date.getTime())) ||
      iso < coerceToIsoDateLocal(this.minDate()) ||
      iso > coerceToIsoDateLocal(this.maxCookingDate(item))
    ) {
      this.errorMessage.set(
        'Choose today or a future date before the planned leftovers, within this seven-day period.',
      );
      this.candidateItems.update((items) =>
        items.map((candidate) =>
          candidate.id === id ? { ...candidate, hasInvalidDate: true } : candidate,
        ),
      );
      return;
    }
    this.errorMessage.set(null);
    const dateObj = coerceToDateLocal(iso);
    this.candidateItems.update((items) =>
      items.map((item) =>
        item.id === id
          ? { ...item, scheduledDate: iso, scheduledDateObj: dateObj, hasInvalidDate: false }
          : item,
      ),
    );
  }

  updateSlot(id: string, slot: MealSlot): void {
    if (this.isBusy()) return;
    this.candidateItems.update((items) =>
      items.map((item) => (item.id === id ? { ...item, slot } : item)),
    );
  }

  updateServings(id: string, servingsPerMeal: number): void {
    if (this.isBusy()) return;
    const item = this.candidateItems().find((candidate) => candidate.id === id);
    if (!item) return;
    if (
      !Number.isFinite(servingsPerMeal) ||
      servingsPerMeal < 0.25 ||
      servingsPerMeal * (1 + item.leftoverDates.length) > 999
    ) {
      this.errorMessage.set(
        'Enter valid servings per meal (at least 0.25 and no more than 999 total cooking servings).',
      );
      this.candidateItems.update((items) =>
        items.map((candidate) =>
          candidate.id === id ? { ...candidate, hasInvalidServings: true } : candidate,
        ),
      );
      return;
    }
    this.errorMessage.set(null);
    this.candidateItems.update((items) =>
      items.map((candidate) =>
        candidate.id === id
          ? {
              ...candidate,
              servingsPerMeal,
              targetServings: servingsPerMeal * (1 + candidate.leftoverDates.length),
              hasInvalidServings: false,
            }
          : candidate,
      ),
    );
  }

  applySelectedMeals(): void {
    const selected = this.candidateItems().filter((item) => item.selected);
    if (selected.length === 0 || this.isBusy()) return;
    if (this.hasInvalidSelection()) {
      this.errorMessage.set(
        'Correct the invalid dates or servings before adding the selected meals.',
      );
      return;
    }
    if (selected.some((item) => !item.isOnDeck && item.scheduledDate < getTodayIsoLocal())) {
      this.errorMessage.set(
        'Drafted meals cannot be applied to past dates. Update their dates or generate a new draft.',
      );
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const rangeStart = this.draftResponse()?.weekStartDate ?? this.data.weekStartDate;
    const entries: CreateMealPlanEntryRequest[] = [];
    for (const item of selected) {
      const sourceIndex = entries.length;
      entries.push({
        entryType: 'Recipe',
        isOnDeck: item.isOnDeck,
        weekStartDate: rangeStart,
        scheduledDate: item.isOnDeck ? null : item.scheduledDate,
        slot: item.isOnDeck ? null : item.slot,
        recipeId: item.recipeId,
        targetServings: item.targetServings,
      });
      for (const date of item.leftoverDates) {
        entries.push({
          entryType: 'Leftovers',
          isOnDeck: false,
          scheduledDate: date,
          slot: item.slot,
          recipeId: item.recipeId,
          targetServings: item.servingsPerMeal,
          sourceDraftEntryIndex: sourceIndex,
          notes:
            date > addDaysIso(item.scheduledDate, 3)
              ? 'Freeze these portions after cooking and thaw safely for this meal.'
              : null,
        });
      }
    }

    this.mealPlanService
      .applyDraftEntries({
        weekStartDate: rangeStart,
        entries,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (week) => {
          this.snackBar.open(
            `Added ${entries.length} meal${entries.length === 1 ? '' : 's'} to plan!`,
            'Dismiss',
            { duration: 3000 },
          );
          this.dialogRef.close(week);
        },
        error: (err) => {
          this.errorMessage.set(
            err?.error?.detail || err?.message || 'Failed to apply meal plan entries.',
          );
          this.isSubmitting.set(false);
        },
      });
  }

  close(): void {
    this.dialogRef.close();
  }
}
