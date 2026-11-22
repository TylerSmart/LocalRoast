import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDividerModule } from '@angular/material/divider';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router } from '@angular/router';
import { APP_BRAND_MARK_SRC, APP_NAME } from '../../../app.branding';
import { RecipeStepDto, UnitSystem } from '../../../core/models/recipe.models';
import {
  PublicRecipeIngredientDto,
  PublicSharedRecipeDto,
  PublicSubRecipeDto,
} from '../../../core/models/share-link.models';
import { ShareLinkService } from '../../../core/services/share-link/share-link.service';
import { ThemeService } from '../../../core/services/theme/theme.service';
import {
  FormattedQuantityResult,
  computeSubRecipeBatchMultiplier,
  formatDuration,
  formatIngredientReplacement,
  formatMinutes,
  scaleIngredient,
  stripOuterParentheses,
} from '../../../core/utils/culinary-math';
import { formatIngredientUseLines, ingredientUses } from '../../../core/utils/step-instruction';
import {
  parseEnumParam,
  parseOneBasedIndexSetParam,
  parsePositiveFloatParam,
  queryParamsMatch,
  serializeZeroBasedIndicesParam,
} from '../../../core/utils/query-param-state';
import {
  ContiguousSectionGroup,
  groupContiguousBySection,
} from '../../../core/utils/section-grouping';
import { StepInstructionComponent } from '../../../shared/components/step-instruction/step-instruction.component';
import { shouldShowTimerLabel } from '../../../core/utils/step-token-parser';

export interface PublicScaledIngredientItem {
  ingredient: PublicRecipeIngredientDto;
  scaled: FormattedQuantityResult;
  displayQuantityAndUnit: string;
}

export interface PublicScaledStepItem {
  step: RecipeStepDto;
}

export type PublicIngredientSectionGroup = ContiguousSectionGroup<PublicScaledIngredientItem>;

export type PublicStepSectionGroup = ContiguousSectionGroup<PublicScaledStepItem>;

@Component({
  imports: [
    DatePipe,
    DecimalPipe,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatDividerModule,
    MatCheckboxModule,
    MatExpansionModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    StepInstructionComponent,
  ],
  selector: 'app-public-shared-recipe',
  templateUrl: './public-shared-recipe.component.html',
  styleUrl: './public-shared-recipe.component.scss',
})
export class PublicSharedRecipeComponent implements OnInit {
  protected readonly appName = APP_NAME;
  protected readonly brandMarkSrc = APP_BRAND_MARK_SRC;

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly shareLinkService = inject(ShareLinkService);
  protected readonly themeService = inject(ThemeService);

  protected readonly recipe = signal<PublicSharedRecipeDto | null>(null);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly isUnavailable = signal<boolean>(false);
  protected readonly previewPhotoUrl = signal<string | null>(null);

  // Culinary math state
  protected readonly targetServings = signal<number>(4);
  protected readonly targetSystem = signal<UnitSystem>('Original');
  protected readonly checkedIngredients = signal<Record<string, boolean>>({});
  protected readonly formatMinutes = formatMinutes;
  protected readonly formatReplacement = formatIngredientReplacement;
  protected readonly cleanPrepNote = stripOuterParentheses;

  protected readonly scaleFactor = computed(() => {
    const r = this.recipe();
    if (!r || r.baseServings <= 0) {
      return 1;
    }
    return this.targetServings() / r.baseServings;
  });

  protected readonly scaledIngredientsMap = computed(() => {
    const r = this.recipe();
    const factor = this.scaleFactor();
    const sys = this.targetSystem();
    const map = new Map<string, PublicScaledIngredientItem>();

    if (!r) {
      return map;
    }

    for (const original of r.ingredients) {
      const ing = {
        ...original,
        preferAlternativeMeasurement:
          this.measurementPreferences()[original.id] ?? original.preferAlternativeMeasurement,
      };
      const scaled = scaleIngredient(ing, factor, sys);

      const qtyAndUnit = scaled.formattedMeasurement;

      map.set(ing.id, {
        ingredient: ing,
        scaled,
        displayQuantityAndUnit: qtyAndUnit,
      });
    }

    return map;
  });

  protected readonly displayIngredients = computed(() =>
    Array.from(this.scaledIngredientsMap().values()).map((item) => item.ingredient),
  );

  protected readonly ingredientUsesMap = computed(() => {
    const r = this.recipe();
    const result = new Map<string, string[]>();
    if (!r) return result;
    const ingredients = this.displayIngredients();
    for (const item of this.scaledIngredientsMap().values()) {
      const uses = ingredientUses(
        r.steps,
        item.ingredient.id,
        ingredients,
        this.scaleFactor(),
        this.targetSystem(),
      );
      if (uses.length > 1) {
        result.set(item.ingredient.id, formatIngredientUseLines(uses));
      }
    }
    return result;
  });

  protected readonly groupedIngredients = computed<PublicIngredientSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    if (!r) {
      return [];
    }

    const items: PublicScaledIngredientItem[] = [];
    for (const ing of r.ingredients) {
      const item = map.get(ing.id);
      if (item) {
        items.push(item);
      }
    }

    return groupContiguousBySection(items, (item) => item.ingredient.sectionName);
  });

  protected readonly groupedSteps = computed<PublicStepSectionGroup[]>(() => {
    const r = this.recipe();
    if (!r) {
      return [];
    }

    return groupContiguousBySection(
      r.steps.map((step) => ({ step })),
      (item) => item.step.sectionName,
    );
  });

  ngOnInit(): void {
    this.route.paramMap.subscribe((params) => {
      const token = params.get('token');
      if (token) {
        this.loadSharedRecipe(token);
      } else {
        this.isLoading.set(false);
        this.isUnavailable.set(true);
      }
    });
  }

  protected readonly measurementPreferences = signal<Record<string, boolean>>({});

  toggleIngredientMeasurement(ingredient: PublicRecipeIngredientDto): void {
    this.measurementPreferences.update((current) => ({
      ...current,
      [ingredient.id]: !(current[ingredient.id] ?? ingredient.preferAlternativeMeasurement),
    }));
    this.syncQueryParams();
  }

  loadSharedRecipe(token: string): void {
    this.isLoading.set(true);
    this.isUnavailable.set(false);

    this.shareLinkService.getPublicSharedRecipe(token).subscribe({
      next: (dto) => {
        this.recipe.set(dto);
        this.restoreStateFromQueryParams(dto);
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
        this.isUnavailable.set(true);
      },
    });
  }

  setMultiplier(mult: number): void {
    const r = this.recipe();
    if (r) {
      this.targetServings.set(Math.max(0.5, Math.round(r.baseServings * mult * 2) / 2));
      this.syncQueryParams();
    }
  }

  adjustServings(delta: number): void {
    this.targetServings.update((curr) => {
      const next = curr + delta;
      return next >= 0.5 ? next : curr;
    });
    this.syncQueryParams();
  }

  setUnitSystem(system: UnitSystem): void {
    this.targetSystem.set(system);
    this.syncQueryParams();
  }

  toggleIngredientChecked(id: string): void {
    this.checkedIngredients.update((curr) => ({
      ...curr,
      [id]: !curr[id],
    }));
    this.syncQueryParams();
  }

  private restoreStateFromQueryParams(dto: PublicSharedRecipeDto): void {
    const qp = this.route.snapshot?.queryParamMap;
    const servingsParam = parsePositiveFloatParam(qp?.get('servings'));
    this.targetServings.set(
      servingsParam !== null && servingsParam <= 999 ? servingsParam : dto.baseServings,
    );
    this.targetSystem.set(
      parseEnumParam(qp?.get('units'), ['Original', 'UsCustomary', 'Metric'] as const, 'Original'),
    );

    const checkedIndices = parseOneBasedIndexSetParam(qp?.get('ing'), dto.ingredients.length);
    const nextChecked: Record<string, boolean> = {};
    for (const idx of checkedIndices) {
      nextChecked[dto.ingredients[idx].id] = true;
    }
    this.checkedIngredients.set(nextChecked);

    const altIndices = parseOneBasedIndexSetParam(qp?.get('alt'), dto.ingredients.length);
    const nextPrefs: Record<string, boolean> = {};
    for (const idx of altIndices) {
      const ing = dto.ingredients[idx];
      nextPrefs[ing.id] = !(ing.preferAlternativeMeasurement ?? false);
    }
    this.measurementPreferences.set(nextPrefs);
  }

  private syncQueryParams(): void {
    const r = this.recipe();
    if (!r) return;

    const checked = this.checkedIngredients();
    const checkedIndices: number[] = [];
    const prefs = this.measurementPreferences();
    const altIndices: number[] = [];

    r.ingredients.forEach((ing, idx) => {
      if (checked[ing.id]) {
        checkedIndices.push(idx);
      }
      const defaultPref = ing.preferAlternativeMeasurement ?? false;
      const effective = prefs[ing.id] ?? defaultPref;
      if (effective !== defaultPref) {
        altIndices.push(idx);
      }
    });

    const queryParams: Record<string, string | number | null> = {
      servings: this.targetServings() !== r.baseServings ? this.targetServings() : null,
      units: this.targetSystem() !== 'Original' ? this.targetSystem() : null,
      ing: serializeZeroBasedIndicesParam(checkedIndices),
      alt: serializeZeroBasedIndicesParam(altIndices),
    };

    const current = this.route.snapshot?.queryParamMap;
    if (current && queryParamsMatch(current, queryParams)) {
      return;
    }

    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams,
      queryParamsHandling: 'merge',
      replaceUrl: true,
      scroll: 'manual',
    });
  }

  formatTimerDuration(seconds: number): string {
    return formatDuration(seconds);
  }

  protected readonly shouldShowTimerLabel = shouldShowTimerLabel;

  getSubRecipe(subRecipeId: string | null): PublicSubRecipeDto | undefined {
    if (!subRecipeId) {
      return undefined;
    }
    return this.recipe()?.linkedSubRecipes.find((s) => s.id === subRecipeId);
  }

  subRecipeBatchFactor(
    parentIng: PublicRecipeIngredientDto,
    subRecipe: PublicSubRecipeDto,
  ): number {
    return this.scaleFactor() * computeSubRecipeBatchMultiplier(parentIng, subRecipe);
  }

  scaleSubRecipeIngredient(
    ing: PublicRecipeIngredientDto,
    parentOrQty: PublicRecipeIngredientDto | number | null = null,
    subRecipe?: PublicSubRecipeDto,
  ): string {
    const factor =
      typeof parentOrQty === 'number' || parentOrQty === null
        ? this.scaleFactor() * (parentOrQty ?? 1)
        : this.subRecipeBatchFactor(parentOrQty, subRecipe!);
    const sys = this.targetSystem();
    const scaled = scaleIngredient(ing, factor, sys);
    const qtyAndUnit = scaled.formattedMeasurement;
    return qtyAndUnit ? `${qtyAndUnit} ${ing.displayName}` : ing.displayName;
  }

  toggleTheme(): void {
    const current = this.themeService.isDark();
    this.themeService.setMode(current ? 'light' : 'dark');
  }

  openPhotoLightbox(url: string): void {
    this.previewPhotoUrl.set(url);
  }

  closePhotoLightbox(): void {
    this.previewPhotoUrl.set(null);
  }

  printRecipe(): void {
    if (typeof globalThis !== 'undefined' && globalThis.print) {
      globalThis.print();
    }
  }
}
