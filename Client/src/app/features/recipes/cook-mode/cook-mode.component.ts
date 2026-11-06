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
import {
  ActiveKitchenTimer,
  KitchenTimerService,
} from '../../../core/services/kitchen-timer/kitchen-timer.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { WakeLockService } from '../../../core/services/wake-lock/wake-lock.service';
import { SpeechNarrationService } from '../../../core/services/speech-narration/speech-narration.service';
import {
  VoiceCommandEvent,
  VoiceControlService,
} from '../../../core/services/voice-control/voice-control.service';
import { Subscription } from 'rxjs';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { IngredientSubstitutionDialogComponent } from '../ingredient-substitution-dialog/ingredient-substitution-dialog.component';
import {
  FormattedQuantityResult,
  formatCountdown,
  formatDuration,
  scaleIngredient,
  formatStepIngredient,
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
  protected readonly speechNarrationService = inject(SpeechNarrationService);
  protected readonly voiceControlService = inject(VoiceControlService);
  protected readonly aiHelper = inject(AiKitchenHelperService);
  private readonly dialog = inject(MatDialog);

  protected readonly showVoiceHelp = signal<boolean>(false);
  private voiceSub?: Subscription;

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

    for (const original of r.ingredients) {
      const ing = {
        ...original,
        preferAlternativeMeasurement:
          this.measurementPreferences()[original.id] ?? original.preferAlternativeMeasurement,
      };
      const scaled = scaleIngredient(ing, factor, sys);

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
          const label = formatStepIngredient(
            item.ingredient,
            step.ingredientAmounts?.find((a) => a.ingredientId === id)?.measurement,
            this.scaleFactor(),
            this.targetSystem(),
          );
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
  protected readonly measurementPreferences = signal<Record<string, boolean>>({});

  toggleIngredientMeasurement(ingredient: RecipeIngredientDto): void {
    this.measurementPreferences.update((current) => ({
      ...current,
      [ingredient.id]: !(current[ingredient.id] ?? ingredient.preferAlternativeMeasurement),
    }));
  }
  scaleSubRecipeIngredient(ingredient: RecipeIngredientDto, sub: SubRecipePreviewDto): string {
    const quantity =
      this.recipe()?.ingredients.find((i) => i.linkedRecipeId === sub.id)?.quantity ?? 1;
    const scaled = scaleIngredient(ingredient, this.scaleFactor() * quantity, this.targetSystem());
    return [scaled.formattedAmount, scaled.unitLabel, ingredient.displayName]
      .filter(Boolean)
      .join(' ');
  }

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

    this.voiceSub = this.voiceControlService.command$.subscribe((cmd) => {
      this.handleVoiceCommand(cmd);
    });

    if (this.voiceControlService.isEnabled()) {
      this.voiceControlService.startListening();
    }

    void this.wakeLockService.acquire();
    this.loadRecipe(recipeId);
  }

  ngOnDestroy(): void {
    void this.wakeLockService.release();
    this.speechNarrationService.stop();
    this.voiceControlService.stopListening();
    this.voiceSub?.unsubscribe();
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
        this.narrateCurrentStepIfEnabled();
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail || err?.error?.title || 'Failed to load recipe.');
        this.isLoading.set(false);
      },
    });
  }

  protected onWindowKeydown(event: KeyboardEvent): void {
    if (event.repeat) {
      return;
    }

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
      this.narrateCurrentStepIfEnabled();
    }
  }

  protected nextStep(): void {
    const current = this.activeStepIndex();
    if (current < this.totalStepsCount() - 1) {
      this.activeStepIndex.set(current + 1);
      this.narrateCurrentStepIfEnabled();
    } else {
      this.speechNarrationService.stop();
      this.openCookLogDialog();
    }
  }

  protected goToStep(index: number): void {
    if (index >= 0 && index < this.totalStepsCount()) {
      this.activeStepIndex.set(index);
      this.narrateCurrentStepIfEnabled();
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
    this.speechNarrationService.stop();
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
    this.speechNarrationService.stop();
    this.voiceControlService.stopListening();
    const r = this.recipe();
    if (this.fromSource() === 'meal-plan') {
      void this.router.navigate(['/meal-plan']);
    } else if (r) {
      void this.router.navigate(['/recipes', r.id]);
    } else {
      void this.router.navigate(['/recipes']);
    }
  }

  protected narrateCurrentStep(): void {
    const step = this.currentStep();
    if (step) {
      const amounts = step.ingredientCallouts.filter((c) =>
        step.step.ingredientAmounts?.some((a) => a.ingredientId === c.id),
      );
      const instruction = amounts.length
        ? `Ingredients for this step: ${amounts.map((a) => a.label).join(', ')}. ${step.step.instruction}`
        : step.step.instruction;
      this.speechNarrationService.speakStep(
        step.step.stepNumber,
        instruction,
        step.step.sectionName,
      );
    }
  }

  protected narrateCurrentStepIfEnabled(): void {
    if (this.speechNarrationService.isEnabled()) {
      this.narrateCurrentStep();
    }
  }

  protected toggleNarration(): void {
    const isNowEnabled = this.speechNarrationService.toggleNarration();
    if (isNowEnabled) {
      this.narrateCurrentStep();
    }
  }

  protected toggleStepAudioManual(): void {
    if (this.speechNarrationService.isSpeaking()) {
      this.speechNarrationService.stop();
    } else {
      this.narrateCurrentStep();
    }
  }

  protected toggleVoiceControl(): void {
    this.voiceControlService.toggleListening();
  }

  protected toggleVoiceHelp(): void {
    this.showVoiceHelp.update((v) => !v);
  }

  private lastVoiceCommandTime = 0;

  protected handleVoiceCommand(cmd: VoiceCommandEvent): void {
    const now = Date.now();
    // Cooldown guard to prevent rapid duplicate voice actions
    if (now - this.lastVoiceCommandTime < 800) {
      return;
    }
    this.lastVoiceCommandTime = now;

    switch (cmd.action) {
      case 'next':
        this.nextStep();
        break;
      case 'previous':
        this.prevStep();
        break;
      case 'repeat':
        this.narrateCurrentStep();
        break;
      case 'timer':
        this.startCurrentStepTimer(cmd.transcript);
        break;
      case 'pause_timer':
        this.pauseActiveTimerVoice(cmd.transcript);
        break;
      case 'resume_timer':
        this.resumeActiveTimerVoice(cmd.transcript);
        break;
      case 'restart_timer':
        this.restartActiveTimerVoice(cmd.transcript);
        break;
      case 'cancel_timer':
        this.cancelActiveTimerVoice(cmd.transcript);
        break;
      case 'check': {
        const current = this.currentStep();
        if (current) {
          this.toggleStepChecked(current.step.id);
        }
        break;
      }
      case 'help':
        this.showVoiceHelp.set(true);
        break;
      case 'stop':
        this.speechNarrationService.stop();
        break;
    }
  }

  private findStepOrActiveTimer(
    predicate?: (timer: ActiveKitchenTimer) => boolean,
    transcript?: string,
  ): ActiveKitchenTimer | null {
    const allTimers = this.kitchenTimerService.timers();
    const filtered = predicate ? allTimers.filter(predicate) : allTimers;
    if (filtered.length === 0) return null;

    if (transcript) {
      const lower = transcript.toLowerCase();
      // Match by duration mention (e.g. "10 minute", "20 min")
      for (const t of filtered) {
        const mins = Math.round(t.totalDurationSeconds / 60);
        if (
          (mins > 0 && (lower.includes(`${mins} min`) || lower.includes(`${mins} minute`))) ||
          (t.totalDurationSeconds < 60 && lower.includes(`${t.totalDurationSeconds} sec`))
        ) {
          return t;
        }
      }
      // Match by ordinal
      if (lower.includes('second') || lower.includes(' 2') || lower.includes('two')) {
        if (filtered.length > 1) return filtered[1];
      } else if (lower.includes('first') || lower.includes(' 1') || lower.includes('one')) {
        return filtered[0];
      }
    }

    const currentStepNum = this.currentStep()?.step.stepNumber;
    if (currentStepNum != null) {
      const stepMatch = filtered.find((t) => t.stepNumber === currentStepNum);
      if (stepMatch) return stepMatch;
    }

    return filtered[filtered.length - 1];
  }

  protected pauseActiveTimerVoice(transcript?: string): void {
    const target = this.findStepOrActiveTimer((t) => t.state === 'running', transcript);
    if (!target) {
      this.voiceControlService.setFeedback('No running timer to pause', 2500);
      return;
    }
    this.kitchenTimerService.pauseTimer(target.id);
    this.voiceControlService.setFeedback(`Paused timer: ${target.label}`);
  }

  protected resumeActiveTimerVoice(transcript?: string): void {
    const target = this.findStepOrActiveTimer((t) => t.state === 'paused', transcript);
    if (!target) {
      this.voiceControlService.setFeedback('No paused timer to resume', 2500);
      return;
    }
    this.kitchenTimerService.resumeTimer(target.id);
    this.voiceControlService.setFeedback(`Resumed timer: ${target.label}`);
  }

  protected restartActiveTimerVoice(transcript?: string): void {
    const target = this.findStepOrActiveTimer(undefined, transcript);
    if (!target) {
      const current = this.currentStep();
      if (current && current.step.timers.length > 0) {
        const stepTimer = current.step.timers[0];
        this.startStepTimer(stepTimer, current.step.stepNumber);
        this.voiceControlService.setFeedback(`Started timer: ${stepTimer.label}`);
        return;
      }
      this.voiceControlService.setFeedback('No timer to restart', 2500);
      return;
    }
    this.kitchenTimerService.restartTimer(target.id);
    this.voiceControlService.setFeedback(`Restarted timer: ${target.label}`);
  }

  protected cancelActiveTimerVoice(transcript?: string): void {
    const target = this.findStepOrActiveTimer(undefined, transcript);
    if (!target) {
      this.voiceControlService.setFeedback('No timer to cancel', 2500);
      return;
    }
    this.kitchenTimerService.cancelTimer(target.id);
    this.voiceControlService.setFeedback(`Cancelled timer: ${target.label}`);
  }

  protected startCurrentStepFirstTimer(): void {
    this.startCurrentStepTimer();
  }

  protected startCurrentStepTimer(transcript?: string): void {
    const current = this.currentStep();
    if (!current || current.step.timers.length === 0) {
      this.voiceControlService.setFeedback('No timer on this step', 2500);
      return;
    }

    const timers = current.step.timers;
    let targetTimer: RecipeStepTimerDto | undefined;

    if (transcript) {
      const lower = transcript.toLowerCase();

      // Check for ordinal/number references: "first", "second", "third", "1", "2", "3"
      if (lower.includes('second') || lower.includes(' 2') || lower.includes('two')) {
        targetTimer = timers[1];
      } else if (lower.includes('third') || lower.includes(' 3') || lower.includes('three')) {
        targetTimer = timers[2];
      } else if (lower.includes('first') || lower.includes(' 1') || lower.includes('one')) {
        targetTimer = timers[0];
      }

      // Check for duration mention in transcript (e.g. "10 minute", "20 min", "45 second")
      if (!targetTimer) {
        for (const t of timers) {
          const mins = Math.round(t.durationSeconds / 60);
          if (
            (mins > 0 && (lower.includes(`${mins} min`) || lower.includes(`${mins} minute`))) ||
            (t.durationSeconds < 60 && lower.includes(`${t.durationSeconds} sec`))
          ) {
            targetTimer = t;
            break;
          }
        }
      }
    }

    // Default sequential fallback: find the first timer on this step that isn't already active
    if (!targetTimer) {
      const activeTimers = this.kitchenTimerService.timers();
      const unstarted = timers.find(
        (st) =>
          !activeTimers.some(
            (at) =>
              at.stepNumber === current.step.stepNumber &&
              at.totalDurationSeconds === st.durationSeconds &&
              at.state !== 'completed',
          ),
      );
      targetTimer = unstarted ?? timers[0];
    }

    const index = timers.indexOf(targetTimer);
    const countInfo = timers.length > 1 ? ` (${index + 1} of ${timers.length})` : '';
    this.startStepTimer(targetTimer, current.step.stepNumber);
    this.voiceControlService.setFeedback(
      `Started timer: ${targetTimer.label} · ${formatDuration(targetTimer.durationSeconds)}${countInfo}`,
    );
  }
}
