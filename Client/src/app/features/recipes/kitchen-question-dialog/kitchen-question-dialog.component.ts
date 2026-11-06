import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
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
import { takeUntil } from 'rxjs';
import { KitchenAnswerDto } from '../../../core/models/ai-helper.models';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';

export interface KitchenQuestionDialogData {
  recipeId?: string;
  recipeTitle?: string;
  recipeIngredientId?: string;
  ingredientName?: string;
  recipeStepId?: string;
  stepNumber?: number;
}

@Component({
  imports: [
    ReactiveFormsModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-kitchen-question-dialog',
  templateUrl: './kitchen-question-dialog.component.html',
  styleUrl: './kitchen-question-dialog.component.scss',
})
export class KitchenQuestionDialogComponent implements OnInit {
  protected readonly data = inject<KitchenQuestionDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<KitchenQuestionDialogComponent>);
  private readonly aiHelper = inject(AiKitchenHelperService);
  private readonly destroyRef = inject(DestroyRef);

  readonly question = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(600), Validators.pattern(/\S/)],
  });
  readonly questionForm = new FormGroup({ question: this.question });
  readonly isLoading = signal(false);
  readonly answer = signal<KitchenAnswerDto | null>(null);
  readonly errorMessage = signal<string | null>(null);

  ngOnInit(): void {
    this.question.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.answer.set(null);
      this.errorMessage.set(null);
    });

    if (this.data.recipeIngredientId) {
      this.question.setValue(
        'What is this ingredient, and how should I prepare it for this recipe?',
      );
      this.ask();
    } else if (this.data.recipeStepId) {
      this.question.setValue('Explain this step and any cooking techniques it uses.');
      this.ask();
    }
  }

  ask(): void {
    if (this.isLoading() || this.question.invalid) {
      this.question.markAsTouched();
      return;
    }

    this.answer.set(null);
    this.errorMessage.set(null);
    this.isLoading.set(true);

    this.aiHelper
      .askKitchenQuestion({
        question: this.question.value.trim(),
        recipeId: this.data.recipeId,
        recipeIngredientId: this.data.recipeIngredientId,
        recipeStepId: this.data.recipeStepId,
      })
      .pipe(takeUntil(this.dialogRef.beforeClosed()), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (answer) => {
          this.answer.set(answer);
          this.isLoading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.errorMessage.set(
            err.error?.detail ||
              err.error?.title ||
              'Unable to get an answer. Check your AI provider and try again.',
          );
          this.isLoading.set(false);
        },
      });
  }

  close(): void {
    this.dialogRef.close();
  }
}
