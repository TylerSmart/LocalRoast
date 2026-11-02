import { Component, OnInit, inject, signal } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ClearCheckedItemsResultDto,
  CreateShoppingListItemRequest,
  ShoppingListDetailDto,
  ShoppingListItemDto,
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { GenerateShoppingListDialogComponent } from '../generate-shopping-list-dialog/generate-shopping-list-dialog.component';
import { ShoppingItemDialogComponent } from '../shopping-item-dialog/shopping-item-dialog.component';
import {
  ShoppingListDialogComponent,
  ShoppingListDialogResult,
} from '../shopping-list-dialog/shopping-list-dialog.component';

@Component({
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatIconModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatMenuModule,
    MatChipsModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-shopping-workspace',
  templateUrl: './shopping-workspace.component.html',
  styleUrl: './shopping-workspace.component.scss',
})
export class ShoppingWorkspaceComponent implements OnInit {
  private readonly shoppingService = inject(ShoppingService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly lists = signal<ShoppingListSummaryDto[]>([]);
  protected readonly activeList = signal<ShoppingListDetailDto | null>(null);
  protected readonly isLoading = signal<boolean>(true);
  protected readonly isAddingItem = signal<boolean>(false);
  protected readonly pantryDrawerExpanded = signal<boolean>(false);
  protected readonly checkedSectionExpanded = signal<boolean>(true);

  protected readonly quickAddName = signal<string>('');

  ngOnInit(): void {
    this.route.queryParamMap.subscribe((params) => {
      const listIdParam = params.get('listId');
      this.loadLists(listIdParam);
    });
  }

  loadLists(preferredListId?: string | null): void {
    this.isLoading.set(true);
    this.shoppingService.getShoppingLists().subscribe({
      next: (lists) => {
        this.lists.set(lists);
        if (lists.length === 0) {
          this.activeList.set(null);
          this.isLoading.set(false);
          return;
        }

        const targetId =
          preferredListId && lists.some((l) => l.id === preferredListId)
            ? preferredListId
            : lists[0].id;

        this.selectList(targetId, false);
      },
      error: (err) => {
        this.isLoading.set(false);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to load shopping lists',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  selectList(id: string, updateUrl = true): void {
    if (updateUrl) {
      this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { listId: id },
        queryParamsHandling: 'merge',
      });
    }

    this.shoppingService.getShoppingListById(id).subscribe({
      next: (detail) => {
        this.activeList.set(detail);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to load shopping list',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  onQuickAddSubmit(): void {
    const raw = this.quickAddName().trim();
    const current = this.activeList();
    if (!raw || !current || this.isAddingItem()) {
      return;
    }

    this.isAddingItem.set(true);
    const request: CreateShoppingListItemRequest = {
      displayName: raw,
      consolidateIfCompatible: true,
    };

    this.shoppingService.addItem(current.id, request).subscribe({
      next: () => {
        this.quickAddName.set('');
        this.isAddingItem.set(false);
        this.refreshActiveList();
      },
      error: (err) => {
        this.isAddingItem.set(false);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to add item',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  openItemDialog(existingItem?: ShoppingListItemDto): void {
    const current = this.activeList();
    if (!current) {
      return;
    }

    const ref = this.dialog.open(ShoppingItemDialogComponent, {
      data: {
        listId: current.id,
        item: existingItem ?? undefined,
      },
      width: '500px',
      maxWidth: '95vw',
    });

    ref.afterClosed().subscribe((savedItem?: ShoppingListItemDto) => {
      if (!savedItem) {
        return;
      }

      this.refreshActiveList();
      this.snackBar.open(
        existingItem ? `Updated ${savedItem.displayName}` : `Added ${savedItem.displayName}`,
        'Close',
        { duration: 3000 },
      );
    });
  }

  toggleItemChecked(item: ShoppingListItemDto, isChecked: boolean): void {
    const current = this.activeList();
    if (!current) {
      return;
    }

    // Optimistic local update
    const previousState = { ...current };
    const updatedItem: ShoppingListItemDto = {
      ...item,
      isChecked,
      checkedAt: isChecked ? new Date().toISOString() : null,
    };

    let nextActive = [...current.activeItems];
    let nextChecked = [...current.checkedItems];

    if (isChecked) {
      nextActive = nextActive.filter((i) => i.id !== item.id);
      nextChecked = [updatedItem, ...nextChecked.filter((i) => i.id !== item.id)];
    } else {
      nextChecked = nextChecked.filter((i) => i.id !== item.id);
      nextActive = [...nextActive.filter((i) => i.id !== item.id), updatedItem].sort((a, b) =>
        a.displayName.localeCompare(b.displayName),
      );
    }

    this.activeList.set({
      ...current,
      activeItems: nextActive,
      checkedItems: nextChecked,
    });

    this.shoppingService.setItemChecked(current.id, item.id, isChecked).subscribe({
      next: (serverItem) => {
        // Sync server updated item
        const refreshed = this.activeList();
        if (refreshed) {
          if (isChecked) {
            this.activeList.set({
              ...refreshed,
              checkedItems: refreshed.checkedItems.map((i) =>
                i.id === serverItem.id ? serverItem : i,
              ),
            });
          } else {
            this.activeList.set({
              ...refreshed,
              activeItems: refreshed.activeItems.map((i) =>
                i.id === serverItem.id ? serverItem : i,
              ),
            });
          }
        }
      },
      error: (err) => {
        // Revert on error
        this.activeList.set(previousState);
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to update item status',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  deleteItem(item: ShoppingListItemDto): void {
    const current = this.activeList();
    if (!current) {
      return;
    }

    this.shoppingService.deleteItem(current.id, item.id).subscribe({
      next: () => {
        this.refreshActiveList();
        this.snackBar.open(`Removed ${item.displayName}`, 'Close', { duration: 3000 });
      },
      error: (err) => {
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to delete item',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  promotePantryStaple(item: ShoppingListItemDto): void {
    const current = this.activeList();
    if (!current) {
      return;
    }

    this.shoppingService.promotePantryStaple(current.id, item.id).subscribe({
      next: (promoted) => {
        this.refreshActiveList();
        this.snackBar.open(`Added ${promoted.displayName} to active list`, 'Close', {
          duration: 3000,
        });
      },
      error: (err) => {
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to promote staple',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  clearCheckedItems(): void {
    const current = this.activeList();
    if (!current || current.checkedItems.length === 0) {
      return;
    }

    this.shoppingService.clearCheckedItems(current.id).subscribe({
      next: (result: ClearCheckedItemsResultDto) => {
        this.refreshActiveList();
        this.snackBar.open(`Cleared ${result.removedCount} checked item(s)`, 'Close', {
          duration: 3000,
        });
      },
      error: (err) => {
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to clear checked items',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }

  openNewListDialog(): void {
    const ref = this.dialog.open(ShoppingListDialogComponent, {
      width: '440px',
      maxWidth: '95vw',
    });

    ref.afterClosed().subscribe((res: ShoppingListDialogResult | undefined) => {
      if (!res) {
        return;
      }

      this.shoppingService
        .createShoppingList({
          name: res.name,
          isPinned: res.isPinned,
        })
        .subscribe({
          next: (created) => {
            this.snackBar.open(`Created list "${created.name}"`, 'Close', { duration: 3000 });
            this.loadLists(created.id);
          },
          error: (err) => {
            this.snackBar.open(
              err?.error?.detail || err?.error?.title || 'Failed to create list',
              'Close',
              { duration: 4000 },
            );
          },
        });
    });
  }

  openRenameListDialog(): void {
    const current = this.activeList();
    if (!current) {
      return;
    }

    const ref = this.dialog.open(ShoppingListDialogComponent, {
      data: {
        list: {
          id: current.id,
          name: current.name,
          isPinned: current.isPinned,
          activeUncheckedCount: current.activeItems.length,
          checkedCount: current.checkedItems.length,
          excludedPantryCount: current.excludedPantryItems.length,
          createdAt: current.createdAt,
          modifiedAt: current.modifiedAt,
        },
      },
      width: '440px',
      maxWidth: '95vw',
    });

    ref.afterClosed().subscribe((res: ShoppingListDialogResult | undefined) => {
      if (!res) {
        return;
      }

      this.shoppingService
        .updateShoppingList(current.id, {
          name: res.name,
          isPinned: res.isPinned,
        })
        .subscribe({
          next: (updated) => {
            this.snackBar.open(`Renamed to "${updated.name}"`, 'Close', { duration: 3000 });
            this.loadLists(updated.id);
          },
          error: (err) => {
            this.snackBar.open(
              err?.error?.detail || err?.error?.title || 'Failed to rename list',
              'Close',
              { duration: 4000 },
            );
          },
        });
    });
  }

  toggleListPinned(): void {
    const current = this.activeList();
    if (!current) {
      return;
    }

    const nextPinned = !current.isPinned;
    this.shoppingService
      .updateShoppingList(current.id, {
        name: current.name,
        isPinned: nextPinned,
      })
      .subscribe({
        next: (updated) => {
          this.snackBar.open(nextPinned ? 'Pinned list to top' : 'Unpinned list', 'Close', {
            duration: 3000,
          });
          this.loadLists(updated.id);
        },
        error: (err) => {
          this.snackBar.open(
            err?.error?.detail || err?.error?.title || 'Failed to update pin',
            'Close',
            { duration: 4000 },
          );
        },
      });
  }

  deleteActiveList(): void {
    const current = this.activeList();
    if (!current) {
      return;
    }

    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Shopping List',
        message: `Are you sure you want to delete "${current.name}" and all of its items?`,
        confirmText: 'Delete',
        cancelText: 'Cancel',
        isDestructive: true,
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }

      this.shoppingService.deleteShoppingList(current.id).subscribe({
        next: () => {
          this.snackBar.open(`Deleted "${current.name}"`, 'Close', { duration: 3000 });
          this.loadLists(null);
        },
        error: (err) => {
          this.snackBar.open(
            err?.error?.detail || err?.error?.title || 'Failed to delete list',
            'Close',
            { duration: 4000 },
          );
        },
      });
    });
  }

  openGenerateDialog(): void {
    const current = this.activeList();
    const ref = this.dialog.open(GenerateShoppingListDialogComponent, {
      data: {
        targetShoppingListId: current?.id,
      },
      width: '680px',
      maxWidth: '95vw',
    });

    ref.afterClosed().subscribe((result) => {
      if (!result) {
        return;
      }

      this.snackBar.open(
        `Imported ${result.importedMealsCount} meal(s) into ${result.shoppingList.name}`,
        'Close',
        { duration: 3500 },
      );
      this.loadLists(result.shoppingList.id);
    });
  }

  private refreshActiveList(): void {
    const current = this.activeList();
    if (!current) {
      return;
    }

    this.shoppingService.getShoppingListById(current.id).subscribe({
      next: (detail) => {
        this.activeList.set(detail);
      },
      error: (err) => {
        this.snackBar.open(
          err?.error?.detail || err?.error?.title || 'Failed to refresh list',
          'Close',
          { duration: 4000 },
        );
      },
    });
  }
}
