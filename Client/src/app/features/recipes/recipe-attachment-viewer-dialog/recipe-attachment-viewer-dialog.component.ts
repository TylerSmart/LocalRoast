import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import type { RecipeAttachmentDto } from '../../../core/models/recipe.models';

export interface RecipeAttachmentViewerDialogData {
  attachments: RecipeAttachmentDto[];
  initialIndex?: number;
}

@Component({
  selector: 'app-recipe-attachment-viewer-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule, MatTooltipModule],
  templateUrl: './recipe-attachment-viewer-dialog.component.html',
  styleUrl: './recipe-attachment-viewer-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeAttachmentViewerDialogComponent {
  readonly data = inject<RecipeAttachmentViewerDialogData>(MAT_DIALOG_DATA);
  readonly dialogRef = inject(MatDialogRef<RecipeAttachmentViewerDialogComponent>);

  readonly activeIndex = signal<number>(this.data.initialIndex ?? 0);
  readonly zoomLevel = signal<number>(1.0);
  readonly rotationDegrees = signal<number>(0);

  readonly attachments = computed<RecipeAttachmentDto[]>(() => this.data.attachments || []);
  readonly totalCount = computed<number>(() => this.attachments().length);

  readonly currentAttachment = computed<RecipeAttachmentDto | null>(() => {
    const list = this.attachments();
    const idx = this.activeIndex();
    return idx >= 0 && idx < list.length ? list[idx] : null;
  });

  readonly isPdf = computed<boolean>(() => {
    const att = this.currentAttachment();
    if (!att) return false;
    return (
      att.contentType === 'application/pdf' ||
      att.fileName.toLowerCase().endsWith('.pdf') ||
      att.mediaUrlPath.toLowerCase().endsWith('.pdf')
    );
  });

  readonly transformStyle = computed<string>(() => {
    return `scale(${this.zoomLevel()}) rotate(${this.rotationDegrees()}deg)`;
  });

  zoomIn(): void {
    this.zoomLevel.update((z) => Math.min(3.0, +(z + 0.25).toFixed(2)));
  }

  zoomOut(): void {
    this.zoomLevel.update((z) => Math.max(0.5, +(z - 0.25).toFixed(2)));
  }

  resetZoom(): void {
    this.zoomLevel.set(1.0);
  }

  rotateClockwise(): void {
    this.rotationDegrees.update((r) => (r + 90) % 360);
  }

  next(): void {
    if (this.activeIndex() < this.totalCount() - 1) {
      this.activeIndex.update((i) => i + 1);
      this.resetZoom();
    }
  }

  prev(): void {
    if (this.activeIndex() > 0) {
      this.activeIndex.update((i) => i - 1);
      this.resetZoom();
    }
  }

  close(): void {
    this.dialogRef.close();
  }
}
