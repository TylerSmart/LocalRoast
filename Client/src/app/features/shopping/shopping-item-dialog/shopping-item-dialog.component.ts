import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MEASUREMENT_UNITS, MeasurementUnit } from '../../../core/models/recipe.models';
import {
  CreateShoppingListItemRequest,
  ShoppingListItemDto,
  UpdateShoppingListItemRequest,
} from '../../../core/models/shopping.models';
import { ShoppingService } from '../../../core/services/shopping/shopping.service';

export interface ShoppingItemDialogData {
  listId: string;
  item?: ShoppingListItemDto;
}

@Component({
  selector: 'app-shopping-item-dialog',
  templateUrl: './shopping-item-dialog.component.html',
  styleUrl: './shopping-item-dialog.component.scss',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatProgressSpinnerModule,
  ],
})
export class ShoppingItemDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly dialogRef = inject(MatDialogRef<ShoppingItemDialogComponent>);
  readonly data = inject<ShoppingItemDialogData>(MAT_DIALOG_DATA);
  private readonly shoppingService = inject(ShoppingService);

  readonly measurementUnits = MEASUREMENT_UNITS;
  readonly isSubmitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.group({
    displayName: ['', [Validators.required, Validators.maxLength(200)]],
    quantity: [null as number | null],
    maxQuantity: [null as number | null],
    unit: [null as MeasurementUnit | null],
    customUnitName: ['', [Validators.maxLength(60)]],
    notes: ['', [Validators.maxLength(300)]],
  });

  get isEditing(): boolean {
    return !!this.data.item;
  }

  ngOnInit(): void {
    if (this.data.item) {
      this.form.patchValue({
        displayName: this.data.item.displayName,
        quantity: this.data.item.quantity,
        maxQuantity: this.data.item.maxQuantity,
        unit: this.data.item.unit,
        customUnitName: this.data.item.customUnitName ?? '',
        notes: this.data.item.notes ?? '',
      });
    }
  }

  onSubmit(): void {
    if (this.form.invalid || this.isSubmitting()) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    const name = val.displayName?.trim() || '';
    if (!name) {
      return;
    }
    const qty = val.quantity != null && val.quantity > 0 ? Number(val.quantity) : null;
    const maxQty = val.maxQuantity != null && val.maxQuantity > 0 ? Number(val.maxQuantity) : null;

    if (qty != null && maxQty != null && maxQty < qty) {
      this.errorMessage.set('Max quantity cannot be less than quantity.');
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    if (this.isEditing) {
      const request: UpdateShoppingListItemRequest = {
        displayName: name,
        quantity: qty,
        maxQuantity: maxQty,
        unit: val.unit,
        customUnitName: val.unit === 'Custom' ? val.customUnitName?.trim() || null : null,
        notes: val.notes?.trim() || null,
        isPantryStapleExcluded: this.data.item!.isPantryStapleExcluded,
      };

      this.shoppingService.updateItem(this.data.listId, this.data.item!.id, request).subscribe({
        next: (updated) => {
          this.dialogRef.close(updated);
        },
        error: (err) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to update item.',
          );
        },
      });
    } else {
      const request: CreateShoppingListItemRequest = {
        displayName: name,
        quantity: qty,
        maxQuantity: maxQty,
        unit: val.unit,
        customUnitName: val.unit === 'Custom' ? val.customUnitName?.trim() || null : null,
        notes: val.notes?.trim() || null,
        consolidateIfCompatible: true,
      };

      this.shoppingService.addItem(this.data.listId, request).subscribe({
        next: (created) => {
          this.dialogRef.close(created);
        },
        error: (err) => {
          this.isSubmitting.set(false);
          this.errorMessage.set(err?.error?.detail || err?.error?.title || 'Failed to add item.');
        },
      });
    }
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
