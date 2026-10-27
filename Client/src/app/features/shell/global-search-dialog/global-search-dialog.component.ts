import { A11yModule } from '@angular/cdk/a11y';
import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router } from '@angular/router';
import { debounceTime, distinctUntilChanged } from 'rxjs';
import { RecipeSummaryDto } from '../../../core/models/recipe.models';
import { CollectionDto } from '../../../core/models/taxonomy.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import { formatMinutes } from '../../../core/utils/culinary-math';

export interface SearchResultItem {
  id: string;
  type: 'action' | 'collection' | 'recipe';
  title: string;
  subtitle?: string;
  icon: string;
  route: string;
}

@Component({
  imports: [A11yModule, ReactiveFormsModule, MatIconModule, MatProgressSpinnerModule],
  selector: 'app-global-search-dialog',
  templateUrl: './global-search-dialog.component.html',
  styleUrl: './global-search-dialog.component.scss',
  host: {
    '(keydown.arrowdown)': 'onArrowDown($event)',
    '(keydown.arrowup)': 'onArrowUp($event)',
    '(keydown.enter)': 'onEnter($event)',
  },
})
export class GlobalSearchDialogComponent implements OnInit {
  protected readonly dialogRef = inject(MatDialogRef<GlobalSearchDialogComponent>);
  private readonly router = inject(Router);
  private readonly recipeService = inject(RecipeService);
  private readonly taxonomyService = inject(TaxonomyService);

  @ViewChild('searchInput') private readonly searchInputRef?: ElementRef<HTMLInputElement>;

  protected readonly searchControl = new FormControl<string>('', { nonNullable: true });
  protected readonly searchQuery = signal<string>('');

  protected readonly isSearching = signal<boolean>(false);
  protected readonly activeIndex = signal<number>(0);

  protected readonly allCollections = signal<CollectionDto[]>([]);
  protected readonly recipes = signal<RecipeSummaryDto[]>([]);

  protected readonly quickActions: SearchResultItem[] = [
    {
      id: 'action-new-recipe',
      type: 'action',
      title: 'New Recipe',
      subtitle: 'Create a new recipe card',
      icon: 'add_circle',
      route: '/recipes/new',
    },
    {
      id: 'action-matcher',
      type: 'action',
      title: 'Use Up Ingredients',
      subtitle: 'Match recipes to what is in your fridge',
      icon: 'blender',
      route: '/matcher',
    },
    {
      id: 'action-catalog',
      type: 'action',
      title: 'Food Catalog',
      subtitle: 'Manage ingredients, staples & aisles',
      icon: 'kitchen',
      route: '/catalog',
    },
  ];

  protected readonly searchResults = computed<SearchResultItem[]>(() => {
    const query = this.searchQuery().trim().toLowerCase();

    if (!query) {
      // When empty query, display quick actions and recent/seeded collections
      const collItems: SearchResultItem[] = this.allCollections()
        .slice(0, 4)
        .map((c) => ({
          id: `col-${c.id}`,
          type: 'collection',
          title: c.name,
          subtitle: `Collection • ${c.recipeCount} recipes`,
          icon: 'collections_bookmark',
          route: `/recipes?collectionId=${c.id}`,
        }));

      return [...this.quickActions, ...collItems];
    }

    // Filter quick actions
    const matchedActions = this.quickActions.filter(
      (a) =>
        a.title.toLowerCase().includes(query) ||
        (a.subtitle && a.subtitle.toLowerCase().includes(query)),
    );

    // Filter collections
    const matchedCollections: SearchResultItem[] = this.allCollections()
      .filter((c) => c.name.toLowerCase().includes(query))
      .map((c) => ({
        id: `col-${c.id}`,
        type: 'collection',
        title: c.name,
        subtitle: `Collection • ${c.recipeCount} recipes`,
        icon: 'collections_bookmark',
        route: `/recipes?collectionId=${c.id}`,
      }));

    // Filter recipes (fetched via API search)
    const recipeItems: SearchResultItem[] = this.recipes().map((r) => ({
      id: `rec-${r.id}`,
      type: 'recipe',
      title: r.title,
      subtitle: r.description
        ? r.description
        : `${r.difficulty} • ${formatMinutes(r.totalTimeMinutes) || '--'}`,
      icon: 'restaurant_menu',
      route: `/recipes/${r.id}`,
    }));

    return [...matchedActions, ...matchedCollections, ...recipeItems];
  });

  ngOnInit(): void {
    this.loadInitialData();
    this.setupSearchSubscription();
  }

  private loadInitialData(): void {
    this.taxonomyService.getCollections().subscribe({
      next: (colls) => this.allCollections.set(colls),
    });
  }

  private setupSearchSubscription(): void {
    this.searchControl.valueChanges.subscribe((value) => {
      this.searchQuery.set(value ?? '');
    });

    this.searchControl.valueChanges
      .pipe(debounceTime(200), distinctUntilChanged())
      .subscribe((query) => {
        this.activeIndex.set(0);
        const q = (query ?? '').trim();
        if (!q) {
          this.recipes.set([]);
          this.isSearching.set(false);
          return;
        }

        this.isSearching.set(true);
        this.recipeService.getRecipes({ search: q }).subscribe({
          next: (results) => {
            this.recipes.set(results);
            this.isSearching.set(false);
          },
          error: () => {
            this.recipes.set([]);
            this.isSearching.set(false);
          },
        });
      });
  }

  onArrowDown(event: Event): void {
    event.preventDefault();
    const count = this.searchResults().length;
    if (count > 0) {
      this.activeIndex.update((i) => (i + 1) % count);
    }
  }

  onArrowUp(event: Event): void {
    event.preventDefault();
    const count = this.searchResults().length;
    if (count > 0) {
      this.activeIndex.update((i) => (i - 1 + count) % count);
    }
  }

  onEnter(event: Event): void {
    event.preventDefault();
    const results = this.searchResults();
    const index = this.activeIndex();
    if (results.length > index && index >= 0) {
      this.selectResult(results[index]);
    }
  }

  selectResult(item: SearchResultItem): void {
    this.dialogRef.close();
    if (item.route.startsWith('/recipes?')) {
      const parts = item.route.split('?');
      const params = new URLSearchParams(parts[1]);
      const collectionId = params.get('collectionId');
      this.router.navigate(['/recipes'], { queryParams: { collectionId } });
    } else {
      this.router.navigateByUrl(item.route);
    }
  }
}
