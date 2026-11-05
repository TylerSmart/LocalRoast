import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
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
import { Router } from '@angular/router';
import { FoodItemDto } from '../../../core/models/food-catalog.models';
import { IngredientMatchResultDto } from '../../../core/models/recipe.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { formatMinutes } from '../../../core/utils/culinary-math';

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
  private readonly catalogService = inject(FoodCatalogService);
  private readonly recipeService = inject(RecipeService);
  private readonly router = inject(Router);

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

  protected readonly filteredFoodItems = computed(() => {
    const raw = this.searchQuery();
    const query = (typeof raw === 'string' ? raw : '').trim().toLowerCase();
    const selectedIds = new Set(this.selectedFoodItems().map((f) => f.id));
    const available = this.foodItems().filter(
      (item) => !selectedIds.has(item.id) && !this.isPlaceholderFood(item.name),
    );

    if (!query) {
      return available
        .slice()
        .sort((a, b) => {
          if (b.referencedRecipeCount !== a.referencedRecipeCount) {
            return b.referencedRecipeCount - a.referencedRecipeCount;
          }
          return a.name.localeCompare(b.name);
        })
        .slice(0, 15);
    }

    return available
      .filter((item) => {
        if (item.name.toLowerCase().includes(query)) {
          return true;
        }
        return item.aliases.some((alias) => alias.toLowerCase().includes(query));
      })
      .sort((a, b) => {
        const aName = a.name.toLowerCase();
        const bName = b.name.toLowerCase();
        const aStarts =
          aName.startsWith(query) || a.aliases.some((al) => al.toLowerCase().startsWith(query));
        const bStarts =
          bName.startsWith(query) || b.aliases.some((al) => al.toLowerCase().startsWith(query));
        if (aStarts && !bStarts) return -1;
        if (!aStarts && bStarts) return 1;

        if (b.referencedRecipeCount !== a.referencedRecipeCount) {
          return b.referencedRecipeCount - a.referencedRecipeCount;
        }
        return a.name.localeCompare(b.name);
      })
      .slice(0, 15);
  });

  protected readonly suggestedFoodItems = computed(() => {
    const selectedIds = new Set(this.selectedFoodItems().map((f) => f.id));
    return this.foodItems()
      .filter(
        (item) =>
          !selectedIds.has(item.id) &&
          !item.isPantryStaple &&
          !this.isPlaceholderFood(item.name) &&
          item.referencedRecipeCount > 0,
      )
      .sort(
        (a, b) => b.referencedRecipeCount - a.referencedRecipeCount || a.name.localeCompare(b.name),
      )
      .slice(0, 10);
  });

  ngOnInit(): void {
    this.loadCatalog();
  }

  loadCatalog(): void {
    this.isLoadingCatalog.set(true);
    this.catalogService.getFoodItems().subscribe({
      next: (items) => {
        this.foodItems.set(items);
        this.isLoadingCatalog.set(false);
      },
      error: () => {
        this.isLoadingCatalog.set(false);
        this.errorMessage.set('Failed to load ingredient catalog.');
      },
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
    this.triggerMatch();
  }

  removeFoodItem(item: FoodItemDto): void {
    this.selectedFoodItems.set(this.selectedFoodItems().filter((f) => f.id !== item.id));
    this.triggerMatch();
  }

  clearSelected(): void {
    this.selectedFoodItems.set([]);
    this.matchResults.set([]);
  }

  triggerMatch(): void {
    const selected = this.selectedFoodItems();
    if (selected.length === 0) {
      this.matchResults.set([]);
      return;
    }

    this.isMatching.set(true);
    this.errorMessage.set(null);

    this.recipeService
      .matchRecipesByIngredients({
        foodItemIds: selected.map((f) => f.id),
      })
      .subscribe({
        next: (results) => {
          this.matchResults.set(results);
          this.isMatching.set(false);
        },
        error: (err: { error?: { detail?: string; title?: string } }) => {
          this.isMatching.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to match recipes.',
          );
        },
      });
  }

  navigateToRecipe(recipeId: string): void {
    this.router.navigate(['/recipes', recipeId]);
  }
}
