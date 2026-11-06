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
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  RecipeDifficulty,
  RecipeQueryParameters,
  RecipeStatus,
  RecipeSummaryDto,
} from '../../../core/models/recipe.models';
import { CollectionDto, TagCategory, TagDto } from '../../../core/models/taxonomy.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { formatMinutes } from '../../../core/utils/culinary-math';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  SearchableSelectComponent,
  SearchableSelectOption,
} from '../../../shared/components/searchable-select/searchable-select.component';
import {
  CollectionDialogComponent,
  CollectionDialogData,
} from '../collection-dialog/collection-dialog.component';
import { TagDialogComponent, TagDialogData } from '../tag-dialog/tag-dialog.component';

@Component({
  imports: [
    FormsModule,
    RouterLink,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatChipsModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    SearchableSelectComponent,
  ],
  selector: 'app-recipe-list',
  templateUrl: './recipe-list.component.html',
  styleUrl: './recipe-list.component.scss',
})
export class RecipeListComponent implements OnInit {
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
          { rootMargin: '300px' },
        );
        this.observer.observe(sentinel);
      }
    });

    this.destroyRef.onDestroy(() => {
      this.observer?.disconnect();
      this.clearSearchTimer();
      this.recipesRequest?.unsubscribe();
      this.loadMoreRequest?.unsubscribe();
    });
  }

  private readonly recipeService = inject(RecipeService);
  private readonly taxonomyService = inject(TaxonomyService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly route = inject(ActivatedRoute);

  protected readonly recipes = signal<RecipeSummaryDto[]>([]);
  protected readonly collections = signal<CollectionDto[]>([]);
  protected readonly tags = signal<TagDto[]>([]);

  protected readonly tagOptions = computed<SearchableSelectOption[]>(() =>
    this.tags().map((t) => ({
      value: t.id,
      label: t.name,
      group: t.category,
    })),
  );

  protected readonly searchQuery = signal<string>('');
  protected readonly selectedStatus = signal<RecipeStatus | 'All'>('Published');
  protected readonly selectedDifficulty = signal<RecipeDifficulty | 'All'>('All');
  protected readonly maxTimeMinutes = signal<number | null>(null);
  protected readonly currentPage = signal<number>(1);
  protected readonly pageSize = signal<number>(24);
  protected readonly totalCount = signal<number>(0);
  protected readonly isLoadingMore = signal<boolean>(false);
  protected readonly hasMore = signal<boolean>(false);
  protected readonly fuzzySearch = signal<boolean>(false);
  protected readonly scrollSentinel = viewChild<ElementRef<HTMLElement>>('scrollSentinel');
  private readonly destroyRef = inject(DestroyRef);
  private observer: IntersectionObserver | null = null;
  protected readonly selectedCollectionId = signal<string | null>(null);
  protected readonly selectedTagIds = signal<string[]>([]);
  protected readonly forgottenFavoritesOnly = signal<boolean>(false);
  protected readonly minRating = signal<number | null>(null);
  protected readonly filtersExpanded = signal<boolean>(false);
  protected readonly tagsManagerExpanded = signal<boolean>(false);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private recipesRequest: Subscription | null = null;
  private loadMoreRequest: Subscription | null = null;

  protected readonly isLoading = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly formatMinutes = formatMinutes;

  protected readonly tagCategories: TagCategory[] = [
    'Course',
    'Cuisine',
    'Dietary',
    'Equipment',
    'Custom',
  ];

  protected readonly activeDrawerFilterCount = computed(() => {
    let count = 0;
    if (this.selectedStatus() !== 'Published') count++;
    if (this.selectedDifficulty() !== 'All') count++;
    if (this.maxTimeMinutes() !== null) count++;
    if (this.selectedTagIds().length > 0) count++;
    if (this.forgottenFavoritesOnly()) count++;
    if (this.minRating() !== null) count++;
    return count;
  });

  protected readonly hasActiveFilters = computed(() => {
    return (
      this.searchQuery().trim() !== '' ||
      this.selectedStatus() !== 'Published' ||
      this.selectedDifficulty() !== 'All' ||
      this.maxTimeMinutes() !== null ||
      this.selectedCollectionId() !== null ||
      this.selectedTagIds().length > 0 ||
      this.forgottenFavoritesOnly() ||
      this.minRating() !== null
    );
  });

  protected readonly filteredRecipes = computed(() => this.recipes());

  ngOnInit(): void {
    this.loadTaxonomy();

    this.route.queryParamMap.subscribe((params) => {
      const colId = params.get('collectionId');
      if (colId) {
        this.selectedCollectionId.set(colId);
      }
      this.loadRecipes();
    });
  }

  loadTaxonomy(): void {
    this.taxonomyService.getCollections().subscribe({
      next: (colls) => this.collections.set(colls),
    });
    this.taxonomyService.getTags().subscribe({
      next: (tgs) => this.tags.set(tgs),
    });
  }

  private buildQueryParams(page: number): RecipeQueryParameters {
    const params: RecipeQueryParameters = {
      page,
      pageSize: this.pageSize(),
      includeTotalCount: page === 1,
      fuzzySearch: page > 1 && this.fuzzySearch(),
    };
    if (this.selectedStatus() !== 'All') {
      params.status = this.selectedStatus() as RecipeStatus;
    }
    if (this.selectedDifficulty() !== 'All') {
      params.difficulty = this.selectedDifficulty() as RecipeDifficulty;
    }
    if (this.maxTimeMinutes() !== null) {
      params.maxTotalTimeMinutes = this.maxTimeMinutes();
    }
    if (this.selectedCollectionId()) {
      params.collectionId = this.selectedCollectionId();
    }
    if (this.selectedTagIds().length > 0) {
      params.tagIds = this.selectedTagIds();
    }
    if (this.forgottenFavoritesOnly()) {
      params.forgottenFavoritesOnly = true;
    }
    if (this.minRating() !== null) {
      params.minRating = this.minRating();
    }
    const searchVal = this.searchQuery().trim();
    if (searchVal) {
      params.search = searchVal;
    }
    return params;
  }

  loadRecipes(): void {
    this.clearSearchTimer();
    this.recipesRequest?.unsubscribe();
    this.loadMoreRequest?.unsubscribe();
    this.loadMoreRequest = null;
    this.currentPage.set(1);
    this.totalCount.set(0);
    this.hasMore.set(false);
    this.fuzzySearch.set(false);
    this.isLoading.set(true);
    this.isLoadingMore.set(false);
    this.errorMessage.set(null);

    this.recipesRequest = this.recipeService.getRecipesPaged(this.buildQueryParams(1)).subscribe({
      next: (res) => {
        this.recipes.set(res.items);
        this.totalCount.set(res.totalCount ?? res.items.length);
        this.hasMore.set(res.hasMore);
        this.fuzzySearch.set(res.isFuzzySearch);
        this.isLoading.set(false);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isLoading.set(false);
        this.hasMore.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to load cookbook recipes.',
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

    this.loadMoreRequest = this.recipeService
      .getRecipesPaged(this.buildQueryParams(nextPage))
      .subscribe({
        next: (res) => {
          this.currentPage.set(nextPage);
          this.recipes.update((prev) => [...prev, ...res.items]);
          if (res.totalCount !== null) {
            this.totalCount.set(res.totalCount);
          }
          this.hasMore.set(res.hasMore);
          this.fuzzySearch.set(res.isFuzzySearch);
          this.isLoadingMore.set(false);
        },
        error: () => {
          this.isLoadingMore.set(false);
        },
      });
  }

  selectCollection(id: string | null): void {
    this.selectedCollectionId.set(id);
    this.loadRecipes();
  }

  onTagSelectionChange(tags: string[]): void {
    this.selectedTagIds.set(tags);
    this.loadRecipes();
  }

  tagsByCategory(category: TagCategory): TagDto[] {
    return this.tags().filter((t) => t.category === category);
  }

  onSearchChange(val: string): void {
    this.searchQuery.set(val);
    this.clearSearchTimer();
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      this.loadRecipes();
    }, 300);
  }

  private clearSearchTimer(): void {
    if (this.searchTimer) {
      clearTimeout(this.searchTimer);
      this.searchTimer = null;
    }
  }

  toggleForgottenFavorites(): void {
    this.forgottenFavoritesOnly.update((v) => !v);
    this.loadRecipes();
  }

  onMinRatingChange(val: number | null): void {
    this.minRating.set(val);
    this.loadRecipes();
  }

  clearFilters(): void {
    this.searchQuery.set('');
    this.selectedStatus.set('Published');
    this.selectedDifficulty.set('All');
    this.maxTimeMinutes.set(null);
    this.selectedCollectionId.set(null);
    this.selectedTagIds.set([]);
    this.forgottenFavoritesOnly.set(false);
    this.minRating.set(null);
    this.loadRecipes();
  }

  openNewCollectionDialog(): void {
    const ref = this.dialog.open<CollectionDialogComponent, CollectionDialogData, CollectionDto>(
      CollectionDialogComponent,
      { width: '420px', data: {} },
    );
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.collections.update((prev) => [...prev, created]);
        this.selectCollection(created.id);
      }
    });
  }

  openEditCollectionDialog(collection: CollectionDto, event: Event): void {
    event.stopPropagation();
    const ref = this.dialog.open<CollectionDialogComponent, CollectionDialogData, CollectionDto>(
      CollectionDialogComponent,
      { width: '420px', data: { collection } },
    );
    ref.afterClosed().subscribe((updated) => {
      if (updated) {
        this.collections.update((prev) => prev.map((c) => (c.id === updated.id ? updated : c)));
        this.loadRecipes();
      }
    });
  }

  deleteCollection(collection: CollectionDto, event: Event): void {
    event.stopPropagation();
    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Collection',
        message: `Are you sure you want to delete the collection "${collection.name}"? Recipes inside it will not be deleted.`,
        confirmText: 'Delete',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) {
        return;
      }

      this.taxonomyService.deleteCollection(collection.id).subscribe({
        next: () => {
          this.collections.update((prev) => prev.filter((c) => c.id !== collection.id));
          if (this.selectedCollectionId() === collection.id) {
            this.selectedCollectionId.set(null);
          }
          this.loadRecipes();
          this.snackBar.open(`Deleted collection "${collection.name}"`, 'Close', {
            duration: 3000,
          });
        },
        error: (err: { error?: { detail?: string; title?: string } }) => {
          this.snackBar.open(
            err?.error?.detail || err?.error?.title || 'Failed to delete collection.',
            'Close',
            { duration: 4000 },
          );
        },
      });
    });
  }

  toggleFiltersExpanded(): void {
    this.filtersExpanded.update((v) => !v);
  }

  toggleTagsManagerExpanded(): void {
    this.tagsManagerExpanded.update((v) => !v);
  }

  openNewTagDialog(defaultCategory: TagCategory = 'Custom'): void {
    const ref = this.dialog.open<TagDialogComponent, TagDialogData, TagDto>(TagDialogComponent, {
      width: '420px',
      data: { defaultCategory },
    });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.tags.update((prev) => [...prev, created]);
      }
    });
  }

  openEditTagDialog(tag: TagDto, event: Event): void {
    event.stopPropagation();
    const ref = this.dialog.open<TagDialogComponent, TagDialogData, TagDto>(TagDialogComponent, {
      width: '420px',
      data: { tag },
    });
    ref.afterClosed().subscribe((updated) => {
      if (updated) {
        this.tags.update((prev) => prev.map((t) => (t.id === updated.id ? updated : t)));
        this.loadRecipes();
      }
    });
  }

  deleteTag(tag: TagDto, event: Event): void {
    event.stopPropagation();
    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Tag',
        message: `Are you sure you want to delete the tag "${tag.name}"? It will be removed from all recipes.`,
        confirmText: 'Delete',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) {
        return;
      }

      this.taxonomyService.deleteTag(tag.id).subscribe({
        next: () => {
          this.tags.update((prev) => prev.filter((t) => t.id !== tag.id));
          if (this.selectedTagIds().includes(tag.id)) {
            this.selectedTagIds.update((ids) => ids.filter((id) => id !== tag.id));
          }
          this.loadRecipes();
          this.snackBar.open(`Deleted tag "${tag.name}"`, 'Close', {
            duration: 3000,
          });
        },
        error: (err: { error?: { detail?: string; title?: string } }) => {
          this.snackBar.open(
            err?.error?.detail || err?.error?.title || 'Failed to delete tag.',
            'Close',
            { duration: 4000 },
          );
        },
      });
    });
  }

  onStatusChange(status: RecipeStatus | 'All'): void {
    this.selectedStatus.set(status);
    this.loadRecipes();
  }

  deleteRecipe(recipe: RecipeSummaryDto, event: Event): void {
    event.stopPropagation();
    event.preventDefault();

    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Recipe',
        message: `Are you sure you want to delete "${recipe.title}"?`,
        confirmText: 'Delete',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) {
        return;
      }

      this.recipeService.deleteRecipe(recipe.id).subscribe({
        next: () => {
          this.recipes.update((list) => list.filter((r) => r.id !== recipe.id));
          this.snackBar.open(`Deleted "${recipe.title}"`, 'Close', { duration: 3000 });
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
