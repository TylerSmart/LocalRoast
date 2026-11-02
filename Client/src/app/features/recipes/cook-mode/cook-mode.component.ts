import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router } from '@angular/router';
import {
  CookLogDto,
  RecipeDetailDto,
  RecipeIngredientDto,
  RecipeStepDto,
  RecipeStepTimerDto,
  SubRecipePreviewDto,
  UnitSystem,
} from '../../../core/models/recipe.models';
import { KitchenTimerService } from '../../../core/services/kitchen-timer/kitchen-timer.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { WakeLockService } from '../../../core/services/wake-lock/wake-lock.service';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { IngredientSubstitutionDialogComponent } from '../ingredient-substitution-dialog/ingredient-substitution-dialog.component';
import {
  FormattedQuantityResult,
  formatCountdown,
  formatDuration,
  scaleAndConvert,
} from '../../../core/utils/culinary-math';
import {
  CookLogDialogComponent,
  CookLogDialogData,
} from '../cook-log-dialog/cook-log-dialog.component';

export interface ScaledCookIngredient {
  ingredient: RecipeIngredientDto;
  scaled: FormattedQuantityResult;
  displayQuantityAndUnit: string;
}

export interface ScaledCookStep {
  step: RecipeStepDto;
  ingredientCallouts: { id: string; label: string }[];
}

export interface CookIngredientSection {
  sectionName: string;
  items: ScaledCookIngredient[];
}

@Component({
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatCheckboxModule,
    MatExpansionModule,
    MatTooltipModule,
    MatDividerModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-cook-mode',
  templateUrl: './cook-mode.component.html',
  styleUrl: './cook-mode.component.scss',
  host: {
    '(window:keydown)': 'onWindowKeydown($event)',
  },
})
export class CookModeComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly recipeService = inject(RecipeService);
  protected readonly wakeLockService = inject(WakeLockService);
  protected readonly kitchenTimerService = inject(KitchenTimerService);
  protected readonly aiHelper = inject(AiKitchenHelperService);
  private readonly dialog = inject(MatDialog);

  protected readonly recipe = signal<RecipeDetailDto | null>(null);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly targetServings = signal<number>(4);
  protected readonly targetSystem = signal<UnitSystem>('Original');
  protected readonly fromSource = signal<'meal-plan' | 'recipe'>('recipe');

  protected readonly activeStepIndex = signal<number>(0);
  protected readonly checkedIngredients = signal<Record<string, boolean>>({});
  protected readonly checkedSteps = signal<Record<string, boolean>>({});
  protected readonly isMobileDrawerOpen = signal<boolean>(false);
  protected readonly isNoteCalloutDismissed = signal<boolean>(false);

  // Custom timer adder state
  protected readonly showCustomTimerForm = signal<boolean>(false);
  protected readonly customTimerMinutes = signal<number>(5);
  protected readonly customTimerLabel = signal<string>('Kitchen Timer');

  protected readonly formatCountdown = formatCountdown;
  protected readonly formatDuration = formatDuration;

  protected readonly scaleFactor = computed(() => {
    const r = this.recipe();
    if (!r || r.baseServings <= 0) return 1;
    return this.targetServings() / r.baseServings;
  });

  protected readonly scaledIngredientsMap = computed(() => {
    const r = this.recipe();
    const factor = this.scaleFactor();
    const sys = this.targetSystem();
    const map = new Map<string, ScaledCookIngredient>();

    if (!r) return map;

    for (const ing of r.ingredients) {
      const scaled = scaleAndConvert(
        ing.quantity,
        ing.maxQuantity,
        ing.unit,
        ing.customUnitName,
        factor,
        sys,
      );

      const qtyAndUnit = [scaled.formattedAmount, scaled.unitLabel]
        .filter((s) => s.length > 0)
        .join(' ');

      map.set(ing.id, {
        ingredient: ing,
        scaled,
        displayQuantityAndUnit: qtyAndUnit,
      });
    }

    return map;
  });

  protected readonly groupedIngredients = computed<CookIngredientSection[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    if (!r) return [];

    const sectionsMap = new Map<string, ScaledCookIngredient[]>();
    for (const ing of r.ingredients) {
      const section = ing.sectionName?.trim() || '';
      if (!sectionsMap.has(section)) {
        sectionsMap.set(section, []);
      }
      const item = map.get(ing.id);
      if (item) {
        sectionsMap.get(section)!.push(item);
      }
    }

    return Array.from(sectionsMap.entries()).map(([sectionName, items]) => ({
      sectionName,
      items,
    }));
  });

  protected readonly subRecipesList = computed<SubRecipePreviewDto[]>(() => {
    return this.recipe()?.linkedSubRecipes ?? [];
  });

  protected readonly flattenedSteps = computed<ScaledCookStep[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    if (!r) return [];

    return r.steps.map((step) => {
      const callouts = step.ingredientIds
        .map((id) => {
          const item = map.get(id);
          if (!item) return null;
          const label = item.displayQuantityAndUnit
            ? `${item.displayQuantityAndUnit} ${item.ingredient.displayName}`
            : item.ingredient.displayName;
          return { id, label };
        })
        .filter((c): c is { id: string; label: string } => c !== null);

      return { step, ingredientCallouts: callouts };
    });
  });

  protected readonly currentStep = computed<ScaledCookStep | null>(() => {
    const steps = this.flattenedSteps();
    const idx = this.activeStepIndex();
    if (idx >= 0 && idx < steps.length) {
      return steps[idx];
    }
    return null;
  });

  protected readonly totalStepsCount = computed<number>(() => this.flattenedSteps().length);

  protected readonly totalIngredientsCount = computed<number>(() => {
    return this.recipe()?.ingredients.length ?? 0;
  });

  protected readonly checkedIngredientsCount = computed<number>(() => {
    const checked = this.checkedIngredients();
    return Object.values(checked).filter(Boolean).length;
  });

  protected readonly latestTweakNote = computed<CookLogDto | null>(() => {
    const logs = this.recipe()?.cookLogs;
    if (!logs || logs.length === 0) return null;
    return logs.find((l) => !!l.notes && l.notes.trim().length > 0) ?? null;
  });

  ngOnInit(): void {
    this.aiHelper.checkStatus().subscribe();
    const recipeId = this.route.snapshot.paramMap.get('id');
    if (!recipeId) {
      this.errorMessage.set('Recipe ID is missing.');
      this.isLoading.set(false);
      return;
    }

    const qp = this.route.snapshot.queryParamMap;
    if (qp.has('servings')) {
      const parsed = parseFloat(qp.get('servings')!);
      if (!isNaN(parsed) && parsed > 0) {
        this.targetServings.set(parsed);
      }
    }

    if (qp.has('units')) {
      const units = qp.get('units') as UnitSystem;
      if (units === 'Original' || units === 'UsCustomary' || units === 'Metric') {
        this.targetSystem.set(units);
      }
    }

    if (qp.get('from') === 'meal-plan') {
      this.fromSource.set('meal-plan');
    }

    void this.wakeLockService.acquire();
    this.loadRecipe(recipeId);
  }

  ngOnDestroy(): void {
    void this.wakeLockService.release();
  }

  private loadRecipe(id: string): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.recipeService.getRecipeById(id).subscribe({
      next: (data) => {
        this.recipe.set(data);
        if (!this.route.snapshot.queryParamMap.has('servings')) {
          this.targetServings.set(data.baseServings);
        }
        this.isLoading.set(false);
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail || err?.error?.title || 'Failed to load recipe.');
        this.isLoading.set(false);
      },
    });
  }

  protected onWindowKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA')) {
      return;
    }

    if (event.key === 'ArrowRight') {
      this.nextStep();
    } else if (event.key === 'ArrowLeft') {
      this.prevStep();
    }
  }

  protected prevStep(): void {
    const current = this.activeStepIndex();
    if (current > 0) {
      this.activeStepIndex.set(current - 1);
    }
  }

  protected nextStep(): void {
    const current = this.activeStepIndex();
    if (current < this.totalStepsCount() - 1) {
      this.activeStepIndex.set(current + 1);
    } else {
      this.openCookLogDialog();
    }
  }

  protected goToStep(index: number): void {
    if (index >= 0 && index < this.totalStepsCount()) {
      this.activeStepIndex.set(index);
    }
  }

  protected toggleIngredient(id: string): void {
    this.checkedIngredients.update((map) => ({
      ...map,
      [id]: !map[id],
    }));
  }

  openIngredientSubstitution(item: ScaledCookIngredient, event?: Event): void {
    event?.stopPropagation();
    const r = this.recipe();
    if (!r) return;

    this.dialog.open(IngredientSubstitutionDialogComponent, {
      width: '540px',
      maxWidth: '95vw',
      data: {
        recipeId: r.id,
        recipeTitle: r.title,
        recipeIngredientId: item.ingredient.id,
        ingredientDisplayName: item.ingredient.displayName,
        scaledAmountDisplay: item.displayQuantityAndUnit,
        targetServings: this.targetServings(),
      },
    });
  }

  openIngredientSubstitutionById(ingredientId: string, event?: Event): void {
    event?.stopPropagation();
    const item = this.scaledIngredientsMap().get(ingredientId);
    if (item) {
      this.openIngredientSubstitution(item, event);
    }
  }

  protected toggleStepChecked(stepId: string): void {
    this.checkedSteps.update((map) => ({
      ...map,
      [stepId]: !map[stepId],
    }));
  }

  protected adjustServings(delta: number): void {
    const current = this.targetServings();
    const next = Math.max(0.5, Math.min(999, Math.round((current + delta) * 2) / 2));
    this.targetServings.set(next);
  }

  protected setMultiplier(mult: number): void {
    const r = this.recipe();
    if (r && r.baseServings > 0) {
      this.targetServings.set(Math.max(0.5, Math.round(r.baseServings * mult * 2) / 2));
    }
  }

  protected setUnitSystem(system: UnitSystem): void {
    this.targetSystem.set(system);
  }

  protected toggleMobileDrawer(): void {
    this.isMobileDrawerOpen.update((open) => !open);
  }

  protected closeMobileDrawer(): void {
    this.isMobileDrawerOpen.set(false);
  }

  protected dismissNoteCallout(): void {
    this.isNoteCalloutDismissed.set(true);
  }

  protected startStepTimer(timer: RecipeStepTimerDto, stepNumber: number): void {
    const r = this.recipe();
    if (!r) return;
    this.kitchenTimerService.startTimer(
      r.id,
      r.title,
      timer.label,
      timer.durationSeconds,
      stepNumber,
    );
  }

  protected toggleCustomTimerForm(): void {
    this.showCustomTimerForm.update((show) => !show);
  }

  protected submitCustomTimer(): void {
    const r = this.recipe();
    if (!r) return;
    const minutes = Math.max(1, this.customTimerMinutes());
    const label = this.customTimerLabel().trim() || 'Kitchen Timer';
    this.kitchenTimerService.startTimer(r.id, r.title, label, minutes * 60, null);
    this.showCustomTimerForm.set(false);
    this.customTimerLabel.set('Kitchen Timer');
  }

  protected openCookLogDialog(): void {
    const r = this.recipe();
    if (!r) return;

    const dialogData: CookLogDialogData = {
      recipeId: r.id,
      recipeTitle: r.title,
      targetServings: this.targetServings(),
    };

    const ref = this.dialog.open(CookLogDialogComponent, {
      data: dialogData,
      width: '520px',
      maxWidth: '96vw',
    });

    ref.afterClosed().subscribe((result) => {
      if (result) {
        this.exitCookMode();
      }
    });
  }

  protected exitCookMode(): void {
    const r = this.recipe();
    if (this.fromSource() === 'meal-plan') {
      void this.router.navigate(['/meal-plan']);
    } else if (r) {
      void this.router.navigate(['/recipes', r.id]);
    } else {
      void this.router.navigate(['/recipes']);
    }
  }
}
