import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
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
  let httpTesting: HttpTestingController;

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
      {
        id: 'att-3',
        sortOrder: 3,
        fileName: 'source.txt',
        mediaUrlPath: '/api/uploads/attachments/source.txt',
        contentType: 'text/plain',
        fileSizeBytes: 1200,
        label: 'Original Source Text',
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
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: mockData },
      ],
    }).compileComponents();

    httpTesting = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RecipeAttachmentViewerDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    httpTesting.verify();
  });

  it('creates component and displays first attachment', () => {
    expect(component).toBeTruthy();
    expect(component.totalCount()).toBe(3);
    expect(component.currentAttachment()?.id).toBe('att-1');
    expect(component.isPdf()).toBe(false);
    expect(component.isText()).toBe(false);
  });

  it('navigates next and renders sanitized PDF embed without error', () => {
    component.next();
    fixture.detectChanges();

    expect(component.activeIndex()).toBe(1);
    expect(component.currentAttachment()?.id).toBe('att-2');
    expect(component.isPdf()).toBe(true);
    expect(component.isText()).toBe(false);
    expect(component.safePdfUrl()).toBeTruthy();

    const pdfObject = fixture.nativeElement.querySelector('object.pdf-embed');
    expect(pdfObject).toBeTruthy();

    component.prev();
    fixture.detectChanges();

    expect(component.activeIndex()).toBe(0);
    expect(component.isPdf()).toBe(false);
    expect(component.isText()).toBe(false);
    const imgEl = fixture.nativeElement.querySelector('img.attachment-image');
    expect(imgEl).toBeTruthy();
  });

  it('navigates to text attachment and fetches content', () => {
    component.next(); // to index 1
    component.next(); // to index 2 (text)
    fixture.detectChanges();

    expect(component.activeIndex()).toBe(2);
    expect(component.isText()).toBe(true);

    const req = httpTesting.expectOne('/api/uploads/attachments/source.txt');
    expect(req.request.method).toBe('GET');
    req.flush('ATK Classic Beef Stew Recipe Raw Text');

    fixture.detectChanges();
    expect(component.textContent()).toBe('ATK Classic Beef Stew Recipe Raw Text');

    const preEl = fixture.nativeElement.querySelector('pre.text-content');
    expect(preEl).toBeTruthy();
    expect(preEl.textContent).toContain('ATK Classic Beef Stew Recipe Raw Text');
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
