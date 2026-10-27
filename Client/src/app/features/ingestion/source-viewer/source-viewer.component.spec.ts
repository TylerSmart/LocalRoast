import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { SourceViewerComponent } from './source-viewer.component';
import type { IngestionSourceFileDto } from '../../../core/models/ingestion.models';

describe('SourceViewerComponent', () => {
  let component: SourceViewerComponent;
  let fixture: ComponentFixture<SourceViewerComponent>;

  const mockFiles: IngestionSourceFileDto[] = [
    {
      pageNumber: 1,
      originalFileName: 'page1.png',
      urlPath: '/api/uploads/ingestion/job-p1.webp',
      contentType: 'image/webp',
      fileSizeBytes: 102400,
    },
    {
      pageNumber: 2,
      originalFileName: 'page2.png',
      urlPath: '/api/uploads/ingestion/job-p2.webp',
      contentType: 'image/webp',
      fileSizeBytes: 105600,
    },
  ];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SourceViewerComponent],
      providers: [provideAnimationsAsync()],
    }).compileComponents();

    fixture = TestBed.createComponent(SourceViewerComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('sourceFiles', mockFiles);
    fixture.detectChanges();
  });

  it('creates component and displays first page', () => {
    expect(component).toBeTruthy();
    expect(component.totalPages()).toBe(2);
    expect(component.currentPage()?.pageNumber).toBe(1);
    expect(component.transformStyle()).toBe('scale(1) rotate(0deg) translate(0px, 0px)');
  });

  it('handles zoom in, zoom out, and reset zoom', () => {
    component.zoomIn();
    expect(component.zoomLevel()).toBe(1.25);
    expect(component.transformStyle()).toContain('scale(1.25)');

    component.zoomOut();
    expect(component.zoomLevel()).toBe(1);

    component.zoomIn();
    component.zoomIn();
    expect(component.zoomLevel()).toBe(1.5);

    component.resetZoom();
    expect(component.zoomLevel()).toBe(1);
  });

  it('rotates 90 degrees on rotateClockwise', () => {
    component.rotateClockwise();
    expect(component.rotationDegrees()).toBe(90);
    expect(component.transformStyle()).toContain('rotate(90deg)');

    component.rotateClockwise();
    expect(component.rotationDegrees()).toBe(180);

    component.rotateClockwise();
    component.rotateClockwise();
    expect(component.rotationDegrees()).toBe(0);
  });

  it('navigates to next page and previous page', () => {
    component.nextPage();
    expect(component.activePageIndex()).toBe(1);
    expect(component.currentPage()?.pageNumber).toBe(2);

    component.prevPage();
    expect(component.activePageIndex()).toBe(0);
    expect(component.currentPage()?.pageNumber).toBe(1);
  });

  it('emits promoteCoverRequested when cover button is clicked', () => {
    let emittedPageNumber: number | null = null;
    component.promoteCoverRequested.subscribe((page) => {
      emittedPageNumber = page;
    });

    component.onPromoteCover();
    expect(emittedPageNumber).toBe(1);

    component.nextPage();
    component.onPromoteCover();
    expect(emittedPageNumber).toBe(2);
  });

  it('toggles view mode between vision and text', () => {
    fixture.componentRef.setInput('rawText', 'Sample digital recipe text');
    fixture.detectChanges();

    expect(component.hasDigitalText()).toBe(true);
    component.toggleViewMode('text');
    expect(component.viewMode()).toBe('text');

    component.toggleViewMode('vision');
    expect(component.viewMode()).toBe('vision');
  });
});
