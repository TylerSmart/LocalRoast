import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  RecipeDetailDto,
  RecipeIngredientDto,
  RecipeStepDto,
  SubRecipePreviewDto,
  UnitSystem,
} from '../../../core/models/recipe.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import {
  FormattedQuantityResult,
  formatDuration,
  scaleAndConvert,
} from '../../../core/utils/culinary-math';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  RecipeOrganizationDialogComponent,
  RecipeOrganizationDialogData,
} from '../recipe-organization-dialog/recipe-organization-dialog.component';

export interface ScaledIngredientItem {
  ingredient: RecipeIngredientDto;
  scaled: FormattedQuantityResult;
  displayQuantityAndUnit: string;
}

export interface ScaledStepItem {
  step: RecipeStepDto;
  ingredientCallouts: { id: string; label: string }[];
}

export interface IngredientSectionGroup {
  sectionName: string;
  items: ScaledIngredientItem[];
}

export interface StepSectionGroup {
  sectionName: string;
  items: ScaledStepItem[];
}

@Component({
  imports: [
    FormsModule,
    RouterLink,
    MatCardModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatChipsModule,
    MatDividerModule,
    MatCheckboxModule,
    MatExpansionModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-recipe-detail',
  templateUrl: './recipe-detail.component.html',
  styleUrl: './recipe-detail.component.scss',
})
export class RecipeDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly recipeService = inject(RecipeService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly recipe = signal<RecipeDetailDto | null>(null);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly errorMessage = signal<string | null>(null);

  // Culinary scaling & units state
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

  // Map of ingredientId -> Scaled item for fast step callout resolution
  protected readonly scaledIngredientsMap = computed(() => {
    const r = this.recipe();
    const factor = this.scaleFactor();
    const sys = this.targetSystem();
    const map = new Map<string, ScaledIngredientItem>();

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

  // Ingredients grouped by section
  protected readonly groupedIngredients = computed<IngredientSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    if (!r) {
      return [];
    }

    const sectionsMap = new Map<string, ScaledIngredientItem[]>();
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

  // Steps grouped by section with resolved ingredient chips
  protected readonly groupedSteps = computed<StepSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    if (!r) {
      return [];
    }

    const sectionsMap = new Map<string, ScaledStepItem[]>();

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
      const id = params.get('id');
      if (id) {
        this.loadRecipe(id);
      }
    });
  }

  loadRecipe(id: string): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.recipeService.getRecipeById(id).subscribe({
      next: (detail) => {
        this.recipe.set(detail);
        this.targetServings.set(detail.baseServings);
        this.isLoading.set(false);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isLoading.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to load recipe details.',
        );
      },
    });
  }

  openOrganizeDialog(): void {
    const r = this.recipe();
    if (!r) {
      return;
    }

    const ref = this.dialog.open<
      RecipeOrganizationDialogComponent,
      RecipeOrganizationDialogData,
      RecipeDetailDto
    >(RecipeOrganizationDialogComponent, {
      width: '520px',
      data: {
        recipeId: r.id,
        recipeTitle: r.title,
        initialCollectionIds: r.collections.map((c) => c.id),
        initialTagIds: r.tags.map((t) => t.id),
      },
    });

    ref.afterClosed().subscribe((updated) => {
      if (updated) {
        this.recipe.set(updated);
        this.snackBar.open('Recipe collections & tags updated!', 'Close', { duration: 3000 });
      }
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

  getSubRecipe(subRecipeId: string | null): SubRecipePreviewDto | undefined {
    if (!subRecipeId) {
      return undefined;
    }
    return this.recipe()?.linkedSubRecipes.find((s) => s.id === subRecipeId);
  }

  deleteRecipe(): void {
    const r = this.recipe();
    if (!r) {
      return;
    }

    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Recipe',
        message: `Are you sure you want to delete "${r.title}"?`,
        confirmText: 'Delete',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) {
        return;
      }

      this.recipeService.deleteRecipe(r.id).subscribe({
        next: () => {
          this.snackBar.open(`Deleted "${r.title}"`, 'Close', { duration: 3000 });
          this.router.navigate(['/recipes']);
        },
        error: (err: { error?: { detail?: string; title?: string } }) => {
          this.snackBar.open(
            err?.error?.detail || err?.error?.title || 'Cannot delete recipe.',
            'Close',
            { duration: 5000 },
          );
        },
      });
    });
  }
}
