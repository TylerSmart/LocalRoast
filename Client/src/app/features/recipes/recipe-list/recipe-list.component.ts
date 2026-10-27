import { Component, OnInit, computed, inject, signal } from '@angular/core';
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
  CollectionDialogComponent,
  CollectionDialogData,
} from '../collection-dialog/collection-dialog.component';

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
  ],
  selector: 'app-recipe-list',
  templateUrl: './recipe-list.component.html',
  styleUrl: './recipe-list.component.scss',
})
export class RecipeListComponent implements OnInit {
  private readonly recipeService = inject(RecipeService);
  private readonly taxonomyService = inject(TaxonomyService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly route = inject(ActivatedRoute);

  protected readonly recipes = signal<RecipeSummaryDto[]>([]);
  protected readonly collections = signal<CollectionDto[]>([]);
  protected readonly tags = signal<TagDto[]>([]);

  protected readonly searchQuery = signal<string>('');
  protected readonly selectedStatus = signal<RecipeStatus | 'All'>('Published');
  protected readonly selectedDifficulty = signal<RecipeDifficulty | 'All'>('All');
  protected readonly maxTimeMinutes = signal<number | null>(null);
  protected readonly selectedCollectionId = signal<string | null>(null);
  protected readonly selectedTagIds = signal<string[]>([]);

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

  protected readonly hasActiveFilters = computed(() => {
    return (
      this.searchQuery().trim() !== '' ||
      this.selectedStatus() !== 'Published' ||
      this.selectedDifficulty() !== 'All' ||
      this.maxTimeMinutes() !== null ||
      this.selectedCollectionId() !== null ||
      this.selectedTagIds().length > 0
    );
  });

  protected readonly filteredRecipes = computed(() => {
    const query = this.searchQuery().toLowerCase().trim();
    const status = this.selectedStatus();
    const difficulty = this.selectedDifficulty();
    const maxTime = this.maxTimeMinutes();
    const collectionId = this.selectedCollectionId();
    const tagIds = this.selectedTagIds();

    return this.recipes().filter((r) => {
      if (status !== 'All' && r.status !== status) {
        return false;
      }
      if (difficulty !== 'All' && r.difficulty !== difficulty) {
        return false;
      }
      if (maxTime !== null && (r.totalTimeMinutes === null || r.totalTimeMinutes > maxTime)) {
        return false;
      }
      if (collectionId !== null && !r.collections.some((c) => c.id === collectionId)) {
        return false;
      }
      if (tagIds.length > 0 && !tagIds.every((tid) => r.tags.some((t) => t.id === tid))) {
        return false;
      }
      if (!query) {
        return true;
      }
      const matchTitle = r.title.toLowerCase().includes(query);
      const matchDesc = (r.description || '').toLowerCase().includes(query);
      const matchSource = (r.sourceName || '').toLowerCase().includes(query);
      const matchColl = r.collections.some((c) => c.name.toLowerCase().includes(query));
      const matchTag = r.tags.some((t) => t.name.toLowerCase().includes(query));
      return matchTitle || matchDesc || matchSource || matchColl || matchTag;
    });
  });

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

  loadRecipes(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    const params: RecipeQueryParameters = {};
    if (this.selectedStatus() !== 'All') {
      params.status = this.selectedStatus() as RecipeStatus;
    }
    if (this.selectedCollectionId()) {
      params.collectionId = this.selectedCollectionId();
    }
    if (this.selectedTagIds().length > 0) {
      params.tagIds = this.selectedTagIds();
    }

    this.recipeService.getRecipes(params).subscribe({
      next: (list) => {
        this.recipes.set(list);
        this.isLoading.set(false);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isLoading.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to load cookbook recipes.',
        );
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

  clearFilters(): void {
    this.searchQuery.set('');
    this.selectedStatus.set('Published');
    this.selectedDifficulty.set('All');
    this.maxTimeMinutes.set(null);
    this.selectedCollectionId.set(null);
    this.selectedTagIds.set([]);
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
