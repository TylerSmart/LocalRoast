import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  FOOD_CATEGORY_OPTIONS,
  FoodCategory,
  FoodItemDto,
  FoodItemQueryParameters,
  foodCategoryLabel,
  MergeFoodItemsResultDto,
} from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import {
  parseBooleanFlagParam,
  parsePositiveIntParam,
  queryParamsMatch,
} from '../../../core/utils/query-param-state';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { FoodItemDialogComponent } from '../food-item-dialog/food-item-dialog.component';
import {
  FoodItemRecipesDialogComponent,
  FoodItemRecipesDialogResult,
} from '../food-item-recipes-dialog/food-item-recipes-dialog.component';
import { MergeFoodItemsDialogComponent } from '../merge-food-items-dialog/merge-food-items-dialog.component';

@Component({
  imports: [
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatDividerModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatMenuModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatChipsModule,
    MatTooltipModule,
    MatPaginatorModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-food-catalog',
  templateUrl: './food-catalog.component.html',
  styleUrl: './food-catalog.component.scss',
})
export class FoodCatalogComponent implements OnInit {
  private static readonly DEFAULT_PAGE_SIZE = 30;
  private static readonly ALLOWED_PAGE_SIZES = new Set([15, 30, 60, 120]);
  private static readonly VALID_CATEGORIES = new Set<FoodCategory>(
    FOOD_CATEGORY_OPTIONS.map((opt) => opt.value),
  );

  private readonly catalogService = inject(FoodCatalogService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private initializedFromRoute = false;

  protected readonly items = signal<FoodItemDto[]>([]);
  protected readonly searchQuery = signal<string>('');
  protected readonly onlyPantryStaples = signal<boolean>(false);
  protected readonly categoryFilter = signal<FoodCategory | null>(null);
  protected readonly categoryOptions = FOOD_CATEGORY_OPTIONS;
  protected readonly categoryLabel = foodCategoryLabel;
  protected readonly pageIndex = signal<number>(0);
  protected readonly pageSize = signal<number>(30);
  protected readonly totalCount = signal<number>(0);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  protected readonly isLoading = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly filteredItems = computed(() => this.items());

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => {
      if (this.searchTimer) {
        clearTimeout(this.searchTimer);
        this.searchTimer = null;
      }
    });

    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const changed = this.applyQueryParams(params);
      if (!this.initializedFromRoute || changed) {
        this.initializedFromRoute = true;
        this.loadItems();
      }
    });
  }

  private applyQueryParams(params: ParamMap): boolean {
    const nextSearch = params.get('q') ?? '';
    const nextStaples = parseBooleanFlagParam(params.get('staples'));
    const rawCat = params.get('category') as FoodCategory | null;
    const nextCategory =
      rawCat && FoodCatalogComponent.VALID_CATEGORIES.has(rawCat) ? rawCat : null;
    const parsedPage = parsePositiveIntParam(params.get('page'));
    const nextPageIndex = parsedPage !== null ? parsedPage - 1 : 0;
    const parsedPageSize = parsePositiveIntParam(params.get('pageSize'));
    const nextPageSize =
      parsedPageSize !== null && FoodCatalogComponent.ALLOWED_PAGE_SIZES.has(parsedPageSize)
        ? parsedPageSize
        : FoodCatalogComponent.DEFAULT_PAGE_SIZE;

    const changed =
      this.searchQuery().trim() !== nextSearch.trim() ||
      this.onlyPantryStaples() !== nextStaples ||
      this.categoryFilter() !== nextCategory ||
      this.pageIndex() !== nextPageIndex ||
      this.pageSize() !== nextPageSize;

    if (!this.initializedFromRoute || changed) {
      this.searchQuery.set(nextSearch);
      this.onlyPantryStaples.set(nextStaples);
      this.categoryFilter.set(nextCategory);
      this.pageIndex.set(nextPageIndex);
      this.pageSize.set(nextPageSize);
    }

    return changed;
  }

  private syncQueryParams(): void {
    const trimmedSearch = this.searchQuery().trim();
    const queryParams: Record<string, string | number | null> = {
      q: trimmedSearch || null,
      category: this.categoryFilter(),
      staples: this.onlyPantryStaples() ? '1' : null,
      page: this.pageIndex() > 0 ? this.pageIndex() + 1 : null,
      pageSize: this.pageSize() !== FoodCatalogComponent.DEFAULT_PAGE_SIZE ? this.pageSize() : null,
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

  loadItems(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    const query: FoodItemQueryParameters = {
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
      search: this.searchQuery().trim() || null,
      isPantryStaple: this.onlyPantryStaples() ? true : null,
      category: this.categoryFilter(),
    };

    this.catalogService.getFoodItemsPaged(query).subscribe({
      next: (res) => {
        this.items.set(res.items);
        this.totalCount.set(res.totalCount);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to load food catalog items.',
        );
      },
    });
  }

  onSearchChange(term: string): void {
    this.searchQuery.set(term);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.pageIndex.set(0);
      this.syncQueryParams();
      this.loadItems();
    }, 300);
  }

  clearSearch(): void {
    if (this.searchTimer) {
      clearTimeout(this.searchTimer);
      this.searchTimer = null;
    }
    this.searchQuery.set('');
    this.pageIndex.set(0);
    this.syncQueryParams();
    this.loadItems();
  }

  onStaplesToggleChange(checked: boolean): void {
    this.onlyPantryStaples.set(checked);
    this.pageIndex.set(0);
    this.syncQueryParams();
    this.loadItems();
  }

  onCategoryFilterChange(category: FoodCategory | null): void {
    this.categoryFilter.set(category);
    this.pageIndex.set(0);
    this.syncQueryParams();
    this.loadItems();
  }

  onPageChange(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.syncQueryParams();
    this.loadItems();
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  openCreateDialog(): void {
    const dialogRef = this.dialog.open(FoodItemDialogComponent, {
      width: '460px',
      data: { initialName: this.searchQuery().trim() },
    });

    dialogRef.afterClosed().subscribe((created: FoodItemDto | null) => {
      if (created) {
        this.items.update((list) => [created, ...list]);
        this.snackBar.open(`Added "${created.name}" to food catalog`, 'Close', {
          duration: 3000,
        });
      }
    });
  }

  openEditDialog(item: FoodItemDto): void {
    const dialogRef = this.dialog.open(FoodItemDialogComponent, {
      width: '460px',
      data: { item },
    });

    dialogRef.afterClosed().subscribe((updated: FoodItemDto | null) => {
      if (updated) {
        this.items.update((list) => list.map((i) => (i.id === updated.id ? updated : i)));
        this.snackBar.open(`Updated "${updated.name}"`, 'Close', { duration: 3000 });
      }
    });
  }

  openMergeDialog(preselectedItem?: FoodItemDto): void {
    const dialogRef = this.dialog.open(MergeFoodItemsDialogComponent, {
      width: '560px',
      maxWidth: '95vw',
      data: preselectedItem ? { targetItem: preselectedItem } : null,
    });

    dialogRef.afterClosed().subscribe((result: MergeFoodItemsResultDto | null) => {
      if (result) {
        this.loadItems();
        this.snackBar.open(
          `Merged ${result.mergedSourceCount} item(s) into "${result.targetFoodItem.name}"`,
          'Close',
          { duration: 4000 },
        );
      }
    });
  }

  togglePantryStaple(item: FoodItemDto): void {
    const nextStaple = !item.isPantryStaple;
    this.catalogService
      .updateFoodItem(item.id, {
        name: item.name,
        isPantryStaple: nextStaple,
        aliases: item.aliases,
        category: item.category ?? null,
      })
      .subscribe({
        next: (updated) => {
          this.items.update((list) => list.map((i) => (i.id === updated.id ? updated : i)));
        },
        error: (err) => {
          this.snackBar.open(
            err?.error?.detail || 'Failed to update pantry staple status',
            'Close',
            { duration: 4000 },
          );
        },
      });
  }

  viewReferencedRecipes(item: FoodItemDto): void {
    const dialogRef = this.dialog.open<
      FoodItemRecipesDialogComponent,
      { foodItem: FoodItemDto },
      FoodItemRecipesDialogResult
    >(FoodItemRecipesDialogComponent, {
      data: { foodItem: item },
      width: '600px',
      maxWidth: '90vw',
    });

    dialogRef.afterClosed().subscribe((result) => {
      if (result?.unlinked) {
        this.items.update((list) =>
          list.map((i) => (i.id === item.id ? { ...i, referencedRecipeCount: 0 } : i)),
        );
      }
    });
  }

  deleteItem(item: FoodItemDto): void {
    const hasRecipes = item.referencedRecipeCount > 0;
    const message = hasRecipes
      ? `"${item.name}" is currently used in ${item.referencedRecipeCount} ${item.referencedRecipeCount === 1 ? 'recipe' : 'recipes'}. Deleting it will unlink it from all referencing recipes. Are you sure you want to proceed?`
      : `Are you sure you want to delete "${item.name}"?`;
    const confirmText = hasRecipes ? 'Unlink & Delete' : 'Delete';

    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Food Item',
        message,
        confirmText,
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) {
        return;
      }

      this.catalogService.deleteFoodItem(item.id, hasRecipes).subscribe({
        next: () => {
          this.items.update((list) => list.filter((i) => i.id !== item.id));
          this.snackBar.open(`Deleted "${item.name}"`, 'Close', { duration: 3000 });
        },
        error: (err) => {
          this.snackBar.open(
            err?.error?.detail || err?.error?.title || 'Cannot delete item.',
            'Close',
            { duration: 5000 },
          );
        },
      });
    });
  }
}
