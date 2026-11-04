import { Component, computed, inject, signal } from '@angular/core';
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
import { DraftMealPlanResponseDto } from '../../../core/models/ai-helper.models';
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

  readonly isLoading = signal<boolean>(false);
  readonly isSubmitting = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);

  readonly draftResponse = signal<DraftMealPlanResponseDto | null>(null);
  readonly candidateItems = signal<DraftMealCandidateFormItem[]>([]);

  readonly selectedCount = computed(
    () => this.candidateItems().filter((item) => item.selected).length,
  );

  selectPreset(preset: string): void {
    this.prompt.set(preset);
  }

  submitDraft(): void {
    const promptText = this.prompt().trim();
    if (!promptText) return;

    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.aiHelper
      .draftWeeklyMealPlan({
        weekStartDate: this.data.weekStartDate,
        prompt: promptText,
        mealCount: this.mealCount(),
        defaultSlot: this.defaultSlot(),
        placeOnDeck: this.placeOnDeck(),
      })
      .subscribe({
        next: (response) => {
          this.draftResponse.set(response);
          const items: DraftMealCandidateFormItem[] = response.candidates.map((c, idx) => {
            const scheduledDate = c.scheduledDate || this.data.weekStartDate;
            return {
              id: `${c.recipeId}-${idx}`,
              selected: true,
              recipeId: c.recipeId,
              recipe: c.recipe,
              scheduledDate,
              scheduledDateObj: coerceToDateLocal(scheduledDate),
              slot: (c.slot as MealSlot) || this.defaultSlot(),
              targetServings: c.targetServings || c.recipe.baseServings || 4,
              isOnDeck: c.isOnDeck || this.placeOnDeck(),
              rationale: c.rationale,
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

  toggleCandidate(id: string): void {
    this.candidateItems.update((items) =>
      items.map((item) => (item.id === id ? { ...item, selected: !item.selected } : item)),
    );
  }

  updateScheduledDate(id: string, date: Date | string | null): void {
    const iso = coerceToIsoDateLocal(date);
    if (!iso) return;
    const dateObj = coerceToDateLocal(iso);
    this.candidateItems.update((items) =>
      items.map((item) =>
        item.id === id ? { ...item, scheduledDate: iso, scheduledDateObj: dateObj } : item,
      ),
    );
  }

  updateSlot(id: string, slot: MealSlot): void {
    this.candidateItems.update((items) =>
      items.map((item) => (item.id === id ? { ...item, slot } : item)),
    );
  }

  updateServings(id: string, targetServings: number): void {
    const valid = Math.max(1, Math.min(50, targetServings || 1));
    this.candidateItems.update((items) =>
      items.map((item) => (item.id === id ? { ...item, targetServings: valid } : item)),
    );
  }

  applySelectedMeals(): void {
    const selected = this.candidateItems().filter((item) => item.selected);
    if (selected.length === 0) return;

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const entries: CreateMealPlanEntryRequest[] = selected.map((item) => ({
      entryType: 'Recipe',
      isOnDeck: item.isOnDeck,
      weekStartDate: this.data.weekStartDate,
      scheduledDate: item.isOnDeck ? null : item.scheduledDate,
      slot: item.isOnDeck ? null : item.slot,
      recipeId: item.recipeId,
      targetServings: item.targetServings,
    }));

    this.mealPlanService
      .applyDraftEntries({
        weekStartDate: this.data.weekStartDate,
        entries,
      })
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
