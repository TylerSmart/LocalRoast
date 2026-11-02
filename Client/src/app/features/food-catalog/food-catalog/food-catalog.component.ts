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
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  FoodItemDto,
  MergeFoodItemsResultDto,
  StoreAisleDto,
} from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { FoodItemDialogComponent } from '../food-item-dialog/food-item-dialog.component';
import { MergeFoodItemsDialogComponent } from '../merge-food-items-dialog/merge-food-items-dialog.component';

@Component({
  imports: [
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatChipsModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-food-catalog',
  templateUrl: './food-catalog.component.html',
  styleUrl: './food-catalog.component.scss',
})
export class FoodCatalogComponent implements OnInit {
  private readonly catalogService = inject(FoodCatalogService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly items = signal<FoodItemDto[]>([]);
  protected readonly aisles = signal<StoreAisleDto[]>([]);
  protected readonly searchQuery = signal<string>('');
  protected readonly selectedAisleId = signal<string>('');
  protected readonly onlyPantryStaples = signal<boolean>(false);
  protected readonly isLoading = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly filteredItems = computed(() => {
    const query = this.searchQuery().toLowerCase().trim();
    const aisleId = this.selectedAisleId();
    const onlyStaples = this.onlyPantryStaples();

    return this.items().filter((item) => {
      if (onlyStaples && !item.isPantryStaple) {
        return false;
      }
      if (aisleId && item.defaultStoreAisleId !== aisleId) {
        return false;
      }
      if (!query) {
        return true;
      }

      const matchName = item.name.toLowerCase().includes(query);
      const matchAlias = item.aliases.some((a) => a.toLowerCase().includes(query));
      const matchAisle = (item.defaultStoreAisleName || '').toLowerCase().includes(query);
      return matchName || matchAlias || matchAisle;
    });
  });

  ngOnInit(): void {
    this.loadAisles();
    this.loadItems();
  }

  loadAisles(): void {
    this.catalogService.getStoreAisles().subscribe({
      next: (list) => {
        this.aisles.set(list.sort((a, b) => a.sortOrder - b.sortOrder));
      },
    });
  }

  loadItems(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.catalogService.getFoodItems().subscribe({
      next: (list) => {
        this.items.set(list);
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
        defaultStoreAisleId: item.defaultStoreAisleId,
        isPantryStaple: nextStaple,
        aliases: item.aliases,
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

  deleteItem(item: FoodItemDto): void {
    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Food Item',
        message: `Are you sure you want to delete "${item.name}"?`,
        confirmText: 'Delete',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) {
        return;
      }

      this.catalogService.deleteFoodItem(item.id).subscribe({
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
