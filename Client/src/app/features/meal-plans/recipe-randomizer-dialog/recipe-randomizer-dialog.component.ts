import { DecimalPipe } from '@angular/common';
import {
  Component,
  DestroyRef,
  ElementRef,
  OnInit,
  computed,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
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
import { RouterLink } from '@angular/router';
import { MEAL_SLOTS, MealSlot } from '../../../core/models/meal-plan.models';
import { RecipeDetailDto, RecipeIngredientDto } from '../../../core/models/recipe.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import {
  formatMinutes,
  scaleIngredient,
  stripOuterParentheses,
} from '../../../core/utils/culinary-math';
import {
  coerceToDateLocal,
  coerceToIsoDateLocal,
  getTodayIsoLocal,
  normalizeToSundayIso,
  shouldUseTouchDatepicker,
} from '../../../core/utils/meal-plan-date';
import { StepInstructionComponent } from '../../../shared/components/step-instruction/step-instruction.component';

export interface RecipeRandomizerDialogData {
  initialDate?: string;
  initialSlot?: MealSlot;
  initialWeekStartDate?: string;
  onMealAdded?: () => void;
}

export interface RandomizerIngredientItem {
  ingredient: RecipeIngredientDto;
  quantityAndUnit: string;
  prepNote: string | null;
}

export type SwipeDirection = 'next' | 'prev' | 'added';

export interface OutgoingCardSnapshot {
  key: number;
  id: string;
  title: string;
  imageUrl: string | null;
  direction: SwipeDirection;
  badgeLabel: string;
  badgeIcon: string;
  startOffsetX: number;
  startRotationDeg: number;
  fromDrag: boolean;
}

export function isTouchPrimaryDevice(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }
  if (typeof window.matchMedia === 'function') {
    if (window.matchMedia('(pointer: coarse)').matches) {
      return true;
    }
    if (window.matchMedia('(pointer: fine)').matches) {
      return false;
    }
    if (window.matchMedia('(hover: none)').matches) {
      return true;
    }
  }
  return typeof navigator !== 'undefined' && (navigator.maxTouchPoints ?? 0) > 0;
}

@Component({
  selector: 'app-recipe-randomizer-dialog',
  imports: [
    DecimalPipe,
    FormsModule,
    RouterLink,
    MatDialogModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTooltipModule,
    StepInstructionComponent,
  ],
  providers: [provideNativeDateAdapter()],
  templateUrl: './recipe-randomizer-dialog.component.html',
  styleUrl: './recipe-randomizer-dialog.component.scss',
  host: {
    '(document:keydown)': 'onKeydown($event)',
  },
})
export class RecipeRandomizerDialogComponent implements OnInit {
  private static readonly BATCH_SIZE = 10;
  private static readonly COLLAPSED_DESCRIPTION_CHARS = 140;
  private static readonly COLLAPSED_INGREDIENTS_COUNT = 5;
  private static readonly COLLAPSED_STEPS_COUNT = 2;
  private static readonly COLLAPSED_STEP_CHARS = 140;

  private readonly recipeService = inject(RecipeService);
  private readonly mealPlanService = inject(MealPlanService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialogRef = inject(MatDialogRef<RecipeRandomizerDialogComponent>);
  private readonly hostElementRef = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly data: RecipeRandomizerDialogData =
    inject<RecipeRandomizerDialogData | null>(MAT_DIALOG_DATA, { optional: true }) ?? {};

  private readonly dialogContentEl = viewChild('dialogContent', { read: ElementRef });

  protected readonly mealSlots = MEAL_SLOTS;
  protected readonly useTouchDatepicker = shouldUseTouchDatepicker();
  protected readonly isTouchDevice = signal<boolean>(isTouchPrimaryDevice());
  protected readonly formatMinutes = formatMinutes;
  protected readonly collapsedIngredientsLimit =
    RecipeRandomizerDialogComponent.COLLAPSED_INGREDIENTS_COUNT;
  protected readonly collapsedStepsLimit = RecipeRandomizerDialogComponent.COLLAPSED_STEPS_COUNT;

  protected readonly deck = signal<RecipeDetailDto[]>([]);
  protected readonly currentIndex = signal<number>(0);
  protected readonly isLoadingInitial = signal<boolean>(true);
  protected readonly isFetchingMore = signal<boolean>(false);
  protected readonly isSubmitting = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly addedRecipeIds = signal<Record<string, boolean>>({});
  protected readonly addedCount = signal<number>(0);

  protected readonly isDraggingCard = signal<boolean>(false);
  protected readonly isCardEntering = signal<boolean>(false);
  protected readonly isSnappingBack = signal<boolean>(false);
  protected readonly dragOffsetX = signal<number>(0);
  protected readonly lastMoveDirection = signal<SwipeDirection | null>(null);
  protected readonly animationKey = signal<number>(0);
  protected readonly outgoingSnapshot = signal<OutgoingCardSnapshot | null>(null);

  protected readonly placement = signal<'ondeck' | 'scheduled'>('ondeck');
  protected readonly scheduledDate = signal<Date>(
    coerceToDateLocal(this.data.initialDate ?? getTodayIsoLocal()),
  );
  protected readonly slot = signal<MealSlot>(this.data.initialSlot ?? 'Dinner');

  protected readonly currentRecipe = computed<RecipeDetailDto | null>(
    () => this.deck()[this.currentIndex()] ?? null,
  );

  protected readonly descriptionExpanded = linkedSignal({
    source: () => this.currentRecipe()?.id,
    computation: () => false,
  });

  protected readonly ingredientsExpanded = linkedSignal({
    source: () => this.currentRecipe()?.id,
    computation: () => false,
  });

  protected readonly stepsExpanded = linkedSignal({
    source: () => this.currentRecipe()?.id,
    computation: () => false,
  });

  protected readonly targetServings = linkedSignal({
    source: () => this.currentRecipe()?.id,
    computation: () => {
      const base = this.currentRecipe()?.baseServings;
      return base && base > 0 ? base : 4;
    },
  });

  protected readonly currentImageUrl = computed<string | null>(() => {
    const recipe = this.currentRecipe();
    return recipe ? recipe.coverImagePath || recipe.thumbnailImagePath || null : null;
  });

  protected readonly scaleFactor = computed<number>(() => {
    const recipe = this.currentRecipe();
    if (!recipe || recipe.baseServings <= 0) {
      return 1;
    }
    return this.targetServings() / recipe.baseServings;
  });

  protected readonly hasExpandableDescription = computed<boolean>(() => {
    const desc = this.currentRecipe()?.description?.trim() ?? '';
    return (
      desc.length > RecipeRandomizerDialogComponent.COLLAPSED_DESCRIPTION_CHARS ||
      desc.includes('\n')
    );
  });

  protected readonly scaledIngredients = computed<RandomizerIngredientItem[]>(() => {
    const recipe = this.currentRecipe();
    if (!recipe) {
      return [];
    }
    const factor = this.scaleFactor();
    return recipe.ingredients.map((ingredient) => ({
      ingredient,
      quantityAndUnit: scaleIngredient(ingredient, factor, 'Original').formattedMeasurement,
      prepNote: stripOuterParentheses(ingredient.preparationNote),
    }));
  });

  protected readonly visibleIngredients = computed<RandomizerIngredientItem[]>(() => {
    const items = this.scaledIngredients();
    return this.ingredientsExpanded()
      ? items
      : items.slice(0, RecipeRandomizerDialogComponent.COLLAPSED_INGREDIENTS_COUNT);
  });

  protected readonly hasExpandableIngredients = computed<boolean>(
    () =>
      (this.currentRecipe()?.ingredients.length ?? 0) >
      RecipeRandomizerDialogComponent.COLLAPSED_INGREDIENTS_COUNT,
  );

  protected readonly visibleSteps = computed(() => {
    const steps = this.currentRecipe()?.steps ?? [];
    return this.stepsExpanded()
      ? steps
      : steps.slice(0, RecipeRandomizerDialogComponent.COLLAPSED_STEPS_COUNT);
  });

  protected readonly hasExpandableSteps = computed<boolean>(() => {
    const steps = this.currentRecipe()?.steps ?? [];
    return (
      steps.length > RecipeRandomizerDialogComponent.COLLAPSED_STEPS_COUNT ||
      steps.some(
        (step) => step.instruction.length > RecipeRandomizerDialogComponent.COLLAPSED_STEP_CHARS,
      )
    );
  });

  protected readonly isCurrentRecipePlanned = computed<boolean>(() => {
    const id = this.currentRecipe()?.id;
    return id ? Boolean(this.addedRecipeIds()[id]) : false;
  });

  protected readonly activeDragDirection = computed<'next' | 'prev' | 'boundary' | null>(() => {
    if (!this.isDraggingCard() || Math.abs(this.dragOffsetX()) < 14) {
      return null;
    }
    if (this.dragOffsetX() < 0) {
      return 'next';
    }
    return this.currentIndex() > 0 ? 'prev' : 'boundary';
  });

  protected readonly isDragPastThreshold = computed<boolean>(
    () =>
      this.isDraggingCard() &&
      (this.dragOffsetX() <= -60 || (this.dragOffsetX() >= 60 && this.currentIndex() > 0)),
  );

  protected readonly dragIndicatorOpacity = computed<number>(() =>
    Math.min(1, Math.abs(this.dragOffsetX()) / 60),
  );

  protected readonly cardTransformStyle = computed<string | null>(() => {
    const x = this.dragOffsetX();
    return x === 0 ? null : `translate3d(${x}px, 0, 0) rotate(${(x * 0.035).toFixed(2)}deg)`;
  });

  protected readonly incomingAnimClass = computed<string>(() => {
    const key = this.animationKey();
    const dir = this.lastMoveDirection();
    if (!this.isCardEntering() || this.isDraggingCard() || !dir || key <= 0) {
      return '';
    }
    const suffix = key % 2 === 1 ? 'a' : 'b';
    return dir === 'prev' ? `slide-in-left-${suffix}` : `slide-in-right-${suffix}`;
  });

  private readonly seenIds = new Set<string>();
  private allUnseenLoaded = false;
  private isBatchRequestInFlight = false;
  private pendingAdvance: { direction: SwipeDirection; startOffsetX: number } | null = null;
  private swipeStartX: number | null = null;
  private swipeStartY: number | null = null;
  private swipePointerId: number | null = null;
  private swipeIntent: 'pending' | 'horizontal' | 'vertical' | null = null;
  private outgoingTimer: ReturnType<typeof setTimeout> | null = null;
  private snapBackTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
      const pointerMedia = window.matchMedia('(pointer: coarse)');
      const handlePointerChange = () => this.isTouchDevice.set(isTouchPrimaryDevice());
      pointerMedia?.addEventListener?.('change', handlePointerChange);
      this.destroyRef.onDestroy(() => {
        pointerMedia?.removeEventListener?.('change', handlePointerChange);
      });
    }

    this.destroyRef.onDestroy(() => {
      this.clearOutgoingTimer();
      this.clearSnapBackTimer();
    });
  }

  ngOnInit(): void {
    this.loadInitialBatch();
  }

  protected loadInitialBatch(): void {
    this.isLoadingInitial.set(true);
    this.isFetchingMore.set(false);
    this.errorMessage.set(null);
    this.seenIds.clear();
    this.allUnseenLoaded = false;
    this.isBatchRequestInFlight = false;
    this.pendingAdvance = null;
    this.lastMoveDirection.set(null);
    this.animationKey.set(0);
    this.isCardEntering.set(false);
    this.clearSnapBack();
    this.clearOutgoingSnapshot();

    this.recipeService
      .getRandomRecipes({ count: RecipeRandomizerDialogComponent.BATCH_SIZE })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (recipes) => {
          for (const recipe of recipes) {
            this.seenIds.add(recipe.id);
          }
          this.allUnseenLoaded = recipes.length < RecipeRandomizerDialogComponent.BATCH_SIZE;
          this.preloadBatchImages(recipes);
          this.deck.set(recipes);
          this.currentIndex.set(0);
          this.isLoadingInitial.set(false);
        },
        error: (err) => {
          this.isLoadingInitial.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to load random recipes.',
          );
        },
      });
  }

  protected previousRecipe(startOffsetX = 0): void {
    if (this.currentIndex() <= 0 || this.isSubmitting()) {
      return;
    }
    this.errorMessage.set(null);
    this.triggerCardTransition('prev', startOffsetX);
    this.currentIndex.update((i) => Math.max(0, i - 1));
    this.scrollContentToTop();
  }

  protected nextRecipe(startOffsetX = 0, direction: SwipeDirection = 'next'): void {
    if (this.isLoadingInitial() || this.isSubmitting() || this.deck().length === 0) {
      return;
    }

    this.errorMessage.set(null);
    const nextIdx = this.currentIndex() + 1;
    if (nextIdx < this.deck().length) {
      this.triggerCardTransition(direction, startOffsetX);
      this.currentIndex.set(nextIdx);
      this.scrollContentToTop();
      if (
        !this.allUnseenLoaded &&
        this.deck().length - 1 - nextIdx <= 2 &&
        !this.isBatchRequestInFlight
      ) {
        this.fetchNextBatch(false, direction);
      }
      return;
    }

    if (this.isBatchRequestInFlight) {
      this.pendingAdvance = { direction, startOffsetX };
      this.isFetchingMore.set(true);
      if (startOffsetX !== 0) {
        this.dragOffsetX.set(startOffsetX);
      }
      return;
    }

    this.fetchNextBatch(true, direction, startOffsetX);
  }

  private fetchNextBatch(
    advanceOnLoad: boolean,
    direction: SwipeDirection = 'next',
    startOffsetX = 0,
  ): void {
    if (this.allUnseenLoaded) {
      if (!advanceOnLoad || this.deck().length <= 1) {
        return;
      }

      this.wrapAroundAndFetchBatch(direction, startOffsetX);
      return;
    }

    this.isBatchRequestInFlight = true;
    if (advanceOnLoad) {
      this.pendingAdvance = { direction, startOffsetX };
      this.isFetchingMore.set(true);
      if (startOffsetX !== 0) {
        this.dragOffsetX.set(startOffsetX);
      }
    }

    this.recipeService
      .getRandomRecipes({
        count: RecipeRandomizerDialogComponent.BATCH_SIZE,
        excludeIds: Array.from(this.seenIds),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (batch) => {
          this.isBatchRequestInFlight = false;
          this.isFetchingMore.set(false);
          const pending = this.pendingAdvance;
          this.pendingAdvance = null;

          if (batch.length > 0) {
            for (const recipe of batch) {
              this.seenIds.add(recipe.id);
            }
            this.allUnseenLoaded = batch.length < RecipeRandomizerDialogComponent.BATCH_SIZE;
            this.preloadBatchImages(batch);
            this.deck.update((current) => [...current, ...batch]);
            if (pending) {
              this.dragOffsetX.set(0);
              this.triggerCardTransition(pending.direction, pending.startOffsetX);
              this.currentIndex.update((i) => i + 1);
              this.scrollContentToTop();
            }
            return;
          }

          this.allUnseenLoaded = true;
          if (pending && this.deck().length > 1) {
            this.wrapAroundAndFetchBatch(pending.direction, pending.startOffsetX);
            return;
          }
          if (this.dragOffsetX() !== 0) {
            this.dragOffsetX.set(0);
            this.startSnapBack();
          }
        },
        error: (err) => {
          this.isBatchRequestInFlight = false;
          this.pendingAdvance = null;
          this.isFetchingMore.set(false);
          if (this.dragOffsetX() !== 0) {
            this.dragOffsetX.set(0);
            this.startSnapBack();
          }
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to load more recipes.',
          );
        },
      });
  }

  private wrapAroundAndFetchBatch(direction: SwipeDirection = 'next', startOffsetX = 0): void {
    const currentId = this.currentRecipe()?.id;
    this.seenIds.clear();
    if (currentId) {
      this.seenIds.add(currentId);
    }
    this.allUnseenLoaded = false;
    this.isBatchRequestInFlight = true;
    this.pendingAdvance = { direction, startOffsetX };
    this.isFetchingMore.set(true);
    if (startOffsetX !== 0) {
      this.dragOffsetX.set(startOffsetX);
    }

    this.recipeService
      .getRandomRecipes({
        count: RecipeRandomizerDialogComponent.BATCH_SIZE,
        excludeIds: Array.from(this.seenIds),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (wrappedBatch) => {
          this.isBatchRequestInFlight = false;
          this.isFetchingMore.set(false);
          const pending = this.pendingAdvance;
          this.pendingAdvance = null;

          for (const recipe of wrappedBatch) {
            this.seenIds.add(recipe.id);
          }
          this.allUnseenLoaded = wrappedBatch.length < RecipeRandomizerDialogComponent.BATCH_SIZE;
          this.preloadBatchImages(wrappedBatch);

          if (wrappedBatch.length > 0 && pending) {
            this.dragOffsetX.set(0);
            this.triggerCardTransition(pending.direction, pending.startOffsetX);
            this.deck.update((current) => [...current, ...wrappedBatch]);
            this.currentIndex.update((i) => i + 1);
            this.scrollContentToTop();
          } else if (this.dragOffsetX() !== 0) {
            this.dragOffsetX.set(0);
            this.startSnapBack();
          }
        },
        error: (err) => {
          this.isBatchRequestInFlight = false;
          this.pendingAdvance = null;
          this.isFetchingMore.set(false);
          if (this.dragOffsetX() !== 0) {
            this.dragOffsetX.set(0);
            this.startSnapBack();
          }
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to load more recipes.',
          );
        },
      });
  }

  protected toggleDescription(): void {
    this.descriptionExpanded.update((v) => !v);
  }

  protected toggleIngredients(): void {
    this.ingredientsExpanded.update((v) => !v);
  }

  protected toggleSteps(): void {
    this.stepsExpanded.update((v) => !v);
  }

  protected adjustServings(delta: number): void {
    const next = Math.max(0.5, Math.min(999, Math.round((this.targetServings() + delta) * 2) / 2));
    this.targetServings.set(next);
  }

  protected setPlacement(value: 'ondeck' | 'scheduled'): void {
    this.placement.set(value);
  }

  protected getSelectedSlotMeta() {
    const current = this.slot();
    return this.mealSlots.find((s) => s.value === current) ?? this.mealSlots[2];
  }

  protected addCurrentToMealPlan(): void {
    const recipe = this.currentRecipe();
    if (!recipe || this.isSubmitting()) {
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const isOnDeck = this.placement() === 'ondeck';
    const scheduledDateIso = coerceToIsoDateLocal(this.scheduledDate());
    const weekStartDate = isOnDeck
      ? normalizeToSundayIso(this.data.initialWeekStartDate || scheduledDateIso)
      : normalizeToSundayIso(scheduledDateIso);

    this.mealPlanService
      .createEntry({
        entryType: 'Recipe',
        isOnDeck,
        weekStartDate,
        scheduledDate: isOnDeck ? null : scheduledDateIso,
        slot: isOnDeck ? null : this.slot(),
        recipeId: recipe.id,
        targetServings: this.targetServings(),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSubmitting.set(false);
          this.addedRecipeIds.update((prev) => ({ ...prev, [recipe.id]: true }));
          this.addedCount.update((count) => count + 1);
          this.data.onMealAdded?.();

          const destination = isOnDeck
            ? 'On-Deck'
            : `${scheduledDateIso} (${this.getSelectedSlotMeta().label})`;
          this.snackBar.open(`Added "${recipe.title}" to ${destination}`, 'Close', {
            duration: 3000,
          });
          this.nextRecipe(0, 'added');
        },
        error: (err) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to add recipe to meal plan.',
          );
        },
      });
  }

  protected onCardPointerDown(event: PointerEvent): void {
    if (event.pointerType === 'touch') {
      this.isTouchDevice.set(true);
    }
    if (event.isPrimary === false || this.swipePointerId !== null) {
      return;
    }
    if (event.pointerType === 'mouse' && event.button !== 0) {
      return;
    }
    const target = event.target as HTMLElement | null;
    if (
      target?.closest(
        'button, a, input, textarea, select, mat-select, mat-button-toggle-group, .mat-mdc-select-trigger',
      )
    ) {
      return;
    }
    this.swipeStartX = event.clientX;
    this.swipeStartY = event.clientY;
    this.swipePointerId = event.pointerId;
    this.swipeIntent = 'pending';
  }

  protected onCardPointerMove(event: PointerEvent): void {
    if (
      this.swipeStartX === null ||
      this.swipeStartY === null ||
      this.swipePointerId !== event.pointerId ||
      this.isSubmitting()
    ) {
      return;
    }

    const dx = event.clientX - this.swipeStartX;
    const dy = event.clientY - this.swipeStartY;

    if (this.swipeIntent === 'pending') {
      if (Math.abs(dy) >= 10 && Math.abs(dy) >= Math.abs(dx)) {
        this.swipeIntent = 'vertical';
        return;
      }
      if (Math.abs(dx) >= 10 && Math.abs(dx) > Math.abs(dy) * 1.2) {
        this.swipeIntent = 'horizontal';
        this.clearSnapBack();
        this.isCardEntering.set(false);
        this.isDraggingCard.set(true);
        (event.currentTarget as HTMLElement | null)?.setPointerCapture?.(event.pointerId);
      } else {
        return;
      }
    }

    if (this.swipeIntent === 'horizontal') {
      if (dx > 0 && this.currentIndex() === 0) {
        this.dragOffsetX.set(Math.min(36, Math.round(dx * 0.25)));
      } else {
        this.dragOffsetX.set(Math.round(Math.max(-220, Math.min(220, dx))));
      }
    }
  }

  protected onCardPointerUp(event: PointerEvent): void {
    if (this.swipePointerId !== null && this.swipePointerId !== event.pointerId) {
      return;
    }
    if (this.swipeStartX === null || this.swipeStartY === null) {
      this.resetSwipeState();
      return;
    }

    const dx = event.clientX - this.swipeStartX;
    const dy = event.clientY - this.swipeStartY;
    const releaseOffset = this.dragOffsetX();
    const intent = this.swipeIntent;

    const shouldSwipe =
      (intent === 'horizontal' && Math.abs(dx) >= 60) ||
      (intent === 'pending' && Math.abs(dx) >= 60 && Math.abs(dx) > Math.abs(dy) * 1.5);
    const canSwipe = (dx < 0 && this.deck().length > 0) || (dx > 0 && this.currentIndex() > 0);

    if (shouldSwipe && canSwipe) {
      this.clearSnapBack();
      this.resetSwipeState();
      if (dx < 0) {
        this.nextRecipe(releaseOffset);
      } else {
        this.previousRecipe(releaseOffset);
      }
      return;
    }

    if (releaseOffset !== 0) {
      this.startSnapBack();
    }
    this.resetSwipeState();
  }

  protected onCardPointerCancel(): void {
    if (this.dragOffsetX() !== 0) {
      this.startSnapBack();
    }
    this.resetSwipeState();
  }

  protected onCardAnimationEnd(event: Event): void {
    if (event.target === event.currentTarget) {
      this.isCardEntering.set(false);
    }
  }

  protected clearOutgoingSnapshot(): void {
    this.clearOutgoingTimer();
    this.outgoingSnapshot.set(null);
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (
      event.defaultPrevented ||
      event.altKey ||
      event.ctrlKey ||
      event.metaKey ||
      event.shiftKey
    ) {
      return;
    }
    const interactiveSelector = 'input, textarea, select, mat-select, [contenteditable="true"]';
    const target = event.target instanceof Element ? event.target : null;
    const activeEl = document.activeElement instanceof Element ? document.activeElement : null;
    if (target?.closest(interactiveSelector) || activeEl?.closest(interactiveSelector)) {
      return;
    }
    if (document.querySelector('.mat-datepicker-content, .mat-mdc-select-panel')) {
      return;
    }
    const openDialogs = document.querySelectorAll('mat-dialog-container');
    const topDialog = openDialogs[openDialogs.length - 1];
    if (topDialog && !topDialog.contains(this.hostElementRef.nativeElement)) {
      return;
    }
    if (event.key === 'ArrowRight') {
      event.preventDefault();
      this.nextRecipe();
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault();
      this.previousRecipe();
    }
  }

  protected closeDialog(): void {
    this.dialogRef.close(this.addedCount() > 0);
  }

  private triggerCardTransition(direction: SwipeDirection, startOffsetX = 0): void {
    this.clearOutgoingTimer();
    this.clearSnapBack();
    const nextKey = this.animationKey() + 1;
    const current = this.currentRecipe();
    if (current) {
      this.outgoingSnapshot.set({
        key: nextKey,
        id: current.id,
        title: current.title,
        imageUrl: this.currentImageUrl(),
        direction,
        badgeLabel:
          direction === 'prev' ? 'Previous' : direction === 'added' ? 'Planned!' : 'Next Recipe',
        badgeIcon:
          direction === 'prev'
            ? 'arrow_back'
            : direction === 'added'
              ? 'check_circle'
              : 'arrow_forward',
        startOffsetX,
        startRotationDeg: Number((startOffsetX * 0.035).toFixed(2)),
        fromDrag: startOffsetX !== 0,
      });
    }
    this.lastMoveDirection.set(direction);
    this.animationKey.set(nextKey);
    this.isCardEntering.set(true);
    this.outgoingTimer = setTimeout(() => {
      this.outgoingSnapshot.set(null);
      this.outgoingTimer = null;
    }, 280);
  }

  private preloadBatchImages(recipes: RecipeDetailDto[]): void {
    if (typeof Image === 'undefined') {
      return;
    }
    for (const recipe of recipes) {
      const imageUrl = recipe.coverImagePath || recipe.thumbnailImagePath;
      if (imageUrl) {
        const img = new Image();
        img.src = imageUrl;
      }
    }
  }

  private startSnapBack(): void {
    this.clearSnapBackTimer();
    this.isSnappingBack.set(true);
    this.snapBackTimer = setTimeout(() => {
      this.isSnappingBack.set(false);
      this.snapBackTimer = null;
    }, 220);
  }

  private clearSnapBack(): void {
    this.clearSnapBackTimer();
    this.isSnappingBack.set(false);
  }

  private clearSnapBackTimer(): void {
    if (this.snapBackTimer !== null) {
      clearTimeout(this.snapBackTimer);
      this.snapBackTimer = null;
    }
  }

  private clearOutgoingTimer(): void {
    if (this.outgoingTimer !== null) {
      clearTimeout(this.outgoingTimer);
      this.outgoingTimer = null;
    }
  }

  private resetSwipeState(): void {
    this.swipeStartX = null;
    this.swipeStartY = null;
    this.swipePointerId = null;
    this.swipeIntent = null;
    this.isDraggingCard.set(false);
    this.dragOffsetX.set(0);
  }

  private scrollContentToTop(): void {
    const el = this.dialogContentEl()?.nativeElement as HTMLElement | undefined;
    if (el) {
      el.scrollTop = 0;
    }
  }
}
