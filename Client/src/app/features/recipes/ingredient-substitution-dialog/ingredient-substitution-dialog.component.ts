import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
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
import { MatTooltipModule } from '@angular/material/tooltip';
import {
  IngredientSubstitutionOptionDto,
  IngredientSubstitutionResponseDto,
  SuggestIngredientSubstitutionsRequest,
} from '../../../core/models/ai-helper.models';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';

export interface IngredientSubstitutionDialogData {
  recipeId: string;
  recipeIngredientId: string;
  ingredientDisplayName: string;
  scaledAmountDisplay?: string;
  targetServings?: number;
}

@Component({
  imports: [
    FormsModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-ingredient-substitution-dialog',
  templateUrl: './ingredient-substitution-dialog.component.html',
  styleUrl: './ingredient-substitution-dialog.component.scss',
})
export class IngredientSubstitutionDialogComponent implements OnInit {
  protected readonly data = inject<IngredientSubstitutionDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<IngredientSubstitutionDialogComponent>);
  private readonly aiHelperService = inject(AiKitchenHelperService);

  readonly isLoading = signal<boolean>(true);
  readonly response = signal<IngredientSubstitutionResponseDto | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly dietaryNote = signal<string>('');
  readonly copiedIndex = signal<number | null>(null);

  ngOnInit(): void {
    this.fetchSubstitutions();
  }

  fetchSubstitutions(): void {
    if (!this.data?.recipeId || !this.data?.recipeIngredientId) {
      this.errorMessage.set('Missing recipe or ingredient information.');
      this.isLoading.set(false);
      return;
    }

    this.isLoading.set(true);
    this.errorMessage.set(null);

    const request: SuggestIngredientSubstitutionsRequest = {
      recipeId: this.data.recipeId,
      recipeIngredientId: this.data.recipeIngredientId,
      targetServings: this.data.targetServings,
      dietaryOrPantryNote: this.dietaryNote().trim() || null,
    };

    this.aiHelperService.suggestSubstitutions(request).subscribe({
      next: (res) => {
        this.response.set(res);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        this.errorMessage.set(
          err?.error?.detail ??
            'AI kitchen helper is currently unavailable. Please verify your local LLM host is online.',
        );
      },
    });
  }

  copySubstitution(option: IngredientSubstitutionOptionDto, index: number): void {
    const orig = this.data.scaledAmountDisplay
      ? `${this.data.scaledAmountDisplay} ${this.data.ingredientDisplayName}`
      : this.data.ingredientDisplayName;
    const textToCopy = `Substitute ${orig} with ${option.label} (${option.ratioAndAmount}). Notes: ${option.culinaryNotes}`;

    navigator.clipboard?.writeText(textToCopy);
    this.copiedIndex.set(index);
    setTimeout(() => {
      if (this.copiedIndex() === index) {
        this.copiedIndex.set(null);
      }
    }, 2500);
  }

  onClose(): void {
    this.dialogRef.close();
  }
}
