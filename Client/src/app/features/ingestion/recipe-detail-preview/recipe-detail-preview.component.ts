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
  scaleAndConvert,
} from '../../../core/utils/culinary-math';

export interface ScaledPreviewIngredientItem {
  ingredient: RecipePreviewIngredient;
  scaled: FormattedQuantityResult;
  displayQuantityAndUnit: string;
}

export interface ScaledPreviewStepItem {
  step: RecipePreviewStep;
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
      const scaled = scaleAndConvert(
        ing.quantity ?? null,
        ing.maxQuantity ?? null,
        ing.unit ?? null,
        ing.customUnitName ?? null,
        factor,
        sys,
      );

      const qtyAndUnit = [scaled.formattedAmount, scaled.unitLabel]
        .filter((s) => s.length > 0)
        .join(' ');

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

  readonly groupedSteps = computed<PreviewStepSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    const sectionsMap = new Map<string, ScaledPreviewStepItem[]>();

    for (const step of r.steps) {
      const section = step.sectionName?.trim() || '';
      if (!sectionsMap.has(section)) {
        sectionsMap.set(section, []);
      }

      const callouts = (step.ingredientIndices || [])
        .map((idx) => {
          const item = map.get(idx);
          if (!item) return null;
          const label = item.displayQuantityAndUnit
            ? `${item.displayQuantityAndUnit} ${item.ingredient.displayName}`
            : item.ingredient.displayName;
          return { id: item.ingredient.id, label };
        })
        .filter((c): c is { id: string; label: string } => c !== null);

      sectionsMap.get(section)!.push({
        step,
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
    this.targetServings.set(Math.max(1, Math.round(base * mult)));
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
