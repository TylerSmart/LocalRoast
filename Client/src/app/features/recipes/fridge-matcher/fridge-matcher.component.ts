import { Component, OnInit, computed, inject, signal } from '@angular/core';
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

  protected readonly foodItems = signal<FoodItemDto[]>([]);
  protected readonly selectedFoodItems = signal<FoodItemDto[]>([]);
  protected readonly matchResults = signal<IngredientMatchResultDto[]>([]);

  protected readonly isLoadingCatalog = signal<boolean>(false);
  protected readonly isMatching = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly formatMinutes = formatMinutes;

  protected readonly filteredFoodItems = computed(() => {
    const query = this.searchControl.value.trim().toLowerCase();
    const selectedIds = new Set(this.selectedFoodItems().map((f) => f.id));
    const available = this.foodItems().filter((item) => !selectedIds.has(item.id));

    if (!query) {
      return available.slice(0, 15);
    }

    return available
      .filter((item) => {
        if (item.name.toLowerCase().includes(query)) {
          return true;
        }
        return item.aliases.some((alias) => alias.toLowerCase().includes(query));
      })
      .slice(0, 15);
  });

  protected readonly suggestedFoodItems = computed(() => {
    const selectedIds = new Set(this.selectedFoodItems().map((f) => f.id));
    // Suggest non-staple items first, up to 10
    return this.foodItems()
      .filter((item) => !selectedIds.has(item.id) && !item.isPantryStaple)
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
    this.addFoodItem(item);
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
