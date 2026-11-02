import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import {
  FoodItemDto,
  MergeFoodItemsRequest,
  MergeFoodItemsResultDto,
} from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';

export interface MergeFoodItemsDialogData {
  targetItem?: FoodItemDto | null;
  sourceItems?: FoodItemDto[] | null;
}

@Component({
  imports: [
    FormsModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatFormFieldModule,
    MatSelectModule,
    MatCheckboxModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-merge-food-items-dialog',
  templateUrl: './merge-food-items-dialog.component.html',
  styleUrl: './merge-food-items-dialog.component.scss',
})
export class MergeFoodItemsDialogComponent implements OnInit {
  protected readonly data = inject<MergeFoodItemsDialogData | null>(MAT_DIALOG_DATA, {
    optional: true,
  });
  protected readonly dialogRef = inject(MatDialogRef<MergeFoodItemsDialogComponent>);
  private readonly foodCatalogService = inject(FoodCatalogService);

  readonly allItems = signal<FoodItemDto[]>([]);
  readonly isLoadingItems = signal<boolean>(false);
  readonly isSaving = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);

  readonly targetItemId = signal<string>(this.data?.targetItem?.id ?? '');
  readonly selectedSourceIds = signal<string[]>(this.data?.sourceItems?.map((s) => s.id) ?? []);
  readonly keepAliases = signal<boolean>(true);

  readonly targetItem = computed(
    () => this.allItems().find((i) => i.id === this.targetItemId()) ?? null,
  );

  readonly availableSources = computed(() =>
    this.allItems().filter((i) => i.id !== this.targetItemId()),
  );

  readonly sourceItems = computed(() =>
    this.allItems().filter((i) => this.selectedSourceIds().includes(i.id)),
  );

  readonly affectedRecipesCount = computed(() =>
    this.sourceItems().reduce((acc, s) => acc + (s.referencedRecipeCount || 0), 0),
  );

  readonly affectedShoppingItemsCount = computed(() =>
    this.sourceItems().reduce((acc, s) => acc + (s.referencedShoppingListItemCount || 0), 0),
  );

  readonly canSwap = computed(() => !!this.targetItemId() && this.selectedSourceIds().length === 1);

  readonly isValid = computed(
    () =>
      !!this.targetItemId() &&
      this.selectedSourceIds().length > 0 &&
      !this.selectedSourceIds().includes(this.targetItemId()),
  );

  ngOnInit(): void {
    this.loadAllItems();
  }

  loadAllItems(): void {
    this.isLoadingItems.set(true);
    this.foodCatalogService.getFoodItems().subscribe({
      next: (items) => {
        this.allItems.set(items);
        this.isLoadingItems.set(false);
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail ?? 'Failed to load food items for merge.');
        this.isLoadingItems.set(false);
      },
    });
  }

  swapTargetAndSource(): void {
    if (!this.canSwap()) return;
    const currentTarget = this.targetItemId();
    const currentSource = this.selectedSourceIds()[0];
    this.targetItemId.set(currentSource);
    this.selectedSourceIds.set([currentTarget]);
  }

  onTargetChange(newTargetId: string): void {
    this.targetItemId.set(newTargetId);
    // Remove new target from selected sources if it was selected
    if (this.selectedSourceIds().includes(newTargetId)) {
      this.selectedSourceIds.update((ids) => ids.filter((id) => id !== newTargetId));
    }
  }

  onSourceSelectionChange(ids: string[]): void {
    this.selectedSourceIds.set(ids.filter((id) => id !== this.targetItemId()));
  }

  onConfirmMerge(): void {
    if (!this.isValid() || this.isSaving()) return;

    this.isSaving.set(true);
    this.errorMessage.set(null);

    const request: MergeFoodItemsRequest = {
      targetFoodItemId: this.targetItemId(),
      sourceFoodItemIds: this.selectedSourceIds(),
      keepSourceNamesAsAliases: this.keepAliases(),
    };

    this.foodCatalogService.mergeFoodItems(request).subscribe({
      next: (result: MergeFoodItemsResultDto) => {
        this.isSaving.set(false);
        this.dialogRef.close(result);
      },
      error: (err) => {
        this.isSaving.set(false);
        this.errorMessage.set(err?.error?.detail ?? 'An error occurred while merging food items.');
      },
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
