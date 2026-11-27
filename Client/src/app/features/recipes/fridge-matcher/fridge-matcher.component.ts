import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import {
  MatAutocompleteModule,
  MatAutocompleteSelectedEvent,
} from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ActivatedRoute, Router } from '@angular/router';
import { EMPTY, Subject, catchError, of, switchMap, timer } from 'rxjs';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import { IngredientMatchResultDto } from '../../../core/models/recipe.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { formatMinutes } from '../../../core/utils/culinary-math';
import {
  parseStringListParam,
  queryParamsMatch,
  serializeStringListParam,
} from '../../../core/utils/query-param-state';

@Component({
  imports: [
    ReactiveFormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatAutocompleteModule,
    MatChipsModule,
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-fridge-matcher',
  templateUrl: './fridge-matcher.component.html',
  styleUrl: './fridge-matcher.component.scss',
})
export class FridgeMatcherComponent implements OnInit {
  private static readonly MATCH_DEBOUNCE_MS = 300;
  private readonly catalogService = inject(FoodCatalogService);
  private readonly recipeService = inject(RecipeService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly matchRequests$ = new Subject<{ immediate: boolean }>();

  protected readonly searchControl = new FormControl<string>('', { nonNullable: true });
  protected readonly searchQuery = toSignal(this.searchControl.valueChanges, {
    initialValue: this.searchControl.value,
  });

  protected readonly foodItems = signal<FoodItemDto[]>([]);
  protected readonly selectedFoodItems = signal<FoodItemDto[]>([]);
  protected readonly matchResults = signal<IngredientMatchResultDto[]>([]);

  protected readonly isLoadingCatalog = signal<boolean>(false);
  protected readonly isMatching = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly formatMinutes = formatMinutes;

  private static readonly DISALLOWED_NAMES = new Set([
    'null',
    'none',
    'n/a',
    'na',
    'undefined',
    'unknown',
    'nil',
    'empty',
    'ingredient',
    'ingredients',
    'food',
    'food item',
    'item',
    '-',
    '--',
  ]);

  private isPlaceholderFood(name: string): boolean {
    const trimmed = name?.trim().toLowerCase();
    return !trimmed || FridgeMatcherComponent.DISALLOWED_NAMES.has(trimmed);
  }

  protected readonly normalizedCatalogItems = computed(() =>
    this.foodItems()
      .filter((item) => !this.isPlaceholderFood(item.name))
      .map((item) => ({
        item,
        lowerName: item.name.toLowerCase(),
        lowerAliases: item.aliases.map((alias) => alias.toLowerCase()),
      }))
      .sort(
        (a, b) =>
          b.item.referencedRecipeCount - a.item.referencedRecipeCount ||
          a.item.name.localeCompare(b.item.name),
      ),
  );

  protected readonly filteredFoodItems = computed(() => {
    const raw = this.searchQuery();
    const query = (typeof raw === 'string' ? raw : '').trim().toLowerCase();
    const selectedIds = new Set(this.selectedFoodItems().map((f) => f.id));
    const available = this.normalizedCatalogItems().filter(
      (entry) => !selectedIds.has(entry.item.id),
    );

    if (!query) {
      return available.slice(0, 15).map((entry) => entry.item);
    }

    return available
      .filter(
        (entry) =>
          entry.lowerName.includes(query) ||
          entry.lowerAliases.some((alias) => alias.includes(query)),
      )
      .sort((a, b) => {
        const aStarts =
          a.lowerName.startsWith(query) || a.lowerAliases.some((al) => al.startsWith(query));
        const bStarts =
          b.lowerName.startsWith(query) || b.lowerAliases.some((al) => al.startsWith(query));
        if (aStarts && !bStarts) return -1;
        if (!aStarts && bStarts) return 1;

        if (b.item.referencedRecipeCount !== a.item.referencedRecipeCount) {
          return b.item.referencedRecipeCount - a.item.referencedRecipeCount;
        }
        return a.item.name.localeCompare(b.item.name);
      })
      .slice(0, 15)
      .map((entry) => entry.item);
  });

  protected readonly suggestedFoodItems = computed(() => {
    const selectedIds = new Set(this.selectedFoodItems().map((f) => f.id));
    return this.normalizedCatalogItems()
      .filter(
        (entry) =>
          !selectedIds.has(entry.item.id) &&
          !entry.item.isPantryStaple &&
          entry.item.referencedRecipeCount > 0,
      )
      .slice(0, 10)
      .map((entry) => entry.item);
  });

  ngOnInit(): void {
    this.setupMatchPipeline();
    this.loadCatalog();
  }

  private setupMatchPipeline(): void {
    this.matchRequests$
      .pipe(
        switchMap(({ immediate }) => {
          if (this.selectedFoodItems().length === 0) {
            this.isMatching.set(false);
            this.matchResults.set([]);
            return EMPTY;
          }

          return (immediate ? of(0) : timer(FridgeMatcherComponent.MATCH_DEBOUNCE_MS)).pipe(
            switchMap(() => {
              const selected = this.selectedFoodItems();
              if (selected.length === 0) {
                this.isMatching.set(false);
                this.matchResults.set([]);
                return EMPTY;
              }

              this.isMatching.set(true);
              this.errorMessage.set(null);
              return this.recipeService
                .matchRecipesByIngredients({
                  foodItemIds: selected.map((f) => f.id),
                })
                .pipe(
                  catchError((err: { error?: { detail?: string; title?: string } }) => {
                    this.isMatching.set(false);
                    this.errorMessage.set(
                      err?.error?.detail || err?.error?.title || 'Failed to match recipes.',
                    );
                    return EMPTY;
                  }),
                );
            }),
          );
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((results) => {
        this.matchResults.set(results);
        this.isMatching.set(false);
      });
  }

  loadCatalog(): void {
    this.isLoadingCatalog.set(true);
    this.catalogService.getFoodItems().subscribe({
      next: (items) => {
        this.foodItems.set(items);
        this.isLoadingCatalog.set(false);
        this.restoreSelectionFromQueryParams(items);
      },
      error: () => {
        this.isLoadingCatalog.set(false);
        this.errorMessage.set('Failed to load ingredient catalog.');
      },
    });
  }

  private restoreSelectionFromQueryParams(items: FoodItemDto[]): void {
    const qp = this.route.snapshot?.queryParamMap;
    const requestedIds = parseStringListParam(qp?.get('ingredients'));
    if (requestedIds.length === 0) {
      return;
    }

    const byId = new Map(items.map((item) => [item.id, item]));
    const restored = requestedIds
      .map((id) => byId.get(id))
      .filter((item): item is FoodItemDto => !!item);

    if (restored.length > 0) {
      this.selectedFoodItems.set(restored);
      this.triggerMatch(true);
    }
  }

  private syncQueryParams(): void {
    const queryParams: Record<string, string | null> = {
      ingredients: serializeStringListParam(this.selectedFoodItems().map((f) => f.id)),
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

  onOptionSelected(event: MatAutocompleteSelectedEvent): void {
    const item = event.option.value as FoodItemDto;
    if (item && item.id) {
      this.addFoodItem(item);
    }
    this.searchControl.setValue('');
  }

  clearSearchInput(): void {
    this.searchControl.setValue('');
  }

  addFoodItem(item: FoodItemDto): void {
    const current = this.selectedFoodItems();
    if (current.some((f) => f.id === item.id)) {
      return;
    }
    this.selectedFoodItems.set([...current, item]);
    this.syncQueryParams();
    this.scheduleMatch();
  }

  removeFoodItem(item: FoodItemDto): void {
    this.selectedFoodItems.set(this.selectedFoodItems().filter((f) => f.id !== item.id));
    this.syncQueryParams();
    this.scheduleMatch();
  }

  clearSelected(): void {
    this.selectedFoodItems.set([]);
    this.matchResults.set([]);
    this.isMatching.set(false);
    this.errorMessage.set(null);
    this.syncQueryParams();
    this.matchRequests$.next({ immediate: true });
  }

  scheduleMatch(): void {
    this.triggerMatch(false);
  }

  triggerMatch(immediate = true): void {
    const selected = this.selectedFoodItems();
    if (selected.length === 0) {
      this.matchResults.set([]);
      this.isMatching.set(false);
      this.errorMessage.set(null);
      this.matchRequests$.next({ immediate: true });
      return;
    }

    this.isMatching.set(true);
    this.errorMessage.set(null);
    this.matchRequests$.next({ immediate });
  }

  navigateToRecipe(recipeId: string): void {
    this.router.navigate(['/recipes', recipeId]);
  }
}
