import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import {
  ApplyTemplateMode,
  MealPlanTemplateDetailDto,
  MealPlanTemplateSummaryDto,
  MealPlanWeekDto,
} from '../../../core/models/meal-plan.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';

export interface MealPlanTemplatesDialogData {
  currentWeekStartDate: string;
  currentWeekLabel: string;
  currentWeekEntryCount: number;
}

@Component({
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatTabsModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatButtonToggleModule,
    MatProgressSpinnerModule,
    MatDividerModule,
    MatExpansionModule,
  ],
  selector: 'app-meal-plan-templates-dialog',
  templateUrl: './meal-plan-templates-dialog.component.html',
  styleUrl: './meal-plan-templates-dialog.component.scss',
})
export class MealPlanTemplatesDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly dialogRef = inject(
    MatDialogRef<MealPlanTemplatesDialogComponent, MealPlanWeekDto>,
  );
  protected readonly data: MealPlanTemplatesDialogData = inject(MAT_DIALOG_DATA) || {
    currentWeekStartDate: '',
    currentWeekLabel: '',
    currentWeekEntryCount: 0,
  };
  private readonly mealPlanService = inject(MealPlanService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly isLoading = signal(false);
  protected readonly isApplying = signal(false);
  protected readonly isSaving = signal(false);
  protected readonly templates = signal<MealPlanTemplateSummaryDto[]>([]);
  protected readonly selectedTemplateDetail = signal<MealPlanTemplateDetailDto | null>(null);
  protected readonly applyMode = signal<ApplyTemplateMode>('Merge');
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly saveForm = this.fb.group({
    name: this.fb.control<string>('', {
      validators: [Validators.required, Validators.minLength(2), Validators.maxLength(120)],
      nonNullable: true,
    }),
    description: this.fb.control<string>(''),
    includeOnDeck: this.fb.control<boolean>(true, { nonNullable: true }),
  });

  ngOnInit(): void {
    this.loadTemplates();
  }

  loadTemplates(): void {
    this.isLoading.set(true);
    this.mealPlanService.getTemplates().subscribe({
      next: (list) => {
        this.templates.set(list);
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
      },
    });
  }

  protected previewTemplate(templateId: string): void {
    if (this.selectedTemplateDetail()?.id === templateId) {
      this.selectedTemplateDetail.set(null);
      return;
    }

    this.mealPlanService.getTemplateById(templateId).subscribe({
      next: (detail) => {
        this.selectedTemplateDetail.set(detail);
      },
    });
  }

  protected applyTemplate(templateId: string): void {
    this.isApplying.set(true);
    this.errorMessage.set(null);

    this.mealPlanService
      .applyTemplate(templateId, {
        targetWeekStartDate: this.data.currentWeekStartDate,
        mode: this.applyMode(),
      })
      .subscribe({
        next: (updatedWeek) => {
          this.isApplying.set(false);
          this.snackBar.open('Meal plan template applied successfully!', 'Close', {
            duration: 3000,
          });
          this.dialogRef.close(updatedWeek);
        },
        error: (err) => {
          this.isApplying.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to apply meal plan template.',
          );
        },
      });
  }

  protected deleteTemplate(template: MealPlanTemplateSummaryDto): void {
    const confirmRef = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete Template',
        message: `Are you sure you want to delete the template "${template.name}"?`,
        confirmText: 'Delete',
        isDestructive: true,
      },
    });

    confirmRef.afterClosed().subscribe((confirmed: boolean) => {
      if (!confirmed) return;

      this.mealPlanService.deleteTemplate(template.id).subscribe({
        next: () => {
          this.templates.update((list) => list.filter((t) => t.id !== template.id));
          if (this.selectedTemplateDetail()?.id === template.id) {
            this.selectedTemplateDetail.set(null);
          }
          this.snackBar.open(`Template "${template.name}" deleted.`, 'Close', { duration: 3000 });
        },
        error: (err) => {
          this.snackBar.open(err?.error?.detail || 'Failed to delete template.', 'Close', {
            duration: 4000,
          });
        },
      });
    });
  }

  protected onSaveCurrentWeek(): void {
    if (this.saveForm.invalid) return;

    this.isSaving.set(true);
    this.errorMessage.set(null);

    const val = this.saveForm.getRawValue();
    this.mealPlanService
      .createTemplateFromWeek({
        weekStartDate: this.data.currentWeekStartDate,
        name: val.name.trim(),
        description: val.description?.trim() || null,
        includeOnDeck: val.includeOnDeck,
      })
      .subscribe({
        next: (created) => {
          this.isSaving.set(false);
          this.saveForm.reset({ name: '', description: '', includeOnDeck: true });
          this.snackBar.open(`Template "${created.name}" created!`, 'Close', { duration: 3000 });
          this.loadTemplates();
        },
        error: (err) => {
          this.isSaving.set(false);
          this.errorMessage.set(
            err?.error?.detail || err?.error?.title || 'Failed to save template from current week.',
          );
        },
      });
  }

  protected onCancel(): void {
    this.dialogRef.close();
  }
}
