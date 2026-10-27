import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import type {
  IngestionSourceFileDto,
  PdfInspectionSummaryDto,
} from '../../../core/models/ingestion.models';

@Component({
  selector: 'app-source-viewer',
  imports: [
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './source-viewer.component.html',
  styleUrl: './source-viewer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SourceViewerComponent {
  private readonly sanitizer = inject(DomSanitizer);

  readonly sourceFiles = input<IngestionSourceFileDto[]>([]);
  readonly pdfInspection = input<PdfInspectionSummaryDto | null | undefined>(null);
  readonly rawText = input<string | null | undefined>(null);
  readonly currentCoverImagePath = input<string | null | undefined>(null);
  readonly isPromoting = input<boolean>(false);

  readonly promoteCoverRequested = output<number>();

  readonly activePageIndex = signal<number>(0);
  readonly zoomLevel = signal<number>(1.0);
  readonly rotationDegrees = signal<number>(0);
  readonly viewMode = signal<'vision' | 'text' | 'pdf'>('vision');
  readonly panOffset = signal<{ x: number; y: number }>({ x: 0, y: 0 });
  readonly isDragging = signal<boolean>(false);
  private dragStart = { x: 0, y: 0 };
  private panStart = { x: 0, y: 0 };

  readonly originalPdfFile = computed<IngestionSourceFileDto | null>(() => {
    return (
      this.sourceFiles().find(
        (f) =>
          f.contentType === 'application/pdf' ||
          f.urlPath.toLowerCase().endsWith('.pdf') ||
          f.originalFileName.toLowerCase().endsWith('.pdf'),
      ) ?? null
    );
  });

  readonly safePdfUrl = computed<SafeResourceUrl | null>(() => {
    const pdf = this.originalPdfFile();
    if (!pdf) return null;
    return this.sanitizer.bypassSecurityTrustResourceUrl(pdf.urlPath);
  });

  readonly pages = computed<IngestionSourceFileDto[]>(() => {
    const pdf = this.pdfInspection();
    if (pdf?.renderedPageImages && pdf.renderedPageImages.length > 0) {
      return pdf.renderedPageImages;
    }
    const direct = this.sourceFiles();
    return direct.filter((f) => this.isImageFile(f));
  });

  readonly effectiveViewMode = computed<'vision' | 'text' | 'pdf'>(() => {
    const mode = this.viewMode();
    if (mode === 'vision' && this.pages().length === 0) {
      if (this.hasDigitalText()) return 'text';
      if (this.originalPdfFile()) return 'pdf';
    }
    return mode;
  });

  private isImageFile(file: IngestionSourceFileDto): boolean {
    if (file.contentType?.startsWith('image/')) return true;
    const url = (file.urlPath || '').toLowerCase();
    const name = (file.originalFileName || '').toLowerCase();
    return (
      /\.(jpe?g|png|webp|gif|bmp|avif)$/i.test(url) ||
      /\.(jpe?g|png|webp|gif|bmp|avif)$/i.test(name)
    );
  }

  readonly currentPage = computed<IngestionSourceFileDto | null>(() => {
    const list = this.pages();
    const idx = this.activePageIndex();
    return idx >= 0 && idx < list.length ? list[idx] : null;
  });

  readonly totalPages = computed<number>(() => this.pages().length);

  readonly hasMultiplePages = computed<boolean>(() => this.pages().length > 1);

  readonly hasDigitalText = computed<boolean>(() => {
    return !!this.rawText() || this.pdfInspection()?.extractionMode === 'DigitalText';
  });

  readonly selectedCoverPageNumber = signal<number | null>(null);

  readonly isCurrentPageCover = computed<boolean>(() => {
    const cur = this.currentPage();
    if (!cur) return false;
    if (this.selectedCoverPageNumber() !== null) {
      return this.selectedCoverPageNumber() === cur.pageNumber;
    }
    const cover = this.currentCoverImagePath();
    if (!cover) return false;
    return cover.includes(cur.urlPath) || cur.urlPath.includes(cover);
  });

  readonly transformStyle = computed<string>(() => {
    const z = this.zoomLevel();
    const r = this.rotationDegrees();
    const p = this.panOffset();
    return `scale(${z}) rotate(${r}deg) translate(${p.x}px, ${p.y}px)`;
  });

  zoomIn(): void {
    this.zoomLevel.update((z) => Math.min(3.0, +(z + 0.25).toFixed(2)));
  }

  zoomOut(): void {
    this.zoomLevel.update((z) => Math.max(0.5, +(z - 0.25).toFixed(2)));
  }

  resetZoom(): void {
    this.zoomLevel.set(1.0);
    this.panOffset.set({ x: 0, y: 0 });
  }

  rotateClockwise(): void {
    this.rotationDegrees.update((r) => (r + 90) % 360);
  }

  selectPage(index: number): void {
    if (index >= 0 && index < this.pages().length) {
      this.activePageIndex.set(index);
      this.resetZoom();
    }
  }

  nextPage(): void {
    if (this.activePageIndex() < this.pages().length - 1) {
      this.selectPage(this.activePageIndex() + 1);
    }
  }

  prevPage(): void {
    if (this.activePageIndex() > 0) {
      this.selectPage(this.activePageIndex() - 1);
    }
  }

  toggleViewMode(mode: 'vision' | 'text' | 'pdf'): void {
    this.viewMode.set(mode);
  }

  onPromoteCover(): void {
    const page = this.currentPage();
    if (page && !this.isPromoting()) {
      this.selectedCoverPageNumber.set(page.pageNumber);
      this.promoteCoverRequested.emit(page.pageNumber);
    }
  }

  onPointerDown(event: PointerEvent): void {
    if (this.zoomLevel() <= 1.0) return;
    this.isDragging.set(true);
    this.dragStart = { x: event.clientX, y: event.clientY };
    this.panStart = { ...this.panOffset() };
    (event.target as HTMLElement)?.setPointerCapture?.(event.pointerId);
  }

  onPointerMove(event: PointerEvent): void {
    if (!this.isDragging()) return;
    const dx = (event.clientX - this.dragStart.x) / this.zoomLevel();
    const dy = (event.clientY - this.dragStart.y) / this.zoomLevel();
    this.panOffset.set({
      x: this.panStart.x + dx,
      y: this.panStart.y + dy,
    });
  }

  onPointerUp(event: PointerEvent): void {
    if (this.isDragging()) {
      this.isDragging.set(false);
      (event.target as HTMLElement)?.releasePointerCapture?.(event.pointerId);
    }
  }
}
