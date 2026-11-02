import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
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
  normalizeToMondayIso,
} from '../../../core/utils/meal-plan-date';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  MealPlanEntryDialogComponent,
  MealPlanEntryDialogData,
} from '../meal-plan-entry-dialog/meal-plan-entry-dialog.component';
import {
  MealPlanTemplatesDialogComponent,
  MealPlanTemplatesDialogData,
} from '../meal-plan-templates-dialog/meal-plan-templates-dialog.component';

export interface DropZoneData {
  isOnDeck: boolean;
  date?: string;
  slot?: MealSlot;
  weekStartDate?: string;
}

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
  selector: 'app-meal-plan-calendar',
  templateUrl: './meal-plan-calendar.component.html',
  styleUrl: './meal-plan-calendar.component.scss',
})
export class MealPlanCalendarComponent implements OnInit {
  private readonly mealPlanService = inject(MealPlanService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);

  protected readonly mealSlots = MEAL_SLOTS;
  protected readonly dayNamesShort = DAY_NAMES_SHORT;
  protected readonly todayIso = getTodayIsoLocal();

  protected readonly viewMode = signal<'week' | 'month'>('week');
  protected readonly activeWeekStart = signal<string>(normalizeToMondayIso(getTodayIsoLocal()));
  protected readonly activeMonth = signal<{ year: number; monthIndex0: number }>({
    year: new Date().getFullYear(),
    monthIndex0: new Date().getMonth(),
  });

  protected readonly isLoading = signal<boolean>(true);
  protected readonly weekData = signal<MealPlanWeekDto | null>(null);
  protected readonly monthRangeData = signal<MealPlanRangeResponseDto | null>(null);
  protected readonly mobileOnDeckOpen = signal<boolean>(false);

  protected readonly periodTitle = computed(() => {
    if (this.viewMode() === 'week') {
      return formatWeekRangeLabel(this.activeWeekStart());
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
    this.loadCurrentView();
  }

  loadCurrentView(): void {
    if (this.viewMode() === 'week') {
      this.loadWeek(this.activeWeekStart());
    } else {
      this.loadMonth();
    }
  }

  private loadWeek(weekMondayIso: string): void {
    this.isLoading.set(true);
    this.mealPlanService.getWeek(weekMondayIso).subscribe({
      next: (data) => {
        this.weekData.set(data);
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
      },
    });
  }

  private loadMonth(): void {
    const grid = this.monthGridDays();
    if (grid.length === 0) return;

    this.isLoading.set(true);
    const start = grid[0].iso;
    const end = grid[grid.length - 1].iso;

    this.mealPlanService.getRange(start, end, true).subscribe({
      next: (data) => {
        this.monthRangeData.set(data);
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
      },
    });
  }

  protected setViewMode(mode: 'week' | 'month'): void {
    this.viewMode.set(mode);
    this.loadCurrentView();
  }

  protected navigatePeriod(delta: number): void {
    if (this.viewMode() === 'week') {
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
    this.activeWeekStart.set(normalizeToMondayIso(today));
    const now = new Date();
    this.activeMonth.set({ year: now.getFullYear(), monthIndex0: now.getMonth() });
    this.loadCurrentView();
  }

  protected selectMonthDay(isoDate: string): void {
    this.activeWeekStart.set(normalizeToMondayIso(isoDate));
    this.viewMode.set('week');
    this.loadWeek(this.activeWeekStart());
  }

  protected openAddEntryDialog(prefill?: {
    date?: string;
    slot?: MealSlot;
    isOnDeck?: boolean;
    entryType?: 'Recipe' | 'Leftovers' | 'FreeformNote';
  }): void {
    const week = this.weekData();
    const allWeekScheduled: MealPlanEntryDto[] = week ? week.days.flatMap((d) => d.entries) : [];

    const weekStart = this.activeWeekStart();
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
          this.loadWeek(this.activeWeekStart());
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
        weekStartDate: this.activeWeekStart(),
      })
      .subscribe({
        next: () => {
          this.snackBar.open(`Moved "${entry.displayTitle}" to On-Deck!`, 'Close', {
            duration: 3000,
          });
          this.loadWeek(this.activeWeekStart());
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to move to On-Deck.', 'Close', {
            duration: 4000,
          });
        },
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
        weekStartDate: dest.weekStartDate ?? this.activeWeekStart(),
        targetSortOrder: event.currentIndex,
      })
      .subscribe({
        next: () => {
          this.loadWeek(this.activeWeekStart());
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to move meal.', 'Close', {
            duration: 4000,
          });
          this.loadWeek(this.activeWeekStart());
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
