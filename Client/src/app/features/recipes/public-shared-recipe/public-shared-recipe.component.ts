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
import { ActivatedRoute } from '@angular/router';
import { APP_BRAND_ICON, APP_NAME } from '../../../app.branding';
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
  formatDuration,
  scaleAndConvert,
} from '../../../core/utils/culinary-math';

export interface PublicScaledIngredientItem {
  ingredient: PublicRecipeIngredientDto;
  scaled: FormattedQuantityResult;
  displayQuantityAndUnit: string;
}

export interface PublicScaledStepItem {
  step: RecipeStepDto;
  ingredientCallouts: { id: string; label: string }[];
}

export interface PublicIngredientSectionGroup {
  sectionName: string;
  items: PublicScaledIngredientItem[];
}

export interface PublicStepSectionGroup {
  sectionName: string;
  items: PublicScaledStepItem[];
}

@Component({
  imports: [
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
  ],
  selector: 'app-public-shared-recipe',
  templateUrl: './public-shared-recipe.component.html',
  styleUrl: './public-shared-recipe.component.scss',
})
export class PublicSharedRecipeComponent implements OnInit {
  protected readonly appName = APP_NAME;
  protected readonly brandIcon = APP_BRAND_ICON;

  private readonly route = inject(ActivatedRoute);
  private readonly shareLinkService = inject(ShareLinkService);
  protected readonly themeService = inject(ThemeService);

  protected readonly recipe = signal<PublicSharedRecipeDto | null>(null);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly isUnavailable = signal<boolean>(false);

  // Culinary math state
  protected readonly targetServings = signal<number>(4);
  protected readonly targetSystem = signal<UnitSystem>('Original');
  protected readonly checkedIngredients = signal<Record<string, boolean>>({});

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

  protected readonly groupedIngredients = computed<PublicIngredientSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    if (!r) {
      return [];
    }

    const sectionsMap = new Map<string, PublicScaledIngredientItem[]>();
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

  protected readonly groupedSteps = computed<PublicStepSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    if (!r) {
      return [];
    }

    const sectionsMap = new Map<string, PublicScaledStepItem[]>();

    for (const step of r.steps) {
      const section = step.sectionName?.trim() || '';
      if (!sectionsMap.has(section)) {
        sectionsMap.set(section, []);
      }

      const callouts = step.ingredientIds
        .map((id) => {
          const item = map.get(id);
          if (!item) {
            return null;
          }
          const label = item.displayQuantityAndUnit
            ? `${item.displayQuantityAndUnit} ${item.ingredient.displayName}`
            : item.ingredient.displayName;
          return { id, label };
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

  loadSharedRecipe(token: string): void {
    this.isLoading.set(true);
    this.isUnavailable.set(false);

    this.shareLinkService.getPublicSharedRecipe(token).subscribe({
      next: (dto) => {
        this.recipe.set(dto);
        this.targetServings.set(dto.baseServings);
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
    }
  }

  adjustServings(delta: number): void {
    this.targetServings.update((curr) => {
      const next = curr + delta;
      return next >= 0.5 ? next : curr;
    });
  }

  toggleIngredientChecked(id: string): void {
    this.checkedIngredients.update((curr) => ({
      ...curr,
      [id]: !curr[id],
    }));
  }

  formatTimerDuration(seconds: number): string {
    return formatDuration(seconds);
  }

  getSubRecipe(subRecipeId: string | null): PublicSubRecipeDto | undefined {
    if (!subRecipeId) {
      return undefined;
    }
    return this.recipe()?.linkedSubRecipes.find((s) => s.id === subRecipeId);
  }

  scaleSubRecipeIngredient(ing: PublicRecipeIngredientDto): string {
    const factor = this.scaleFactor();
    const sys = this.targetSystem();
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
    return qtyAndUnit ? `${qtyAndUnit} ${ing.displayName}` : ing.displayName;
  }

  toggleTheme(): void {
    const current = this.themeService.isDark();
    this.themeService.setMode(current ? 'light' : 'dark');
  }

  printRecipe(): void {
    if (typeof globalThis !== 'undefined' && globalThis.print) {
      globalThis.print();
    }
  }
}
