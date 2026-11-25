import { TestBed } from '@angular/core/testing';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { APP_STORAGE_PREFIX } from '../../../app.branding';
import { KitchenReferenceDialogComponent } from '../../../features/kitchen-reference-dialog/kitchen-reference-dialog.component';
import { KitchenReferenceService } from './kitchen-reference.service';

describe('KitchenReferenceService', () => {
  let service: KitchenReferenceService;
  let mockDialog: {
    open: ReturnType<typeof vi.fn>;
    openDialogs: { componentInstance: unknown; close: ReturnType<typeof vi.fn> }[];
  };

  beforeEach(() => {
    localStorage.removeItem(`${APP_STORAGE_PREFIX}-kitchen-ref-tab`);
    mockDialog = {
      open: vi.fn(),
      openDialogs: [],
    };

    TestBed.configureTestingModule({
      providers: [KitchenReferenceService, { provide: MatDialog, useValue: mockDialog }],
    });

    service = TestBed.inject(KitchenReferenceService);
  });

  it('persists active tab to localStorage', () => {
    service.setActiveTab('glossary');
    expect(service.activeTab()).toBe('glossary');
    expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-kitchen-ref-tab`)).toBe('glossary');
  });

  it('opens KitchenReferenceDialogComponent and avoids stacking duplicates', () => {
    const fakeRef = {
      componentInstance: Object.create(KitchenReferenceDialogComponent.prototype),
      close: vi.fn(),
    };
    mockDialog.open.mockReturnValue(
      fakeRef as unknown as MatDialogRef<KitchenReferenceDialogComponent>,
    );

    service.openDialog({ tab: 'temps', query: 'salmon' });
    expect(service.activeTab()).toBe('temps');
    expect(service.searchQuery()).toBe('salmon');
    expect(mockDialog.open).toHaveBeenCalledTimes(1);

    mockDialog.openDialogs.push(fakeRef);
    expect(service.isOpen()).toBe(true);

    service.openDialog({ tab: 'swaps' });
    expect(mockDialog.open).toHaveBeenCalledTimes(1);
    expect(service.activeTab()).toBe('swaps');
  });

  it('toggles dialog open and closed', () => {
    const fakeRef = {
      componentInstance: Object.create(KitchenReferenceDialogComponent.prototype),
      close: vi.fn(),
    };
    mockDialog.open.mockReturnValue(fakeRef);

    service.toggleDialog();
    expect(mockDialog.open).toHaveBeenCalledTimes(1);

    mockDialog.openDialogs.push(fakeRef);
    service.toggleDialog();
    expect(fakeRef.close).toHaveBeenCalledTimes(1);
  });
});
