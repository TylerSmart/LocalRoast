import { Component, OnInit, inject, signal } from '@angular/core';
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
  ShoppingListSummaryDto,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';

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

  readonly shoppingLists = signal<ShoppingListSummaryDto[]>([]);
  readonly isLoadingLists = signal(true);
  readonly isSubmitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    targetShoppingListId: ['', [Validators.required]],
    targetServings: [this.data.targetServings || 4, [Validators.required, Validators.min(0.25)]],
    includeSubRecipes: [true],
    includeOptionalIngredients: [true],
  });

  ngOnInit(): void {
    this.shoppingService.getShoppingLists().subscribe({
      next: (lists) => {
        this.shoppingLists.set(lists);
        this.isLoadingLists.set(false);

        if (lists.length > 0) {
          const pinned = lists.find((l) => l.isPinned);
          this.form.patchValue({
            targetShoppingListId: pinned ? pinned.id : lists[0].id,
          });
        }
      },
      error: () => {
        this.isLoadingLists.set(false);
        this.errorMessage.set('Failed to load shopping lists.');
      },
    });
  }

  onSubmit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const request: AddRecipeToShoppingListRequest = {
      recipeId: this.data.recipeId,
      targetServings: Number(val.targetServings),
      includeSubRecipes: val.includeSubRecipes ?? true,
      includeOptionalIngredients: val.includeOptionalIngredients ?? true,
    };

    this.shoppingService.addRecipeToList(val.targetShoppingListId, request).subscribe({
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
