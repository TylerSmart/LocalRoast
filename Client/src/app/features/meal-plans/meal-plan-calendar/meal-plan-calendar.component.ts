import {
  CDK_DRAG_CONFIG,
  CdkDrag,
  CdkDragDrop,
  CdkDropList,
  CdkDropListGroup,
  DragDropConfig,
  DragStartDelay,
} from '@angular/cdk/drag-drop';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import {
  MEAL_SLOTS,
  MealPlanDayDto,
  MealPlanEntryDto,
  MealPlanRangeResponseDto,
  MealPlanWeekDto,
  MealSlot,
} from '../../../core/models/meal-plan.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import {
  DAY_NAMES_SHORT,
  MonthGridDay,
  addDaysIso,
  formatDayHeader,
  formatMonthYearLabel,
  formatWeekRangeLabel,
  getMonthGrid,
  getTodayIsoLocal,
  normalizeToSundayIso,
} from '../../../core/utils/meal-plan-date';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { ShoppingListGenerationResultDto } from '../../../core/models/shopping.models';
import { GenerateShoppingListDialogComponent } from '../../shopping/generate-shopping-list-dialog/generate-shopping-list-dialog.component';
import {
  MealPlanEntryDialogComponent,
  MealPlanEntryDialogData,
} from '../meal-plan-entry-dialog/meal-plan-entry-dialog.component';
import {
  MealPlanTemplatesDialogComponent,
  MealPlanTemplatesDialogData,
} from '../meal-plan-templates-dialog/meal-plan-templates-dialog.component';
import { CookLogDialogComponent } from '../../recipes/cook-log-dialog/cook-log-dialog.component';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import {
  DraftMealPlanDialogComponent,
  DraftMealPlanDialogData,
} from '../draft-meal-plan-dialog/draft-meal-plan-dialog.component';

export interface DropZoneData {
  isOnDeck: boolean;
  date?: string;
  slot?: MealSlot;
  weekStartDate?: string;
}

export const MEAL_PLAN_DRAG_CONFIG: DragDropConfig = {
  dragStartDelay: {
    touch: 250,
    mouse: 0,
  },
};

@Component({
  imports: [
    RouterLink,
    CdkDropListGroup,
    CdkDropList,
    CdkDrag,
    MatCardModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatChipsModule,
    MatDividerModule,
    MatMenuModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MatExpansionModule,
  ],
  providers: [
    {
      provide: CDK_DRAG_CONFIG,
      useValue: MEAL_PLAN_DRAG_CONFIG,
    },
  ],
  selector: 'app-meal-plan-calendar',
  templateUrl: './meal-plan-calendar.component.html',
  styleUrl: './meal-plan-calendar.component.scss',
})
export class MealPlanCalendarComponent implements OnInit {
  private readonly mealPlanService = inject(MealPlanService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private viewLoadSubscription?: Subscription;
  protected readonly aiHelper = inject(AiKitchenHelperService);

  protected readonly dragStartDelay: DragStartDelay = { touch: 250, mouse: 0 };
  protected readonly mealSlots = MEAL_SLOTS;
  protected readonly dayNamesShort = DAY_NAMES_SHORT;
  protected readonly todayIso = getTodayIsoLocal();

  protected readonly viewMode = signal<'next7' | 'week' | 'month'>('next7');
  protected readonly activeRollingStart = signal<string>(getTodayIsoLocal());
  protected readonly activeWeekStart = signal<string>(normalizeToSundayIso(getTodayIsoLocal()));
  protected readonly activePeriodStart = computed(() =>
    this.viewMode() === 'next7' ? this.activeRollingStart() : this.activeWeekStart(),
  );
  protected readonly activeMonth = signal<{ year: number; monthIndex0: number }>({
    year: new Date().getFullYear(),
    monthIndex0: new Date().getMonth(),
  });

  protected readonly isLoading = signal<boolean>(true);
  protected readonly weekData = signal<MealPlanWeekDto | null>(null);
  protected readonly monthRangeData = signal<MealPlanRangeResponseDto | null>(null);
  protected readonly mobileOnDeckOpen = signal<boolean>(false);

  protected readonly periodTitle = computed(() => {
    if (this.viewMode() !== 'month') {
      return formatWeekRangeLabel(this.activePeriodStart());
    }
    const { year, monthIndex0 } = this.activeMonth();
    return formatMonthYearLabel(year, monthIndex0);
  });

  protected readonly monthGridDays = computed<MonthGridDay[]>(() => {
    const { year, monthIndex0 } = this.activeMonth();
    return getMonthGrid(year, monthIndex0);
  });

  protected readonly monthEntriesMap = computed(() => {
    const range = this.monthRangeData();
    const map = new Map<string, MealPlanEntryDto[]>();
    if (!range) return map;

    for (const entry of range.scheduledEntries) {
      if (!entry.scheduledDate) continue;
      if (!map.has(entry.scheduledDate)) {
        map.set(entry.scheduledDate, []);
      }
      map.get(entry.scheduledDate)!.push(entry);
    }
    return map;
  });

  protected readonly totalWeekMealsCount = computed(() => {
    const w = this.weekData();
    if (!w) return 0;
    const scheduled = w.days.reduce((acc, d) => acc + d.entries.length, 0);
    return scheduled + w.onDeckEntries.length;
  });

  ngOnInit(): void {
    this.aiHelper.checkStatus().subscribe();
    this.loadCurrentView();
  }

  loadCurrentView(): void {
    if (this.viewMode() === 'next7') {
      this.loadRolling();
    } else if (this.viewMode() === 'week') {
      this.loadWeek(this.activeWeekStart());
    } else {
      this.loadMonth();
    }
  }

  private loadRolling(): void {
    this.viewLoadSubscription?.unsubscribe();
    const start = this.activeRollingStart();
    const end = addDaysIso(start, 6);
    this.isLoading.set(true);
    this.viewLoadSubscription = this.mealPlanService
      .getRange(start, end, true)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (data) => {
          this.weekData.set({
            weekStartDate: start,
            weekEndDate: end,
            days: Array.from({ length: 7 }, (_, offset) => {
              const date = addDaysIso(start, offset);
              return {
                date,
                dayOffset: offset,
                entries: data.scheduledEntries.filter((entry) => entry.scheduledDate === date),
              };
            }),
            onDeckEntries: data.onDeckEntries,
          });
          this.isLoading.set(false);
        },
        error: (err) => {
          this.weekData.set(null);
          this.isLoading.set(false);
          this.snackBar.open(
            err?.error?.detail || 'Failed to load the seven-day meal plan.',
            'Close',
            {
              duration: 4000,
            },
          );
        },
      });
  }

  private loadWeek(weekMondayIso: string): void {
    this.viewLoadSubscription?.unsubscribe();
    this.isLoading.set(true);
    this.viewLoadSubscription = this.mealPlanService
      .getWeek(weekMondayIso)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (data) => {
          this.weekData.set(data);
          this.isLoading.set(false);
        },
        error: (err) => {
          this.weekData.set(null);
          this.isLoading.set(false);
          this.snackBar.open(
            err?.error?.detail || 'Failed to load the weekly meal plan.',
            'Close',
            {
              duration: 4000,
            },
          );
        },
      });
  }

  private loadMonth(): void {
    this.viewLoadSubscription?.unsubscribe();
    const grid = this.monthGridDays();
    if (grid.length === 0) return;

    this.isLoading.set(true);
    const start = grid[0].iso;
    const end = grid[grid.length - 1].iso;

    this.viewLoadSubscription = this.mealPlanService
      .getRange(start, end, true)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (data) => {
          this.monthRangeData.set(data);
          this.isLoading.set(false);
        },
        error: (err) => {
          this.monthRangeData.set(null);
          this.isLoading.set(false);
          this.snackBar.open(
            err?.error?.detail || 'Failed to load the monthly meal plan.',
            'Close',
            {
              duration: 4000,
            },
          );
        },
      });
  }

  protected setViewMode(mode: 'next7' | 'week' | 'month'): void {
    this.viewMode.set(mode);
    this.loadCurrentView();
  }

  protected navigatePeriod(delta: number): void {
    if (this.viewMode() === 'next7') {
      this.activeRollingStart.update((start) => addDaysIso(start, delta * 7));
      this.loadRolling();
    } else if (this.viewMode() === 'week') {
      const nextMonday = addDaysIso(this.activeWeekStart(), delta * 7);
      this.activeWeekStart.set(nextMonday);
      this.loadWeek(nextMonday);
    } else {
      let { year, monthIndex0 } = this.activeMonth();
      monthIndex0 += delta;
      if (monthIndex0 > 11) {
        monthIndex0 = 0;
        year++;
      } else if (monthIndex0 < 0) {
        monthIndex0 = 11;
        year--;
      }
      this.activeMonth.set({ year, monthIndex0 });
      this.loadMonth();
    }
  }

  protected goToToday(): void {
    const today = getTodayIsoLocal();
    this.activeRollingStart.set(today);
    this.activeWeekStart.set(normalizeToSundayIso(today));
    const now = new Date();
    this.activeMonth.set({ year: now.getFullYear(), monthIndex0: now.getMonth() });
    this.loadCurrentView();
  }

  protected selectMonthDay(isoDate: string): void {
    this.activeWeekStart.set(normalizeToSundayIso(isoDate));
    this.viewMode.set('week');
    this.loadCurrentView();
  }

  protected openAddEntryDialog(prefill?: {
    date?: string;
    slot?: MealSlot;
    isOnDeck?: boolean;
    entryType?: 'Recipe' | 'Leftovers' | 'FreeformNote';
  }): void {
    const week = this.weekData();
    const allWeekScheduled: MealPlanEntryDto[] = week ? week.days.flatMap((d) => d.entries) : [];

    const weekStart = this.activePeriodStart();
    const defaultDate =
      this.todayIso >= weekStart && this.todayIso <= addDaysIso(weekStart, 6)
        ? this.todayIso
        : weekStart;

    const dialogRef = this.dialog.open<
      MealPlanEntryDialogComponent,
      MealPlanEntryDialogData,
      MealPlanEntryDto
    >(MealPlanEntryDialogComponent, {
      width: '540px',
      maxWidth: '95vw',
      data: {
        initialDate: prefill?.date ?? defaultDate,
        initialSlot: prefill?.slot ?? 'Dinner',
        initialIsOnDeck: prefill?.isOnDeck ?? false,
        initialWeekStartDate: weekStart,
        initialEntryType: prefill?.entryType ?? 'Recipe',
        weekScheduledEntries: allWeekScheduled,
      },
    });

    dialogRef.afterClosed().subscribe((created) => {
      if (created) {
        this.snackBar.open(`Planned: ${created.displayTitle}`, 'Close', { duration: 3000 });
        this.loadCurrentView();
      }
    });
  }

  protected editEntry(entry: MealPlanEntryDto): void {
    const week = this.weekData();
    const allWeekScheduled: MealPlanEntryDto[] = week
      ? week.days.flatMap((d) => d.entries).filter((e) => e.id !== entry.id)
      : [];

    const dialogRef = this.dialog.open<
      MealPlanEntryDialogComponent,
      MealPlanEntryDialogData,
      MealPlanEntryDto
    >(MealPlanEntryDialogComponent, {
      width: '540px',
      maxWidth: '95vw',
      data: {
        entry,
        weekScheduledEntries: allWeekScheduled,
      },
    });

    dialogRef.afterClosed().subscribe((updated) => {
      if (updated) {
        this.snackBar.open(`Updated: ${updated.displayTitle}`, 'Close', { duration: 3000 });
        this.loadCurrentView();
      }
    });
  }

  protected scheduleLeftoversFrom(source: MealPlanEntryDto): void {
    const nextDay = source.scheduledDate ? addDaysIso(source.scheduledDate, 1) : this.todayIso;

    this.openAddEntryDialog({
      date: nextDay,
      slot: 'Lunch',
      isOnDeck: false,
      entryType: 'Leftovers',
    });
  }

  protected cookTonight(entry: MealPlanEntryDto): void {
    this.mealPlanService
      .cookTonight(entry.id, { targetDate: this.todayIso, slot: 'Dinner' })
      .subscribe({
        next: (cooked) => {
          this.snackBar.open(`Moved "${cooked.displayTitle}" to Tonight's Dinner!`, 'Close', {
            duration: 3000,
          });
          this.loadCurrentView();
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to cook tonight.', 'Close', {
            duration: 4000,
          });
        },
      });
  }

  protected moveToOnDeck(entry: MealPlanEntryDto): void {
    this.mealPlanService
      .moveEntry(entry.id, {
        isOnDeck: true,
        weekStartDate: this.activePeriodStart(),
      })
      .subscribe({
        next: () => {
          this.snackBar.open(`Moved "${entry.displayTitle}" to On-Deck!`, 'Close', {
            duration: 3000,
          });
          this.loadCurrentView();
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to move to On-Deck.', 'Close', {
            duration: 4000,
          });
        },
      });
  }

  protected startCooking(entry: MealPlanEntryDto): void {
    if (!entry.recipe?.id) return;
    void this.router.navigate(['/recipes', entry.recipe.id, 'cook'], {
      queryParams: {
        servings: entry.targetServings,
        from: 'meal-plan',
      },
    });
  }

  protected logCook(entry: MealPlanEntryDto): void {
    if (!entry.recipe?.id) return;
    this.dialog.open(CookLogDialogComponent, {
      data: {
        recipeId: entry.recipe.id,
        recipeTitle: entry.displayTitle,
        targetServings: entry.targetServings,
      },
      width: '520px',
      maxWidth: '96vw',
    });
  }

  protected deleteEntry(entry: MealPlanEntryDto): void {
    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Remove Meal',
        message: `Remove "${entry.displayTitle}" from your meal plan?`,
        confirmText: 'Remove',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) return;

      this.mealPlanService.deleteEntry(entry.id).subscribe({
        next: () => {
          this.snackBar.open(`Removed "${entry.displayTitle}".`, 'Close', { duration: 3000 });
          this.loadCurrentView();
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to remove meal.', 'Close', {
            duration: 4000,
          });
        },
      });
    });
  }

  protected openGenerateShoppingListDialog(): void {
    const start = this.activePeriodStart();
    const end = addDaysIso(start, 6);

    const ref = this.dialog.open(GenerateShoppingListDialogComponent, {
      data: {
        startDate: start,
        endDate: end,
      },
      width: '680px',
      maxWidth: '95vw',
    });

    ref.afterClosed().subscribe((result?: ShoppingListGenerationResultDto) => {
      if (!result) {
        return;
      }

      const snack = this.snackBar.open(
        `Generated shopping list for ${result.importedMealsCount} meal(s)`,
        'Open List',
        { duration: 5000 },
      );

      snack.onAction().subscribe(() => {
        this.router.navigate(['/shopping'], {
          queryParams: { listId: result.shoppingList.id },
        });
      });
    });
  }

  protected openDraftMealPlanDialog(): void {
    const ref = this.dialog.open<
      DraftMealPlanDialogComponent,
      DraftMealPlanDialogData,
      MealPlanWeekDto
    >(DraftMealPlanDialogComponent, {
      width: '680px',
      maxWidth: '96vw',
      data: {
        weekStartDate: this.activePeriodStart(),
      },
    });

    ref.afterClosed().subscribe((updatedWeek) => {
      if (updatedWeek) {
        this.loadCurrentView();
      }
    });
  }

  protected openTemplatesDialog(): void {
    const ref = this.dialog.open<
      MealPlanTemplatesDialogComponent,
      MealPlanTemplatesDialogData,
      MealPlanWeekDto
    >(MealPlanTemplatesDialogComponent, {
      width: '680px',
      maxWidth: '95vw',
      data: {
        currentWeekStartDate: this.activeWeekStart(),
        currentWeekLabel: formatWeekRangeLabel(this.activeWeekStart()),
        currentWeekEntryCount: this.totalWeekMealsCount(),
      },
    });

    ref.afterClosed().subscribe((updatedWeek) => {
      if (updatedWeek) {
        this.weekData.set(updatedWeek);
      }
    });
  }

  protected clearCurrentWeek(): void {
    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Clear Week Meal Plan',
        message: `Clear all scheduled meals for ${formatWeekRangeLabel(this.activeWeekStart())}? (On-Deck meals will be preserved)`,
        confirmText: 'Clear Week',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) return;

      this.mealPlanService.clearWeek(this.activeWeekStart(), false).subscribe({
        next: () => {
          this.snackBar.open('Week meal plan cleared.', 'Close', { duration: 3000 });
          this.loadWeek(this.activeWeekStart());
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to clear week.', 'Close', {
            duration: 4000,
          });
        },
      });
    });
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  protected onDrop(event: CdkDragDrop<any>): void {
    const entry = event.item.data as MealPlanEntryDto;
    const dest = event.container.data as DropZoneData;

    if (event.previousContainer === event.container && event.previousIndex === event.currentIndex) {
      return;
    }

    this.mealPlanService
      .moveEntry(entry.id, {
        isOnDeck: dest.isOnDeck,
        scheduledDate: dest.date ?? null,
        slot: dest.slot ?? null,
        weekStartDate: dest.weekStartDate ?? this.activePeriodStart(),
        targetSortOrder: event.currentIndex,
      })
      .subscribe({
        next: () => {
          this.loadCurrentView();
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to move meal.', 'Close', {
            duration: 4000,
          });
          this.loadCurrentView();
        },
      });
  }

  protected getDayHeader(dateIso: string) {
    return formatDayHeader(dateIso);
  }

  protected getSlotEntries(day: MealPlanDayDto, slot: MealSlot): MealPlanEntryDto[] {
    return day.entries.filter((e) => e.slot === slot);
  }

  protected toggleMobileOnDeck(): void {
    this.mobileOnDeckOpen.update((v) => !v);
  }
}
