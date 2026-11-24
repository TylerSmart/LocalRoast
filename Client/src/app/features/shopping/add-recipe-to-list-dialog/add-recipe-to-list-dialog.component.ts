import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import {
  AddRecipeToShoppingListRequest,
  NEW_SHOPPING_LIST_ID,
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';
import {
  buildDefaultWeeklyShoppingListName,
  getTodayIsoLocal,
  normalizeToSundayIso,
} from '../../../core/utils/meal-plan-date';

export interface AddRecipeToListDialogData {
  recipeId: string;
  recipeTitle: string;
  targetServings: number;
  hasSubRecipes: boolean;
}

@Component({
  selector: 'app-add-recipe-to-list-dialog',
  templateUrl: './add-recipe-to-list-dialog.component.html',
  styleUrl: './add-recipe-to-list-dialog.component.scss',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
    MatProgressSpinnerModule,
  ],
})
export class AddRecipeToListDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly dialogRef = inject(MatDialogRef<AddRecipeToListDialogComponent>);
  readonly data = inject<AddRecipeToListDialogData>(MAT_DIALOG_DATA);
  private readonly shoppingService = inject(ShoppingService);
  private readonly destroyRef = inject(DestroyRef);

  readonly newListSentinel = NEW_SHOPPING_LIST_ID;
  readonly shoppingLists = signal<ShoppingListSummaryDto[]>([]);
  readonly isLoadingLists = signal(true);
  readonly isSubmitting = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly selectedTargetListId = signal<string>(NEW_SHOPPING_LIST_ID);
  readonly isCreatingNewList = computed(() => this.selectedTargetListId() === NEW_SHOPPING_LIST_ID);
  private customListNameEdited = false;

  readonly form = this.fb.nonNullable.group({
    targetShoppingListId: [NEW_SHOPPING_LIST_ID, [Validators.required]],
    newListName: [''],
    targetServings: [this.data.targetServings || 4, [Validators.required, Validators.min(0.25)]],
    includeSubRecipes: [true],
    includeOptionalIngredients: [true],
  });

  ngOnInit(): void {
    const weekStartIso = normalizeToSundayIso(getTodayIsoLocal());
    this.form.patchValue(
      { newListName: buildDefaultWeeklyShoppingListName(weekStartIso, []) },
      { emitEvent: false },
    );

    this.form.controls.targetShoppingListId.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((value) => {
        this.selectedTargetListId.set(value);
        if (
          value === NEW_SHOPPING_LIST_ID &&
          !this.customListNameEdited &&
          !this.form.controls.newListName.value.trim()
        ) {
          this.form.patchValue(
            {
              newListName: buildDefaultWeeklyShoppingListName(weekStartIso, this.shoppingLists()),
            },
            { emitEvent: false },
          );
        }
      });

    this.shoppingService
      .getShoppingLists()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (lists) => {
          this.shoppingLists.set(lists);
          this.isLoadingLists.set(false);

          const pinned = lists.find((l) => l.isPinned);
          const initialListId =
            lists.length > 0 ? (pinned ? pinned.id : lists[0].id) : NEW_SHOPPING_LIST_ID;
          const defaultName = buildDefaultWeeklyShoppingListName(weekStartIso, lists);

          this.selectedTargetListId.set(initialListId);
          this.form.patchValue(
            {
              targetShoppingListId: initialListId,
              newListName: this.customListNameEdited
                ? this.form.controls.newListName.value
                : defaultName,
            },
            { emitEvent: false },
          );
        },
        error: () => {
          this.isLoadingLists.set(false);
          this.errorMessage.set('Failed to load shopping lists.');
        },
      });
  }

  onNewListNameInput(): void {
    this.customListNameEdited = true;
  }

  onSubmit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    const creatingNew = val.targetShoppingListId === NEW_SHOPPING_LIST_ID;
    const newListName = val.newListName.trim();
    if (creatingNew && !newListName) {
      this.errorMessage.set('Please enter a name for the new shopping list.');
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const request: AddRecipeToShoppingListRequest = {
      recipeId: this.data.recipeId,
      targetServings: Number(val.targetServings),
      includeSubRecipes: val.includeSubRecipes ?? true,
      includeOptionalIngredients: val.includeOptionalIngredients ?? true,
    };

    const submitObs = creatingNew
      ? this.shoppingService.createAndAddRecipeToList({
          name: newListName,
          isPinned: false,
          recipe: request,
        })
      : this.shoppingService.addRecipeToList(val.targetShoppingListId, request);

    submitObs.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (result) => {
        this.dialogRef.close(result);
      },
      error: (err) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to add ingredients to list.',
        );
      },
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
