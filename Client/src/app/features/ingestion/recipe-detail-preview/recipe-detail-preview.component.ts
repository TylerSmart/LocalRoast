import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  RecipePreviewData,
  RecipePreviewIngredient,
  RecipePreviewStep,
  UnitSystem,
} from '../../../core/models/recipe.models';
import {
  FormattedQuantityResult,
  formatMinutes,
  scaleIngredient,
} from '../../../core/utils/culinary-math';
import {
  NumberedInstructionStep,
  formatIngredientUses,
  ingredientUses,
  isIngredientDivided,
  stepIngredientCallouts,
} from '../../../core/utils/step-instruction';
import { StepInstructionComponent } from '../../../shared/components/step-instruction/step-instruction.component';

export interface ScaledPreviewIngredientItem {
  ingredient: RecipePreviewIngredient;
  scaled: FormattedQuantityResult;
  displayQuantityAndUnit: string;
}

export interface ScaledPreviewStepItem {
  step: RecipePreviewStep;
  instructionStep: NumberedInstructionStep;
  ingredientCallouts: { id: string; label: string }[];
}

export interface PreviewIngredientSectionGroup {
  sectionName: string;
  items: ScaledPreviewIngredientItem[];
}

export interface PreviewStepSectionGroup {
  sectionName: string;
  items: ScaledPreviewStepItem[];
}

@Component({
  selector: 'app-recipe-detail-preview',
  imports: [
    FormsModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatCheckboxModule,
    MatChipsModule,
    MatDividerModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    StepInstructionComponent,
  ],
  templateUrl: './recipe-detail-preview.component.html',
  styleUrl: './recipe-detail-preview.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeDetailPreviewComponent {
  readonly recipe = input.required<RecipePreviewData>();
  readonly showPreviewBanner = input<boolean>(true);
  readonly showPublishButton = input<boolean>(false);
  readonly isPublishing = input<boolean>(false);

  readonly publishRequested = output<void>();

  readonly targetServings = linkedSignal(() => this.recipe().baseServings || 4);
  readonly targetSystem = signal<UnitSystem>('Original');

  readonly scaleFactor = computed(() => {
    const base = this.recipe().baseServings;
    if (!base || base <= 0) return 1;
    return this.targetServings() / base;
  });

  // Map of ingredient index -> Scaled item for quick lookup
  readonly scaledIngredientsMap = computed(() => {
    const r = this.recipe();
    const factor = this.scaleFactor();
    const sys = this.targetSystem();
    const map = new Map<number, ScaledPreviewIngredientItem>();

    for (const ing of r.ingredients) {
      const scaled = scaleIngredient(ing, factor, sys);

      const qtyAndUnit = scaled.formattedMeasurement;

      map.set(ing.index, {
        ingredient: ing,
        scaled,
        displayQuantityAndUnit: qtyAndUnit,
      });
    }

    return map;
  });

  readonly groupedIngredients = computed<PreviewIngredientSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    const sectionsMap = new Map<string, ScaledPreviewIngredientItem[]>();

    for (const ing of r.ingredients) {
      const section = ing.sectionName?.trim() || '';
      if (!sectionsMap.has(section)) {
        sectionsMap.set(section, []);
      }
      const item = map.get(ing.index);
      if (item) {
        sectionsMap.get(section)!.push(item);
      }
    }

    return Array.from(sectionsMap.entries()).map(([sectionName, items]) => ({
      sectionName,
      items,
    }));
  });

  readonly displayIngredients = computed(() =>
    Array.from(this.scaledIngredientsMap().values()).map((item) => item.ingredient),
  );

  /** Preview steps with inline amounts keyed by preview ingredient ids (`ing-N`). */
  readonly instructionSteps = computed<NumberedInstructionStep[]>(() => {
    const map = this.scaledIngredientsMap();
    return this.recipe().steps.map((step) => ({
      stepNumber: step.stepNumber,
      instruction: step.instruction,
      ingredientIds: (step.ingredientIndices ?? []).flatMap((idx) => {
        const item = map.get(idx);
        return item ? [item.ingredient.id] : [];
      }),
    }));
  });

  readonly ingredientUsesMap = computed(() => {
    const result = new Map<string, string>();
    const steps = this.instructionSteps();
    const ingredients = this.displayIngredients();
    for (const item of this.scaledIngredientsMap().values()) {
      const uses = ingredientUses(
        steps,
        item.ingredient.id,
        ingredients,
        this.scaleFactor(),
        this.targetSystem(),
      );
      if (
        isIngredientDivided(
          steps,
          item.ingredient.id,
          ingredients,
          this.scaleFactor(),
          this.targetSystem(),
        )
      ) {
        result.set(item.ingredient.id, formatIngredientUses(uses));
      }
    }
    return result;
  });

  readonly groupedSteps = computed<PreviewStepSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    const instructionSteps = this.instructionSteps();
    const sectionsMap = new Map<string, ScaledPreviewStepItem[]>();

    for (const [stepIndex, step] of r.steps.entries()) {
      const instructionStep = instructionSteps[stepIndex];
      const section = step.sectionName?.trim() || '';
      if (!sectionsMap.has(section)) {
        sectionsMap.set(section, []);
      }

      const callouts = stepIngredientCallouts(
        {
          ...instructionStep,
          ingredientIds: instructionStep.ingredientIds ?? [],
          ingredientAmounts: (step.ingredientAmounts ?? []).flatMap((amount) => {
            const item = map.get(amount.ingredientIndex);
            return item
              ? [{ ingredientId: item.ingredient.id, measurement: amount.measurement }]
              : [];
          }),
        },
        this.displayIngredients(),
        this.scaleFactor(),
        this.targetSystem(),
      );

      sectionsMap.get(section)!.push({
        step,
        instructionStep,
        ingredientCallouts: callouts,
      });
    }

    return Array.from(sectionsMap.entries()).map(([sectionName, items]) => ({
      sectionName,
      items,
    }));
  });

  adjustServings(delta: number): void {
    const next = Math.max(1, this.targetServings() + delta);
    this.targetServings.set(next);
  }

  setMultiplier(mult: number): void {
    const base = this.recipe().baseServings || 4;
    this.targetServings.set(Math.max(0.5, Math.round(base * mult * 2) / 2));
  }

  formatMinutes(minutes: number | null | undefined): string {
    return formatMinutes(minutes);
  }

  formatTimerDuration(seconds: number): string {
    if (!seconds || seconds <= 0) return '';
    const mins = Math.floor(seconds / 60);
    const secs = seconds % 60;
    if (mins === 0) return `${secs}s`;
    if (secs === 0) return `${mins} min${mins === 1 ? '' : 's'}`;
    return `${mins}m ${secs}s`;
  }

  onPublish(): void {
    this.publishRequested.emit();
  }
}
