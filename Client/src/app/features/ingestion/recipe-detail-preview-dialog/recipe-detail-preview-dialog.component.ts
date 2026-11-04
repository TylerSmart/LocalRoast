import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { RecipePreviewData } from '../../../core/models/recipe.models';
import { RecipeDetailPreviewComponent } from '../recipe-detail-preview/recipe-detail-preview.component';

export interface RecipeDetailPreviewDialogData {
  recipe: RecipePreviewData;
  canPublish?: boolean;
  isPublishing?: boolean;
}

export interface RecipeDetailPreviewDialogResult {
  action: 'publish' | 'close';
}

@Component({
  selector: 'app-recipe-detail-preview-dialog',
  imports: [
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    RecipeDetailPreviewComponent,
  ],
  templateUrl: './recipe-detail-preview-dialog.component.html',
  styleUrl: './recipe-detail-preview-dialog.component.scss',
})
export class RecipeDetailPreviewDialogComponent {
  readonly data = inject<RecipeDetailPreviewDialogData>(MAT_DIALOG_DATA);
  readonly dialogRef = inject(MatDialogRef<RecipeDetailPreviewDialogComponent>);

  close(): void {
    this.dialogRef.close({ action: 'close' });
  }

  publish(): void {
    this.dialogRef.close({ action: 'publish' });
  }
}
