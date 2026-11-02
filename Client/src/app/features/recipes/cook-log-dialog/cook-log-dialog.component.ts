import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  CookLogDto,
  CookLogMutationResultDto,
  UpsertCookLogRequest,
} from '../../../core/models/recipe.models';
import { MediaService } from '../../../core/services/media/media.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import {
  coerceToDateLocal,
  coerceToIsoDateLocal,
  shouldUseTouchDatepicker,
} from '../../../core/utils/meal-plan-date';

export interface CookLogDialogData {
  recipeId: string;
  recipeTitle: string;
  targetServings?: number | null;
  logToEdit?: CookLogDto | null;
}

@Component({
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatDatepickerModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  providers: [provideNativeDateAdapter()],
  selector: 'app-cook-log-dialog',
  templateUrl: './cook-log-dialog.component.html',
  styleUrl: './cook-log-dialog.component.scss',
})
export class CookLogDialogComponent implements OnInit {
  protected readonly data = inject<CookLogDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(
    MatDialogRef<CookLogDialogComponent, CookLogMutationResultDto>,
  );
  private readonly recipeService = inject(RecipeService);
  private readonly mediaService = inject(MediaService);
  private readonly fb = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly isSubmitting = signal<boolean>(false);
  protected readonly isUploadingPhoto = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly useTouchDatepicker = shouldUseTouchDatepicker();

  protected readonly selectedRating = signal<number | null>(null);
  protected readonly photoPreviewUrl = signal<string | null>(null);
  protected readonly photoThumbnailUrl = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    cookedDate: [coerceToDateLocal(new Date()), [Validators.required]],
    servingsMade: [
      this.data.targetServings ?? 4,
      [Validators.required, Validators.min(0.25), Validators.max(999)],
    ],
    notes: ['', [Validators.maxLength(2000)]],
  });

  ngOnInit(): void {
    if (this.data.logToEdit) {
      const log = this.data.logToEdit;
      const dateVal = coerceToDateLocal(log.cookedAt ? log.cookedAt.substring(0, 10) : new Date());
      this.form.patchValue({
        cookedDate: dateVal,
        servingsMade: log.servingsMade ?? this.data.targetServings ?? 4,
        notes: log.notes ?? '',
      });
      this.selectedRating.set(log.rating ?? null);
      this.photoPreviewUrl.set(log.photoImagePath ?? null);
      this.photoThumbnailUrl.set(log.photoThumbnailPath ?? null);
    }
  }

  protected setRating(stars: number): void {
    if (this.selectedRating() === stars) {
      this.selectedRating.set(null);
    } else {
      this.selectedRating.set(stars);
    }
  }

  protected clearRating(): void {
    this.selectedRating.set(null);
  }

  protected onPhotoSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0) return;

    const file = input.files[0];
    this.isUploadingPhoto.set(true);
    this.errorMessage.set(null);

    this.mediaService.uploadCookLogPhoto(file).subscribe({
      next: (res) => {
        this.photoPreviewUrl.set(res.photoImagePath);
        this.photoThumbnailUrl.set(res.photoThumbnailPath);
        this.isUploadingPhoto.set(false);
        input.value = '';
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail || err?.error?.title || 'Failed to upload photo.');
        this.isUploadingPhoto.set(false);
        input.value = '';
      },
    });
  }

  protected removePhoto(): void {
    this.photoPreviewUrl.set(null);
    this.photoThumbnailUrl.set(null);
  }

  protected onSubmit(): void {
    if (this.form.invalid) return;

    this.isSubmitting.set(true);
    this.errorMessage.set(null);

    const val = this.form.getRawValue();
    const dateStr = coerceToIsoDateLocal(val.cookedDate);
    const cookedAtIso = new Date(dateStr + 'T12:00:00Z').toISOString();

    const request: UpsertCookLogRequest = {
      cookedAt: cookedAtIso,
      servingsMade: val.servingsMade,
      rating: this.selectedRating(),
      notes: val.notes.trim() || null,
      photoImagePath: this.photoPreviewUrl(),
      photoThumbnailPath: this.photoThumbnailUrl(),
    };

    const action$ = this.data.logToEdit
      ? this.recipeService.updateCookLog(this.data.recipeId, this.data.logToEdit.id, request)
      : this.recipeService.createCookLog(this.data.recipeId, request);

    action$.subscribe({
      next: (result) => {
        this.isSubmitting.set(false);
        this.snackBar.open(
          this.data.logToEdit ? 'Cook log updated.' : 'Cook logged! Great job in the kitchen!',
          'OK',
          { duration: 3500 },
        );
        this.dialogRef.close(result);
      },
      error: (err) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(
          err?.error?.detail || err?.error?.title || 'Failed to save cook log.',
        );
      },
    });
  }

  protected onCancel(): void {
    this.dialogRef.close();
  }
}
