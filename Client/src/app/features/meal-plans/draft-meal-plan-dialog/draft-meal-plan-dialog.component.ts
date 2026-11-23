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
import { MatTooltipModule } from '@angular/material/tooltip';
import { APP_STORAGE_PREFIX } from '../../../app.branding';
import {
  DraftMealCookSlotRequest,
  DraftMealLeftoverSlotRequest,
  DraftMealPlanResponseDto,
  DraftWeeklyMealPlanRequest,
} from '../../../core/models/ai-helper.models';
import {
  CreateMealPlanEntryRequest,
  MEAL_SLOTS,
  MealPlanRecipeBadgeDto,
  MealPlanWeekDto,
  MealSlot,
} from '../../../core/models/meal-plan.models';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import {
  addDaysIso,
  coerceToDateLocal,
  coerceToIsoDateLocal,
  formatDayHeader,
  getTodayIsoLocal,
  parseIsoDateLocal,
  shouldUseTouchDatepicker,
} from '../../../core/utils/meal-plan-date';

export const DRAFT_MEAL_PLAN_PREFS_STORAGE_KEY = `${APP_STORAGE_PREFIX}:draft-meal-plan-grid-v1`;

const SLOT_ORDER: Record<MealSlot, number> = {
  Breakfast: 0,
  Lunch: 1,
  Dinner: 2,
  SnackPrep: 3,
};

export type GridCellMode = 'off' | 'cook' | 'leftovers';

export interface DraftMealPlanDialogData {
  weekStartDate: string;
  existingWeekData?: MealPlanWeekDto | null;
}

export interface DraftGridDayColumn {
  dateIso: string;
  dayOfWeek: number;
  dayShort: string;
  dayLong: string;
  dateNum: number;
  isToday: boolean;
  isPast: boolean;
}

export interface DraftGridCellState {
  key: string;
  dateIso: string;
  dayOfWeek: number;
  slot: MealSlot;
  mode: GridCellMode;
  servings: number;
  hasCustomServings: boolean;
  isPast: boolean;
  existingTitles: string[];
}

export interface DraftLeftoverAssignment {
  id: string;
  scheduledDate: string;
  slot: MealSlot;
  servings: number;
  sourceCandidateId: string;
}

export interface DraftMealCandidateFormItem {
  id: string;
  selected: boolean;
  recipeId: string;
  recipe: MealPlanRecipeBadgeDto;
  scheduledDate: string;
  scheduledDateObj: Date;
  slot: MealSlot;
  cookServings: number;
  isOnDeck: boolean;
  rationale: string;
  hasInvalidDate: boolean;
  hasInvalidServings: boolean;
}

interface SavedCellPreference {
  mode: GridCellMode;
  customServings?: number | null;
}

interface SavedDraftGridPreferences {
  defaultServings: number;
  onDeckCount: number;
  onDeckServings: number;
  cellsByDayAndSlot: Record<string, SavedCellPreference>;
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
    MatTooltipModule,
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

  readonly gridDays: DraftGridDayColumn[];
  readonly prompt = signal<string>('');
  readonly defaultServings = signal<number>(4);
  readonly onDeckCount = signal<number>(0);
  readonly onDeckServings = signal<number>(4);
  private onDeckCustomServings = false;
  readonly gridCells = signal<DraftGridCellState[]>([]);

  readonly retryingCandidateId = signal<string | null>(null);
  readonly isLoading = signal<boolean>(false);
  readonly isSubmitting = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);

  readonly draftResponse = signal<DraftMealPlanResponseDto | null>(null);
  readonly candidateItems = signal<DraftMealCandidateFormItem[]>([]);
  readonly leftoverAssignments = signal<DraftLeftoverAssignment[]>([]);

  readonly gridCellMap = computed(() => {
    const map = new Map<string, DraftGridCellState>();
    for (const cell of this.gridCells()) {
      map.set(cell.key, cell);
    }
    return map;
  });

  readonly pairingAnalysis = computed(() => {
    const ordered = [...this.gridCells()].sort(
      (a, b) => a.dateIso.localeCompare(b.dateIso) || SLOT_ORDER[a.slot] - SLOT_ORDER[b.slot],
    );

    const cookCells = ordered.filter((c) => c.mode === 'cook' && !c.isPast);
    const leftoverCells = ordered.filter((c) => c.mode === 'leftovers' && !c.isPast);
    const leftoversByCookKey = new Map<string, DraftMealLeftoverSlotRequest[]>();
    const cookTargetLabelByLeftoverKey = new Map<string, string>();
    const orphanedLeftovers: DraftGridCellState[] = [];

    for (const cook of cookCells) {
      leftoversByCookKey.set(cook.key, []);
    }

    for (const leftover of leftoverCells) {
      const earlierCooks = cookCells.filter(
        (c) =>
          c.dateIso < leftover.dateIso ||
          (c.dateIso === leftover.dateIso && SLOT_ORDER[c.slot] < SLOT_ORDER[leftover.slot]),
      );

      if (earlierCooks.length === 0) {
        orphanedLeftovers.push(leftover);
        continue;
      }

      const targetGroup = getSlotMealGroup(leftover.slot);
      const sameGroupCooks = earlierCooks.filter((c) => getSlotMealGroup(c.slot) === targetGroup);
      const chosenCook =
        sameGroupCooks.length > 0
          ? sameGroupCooks[sameGroupCooks.length - 1]
          : earlierCooks[earlierCooks.length - 1];

      leftoversByCookKey.get(chosenCook.key)!.push({
        scheduledDate: leftover.dateIso,
        slot: leftover.slot,
        servings: leftover.servings,
      });

      const cookDay = formatDayHeader(chosenCook.dateIso);
      cookTargetLabelByLeftoverKey.set(
        leftover.key,
        `${cookDay.dayShort} ${this.getSlotShortLabel(chosenCook.slot)}`,
      );
    }

    const cookRequests: DraftMealCookSlotRequest[] = cookCells.map((cook) => ({
      isOnDeck: false,
      scheduledDate: cook.dateIso,
      slot: cook.slot,
      servings: cook.servings,
      leftovers: leftoversByCookKey.get(cook.key) ?? [],
    }));

    const deckCount = this.onDeckCount();
    const deckServings = this.onDeckServings();
    for (let i = 0; i < deckCount; i++) {
      cookRequests.push({
        isOnDeck: true,
        scheduledDate: null,
        slot: 'Dinner',
        servings: deckServings,
        leftovers: [],
      });
    }

    return {
      cookCount: cookCells.length,
      leftoverCount: leftoverCells.length,
      onDeckCount: deckCount,
      orphanedLeftovers,
      cookRequests,
      leftoversByCookKey,
      cookTargetLabelByLeftoverKey,
    };
  });

  readonly minDate = computed(() => {
    const today = getTodayIsoLocal();
    const start = this.draftResponse()?.weekStartDate ?? today;
    return coerceToDateLocal(start > today ? start : today);
  });

  readonly selectedCount = computed(
    () => this.candidateItems().filter((item) => item.selected).length,
  );

  readonly selectedMealCount = computed(() => {
    const selectedIds = new Set(
      this.candidateItems()
        .filter((item) => item.selected)
        .map((item) => item.id),
    );
    const assignedLeftovers = this.leftoverAssignments().filter((l) =>
      selectedIds.has(l.sourceCandidateId),
    ).length;
    return selectedIds.size + assignedLeftovers;
  });

  readonly hasInvalidSelection = computed(() =>
    this.candidateItems().some(
      (item) => item.selected && (item.hasInvalidDate || item.hasInvalidServings),
    ),
  );

  readonly canSubmitDraft = computed(
    () =>
      !this.isBusy() &&
      this.pairingAnalysis().cookRequests.length > 0 &&
      this.pairingAnalysis().orphanedLeftovers.length === 0,
  );

  readonly isBusy = computed(
    () => this.isLoading() || this.isSubmitting() || this.retryingCandidateId() !== null,
  );

  constructor() {
    const today = getTodayIsoLocal();
    const periodEnd = addDaysIso(this.data.weekStartDate, 6);
    const gridStartIso = periodEnd < today ? today : this.data.weekStartDate;

    this.gridDays = Array.from({ length: 7 }, (_, i) => {
      const dateIso = addDaysIso(gridStartIso, i);
      const header = formatDayHeader(dateIso);
      return {
        dateIso,
        dayOfWeek: parseIsoDateLocal(dateIso).getDay(),
        dayShort: header.dayShort,
        dayLong: header.dayLong,
        dateNum: header.dateNum,
        isToday: header.isToday,
        isPast: dateIso < today,
      };
    });

    this.initializeGridFromPreferences();
  }

  private initializeGridFromPreferences(): void {
    const saved = this.readSavedPreferences();
    const defaultSrv = saved?.defaultServings ?? 4;
    this.defaultServings.set(defaultSrv);
    this.onDeckCount.set(saved?.onDeckCount ?? 0);
    this.onDeckServings.set(saved?.onDeckServings ?? defaultSrv);
    this.onDeckCustomServings = saved ? saved.onDeckServings !== defaultSrv : false;

    const existingMap = new Map<string, string[]>();
    for (const day of this.data.existingWeekData?.days ?? []) {
      for (const entry of day.entries) {
        if (!entry.slot) continue;
        const key = `${day.date}:${entry.slot}`;
        if (!existingMap.has(key)) {
          existingMap.set(key, []);
        }
        existingMap.get(key)!.push(entry.displayTitle);
      }
    }

    const cells: DraftGridCellState[] = [];
    for (const day of this.gridDays) {
      for (const slotMeta of this.slots) {
        const key = `${day.dateIso}:${slotMeta.value}`;
        const existingTitles = existingMap.get(key) ?? [];
        const prefKey = `${day.dayOfWeek}:${slotMeta.value}`;
        const savedCell = saved?.cellsByDayAndSlot?.[prefKey];

        let mode: GridCellMode;
        if (day.isPast || existingTitles.length > 0) {
          mode = 'off';
        } else if (savedCell) {
          mode = savedCell.mode;
        } else {
          mode = slotMeta.value === 'Dinner' ? 'cook' : 'off';
        }

        const customSrv =
          savedCell?.customServings && savedCell.customServings >= 0.25
            ? savedCell.customServings
            : null;

        cells.push({
          key,
          dateIso: day.dateIso,
          dayOfWeek: day.dayOfWeek,
          slot: slotMeta.value,
          mode,
          servings: customSrv ?? defaultSrv,
          hasCustomServings: customSrv !== null,
          isPast: day.isPast,
          existingTitles,
        });
      }
    }

    // Remove any orphaned leftovers from saved patterns when earlier cook days are occupied/past.
    cells.sort(
      (a, b) => a.dateIso.localeCompare(b.dateIso) || SLOT_ORDER[a.slot] - SLOT_ORDER[b.slot],
    );
    let sawCook = false;
    for (const cell of cells) {
      if (cell.isPast) continue;
      if (cell.mode === 'cook') {
        sawCook = true;
      } else if (cell.mode === 'leftovers' && !sawCook) {
        cell.mode = 'off';
      }
    }

    this.gridCells.set(cells);
  }

  private readSavedPreferences(): SavedDraftGridPreferences | null {
    try {
      if (typeof localStorage === 'undefined') return null;
      const raw = localStorage.getItem(DRAFT_MEAL_PLAN_PREFS_STORAGE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as SavedDraftGridPreferences;
      if (!parsed || typeof parsed !== 'object') return null;
      return {
        defaultServings: clampServings(parsed.defaultServings, 4),
        onDeckCount: Math.max(0, Math.min(7, Math.trunc(Number(parsed.onDeckCount) || 0))),
        onDeckServings: clampServings(
          parsed.onDeckServings,
          clampServings(parsed.defaultServings, 4),
        ),
        cellsByDayAndSlot: parsed.cellsByDayAndSlot ?? {},
      };
    } catch {
      return null;
    }
  }

  private persistPreferences(): void {
    try {
      if (typeof localStorage === 'undefined') return;
      const existing = this.readSavedPreferences();
      const cellsByDayAndSlot: Record<string, SavedCellPreference> = {
        ...(existing?.cellsByDayAndSlot ?? {}),
      };

      for (const cell of this.gridCells()) {
        if (cell.isPast || cell.existingTitles.length > 0) {
          continue;
        }
        cellsByDayAndSlot[`${cell.dayOfWeek}:${cell.slot}`] = {
          mode: cell.mode,
          customServings: cell.hasCustomServings ? cell.servings : null,
        };
      }

      const payload: SavedDraftGridPreferences = {
        defaultServings: this.defaultServings(),
        onDeckCount: this.onDeckCount(),
        onDeckServings: this.onDeckServings(),
        cellsByDayAndSlot,
      };
      localStorage.setItem(DRAFT_MEAL_PLAN_PREFS_STORAGE_KEY, JSON.stringify(payload));
    } catch {
      // Ignore storage quota/access errors
    }
  }

  getCell(dateIso: string, slot: MealSlot): DraftGridCellState {
    return (
      this.gridCellMap().get(`${dateIso}:${slot}`) ?? {
        key: `${dateIso}:${slot}`,
        dateIso,
        dayOfWeek: parseIsoDateLocal(dateIso).getDay(),
        slot,
        mode: 'off',
        servings: this.defaultServings(),
        hasCustomServings: false,
        isPast: false,
        existingTitles: [],
      }
    );
  }

  getSlotShortLabel(slot: MealSlot): string {
    return this.slots.find((s) => s.value === slot)?.shortLabel ?? slot;
  }

  isRowActive(slot: MealSlot): boolean {
    return this.gridCells().some((c) => c.slot === slot && !c.isPast && c.mode !== 'off');
  }

  isDayActive(dateIso: string): boolean {
    return this.gridCells().some((c) => c.dateIso === dateIso && !c.isPast && c.mode !== 'off');
  }

  hasEarlierCook(dateIso: string, slot: MealSlot): boolean {
    return this.gridCells().some(
      (c) =>
        !c.isPast &&
        c.mode === 'cook' &&
        (c.dateIso < dateIso || (c.dateIso === dateIso && SLOT_ORDER[c.slot] < SLOT_ORDER[slot])),
    );
  }

  toggleSlotRow(slot: MealSlot): void {
    if (this.isBusy()) return;
    const rowCells = this.gridCells().filter((c) => c.slot === slot && !c.isPast);
    if (rowCells.length === 0) return;

    const anyActive = rowCells.some((c) => c.mode !== 'off');
    if (anyActive) {
      this.gridCells.update((cells) =>
        cells.map((c) => (c.slot === slot && !c.isPast ? { ...c, mode: 'off' } : c)),
      );
    } else {
      const hasUnoccupied = rowCells.some((c) => c.existingTitles.length === 0);
      this.gridCells.update((cells) =>
        cells.map((c) => {
          if (c.slot !== slot || c.isPast) return c;
          if (hasUnoccupied && c.existingTitles.length > 0) return c;
          return { ...c, mode: 'cook' };
        }),
      );
    }
    this.persistPreferences();
  }

  toggleDayColumn(dateIso: string): void {
    if (this.isBusy()) return;
    const colCells = this.gridCells().filter((c) => c.dateIso === dateIso && !c.isPast);
    if (colCells.length === 0) return;

    const anyActive = colCells.some((c) => c.mode !== 'off');
    if (anyActive) {
      this.gridCells.update((cells) =>
        cells.map((c) => (c.dateIso === dateIso && !c.isPast ? { ...c, mode: 'off' } : c)),
      );
    } else {
      const otherActiveSlots = new Set(
        this.gridCells()
          .filter((c) => c.dateIso !== dateIso && !c.isPast && c.mode !== 'off')
          .map((c) => c.slot),
      );
      const targetSlots: Set<MealSlot> =
        otherActiveSlots.size > 0 ? otherActiveSlots : new Set<MealSlot>(['Dinner']);

      this.gridCells.update((cells) =>
        cells.map((c) => {
          if (c.dateIso !== dateIso || c.isPast) return c;
          return targetSlots.has(c.slot) ? { ...c, mode: 'cook' } : c;
        }),
      );
    }
    this.persistPreferences();
  }

  cycleCellMode(dateIso: string, slot: MealSlot): void {
    if (this.isBusy()) return;
    const cell = this.getCell(dateIso, slot);
    if (cell.isPast) return;

    const canBeLeftovers = this.hasEarlierCook(dateIso, slot);
    let nextMode: GridCellMode;
    if (cell.mode === 'off') {
      nextMode = 'cook';
    } else if (cell.mode === 'cook') {
      nextMode = canBeLeftovers ? 'leftovers' : 'off';
    } else {
      nextMode = 'off';
    }

    this.gridCells.update((cells) =>
      cells.map((c) => (c.key === cell.key ? { ...c, mode: nextMode } : c)),
    );
    this.persistPreferences();
  }

  adjustCellServings(dateIso: string, slot: MealSlot, delta: number, event?: Event): void {
    event?.stopPropagation();
    if (this.isBusy()) return;
    const cell = this.getCell(dateIso, slot);
    if (cell.isPast) return;
    const next = clampServings(cell.servings + delta, cell.servings);
    this.gridCells.update((cells) =>
      cells.map((c) =>
        c.key === cell.key ? { ...c, servings: next, hasCustomServings: true } : c,
      ),
    );
    this.persistPreferences();
  }

  updateDefaultServings(value: number): void {
    const clean = clampServings(value, 4);
    this.defaultServings.set(clean);
    this.gridCells.update((cells) =>
      cells.map((c) => (c.hasCustomServings ? c : { ...c, servings: clean })),
    );
    if (!this.onDeckCustomServings) {
      this.onDeckServings.set(clean);
    }
    this.persistPreferences();
  }

  adjustOnDeckCount(delta: number): void {
    if (this.isBusy()) return;
    const next = Math.max(0, Math.min(7, this.onDeckCount() + delta));
    this.onDeckCount.set(next);
    this.persistPreferences();
  }

  updateOnDeckServings(value: number): void {
    this.onDeckCustomServings = true;
    this.onDeckServings.set(clampServings(value, this.defaultServings()));
    this.persistPreferences();
  }

  selectPreset(preset: string): void {
    this.prompt.set(this.prompt() === preset ? '' : preset);
  }

  submitDraft(): void {
    if (!this.canSubmitDraft()) return;

    this.persistPreferences();
    const promptText = this.prompt().trim();
    const { cookRequests } = this.pairingAnalysis();

    this.isLoading.set(true);
    this.errorMessage.set(null);

    const today = getTodayIsoLocal();
    const scheduledSlots = cookRequests.filter((r) => !r.isOnDeck && r.slot);
    const uniqueScheduledSlots = new Set(scheduledSlots.map((r) => r.slot!));
    const primarySlot: MealSlot =
      uniqueScheduledSlots.size === 1 ? Array.from(uniqueScheduledSlots)[0] : 'Dinner';

    const request: DraftWeeklyMealPlanRequest = {
      weekStartDate: this.data.weekStartDate < today ? today : this.data.weekStartDate,
      prompt: promptText,
      mealCount: cookRequests.length,
      defaultSlot: primarySlot,
      placeOnDeck: cookRequests.every((r) => r.isOnDeck),
      servingsPerMeal: this.defaultServings(),
      todayDate: today,
      requestedSlots: cookRequests,
    };

    this.aiHelper
      .draftWeeklyMealPlan(request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.lastDraftRequest = request;
          this.draftResponse.set(response);
          const items: DraftMealCandidateFormItem[] = [];
          const leftovers: DraftLeftoverAssignment[] = [];

          response.candidates.forEach((c, idx) => {
            const candidateId = `${c.recipeId}-${idx}`;
            const scheduledDate = c.scheduledDate ?? response.weekStartDate;
            const candidateSlot = c.slot ?? primarySlot;
            const slotEntries =
              c.leftoverSlots && c.leftoverSlots.length > 0
                ? c.leftoverSlots
                : (c.leftoverDates ?? []).map((date) => ({
                    scheduledDate: date,
                    slot: candidateSlot,
                    servings: c.targetServings / (1 + (c.leftoverDates?.length ?? 0)),
                  }));

            const cookServings =
              c.cookServings && c.cookServings > 0
                ? c.cookServings
                : Math.max(
                    0.25,
                    c.targetServings - slotEntries.reduce((sum, l) => sum + l.servings, 0),
                  );

            items.push({
              id: candidateId,
              selected: true,
              recipeId: c.recipeId,
              recipe: c.recipe,
              scheduledDate,
              scheduledDateObj: coerceToDateLocal(scheduledDate),
              slot: candidateSlot,
              cookServings,
              isOnDeck: c.isOnDeck,
              rationale: c.rationale,
              hasInvalidDate: false,
              hasInvalidServings: false,
            });

            slotEntries.forEach((lo, lIdx) => {
              leftovers.push({
                id: `lo-${idx}-${lIdx}`,
                scheduledDate: lo.scheduledDate,
                slot: lo.slot,
                servings: lo.servings,
                sourceCandidateId: candidateId,
              });
            });
          });

          this.candidateItems.set(items);
          this.leftoverAssignments.set(leftovers);
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
        defaultSlot: item.slot,
        placeOnDeck: item.isOnDeck,
        servingsPerMeal: item.cookServings,
        excludedRecipeIds: this.candidateItems().map((candidate) => candidate.recipeId),
        requestedSlots: [
          {
            isOnDeck: item.isOnDeck,
            scheduledDate: item.isOnDeck ? null : item.scheduledDate,
            slot: item.slot,
            servings: item.cookServings,
            leftovers: [],
          },
        ],
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

  getLeftoversForCandidate(candidateId: string): DraftLeftoverAssignment[] {
    return this.leftoverAssignments()
      .filter((l) => l.sourceCandidateId === candidateId)
      .sort(
        (a, b) =>
          a.scheduledDate.localeCompare(b.scheduledDate) || SLOT_ORDER[a.slot] - SLOT_ORDER[b.slot],
      );
  }

  getTotalCookServings(item: DraftMealCandidateFormItem): number {
    const leftoverSum = this.getLeftoversForCandidate(item.id).reduce(
      (sum, l) => sum + l.servings,
      0,
    );
    return item.cookServings + leftoverSum;
  }

  getEligibleCookSourcesForLeftover(
    leftover: DraftLeftoverAssignment,
  ): DraftMealCandidateFormItem[] {
    return this.candidateItems().filter(
      (c) =>
        c.selected &&
        !c.isOnDeck &&
        (c.scheduledDate < leftover.scheduledDate ||
          (c.scheduledDate === leftover.scheduledDate &&
            SLOT_ORDER[c.slot] < SLOT_ORDER[leftover.slot])),
    );
  }

  reassignLeftoverSource(leftoverId: string, newSourceCandidateId: string): void {
    if (this.isBusy()) return;
    this.leftoverAssignments.update((list) =>
      list.map((l) =>
        l.id === leftoverId ? { ...l, sourceCandidateId: newSourceCandidateId } : l,
      ),
    );
  }

  maxCookingDate(item: DraftMealCandidateFormItem): Date {
    const assigned = this.getLeftoversForCandidate(item.id);
    if (assigned.length === 0) {
      return coerceToDateLocal(this.draftResponse()?.weekEndDate);
    }

    const earliest = assigned[0];
    const maxIso =
      SLOT_ORDER[item.slot] < SLOT_ORDER[earliest.slot]
        ? earliest.scheduledDate
        : addDaysIso(earliest.scheduledDate, -1);
    return coerceToDateLocal(maxIso);
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

    const assigned = this.getLeftoversForCandidate(id);
    const violatesLeftover = assigned.some(
      (lo) =>
        iso > lo.scheduledDate ||
        (iso === lo.scheduledDate && SLOT_ORDER[item.slot] >= SLOT_ORDER[lo.slot]),
    );

    if (
      !date ||
      (date instanceof Date && Number.isNaN(date.getTime())) ||
      iso < coerceToIsoDateLocal(this.minDate()) ||
      iso > coerceToIsoDateLocal(this.draftResponse()?.weekEndDate) ||
      violatesLeftover
    ) {
      this.errorMessage.set(
        'Choose today or a future date before any assigned leftovers, within this seven-day period.',
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
      items.map((candidate) =>
        candidate.id === id
          ? { ...candidate, scheduledDate: iso, scheduledDateObj: dateObj, hasInvalidDate: false }
          : candidate,
      ),
    );
  }

  updateSlot(id: string, slot: MealSlot): void {
    if (this.isBusy()) return;
    const item = this.candidateItems().find((c) => c.id === id);
    if (!item) return;

    const assigned = this.getLeftoversForCandidate(id);
    const violatesSameDayLeftover = assigned.some(
      (lo) => lo.scheduledDate === item.scheduledDate && SLOT_ORDER[slot] >= SLOT_ORDER[lo.slot],
    );
    if (violatesSameDayLeftover) {
      this.errorMessage.set(
        'Cooking slot must remain chronologically before any same-day leftovers.',
      );
      return;
    }

    this.errorMessage.set(null);
    this.candidateItems.update((items) =>
      items.map((candidate) => (candidate.id === id ? { ...candidate, slot } : candidate)),
    );
  }

  updateServings(id: string, cookServings: number): void {
    if (this.isBusy()) return;
    const item = this.candidateItems().find((candidate) => candidate.id === id);
    if (!item) return;

    const leftoverSum = this.getLeftoversForCandidate(id).reduce((sum, l) => sum + l.servings, 0);
    if (!Number.isFinite(cookServings) || cookServings < 0.25 || cookServings + leftoverSum > 999) {
      this.errorMessage.set(
        'Enter valid servings for this meal (at least 0.25 and no more than 999 total cooking servings).',
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
              cookServings,
              hasInvalidServings: false,
            }
          : candidate,
      ),
    );
  }

  formatDateBadge(iso: string): string {
    const h = formatDayHeader(iso);
    return `${h.dayShort} ${h.formattedDate}`;
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

    const today = getTodayIsoLocal();
    if (selected.some((item) => !item.isOnDeck && item.scheduledDate < today)) {
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
      const totalServings = this.getTotalCookServings(item);
      entries.push({
        entryType: 'Recipe',
        isOnDeck: item.isOnDeck,
        weekStartDate: rangeStart,
        scheduledDate: item.isOnDeck ? null : item.scheduledDate,
        slot: item.isOnDeck ? null : item.slot,
        recipeId: item.recipeId,
        targetServings: totalServings,
      });

      for (const lo of this.getLeftoversForCandidate(item.id)) {
        entries.push({
          entryType: 'Leftovers',
          isOnDeck: false,
          scheduledDate: lo.scheduledDate,
          slot: lo.slot,
          recipeId: item.recipeId,
          targetServings: lo.servings,
          sourceDraftEntryIndex: sourceIndex,
          notes:
            lo.scheduledDate > addDaysIso(item.scheduledDate, 3)
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

function getSlotMealGroup(slot: MealSlot): 'breakfast' | 'main' | 'snack' {
  if (slot === 'Breakfast') return 'breakfast';
  if (slot === 'SnackPrep') return 'snack';
  return 'main';
}

function clampServings(raw: unknown, fallback: number): number {
  const num = Number(raw);
  if (!Number.isFinite(num) || num < 0.25) return fallback;
  return Math.min(999, Math.round(num * 4) / 4);
}
