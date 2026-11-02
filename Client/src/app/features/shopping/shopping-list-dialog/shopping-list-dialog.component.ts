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
import { ShoppingListSummaryDto } from '../../../core/models/shopping.models';

export interface ShoppingListDialogData {
  list?: ShoppingListSummaryDto | null;
}

export interface ShoppingListDialogResult {
  name: string;
  isPinned: boolean;
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
  ],
  selector: 'app-shopping-list-dialog',
  templateUrl: './shopping-list-dialog.component.html',
  styleUrl: './shopping-list-dialog.component.scss',
})
export class ShoppingListDialogComponent {
  protected readonly data = inject<ShoppingListDialogData | null>(MAT_DIALOG_DATA, {
    optional: true,
  });
  protected readonly dialogRef = inject(MatDialogRef<ShoppingListDialogComponent>);
  private readonly fb = inject(FormBuilder);

  protected readonly isEdit = signal<boolean>(!!this.data?.list);

  protected readonly form = this.fb.nonNullable.group({
    name: [
      this.data?.list?.name ?? '',
      [Validators.required, Validators.maxLength(150), Validators.pattern(/\S+/)],
    ],
    isPinned: [this.data?.list?.isPinned ?? false],
  });

  onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const value = this.form.getRawValue();
    const result: ShoppingListDialogResult = {
      name: value.name.trim(),
      isPinned: value.isPinned,
    };
    this.dialogRef.close(result);
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}
