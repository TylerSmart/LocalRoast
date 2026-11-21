import { RecipeAttachmentViewerDialogComponent } from '../recipe-attachment-viewer-dialog/recipe-attachment-viewer-dialog.component';
import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Subscription, interval } from 'rxjs';
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
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import {
  FormattedQuantityResult,
  formatDuration,
  formatIngredientReplacement,
  formatMinutes,
  scaleIngredient,
} from '../../../core/utils/culinary-math';
import {
  formatIngredientUses,
  ingredientUses,
  isIngredientDivided,
} from '../../../core/utils/step-instruction';
import {
  ContiguousSectionGroup,
  groupContiguousBySection,
} from '../../../core/utils/section-grouping';
import { StepInstructionComponent } from '../../../shared/components/step-instruction/step-instruction.component';
import { getTodayIsoLocal } from '../../../core/utils/meal-plan-date';
import { shouldShowTimerLabel } from '../../../core/utils/step-token-parser';
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
import {
  KitchenQuestionDialogComponent,
  KitchenQuestionDialogData,
} from '../kitchen-question-dialog/kitchen-question-dialog.component';
import { Title } from '@angular/platform-browser';

export interface ScaledIngredientItem {
  ingredient: RecipeIngredientDto;
  scaled: FormattedQuantityResult;
  displayQuantityAndUnit: string;
}

export interface ScaledStepItem {
  step: RecipeStepDto;
}

export type IngredientSectionGroup = ContiguousSectionGroup<ScaledIngredientItem>;

export type StepSectionGroup = ContiguousSectionGroup<ScaledStepItem>;

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
    StepInstructionComponent,
  ],
  selector: 'app-recipe-detail',
  templateUrl: './recipe-detail.component.html',
  styleUrl: './recipe-detail.component.scss',
})
export class RecipeDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly recipeService = inject(RecipeService);
  private readonly ingestionService = inject(IngestionService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly aiHelper = inject(AiKitchenHelperService);
  private readonly title = inject(Title);

  protected readonly recipe = signal<RecipeDetailDto | null>(null);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly errorMessage = signal<string | null>(null);

  // Culinary scaling & units state
  protected readonly targetServings = signal<number>(4);
  protected readonly targetSystem = signal<UnitSystem>('Original');
  protected readonly checkedIngredients = signal<Record<string, boolean>>({});
  protected readonly isUploadingAttachment = signal<boolean>(false);
  protected readonly isRerunningIngestion = signal<boolean>(false);
  protected readonly isQueueingRefinement = signal<boolean>(false);
  protected readonly isDismissingRefinement = signal<boolean>(false);
  protected readonly formatMinutes = formatMinutes;
  protected readonly formatReplacement = formatIngredientReplacement;
  protected readonly previewPhotoUrl = signal<string | null>(null);
  private refinementPollSub: Subscription | null = null;

  protected readonly hasRefinementModel = computed(() =>
    Boolean(this.aiHelper.hasRefinementModel?.()),
  );

  protected readonly isRefinementQueuedOrRunning = computed(() => {
    const status = this.recipe()?.refinementStatus;
    return status === 'Queued' || status === 'Running';
  });

  protected readonly canRefineRecipe = computed(() => {
    const r = this.recipe();
    if (!r || r.status === 'Archived') {
      return false;
    }
    return this.hasRefinementModel() || this.isRefinementQueuedOrRunning();
  });

  protected readonly refineMenuLabel = computed(() => {
    if (this.isQueueingRefinement()) {
      return 'Queueing Refinement...';
    }
    const r = this.recipe();
    if (r?.refinementStatus === 'Running') {
      return 'Refining Recipe...';
    }
    if (r?.refinementStatus === 'Queued') {
      return 'Refinement Queued...';
    }
    return r?.refinedModel ? 'Re-run Refinement' : 'Refine Recipe';
  });

  protected readonly refineMenuTooltip = computed(() => {
    const model = this.aiHelper.refineModel?.();
    return model
      ? `Run refinement via ${model} now (runs even when the queue is paused)`
      : 'Run refinement now (runs even when the queue is paused)';
  });

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
  protected readonly measurementPreferences = signal<Record<string, boolean>>({});

  toggleIngredientMeasurement(ingredient: RecipeIngredientDto): void {
    this.measurementPreferences.update((current) => ({
      ...current,
      [ingredient.id]: !(current[ingredient.id] ?? ingredient.preferAlternativeMeasurement),
    }));
  }

  // Map of ingredientId -> Scaled item for fast step callout resolution
  protected readonly scaledIngredientsMap = computed(() => {
    const r = this.recipe();
    const factor = this.scaleFactor();
    const sys = this.targetSystem();
    const map = new Map<string, ScaledIngredientItem>();

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

  /** ingredientId -> "Divided: ..." text when the ingredient is split across tagged step amounts. */
  protected readonly ingredientUsesMap = computed(() => {
    const r = this.recipe();
    const result = new Map<string, string>();
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
      if (
        isIngredientDivided(
          r.steps,
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

  // Ingredients grouped by contiguous section
  protected readonly groupedIngredients = computed<IngredientSectionGroup[]>(() => {
    const r = this.recipe();
    const map = this.scaledIngredientsMap();
    if (!r) {
      return [];
    }

    const items: ScaledIngredientItem[] = [];
    for (const ing of r.ingredients) {
      const item = map.get(ing.id);
      if (item) {
        items.push(item);
      }
    }

    return groupContiguousBySection(items, (item) => item.ingredient.sectionName);
  });

  // Steps grouped by contiguous section
  protected readonly groupedSteps = computed<StepSectionGroup[]>(() => {
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
    this.destroyRef.onDestroy(() => this.stopRefinementPolling());
    this.aiHelper.checkStatus().subscribe();
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
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

  openKitchenQuestion(context: Partial<KitchenQuestionDialogData> = {}): void {
    const recipe = this.recipe();
    if (!recipe) return;

    this.dialog.open(KitchenQuestionDialogComponent, {
      width: '560px',
      maxWidth: 'calc(100vw - 24px)',
      maxHeight: '90vh',
      autoFocus: 'textarea',
      data: { ...context, recipeId: recipe.id, recipeTitle: recipe.title },
    });
  }

  loadRecipe(id: string): void {
    this.stopRefinementPolling();
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.recipeService.getRecipeById(id).subscribe({
      next: (detail) => {
        this.recipe.set(detail);
        this.targetServings.set(detail.baseServings);
        this.title.setTitle(`LocalRoast | ${detail.title}`);
        this.isLoading.set(false);
        if (detail.refinementStatus === 'Queued' || detail.refinementStatus === 'Running') {
          this.startRefinementPolling(detail.id);
        }
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isLoading.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to load recipe details.',
        );
      },
    });
  }

  private startRefinementPolling(recipeId: string): void {
    this.stopRefinementPolling();
    this.refinementPollSub = interval(2000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.recipeService.getRecipeById(recipeId).subscribe({
          next: (detail) => {
            if (this.recipe()?.id !== recipeId) {
              this.stopRefinementPolling();
              return;
            }
            const wasActive = this.isRefinementQueuedOrRunning();
            this.recipe.set(detail);
            this.title.setTitle(`LocalRoast | ${detail.title}`);

            if (detail.refinementStatus !== 'Queued' && detail.refinementStatus !== 'Running') {
              this.stopRefinementPolling();
              if (wasActive && detail.refinementStatus !== 'Failed') {
                const suffix = detail.refinedModel ? ` via ${detail.refinedModel}` : '';
                this.snackBar.open(`Refinement complete${suffix}.`, 'Close', {
                  duration: 4000,
                });
              }
            }
          },
          error: () => {
            // Keep polling quietly on transient network hiccups
          },
        });
      });
  }

  private stopRefinementPolling(): void {
    if (this.refinementPollSub) {
      this.refinementPollSub.unsubscribe();
      this.refinementPollSub = null;
    }
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

  protected readonly shouldShowTimerLabel = shouldShowTimerLabel;

  getSubRecipe(subRecipeId: string | null): SubRecipePreviewDto | undefined {
    if (!subRecipeId) {
      return undefined;
    }
    return this.recipe()?.linkedSubRecipes.find((s) => s.id === subRecipeId);
  }

  scaleSubRecipeIngredient(ingredient: RecipeIngredientDto, quantity: number | null): string {
    const scaled = scaleIngredient(
      ingredient,
      this.scaleFactor() * (quantity ?? 1),
      this.targetSystem(),
    );
    return [scaled.formattedMeasurement, ingredient.displayName].filter(Boolean).join(' ');
  }

  isTextAttachment(att: RecipeAttachmentDto): boolean {
    if (!att) return false;
    const name = (att.fileName || '').toLowerCase();
    const type = (att.contentType || '').toLowerCase();
    return (
      type === 'text/plain' ||
      type === 'text/markdown' ||
      name.endsWith('.txt') ||
      name.endsWith('.md')
    );
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

  refineRecipeNow(): void {
    const r = this.recipe();
    if (!r || this.isQueueingRefinement() || this.isRefinementQueuedOrRunning()) {
      return;
    }

    this.isQueueingRefinement.set(true);
    this.recipeService.refineRecipeNow(r.id).subscribe({
      next: (updated) => {
        this.isQueueingRefinement.set(false);
        this.recipe.set(updated);
        const model = this.aiHelper.refineModel?.();
        this.snackBar.open(
          model
            ? `Queued refinement for "${r.title}" via ${model}.`
            : `Queued refinement for "${r.title}".`,
          'Close',
          { duration: 4000 },
        );
        if (updated.refinementStatus === 'Queued' || updated.refinementStatus === 'Running') {
          this.startRefinementPolling(updated.id);
        }
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isQueueingRefinement.set(false);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to queue recipe refinement.',
          'Close',
          { duration: 5000 },
        );
      },
    });
  }

  dismissRefinementFailure(): void {
    const r = this.recipe();
    if (!r || this.isDismissingRefinement()) {
      return;
    }

    this.isDismissingRefinement.set(true);
    this.recipeService.dismissRefinementFailure(r.id).subscribe({
      next: (updated) => {
        this.isDismissingRefinement.set(false);
        this.recipe.set(updated);
      },
      error: () => {
        this.isDismissingRefinement.set(false);
      },
    });
  }

  rerunIngestion(): void {
    const r = this.recipe();
    if (!r?.ingestionJobId || this.isRerunningIngestion()) {
      return;
    }

    this.isRerunningIngestion.set(true);
    this.ingestionService.retryJob(r.ingestionJobId).subscribe({
      next: () => {
        this.isRerunningIngestion.set(false);
        this.snackBar.open(
          `Re-queued '${r.title}' for extraction with current LLM model.`,
          'Close',
          { duration: 4000 },
        );
        this.router.navigate(['/ingest']);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isRerunningIngestion.set(false);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to rerun recipe ingestion.',
          'Close',
          { duration: 5000 },
        );
      },
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
