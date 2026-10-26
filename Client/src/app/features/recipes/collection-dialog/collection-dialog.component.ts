import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
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
import { CollectionDto, UpsertCollectionRequest } from '../../../core/models/taxonomy.models';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';

export interface CollectionDialogData {
  collection?: CollectionDto | null;
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
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-collection-dialog',
  templateUrl: './collection-dialog.component.html',
  styleUrl: './collection-dialog.component.scss',
})
export class CollectionDialogComponent {
  protected readonly data = inject<CollectionDialogData | null>(MAT_DIALOG_DATA, {
    optional: true,
  });
  protected readonly dialogRef = inject(MatDialogRef<CollectionDialogComponent>);
  private readonly fb = inject(FormBuilder);
  private readonly taxonomyService = inject(TaxonomyService);

  protected readonly isEdit = signal<boolean>(!!this.data?.collection);
  protected readonly isSubmitting = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    name: [
      this.data?.collection?.name ?? this.data?.initialName ?? '',
      [Validators.required, Validators.maxLength(100)],
    ],
    description: [this.data?.collection?.description ?? '', [Validators.maxLength(500)]],
    sortOrder: [this.data?.collection?.sortOrder ?? 0],
  });

  onSave(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    const request: UpsertCollectionRequest = {
      name: val.name.trim(),
      description: val.description.trim() || null,
      sortOrder: Number(val.sortOrder) || 0,
    };

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const req$ =
      this.isEdit() && this.data?.collection
        ? this.taxonomyService.updateCollection(this.data.collection.id, request)
        : this.taxonomyService.createCollection(request);

    req$.subscribe({
      next: (saved) => {
        this.isSubmitting.set(false);
        this.dialogRef.close(saved);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(
          err?.error?.detail ||
            err?.error?.title ||
            'An error occurred while saving the collection.',
        );
      },
    });
  }
}
