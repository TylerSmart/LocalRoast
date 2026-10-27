import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import {
  RecipeAttachmentViewerDialogComponent,
  RecipeAttachmentViewerDialogData,
} from './recipe-attachment-viewer-dialog.component';

describe('RecipeAttachmentViewerDialogComponent', () => {
  let component: RecipeAttachmentViewerDialogComponent;
  let fixture: ComponentFixture<RecipeAttachmentViewerDialogComponent>;

  const mockDialogRef = {
    close: vi.fn(),
  };

  const mockData: RecipeAttachmentViewerDialogData = {
    attachments: [
      {
        id: 'att-1',
        sortOrder: 1,
        fileName: 'scan1.webp',
        mediaUrlPath: '/api/uploads/attachments/scan1.webp',
        contentType: 'image/webp',
        fileSizeBytes: 50000,
        label: 'Front Cover',
        createdAt: '2026-10-26T12:00:00Z',
      },
      {
        id: 'att-2',
        sortOrder: 2,
        fileName: 'recipe.pdf',
        mediaUrlPath: '/api/uploads/attachments/recipe.pdf',
        contentType: 'application/pdf',
        fileSizeBytes: 120000,
        label: 'Original PDF Document',
        createdAt: '2026-10-26T12:00:00Z',
      },
    ],
    initialIndex: 0,
  };

  beforeEach(async () => {
    mockDialogRef.close.mockClear();

    await TestBed.configureTestingModule({
      imports: [RecipeAttachmentViewerDialogComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockData },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeAttachmentViewerDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('creates component and displays first attachment', () => {
    expect(component).toBeTruthy();
    expect(component.totalCount()).toBe(2);
    expect(component.currentAttachment()?.id).toBe('att-1');
    expect(component.isPdf()).toBe(false);
  });

  it('navigates next and renders sanitized PDF embed without error', () => {
    component.next();
    fixture.detectChanges();

    expect(component.activeIndex()).toBe(1);
    expect(component.currentAttachment()?.id).toBe('att-2');
    expect(component.isPdf()).toBe(true);
    expect(component.safePdfUrl()).toBeTruthy();

    const pdfObject = fixture.nativeElement.querySelector('object.pdf-embed');
    expect(pdfObject).toBeTruthy();

    component.prev();
    fixture.detectChanges();

    expect(component.activeIndex()).toBe(0);
    expect(component.isPdf()).toBe(false);
    const imgEl = fixture.nativeElement.querySelector('img.attachment-image');
    expect(imgEl).toBeTruthy();
  });

  it('zooms and rotates image attachments', () => {
    component.zoomIn();
    expect(component.zoomLevel()).toBe(1.25);

    component.rotateClockwise();
    expect(component.rotationDegrees()).toBe(90);

    component.resetZoom();
    expect(component.zoomLevel()).toBe(1);
  });

  it('closes dialog on close()', () => {
    component.close();
    expect(mockDialogRef.close).toHaveBeenCalled();
  });
});
