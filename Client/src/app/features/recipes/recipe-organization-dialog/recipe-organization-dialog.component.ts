import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { RecipeDetailDto } from '../../../core/models/recipe.models';
import { CollectionDto, TagCategory, TagDto } from '../../../core/models/taxonomy.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';
import {
  CollectionDialogComponent,
  CollectionDialogData,
} from '../collection-dialog/collection-dialog.component';
import { TagDialogComponent, TagDialogData } from '../tag-dialog/tag-dialog.component';

export interface RecipeOrganizationDialogData {
  recipeId: string;
  recipeTitle: string;
  initialCollectionIds?: string[];
  initialTagIds?: string[];
}

@Component({
  imports: [
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatChipsModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-recipe-organization-dialog',
  templateUrl: './recipe-organization-dialog.component.html',
  styleUrl: './recipe-organization-dialog.component.scss',
})
export class RecipeOrganizationDialogComponent implements OnInit {
  protected readonly data = inject<RecipeOrganizationDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<RecipeOrganizationDialogComponent>);
  private readonly dialog = inject(MatDialog);
  private readonly taxonomyService = inject(TaxonomyService);
  private readonly recipeService = inject(RecipeService);

  protected readonly isLoading = signal<boolean>(true);
  protected readonly isSubmitting = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly collections = signal<CollectionDto[]>([]);
  protected readonly tags = signal<TagDto[]>([]);

  protected readonly selectedCollectionIds = signal<Set<string>>(
    new Set(this.data?.initialCollectionIds ?? []),
  );
  protected readonly selectedTagIds = signal<Set<string>>(new Set(this.data?.initialTagIds ?? []));

  protected readonly tagCategories: TagCategory[] = [
    'Course',
    'Cuisine',
    'Dietary',
    'Equipment',
    'Custom',
  ];

  protected readonly tagsByCategory = computed(() => {
    const map: Record<TagCategory, TagDto[]> = {
      Course: [],
      Cuisine: [],
      Dietary: [],
      Equipment: [],
      Custom: [],
    };
    for (const t of this.tags()) {
      if (map[t.category]) {
        map[t.category].push(t);
      }
    }
    return map;
  });

  ngOnInit(): void {
    this.loadTaxonomy();
  }

  loadTaxonomy(): void {
    this.isLoading.set(true);
    let collectionsDone = false;
    let tagsDone = false;

    const checkComplete = () => {
      if (collectionsDone && tagsDone) {
        this.isLoading.set(false);
      }
    };

    this.taxonomyService.getCollections().subscribe({
      next: (colls) => {
        this.collections.set(colls);
        collectionsDone = true;
        checkComplete();
      },
      error: () => {
        collectionsDone = true;
        checkComplete();
      },
    });

    this.taxonomyService.getTags().subscribe({
      next: (tgs) => {
        this.tags.set(tgs);
        tagsDone = true;
        checkComplete();
      },
      error: () => {
        tagsDone = true;
        checkComplete();
      },
    });
  }

  toggleCollection(id: string): void {
    const next = new Set(this.selectedCollectionIds());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.selectedCollectionIds.set(next);
  }

  toggleTag(id: string): void {
    const next = new Set(this.selectedTagIds());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.selectedTagIds.set(next);
  }

  openNewCollection(): void {
    const ref = this.dialog.open<CollectionDialogComponent, CollectionDialogData, CollectionDto>(
      CollectionDialogComponent,
      { width: '420px', data: {} },
    );
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.collections.update((prev) => [...prev, created]);
        this.toggleCollection(created.id);
      }
    });
  }

  openNewTag(defaultCategory: TagCategory = 'Custom'): void {
    const ref = this.dialog.open<TagDialogComponent, TagDialogData, TagDto>(TagDialogComponent, {
      width: '420px',
      data: { defaultCategory },
    });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.tags.update((prev) => [...prev, created]);
        this.toggleTag(created.id);
      }
    });
  }

  onSave(): void {
    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    this.recipeService
      .updateRecipeOrganization(this.data.recipeId, {
        collectionIds: Array.from(this.selectedCollectionIds()),
        tagIds: Array.from(this.selectedTagIds()),
      })
      .subscribe({
        next: (updatedRecipe: RecipeDetailDto) => {
          this.isSubmitting.set(false);
          this.dialogRef.close(updatedRecipe);
        },
        error: (err: { error?: { detail?: string; title?: string } }) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to update recipe organization.',
          );
        },
      });
  }
}
