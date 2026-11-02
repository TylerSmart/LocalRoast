import { RecipeAttachmentViewerDialogComponent } from '../recipe-attachment-viewer-dialog/recipe-attachment-viewer-dialog.component';
import { DatePipe, DecimalPipe } from '@angular/common';
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
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MealPlanEntryDto } from '../../../core/models/meal-plan.models';
import {
  CookLogDto,
  RecipeAttachmentDto,
  RecipeDetailDto,
  RecipeIngredientDto,
  RecipeStepDto,
  SubRecipePreviewDto,
  UnitSystem,
} from '../../../core/models/recipe.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import {
  FormattedQuantityResult,
  formatDuration,
  formatMinutes,
  scaleAndConvert,
} from '../../../core/utils/culinary-math';
import { getTodayIsoLocal } from '../../../core/utils/meal-plan-date';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  MealPlanEntryDialogComponent,
  MealPlanEntryDialogData,
} from '../../meal-plans/meal-plan-entry-dialog/meal-plan-entry-dialog.component';
import {
  RecipeOrganizationDialogComponent,
  RecipeOrganizationDialogData,
} from '../recipe-organization-dialog/recipe-organization-dialog.component';
import {
  RecipeShareDialogComponent,
  RecipeShareDialogData,
} from '../recipe-share-dialog/recipe-share-dialog.component';
import { ShoppingListGenerationResultDto } from '../../../core/models/shopping.models';
import { CookLogDialogComponent } from '../cook-log-dialog/cook-log-dialog.component';
import { AddRecipeToListDialogComponent } from '../../shopping/add-recipe-to-list-dialog/add-recipe-to-list-dialog.component';
import { IngredientSubstitutionDialogComponent } from '../ingredient-substitution-dialog/ingredient-substitution-dialog.component';

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
    DatePipe,
    DecimalPipe,
    MatCardModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatChipsModule,
    MatDividerModule,
    MatCheckboxModule,
    MatExpansionModule,
    MatMenuModule,
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
  protected readonly aiHelper = inject(AiKitchenHelperService);

  protected readonly recipe = signal<RecipeDetailDto | null>(null);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly errorMessage = signal<string | null>(null);

  // Culinary scaling & units state
  protected readonly targetServings = signal<number>(4);
  protected readonly targetSystem = signal<UnitSystem>('Original');
  protected readonly checkedIngredients = signal<Record<string, boolean>>({});
  protected readonly isUploadingAttachment = signal<boolean>(false);
  protected readonly formatMinutes = formatMinutes;
  protected readonly previewPhotoUrl = signal<string | null>(null);

  protected readonly latestCookNote = computed<CookLogDto | null>(() => {
    const logs = this.recipe()?.cookLogs;
    if (!logs || logs.length === 0) return null;
    return logs.find((l) => !!l.notes && l.notes.trim().length > 0) ?? null;
  });

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
    this.aiHelper.checkStatus().subscribe();
    this.route.paramMap.subscribe((params) => {
      const id = params.get('id');
      if (id) {
        this.loadRecipe(id);
      }
    });
  }

  openIngredientSubstitution(item: ScaledIngredientItem): void {
    const r = this.recipe();
    if (!r) return;

    this.dialog.open(IngredientSubstitutionDialogComponent, {
      width: '540px',
      maxWidth: '95vw',
      data: {
        recipeId: r.id,
        recipeIngredientId: item.ingredient.id,
        ingredientDisplayName: item.ingredient.displayName,
        scaledAmountDisplay: item.displayQuantityAndUnit,
        targetServings: this.targetServings(),
      },
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

  openAddToShoppingListDialog(): void {
    const r = this.recipe();
    if (!r) {
      return;
    }

    const hasSubRecipes = r.ingredients.some((i) => !!i.linkedRecipeId);

    const ref = this.dialog.open(AddRecipeToListDialogComponent, {
      data: {
        recipeId: r.id,
        recipeTitle: r.title,
        targetServings: this.targetServings(),
        hasSubRecipes,
      },
      width: '520px',
      maxWidth: '95vw',
    });

    ref.afterClosed().subscribe((result?: ShoppingListGenerationResultDto) => {
      if (!result) {
        return;
      }

      const snack = this.snackBar.open(
        `Added ingredients to ${result.shoppingList.name}`,
        'View List',
        { duration: 5000 },
      );

      snack.onAction().subscribe(() => {
        this.router.navigate(['/shopping'], {
          queryParams: { listId: result.shoppingList.id },
        });
      });
    });
  }

  openPlanMealDialog(): void {
    const r = this.recipe();
    if (!r) {
      return;
    }

    const ref = this.dialog.open<
      MealPlanEntryDialogComponent,
      MealPlanEntryDialogData,
      MealPlanEntryDto
    >(MealPlanEntryDialogComponent, {
      width: '540px',
      maxWidth: '95vw',
      data: {
        initialDate: getTodayIsoLocal(),
        initialSlot: 'Dinner',
        initialIsOnDeck: false,
        preselectedRecipe: {
          id: r.id,
          title: r.title,
          baseServings: r.baseServings,
          thumbnailImagePath: r.thumbnailImagePath,
        },
        initialTargetServings: this.targetServings(),
      },
    });

    ref.afterClosed().subscribe((created) => {
      if (created) {
        const snackRef = this.snackBar.open(
          `Planned "${r.title}" for meal plan!`,
          'View Meal Plan',
          { duration: 5000 },
        );
        snackRef.onAction().subscribe(() => {
          this.router.navigate(['/meal-plan']);
        });
      }
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

  openShareDialog(): void {
    const r = this.recipe();
    if (!r) {
      return;
    }

    this.dialog.open<RecipeShareDialogComponent, RecipeShareDialogData>(
      RecipeShareDialogComponent,
      {
        width: '560px',
        data: {
          recipeId: r.id,
          recipeTitle: r.title,
          recipeStatus: r.status,
        },
      },
    );
  }

  printRecipe(): void {
    if (typeof globalThis !== 'undefined' && globalThis.print) {
      globalThis.print();
    }
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

  openAttachmentViewer(initialIndex = 0): void {
    const attachments = this.recipe()?.attachments || [];
    if (attachments.length === 0) return;
    this.dialog.open(RecipeAttachmentViewerDialogComponent, {
      data: { attachments, initialIndex },
      maxWidth: '95vw',
      maxHeight: '90vh',
    });
  }

  onAttachmentSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    const r = this.recipe();
    if (!file || !r || this.isUploadingAttachment()) return;

    this.isUploadingAttachment.set(true);
    this.recipeService.addRecipeAttachment(r.id, file).subscribe({
      next: (newAttachment) => {
        this.recipe.update((current) => {
          if (!current) return current;
          const updated = [...(current.attachments || []), newAttachment];
          return { ...current, attachments: updated };
        });
        this.isUploadingAttachment.set(false);
        this.snackBar.open(`Attachment "${newAttachment.fileName}" added!`, 'Close', {
          duration: 3000,
        });
        input.value = '';
      },
      error: (err) => {
        this.isUploadingAttachment.set(false);
        this.snackBar.open(err?.error?.detail || 'Failed to add attachment.', 'Close', {
          duration: 4000,
        });
        input.value = '';
      },
    });
  }

  deleteAttachment(attachmentId: string, event: Event): void {
    event.stopPropagation();
    const r = this.recipe();
    if (!r) return;

    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Attachment',
        message: 'Are you sure you want to remove this attachment from the recipe?',
        confirmText: 'Delete',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) return;

      this.recipeService.deleteRecipeAttachment(r.id, attachmentId).subscribe({
        next: () => {
          this.recipe.update((current) => {
            if (!current) return current;
            return {
              ...current,
              attachments: (current.attachments || []).filter(
                (a: RecipeAttachmentDto) => a.id !== attachmentId,
              ),
            };
          });
          this.snackBar.open('Attachment removed.', 'Close', { duration: 3000 });
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to remove attachment.', 'Close', {
            duration: 4000,
          });
        },
      });
    });
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

  startCooking(): void {
    const r = this.recipe();
    if (!r) {
      return;
    }
    this.router.navigate(['/recipes', r.id, 'cook'], {
      queryParams: {
        servings: this.targetServings(),
        units: this.targetSystem(),
        from: 'recipe',
      },
    });
  }

  openCookLogDialog(logToEdit?: CookLogDto): void {
    const r = this.recipe();
    if (!r) {
      return;
    }

    const ref = this.dialog.open(CookLogDialogComponent, {
      data: {
        recipeId: r.id,
        recipeTitle: r.title,
        targetServings: this.targetServings(),
        logToEdit,
      },
      width: '520px',
      maxWidth: '96vw',
    });

    ref.afterClosed().subscribe((saved: boolean) => {
      if (saved) {
        this.loadRecipe(r.id);
      }
    });
  }

  deleteCookLog(logId: string): void {
    const r = this.recipe();
    if (!r) {
      return;
    }

    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Cook Log',
        message: 'Are you sure you want to delete this cook log entry?',
        confirmText: 'Delete',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) {
        return;
      }

      this.recipeService.deleteCookLog(r.id, logId).subscribe({
        next: () => {
          this.snackBar.open('Cook log deleted', 'Close', { duration: 3000 });
          this.loadRecipe(r.id);
        },
        error: () => {
          this.snackBar.open('Failed to delete cook log', 'Close', { duration: 3000 });
        },
      });
    });
  }

  openPhotoLightbox(url: string): void {
    this.previewPhotoUrl.set(url);
  }

  closePhotoLightbox(): void {
    this.previewPhotoUrl.set(null);
  }
}
