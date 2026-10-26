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
import { MatSelectModule } from '@angular/material/select';
import { TagCategory, TagDto, UpsertTagRequest } from '../../../core/models/taxonomy.models';
import { TaxonomyService } from '../../../core/services/taxonomy/taxonomy.service';

export interface TagDialogData {
  tag?: TagDto | null;
  initialName?: string;
  defaultCategory?: TagCategory;
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
    MatSelectModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-tag-dialog',
  templateUrl: './tag-dialog.component.html',
  styleUrl: './tag-dialog.component.scss',
})
export class TagDialogComponent {
  protected readonly data = inject<TagDialogData | null>(MAT_DIALOG_DATA, { optional: true });
  protected readonly dialogRef = inject(MatDialogRef<TagDialogComponent>);
  private readonly fb = inject(FormBuilder);
  private readonly taxonomyService = inject(TaxonomyService);

  protected readonly isEdit = signal<boolean>(!!this.data?.tag);
  protected readonly isSubmitting = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly categories: TagCategory[] = [
    'Course',
    'Cuisine',
    'Dietary',
    'Equipment',
    'Custom',
  ];

  protected readonly form = this.fb.nonNullable.group({
    name: [
      this.data?.tag?.name ?? this.data?.initialName ?? '',
      [Validators.required, Validators.maxLength(80)],
    ],
    category: [
      this.data?.tag?.category ?? this.data?.defaultCategory ?? ('Custom' as TagCategory),
      [Validators.required],
    ],
    sortOrder: [this.data?.tag?.sortOrder ?? 0],
  });

  onSave(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    const request: UpsertTagRequest = {
      name: val.name.trim(),
      category: val.category,
      sortOrder: Number(val.sortOrder) || 0,
    };

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const req$ =
      this.isEdit() && this.data?.tag
        ? this.taxonomyService.updateTag(this.data.tag.id, request)
        : this.taxonomyService.createTag(request);

    req$.subscribe({
      next: (saved) => {
        this.isSubmitting.set(false);
        this.dialogRef.close(saved);
      },
      error: (err: { error?: { detail?: string; title?: string } }) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'An error occurred while saving the tag.',
        );
      },
    });
  }
}
