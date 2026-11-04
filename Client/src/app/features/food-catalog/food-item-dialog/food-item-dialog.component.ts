import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
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
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { CreateFoodItemRequest, FoodItemDto } from '../../../core/models/food-catalog.models';
import { FoodCatalogService } from '../../../core/services/food-catalog/food-catalog.service';

export interface FoodItemDialogData {
  item?: FoodItemDto | null;
  initialName?: string;
}

@Component({
  imports: [
    ReactiveFormsModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-food-item-dialog',
  templateUrl: './food-item-dialog.component.html',
  styleUrl: './food-item-dialog.component.scss',
})
export class FoodItemDialogComponent {
  protected readonly data = inject<FoodItemDialogData | null>(MAT_DIALOG_DATA, { optional: true });
  protected readonly dialogRef = inject(MatDialogRef<FoodItemDialogComponent>);
  private readonly fb = inject(FormBuilder);
  private readonly catalogService = inject(FoodCatalogService);

  protected readonly isEdit = signal<boolean>(!!this.data?.item);
  protected readonly isSubmitting = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    name: [
      this.data?.item?.name ?? this.data?.initialName ?? '',
      [Validators.required, Validators.maxLength(150)],
    ],
    isPantryStaple: [this.data?.item?.isPantryStaple ?? false],
    aliasesText: [this.data?.item?.aliases ? this.data.item.aliases.join(', ') : ''],
  });

  onSave(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    const rawAliases = val.aliasesText
      .split(',')
      .map((a) => a.trim())
      .filter((a) => a.length > 0);

    const request: CreateFoodItemRequest = {
      name: val.name.trim(),
      isPantryStaple: val.isPantryStaple,
      aliases: rawAliases.length > 0 ? rawAliases : null,
    };

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const obs$ =
      this.isEdit() && this.data?.item
        ? this.catalogService.updateFoodItem(this.data.item.id, request)
        : this.catalogService.createFoodItem(request);

    obs$.subscribe({
      next: (savedItem) => {
        this.isSubmitting.set(false);
        this.dialogRef.close(savedItem);
      },
      error: (err) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to save food item.',
        );
      },
    });
  }
}
