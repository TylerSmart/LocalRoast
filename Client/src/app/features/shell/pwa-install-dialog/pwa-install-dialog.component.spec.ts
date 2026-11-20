import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { APP_BRAND_MARK_SRC, APP_NAME } from '../../../app.branding';
import { PwaInstallDialogComponent } from './pwa-install-dialog.component';

describe('PwaInstallDialogComponent', () => {
  let component: PwaInstallDialogComponent;
  let fixture: ComponentFixture<PwaInstallDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [PwaInstallDialogComponent],
      providers: [{ provide: MatDialogRef, useValue: mockDialogRef }],
    }).compileComponents();

    fixture = TestBed.createComponent(PwaInstallDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders app branding and iOS Home Screen instructions', () => {
    expect(component).toBeTruthy();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain(`Install ${APP_NAME}`);
    expect(text).toContain('Add to Home Screen');

    const img = fixture.nativeElement.querySelector(
      '.header-icon-badge img.brand-icon',
    ) as HTMLImageElement | null;
    expect(img?.getAttribute('src')).toBe(APP_BRAND_MARK_SRC);
  });

  it('closes dialog when Got it button is clicked', () => {
    const closeButtons = Array.from(
      fixture.nativeElement.querySelectorAll('button'),
    ) as HTMLButtonElement[];
    const gotItBtn = closeButtons.find((btn) => btn.textContent?.includes('Got it'));
    expect(gotItBtn).toBeTruthy();
    gotItBtn?.click();

    expect(mockDialogRef.close).toHaveBeenCalledTimes(1);
  });
});
