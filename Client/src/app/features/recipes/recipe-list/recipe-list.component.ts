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
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { Subscription, forkJoin } from 'rxjs';
import {
  RecipeDifficulty,
  RecipeQueryParameters,
  RecipeStatus,
  RecipeSummaryDto,
} from '../../../core/models/recipe.models';
import {
  AllTagCategories,
  CollectionDto,
  TagCategory,
  TagCategoryLabels,
  TagDto,
} from '../../../core/models/taxonomy.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { formatMinutes } from '../../../core/utils/culinary-math';
import {
  parseBooleanFlagParam,
  parseEnumParam,
  parsePositiveFloatParam,
  parsePositiveIntParam,
  parseStringListParam,
  queryParamsMatch,
  serializeStringListParam,
} from '../../../core/utils/query-param-state';
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
    MatMenuModule,
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
      this.clearTagFilterTimer();
      this.recipesRequest?.unsubscribe();
      this.loadMoreRequest?.unsubscribe();
    });
  }

  private readonly recipeService = inject(RecipeService);
  private readonly taxonomyService = inject(TaxonomyService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private initializedFromRoute = false;

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
  protected readonly tagManagerSearch = signal<string>('');
  protected readonly maxVisibleCardTags = 4;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private tagFilterTimer: ReturnType<typeof setTimeout> | null = null;
  private recipesRequest: Subscription | null = null;
  private loadMoreRequest: Subscription | null = null;

  protected readonly isLoading = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly formatMinutes = formatMinutes;

  protected readonly tagCategories: TagCategory[] = AllTagCategories;

  protected categoryLabel(cat: TagCategory): string {
    return TagCategoryLabels[cat] ?? cat;
  }

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

  protected readonly activeFilterChips = computed(() => {
    const chips: { key: string; label: string; remove: () => void }[] = [];
    const status = this.selectedStatus();
    if (status !== 'Published') {
      const statusLabel =
        status === 'DraftReview' ? 'Draft / Review' : status === 'All' ? 'All Recipes' : status;
      chips.push({
        key: 'status',
        label: `Status: ${statusLabel}`,
        remove: () => this.onStatusChange('Published'),
      });
    }
    const diff = this.selectedDifficulty();
    if (diff !== 'All') {
      chips.push({
        key: 'difficulty',
        label: `Difficulty: ${diff}`,
        remove: () => this.onDifficultyChange('All'),
      });
    }
    const maxTime = this.maxTimeMinutes();
    if (maxTime !== null) {
      chips.push({
        key: 'maxTime',
        label: maxTime >= 120 ? 'Under 2 hours' : `Under ${maxTime} min`,
        remove: () => this.onMaxTimeChange(null),
      });
    }
    const rating = this.minRating();
    if (rating !== null) {
      chips.push({
        key: 'rating',
        label: `${rating}${rating < 5 ? '+' : ''} Stars`,
        remove: () => this.onMinRatingChange(null),
      });
    }
    const tagMap = new Map(this.tags().map((t) => [t.id, t.name]));
    for (const tagId of this.selectedTagIds()) {
      chips.push({
        key: `tag-${tagId}`,
        label: tagMap.get(tagId) ?? 'Tag',
        remove: () => this.onTagSelectionChange(this.selectedTagIds().filter((id) => id !== tagId)),
      });
    }
    if (this.forgottenFavoritesOnly()) {
      chips.push({
        key: 'forgotten',
        label: 'Forgotten Favorites',
        remove: () => this.toggleForgottenFavorites(),
      });
    }
    return chips;
  });

  protected readonly hasFilteredManagerTags = computed(() => {
    const q = this.tagManagerSearch().trim().toLowerCase();
    if (!q) {
      return this.tags().length > 0;
    }
    return this.tags().some((t) => t.name.toLowerCase().includes(q));
  });

  protected readonly filteredRecipes = computed(() => this.recipes());

  ngOnInit(): void {
    this.loadTaxonomy();

    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const { changed, targetPage } = this.applyQueryParams(params);
      if (!this.initializedFromRoute || changed) {
        this.initializedFromRoute = true;
        this.loadRecipes(targetPage, false);
      }
    });
  }

  private applyQueryParams(params: ParamMap): { changed: boolean; targetPage: number } {
    const nextSearch = params.get('q') ?? '';
    const nextStatus = parseEnumParam<RecipeStatus | 'All'>(
      params.get('status'),
      ['All', 'Published', 'DraftReview', 'Archived'] as const,
      'Published',
    );
    const nextDifficulty = parseEnumParam<RecipeDifficulty | 'All'>(
      params.get('difficulty'),
      ['All', 'Easy', 'Medium', 'Project'] as const,
      'All',
    );
    const nextMaxTime = parsePositiveIntParam(params.get('maxTime'));
    const nextMinRating = parsePositiveFloatParam(params.get('minRating'));
    const nextCollectionId = params.get('collectionId')?.trim() || null;
    const nextTagIds = parseStringListParam(params.get('tags'));
    const nextForgotten = parseBooleanFlagParam(params.get('forgotten'));
    const parsedPage = parsePositiveIntParam(params.get('page'));
    const targetPage = parsedPage !== null ? Math.min(parsedPage, 20) : 1;

    const currentTags = this.selectedTagIds();
    const effectiveNextSearch =
      this.initializedFromRoute && this.searchTimer !== null ? this.searchQuery() : nextSearch;
    const effectiveNextTags =
      this.initializedFromRoute && this.tagFilterTimer !== null ? currentTags : nextTagIds;
    const tagsDiffer =
      currentTags.length !== effectiveNextTags.length ||
      currentTags.some((id, idx) => id !== effectiveNextTags[idx]);

    const changed =
      this.searchQuery().trim() !== effectiveNextSearch.trim() ||
      this.selectedStatus() !== nextStatus ||
      this.selectedDifficulty() !== nextDifficulty ||
      this.maxTimeMinutes() !== nextMaxTime ||
      this.minRating() !== nextMinRating ||
      this.selectedCollectionId() !== nextCollectionId ||
      tagsDiffer ||
      this.forgottenFavoritesOnly() !== nextForgotten ||
      this.currentPage() !== targetPage;

    if (!this.initializedFromRoute || changed) {
      if (!this.initializedFromRoute || this.searchTimer === null) {
        this.searchQuery.set(nextSearch);
      }
      this.selectedStatus.set(nextStatus);
      this.selectedDifficulty.set(nextDifficulty);
      this.maxTimeMinutes.set(nextMaxTime);
      this.minRating.set(nextMinRating);
      this.selectedCollectionId.set(nextCollectionId);
      if (!this.initializedFromRoute || this.tagFilterTimer === null) {
        this.selectedTagIds.set(nextTagIds);
      }
      this.forgottenFavoritesOnly.set(nextForgotten);
      this.currentPage.set(targetPage);
    }

    return { changed, targetPage };
  }

  private syncQueryParams(): void {
    const trimmedSearch = this.searchQuery().trim();
    const queryParams: Record<string, string | number | null> = {
      q: trimmedSearch || null,
      status: this.selectedStatus() !== 'Published' ? this.selectedStatus() : null,
      difficulty: this.selectedDifficulty() !== 'All' ? this.selectedDifficulty() : null,
      maxTime: this.maxTimeMinutes(),
      minRating: this.minRating(),
      collectionId: this.selectedCollectionId(),
      tags: serializeStringListParam(this.selectedTagIds()),
      forgotten: this.forgottenFavoritesOnly() ? '1' : null,
      page: this.currentPage() > 1 ? this.currentPage() : null,
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

  loadRecipes(targetPage = 1, syncUrl = true): void {
    this.clearSearchTimer();
    this.clearTagFilterTimer();
    this.recipesRequest?.unsubscribe();
    this.loadMoreRequest?.unsubscribe();
    this.loadMoreRequest = null;
    const clampedTargetPage = Math.max(1, Math.min(targetPage, 20));
    this.currentPage.set(clampedTargetPage);
    if (syncUrl) {
      this.syncQueryParams();
    }
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

        if (clampedTargetPage > 1 && res.hasMore) {
          const extraPageRequests = Array.from({ length: clampedTargetPage - 1 }, (_, i) =>
            this.recipeService.getRecipesPaged({
              ...this.buildQueryParams(i + 2),
              fuzzySearch: res.isFuzzySearch,
            }),
          );
          this.loadMoreRequest = forkJoin(extraPageRequests).subscribe({
            next: (extraPages) => {
              const combined = [...res.items];
              let reachedPage = 1;
              let lastHasMore = res.hasMore;
              for (let i = 0; i < extraPages.length; i++) {
                const pageRes = extraPages[i];
                if (pageRes.items.length > 0) {
                  combined.push(...pageRes.items);
                  reachedPage = i + 2;
                }
                lastHasMore = pageRes.hasMore;
                if (pageRes.totalCount !== null) {
                  this.totalCount.set(pageRes.totalCount);
                }
                if (!pageRes.hasMore) {
                  break;
                }
              }
              this.recipes.set(combined);
              this.currentPage.set(reachedPage);
              this.hasMore.set(lastHasMore);
              this.isLoading.set(false);
              if (reachedPage !== clampedTargetPage) {
                this.syncQueryParams();
              }
            },
            error: () => {
              this.isLoading.set(false);
            },
          });
          return;
        }

        if (clampedTargetPage > 1) {
          this.currentPage.set(1);
          this.syncQueryParams();
        }
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
          this.syncQueryParams();
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
    this.clearTagFilterTimer();
    this.tagFilterTimer = setTimeout(() => {
      this.tagFilterTimer = null;
      this.loadRecipes();
    }, 250);
  }

  private clearTagFilterTimer(): void {
    if (this.tagFilterTimer) {
      clearTimeout(this.tagFilterTimer);
      this.tagFilterTimer = null;
    }
  }

  tagsByCategory(category: TagCategory): TagDto[] {
    const q = this.tagManagerSearch().trim().toLowerCase();
    return this.tags().filter(
      (t) => t.category === category && (!q || t.name.toLowerCase().includes(q)),
    );
  }

  hiddenTagsTooltip(recipe: RecipeSummaryDto): string {
    return recipe.tags
      .slice(this.maxVisibleCardTags)
      .map((t) => t.name)
      .join(', ');
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

  onDifficultyChange(difficulty: RecipeDifficulty | 'All'): void {
    this.selectedDifficulty.set(difficulty);
    this.loadRecipes();
  }

  onMaxTimeChange(minutes: number | null): void {
    this.maxTimeMinutes.set(minutes);
    this.loadRecipes();
  }
}
