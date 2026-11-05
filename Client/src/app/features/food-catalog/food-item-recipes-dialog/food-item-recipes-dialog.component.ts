import {
  Component,
  DestroyRef,
  ElementRef,
  OnInit,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import {
  FoodItemDto,
  FoodItemRecipeUsageDto,
  UnlinkFoodItemRecipesResultDto,
} from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';

export interface FoodItemRecipesDialogData {
  foodItem: FoodItemDto;
}

export interface FoodItemRecipesDialogResult {
  unlinked: boolean;
  unlinkedRecipeCount?: number;
}

@Component({
  imports: [
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-food-item-recipes-dialog',
  templateUrl: './food-item-recipes-dialog.component.html',
  styleUrl: './food-item-recipes-dialog.component.scss',
})
export class FoodItemRecipesDialogComponent implements OnInit {
  constructor() {
    effect(() => {
      const sentinel = this.scrollSentinel()?.nativeElement;
      if (!sentinel) return;

      if (this.observer) {
        this.observer.disconnect();
      }

      if (typeof IntersectionObserver !== 'undefined') {
        this.observer = new IntersectionObserver(
          (entries) => {
            if (
              entries[0]?.isIntersecting &&
              !this.isLoading() &&
              !this.isLoadingMore() &&
              this.hasMore()
            ) {
              this.loadMoreRecipes();
            }
          },
          { rootMargin: '200px' },
        );
        this.observer.observe(sentinel);
      }
    });

    this.destroyRef.onDestroy(() => {
      this.observer?.disconnect();
    });
  }

  protected readonly data = inject<FoodItemRecipesDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<FoodItemRecipesDialogComponent>);
  private readonly foodCatalogService = inject(FoodCatalogService);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);

  readonly recipes = signal<FoodItemRecipeUsageDto[]>([]);
  readonly currentPage = signal<number>(1);
  readonly pageSize = signal<number>(15);
  readonly totalCount = signal<number>(0);
  readonly isLoadingMore = signal<boolean>(false);
  readonly hasMore = computed(() => this.recipes().length < this.totalCount());
  readonly scrollSentinel = viewChild<ElementRef<HTMLElement>>('scrollSentinel');
  private readonly destroyRef = inject(DestroyRef);
  private observer: IntersectionObserver | null = null;
  readonly isLoading = signal<boolean>(true);
  readonly isUnlinking = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);
  readonly unlinkedResult = signal<UnlinkFoodItemRecipesResultDto | null>(null);

  ngOnInit(): void {
    this.loadRecipes();
  }

  loadRecipes(): void {
    this.currentPage.set(1);
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.foodCatalogService
      .getFoodItemRecipesPaged(this.data.foodItem.id, 1, this.pageSize())
      .subscribe({
        next: (res) => {
          this.recipes.set(res.items);
          this.totalCount.set(res.totalCount);
          this.isLoading.set(false);
        },
        error: (err: { error?: { detail?: string; title?: string } }) => {
          this.isLoading.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to load referencing recipes.',
          );
        },
      });
  }

  loadMoreRecipes(): void {
    if (this.isLoading() || this.isLoadingMore() || !this.hasMore()) {
      return;
    }

    const nextPage = this.currentPage() + 1;
    this.isLoadingMore.set(true);

    this.foodCatalogService
      .getFoodItemRecipesPaged(this.data.foodItem.id, nextPage, this.pageSize())
      .subscribe({
        next: (res) => {
          this.currentPage.set(nextPage);
          this.recipes.update((prev) => [...prev, ...res.items]);
          this.totalCount.set(res.totalCount);
          this.isLoadingMore.set(false);
        },
        error: () => {
          this.isLoadingMore.set(false);
        },
      });
  }

  navigateToRecipe(recipeId: string): void {
    this.dialogRef.close({ unlinked: false });
    this.router.navigate(['/recipes', recipeId]);
  }

  unlinkAll(): void {
    this.isUnlinking.set(true);
    this.errorMessage.set(null);

    this.foodCatalogService.unlinkFoodItemRecipes(this.data.foodItem.id).subscribe({
      next: (res) => {
        this.isUnlinking.set(false);
        this.unlinkedResult.set(res);
        this.recipes.set([]);
        this.totalCount.set(0);
        this.snackBar.open(
          `Unlinked "${this.data.foodItem.name}" from ${res.unlinkedRecipeCount} ${res.unlinkedRecipeCount === 1 ? 'recipe' : 'recipes'}.`,
          'Close',
          { duration: 4000 },
        );
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isUnlinking.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to unlink food item from recipes.',
        );
      },
    });
  }

  close(): void {
    const unlinked = this.unlinkedResult() !== null;
    this.dialogRef.close({
      unlinked,
      unlinkedRecipeCount: this.unlinkedResult()?.unlinkedRecipeCount,
    });
  }
}
