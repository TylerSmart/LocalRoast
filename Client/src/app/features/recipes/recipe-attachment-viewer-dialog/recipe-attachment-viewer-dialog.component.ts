import { HttpClient } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import type { RecipeAttachmentDto } from '../../../core/models/recipe.models';

export interface RecipeAttachmentViewerDialogData {
  attachments: RecipeAttachmentDto[];
  initialIndex?: number;
}

@Component({
  selector: 'app-recipe-attachment-viewer-dialog',
  imports: [
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
    MatSnackBarModule,
  ],
  templateUrl: './recipe-attachment-viewer-dialog.component.html',
  styleUrl: './recipe-attachment-viewer-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeAttachmentViewerDialogComponent {
  readonly data = inject<RecipeAttachmentViewerDialogData>(MAT_DIALOG_DATA);
  readonly dialogRef = inject(MatDialogRef<RecipeAttachmentViewerDialogComponent>);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly http = inject(HttpClient);
  private readonly snackBar = inject(MatSnackBar);

  readonly activeIndex = signal<number>(this.data.initialIndex ?? 0);
  readonly zoomLevel = signal<number>(1.0);
  readonly rotationDegrees = signal<number>(0);
  readonly textContent = signal<string | null>(null);
  readonly isLoadingText = signal<boolean>(false);
  readonly copied = signal<boolean>(false);

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

  readonly isText = computed<boolean>(() => {
    const att = this.currentAttachment();
    if (!att) return false;
    const name = (att.fileName || '').toLowerCase();
    const type = (att.contentType || '').toLowerCase();
    const path = (att.mediaUrlPath || '').toLowerCase();
    return (
      type === 'text/plain' ||
      type === 'text/markdown' ||
      name.endsWith('.txt') ||
      name.endsWith('.md') ||
      path.endsWith('.txt') ||
      path.endsWith('.md')
    );
  });

  readonly safePdfUrl = computed<SafeResourceUrl | null>(() => {
    const att = this.currentAttachment();
    if (!att || !this.isPdf()) return null;
    return this.sanitizer.bypassSecurityTrustResourceUrl(att.mediaUrlPath);
  });

  readonly transformStyle = computed<string>(() => {
    return `scale(${this.zoomLevel()}) rotate(${this.rotationDegrees()}deg)`;
  });

  constructor() {
    effect(() => {
      const att = this.currentAttachment();
      if (this.isText() && att) {
        this.loadTextContent(att.mediaUrlPath);
      } else {
        this.textContent.set(null);
      }
    });
  }

  private loadTextContent(url: string): void {
    this.isLoadingText.set(true);
    this.http.get(url, { responseType: 'text' }).subscribe({
      next: (txt) => {
        this.textContent.set(txt);
        this.isLoadingText.set(false);
      },
      error: () => {
        this.textContent.set('Failed to load text content.');
        this.isLoadingText.set(false);
      },
    });
  }

  async copyText(): Promise<void> {
    const txt = this.textContent();
    if (!txt) return;
    try {
      await navigator.clipboard.writeText(txt);
      this.copied.set(true);
      this.snackBar.open('Source text copied to clipboard', undefined, { duration: 2500 });
      setTimeout(() => this.copied.set(false), 2000);
    } catch {
      this.snackBar.open('Could not copy text to clipboard', undefined, { duration: 2500 });
    }
  }

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
