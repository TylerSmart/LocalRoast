import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';
import { ShareLinkDto } from '../../../core/models/share-link.models';
import { ShareLinkService } from '../../../core/services/share-link/share-link.service';
import { RecipeShareDialogComponent, RecipeShareDialogData } from './recipe-share-dialog.component';

describe('RecipeShareDialogComponent', () => {
  let component: RecipeShareDialogComponent;
  let fixture: ComponentFixture<RecipeShareDialogComponent>;
  let shareLinkServiceSpy: {
    getShareLinks: ReturnType<typeof vi.fn>;
    createShareLink: ReturnType<typeof vi.fn>;
    revokeShareLink: ReturnType<typeof vi.fn>;
    deleteShareLink: ReturnType<typeof vi.fn>;
    buildPublicShareUrl: ReturnType<typeof vi.fn>;
  };
  let snackBarSpy: { open: ReturnType<typeof vi.fn> };
  let dialogRefSpy: { close: ReturnType<typeof vi.fn> };

  const mockDialogData: RecipeShareDialogData = {
    recipeId: 'rec-1',
    recipeTitle: 'Fish Tacos',
    recipeStatus: 'Published',
  };

  const mockLinks: ShareLinkDto[] = [
    {
      id: 'link-1',
      recipeId: 'rec-1',
      token: 'tok12345678901234567890123456789',
      label: 'Family Share',
      includeCookLogs: true,
      expiresAt: null,
      revokedAt: null,
      isActive: true,
      viewCount: 5,
      lastAccessedAt: '2026-10-26T20:00:00Z',
      createdAt: '2026-10-26T18:00:00Z',
      createdBy: 'user1',
    },
  ];

  beforeEach(async () => {
    shareLinkServiceSpy = {
      getShareLinks: vi.fn().mockReturnValue(of(mockLinks)),
      createShareLink: vi.fn(),
      revokeShareLink: vi.fn(),
      deleteShareLink: vi.fn(),
      buildPublicShareUrl: vi.fn((token: string) => `http://localhost:4200/share/${token}`),
    };

    snackBarSpy = {
      open: vi.fn(),
    };
    dialogRefSpy = {
      close: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [RecipeShareDialogComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: mockDialogData },
        { provide: MatDialogRef, useValue: dialogRefSpy },
        { provide: ShareLinkService, useValue: shareLinkServiceSpy },
        { provide: MatSnackBar, useValue: snackBarSpy },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeShareDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads and renders existing share links on init', () => {
    expect(shareLinkServiceSpy.getShareLinks).toHaveBeenCalledWith('rec-1');
    expect(component['shareLinks']().length).toBe(1);
    expect(component['shareLinks']()[0].label).toBe('Family Share');
  });

  it('creates a new share link and prepends to list', () => {
    const newLink: ShareLinkDto = {
      id: 'link-2',
      recipeId: 'rec-1',
      token: 'tok_new_123456789012345678901234',
      label: 'New Party Link',
      includeCookLogs: false,
      expiresAt: null,
      revokedAt: null,
      isActive: true,
      viewCount: 0,
      lastAccessedAt: null,
      createdAt: '2026-10-26T21:00:00Z',
      createdBy: 'user1',
    };
    shareLinkServiceSpy.createShareLink.mockReturnValue(of(newLink));

    component['form'].controls.label.setValue('New Party Link');
    component['form'].controls.expirationPreset.setValue('7d');
    component.onCreateShareLink();

    expect(shareLinkServiceSpy.createShareLink).toHaveBeenCalled();
    expect(component['shareLinks']().length).toBe(2);
    expect(component['shareLinks']()[0].id).toBe('link-2');
  });

  it('revokes an active share link and updates state in-place', () => {
    const revokedLink: ShareLinkDto = {
      ...mockLinks[0],
      isActive: false,
      revokedAt: '2026-10-26T21:00:00Z',
    };
    shareLinkServiceSpy.revokeShareLink.mockReturnValue(of(revokedLink));

    component.onRevoke(mockLinks[0]);

    expect(shareLinkServiceSpy.revokeShareLink).toHaveBeenCalledWith('rec-1', 'link-1');
    expect(component['shareLinks']()[0].isActive).toBe(false);
    expect(snackBarSpy.open).toHaveBeenCalledWith(
      'Share link revoked.',
      'Close',
      expect.any(Object),
    );
  });

  it('deletes an inactive share link and removes from list', () => {
    shareLinkServiceSpy.deleteShareLink.mockReturnValue(of(undefined));

    component.onDelete(mockLinks[0]);

    expect(shareLinkServiceSpy.deleteShareLink).toHaveBeenCalledWith('rec-1', 'link-1');
    expect(component['shareLinks']().length).toBe(0);
    expect(snackBarSpy.open).toHaveBeenCalledWith(
      'Share link deleted.',
      'Close',
      expect.any(Object),
    );
  });

  it('shows error banner when loading share links fails', () => {
    shareLinkServiceSpy.getShareLinks.mockReturnValue(
      throwError(() => ({ error: { detail: 'Network error' } })),
    );
    component.loadShareLinks();
    expect(component['errorMessage']()).toBe('Network error');
  });

  it('closes dialog when close() is invoked', () => {
    component.close();
    expect(dialogRefSpy.close).toHaveBeenCalled();
  });
});
