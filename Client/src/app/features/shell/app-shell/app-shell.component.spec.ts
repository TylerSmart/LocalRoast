import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { APP_BRAND_MARK_SRC, APP_NAME } from '../../../app.branding';
import { AccountService } from '../../../core/services/account/account.service';
import { AuthService } from '../../../core/services/auth/auth.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { KitchenReferenceService } from '../../../core/services/kitchen-reference/kitchen-reference.service';
import {
  ActiveKitchenTimer,
  KitchenTimerService,
} from '../../../core/services/kitchen-timer/kitchen-timer.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { PwaService } from '../../../core/services/pwa/pwa.service';
import { ThemeService } from '../../../core/services/theme/theme.service';
import { AppShellComponent } from './app-shell.component';

describe('AppShellComponent', () => {
  let component: AppShellComponent;
  let fixture: ComponentFixture<AppShellComponent>;
  let mockDialog: { open: ReturnType<typeof vi.fn>; openDialogs: unknown[] };

  const mockIsBackendUnreachable = signal<boolean>(false);
  const mockIsRetryingConnection = signal<boolean>(false);
  const mockBackendRecovered$ = new Subject<boolean>();
  const mockAuthService = {
    currentUser: () => ({ id: '1', username: 'chef', email: 'chef@example.com' }),
    isBackendUnreachable: mockIsBackendUnreachable.asReadonly(),
    isRetryingConnection: mockIsRetryingConnection.asReadonly(),
    backendRecovered$: mockBackendRecovered$,
    retryConnectionNow: vi.fn(),
    logout: vi.fn(),
  };

  const mockAccountService = {
    openAccountSettingsDialog: vi.fn(),
  };

  const mockThemeService = {
    mode: () => 'light',
    setMode: vi.fn(),
    openThemeSettingsDialog: vi.fn(),
  };

  const mockReviewInboxCount = signal<number>(2);
  const mockIngestionService = {
    reviewInboxCount: mockReviewInboxCount,
    refreshSummary: vi.fn(),
  };

  const mockLlmSettingsService = {
    openLlmSettingsDialog: vi.fn(),
  };

  const mockCanShowInstallOption = signal<boolean>(false);
  const mockUpdateAvailable = signal<boolean>(false);
  const mockIsApplyingUpdate = signal<boolean>(false);
  const mockPwaService = {
    canShowInstallOption: mockCanShowInstallOption.asReadonly(),
    updateAvailable: mockUpdateAvailable.asReadonly(),
    isApplyingUpdate: mockIsApplyingUpdate.asReadonly(),
    promptInstall: vi.fn(),
    applyUpdate: vi.fn(),
  };

  const mockTimers = signal<ActiveKitchenTimer[]>([]);
  const mockKitchenTimerService = {
    timers: mockTimers,
  };

  const mockKitchenReferenceService = {
    openDialog: vi.fn(),
  };

  beforeEach(async () => {
    mockTimers.set([]);
    mockIsBackendUnreachable.set(false);
    mockIsRetryingConnection.set(false);
    mockCanShowInstallOption.set(false);
    mockUpdateAvailable.set(false);
    mockIsApplyingUpdate.set(false);
    mockAuthService.retryConnectionNow.mockClear();
    mockIngestionService.refreshSummary.mockClear();
    mockPwaService.promptInstall.mockClear();
    mockPwaService.applyUpdate.mockClear();
    mockKitchenReferenceService.openDialog.mockClear();
    mockDialog = {
      open: vi.fn(),
      openDialogs: [],
    };

    await TestBed.configureTestingModule({
      imports: [AppShellComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: mockAuthService },
        { provide: AccountService, useValue: mockAccountService },
        { provide: ThemeService, useValue: mockThemeService },
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: LlmSettingsService, useValue: mockLlmSettingsService },
        { provide: PwaService, useValue: mockPwaService },
        { provide: KitchenTimerService, useValue: mockKitchenTimerService },
        { provide: KitchenReferenceService, useValue: mockKitchenReferenceService },
        { provide: MatDialog, useValue: mockDialog },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(AppShellComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders branding and navigation links including Ingest', () => {
    expect(component).toBeTruthy();
    expect(mockIngestionService.refreshSummary).toHaveBeenCalled();

    const text = fixture.nativeElement.textContent;
    expect(text).toContain(APP_NAME);
    const brandImg = fixture.nativeElement.querySelector(
      '.brand-badge img.brand-icon',
    ) as HTMLImageElement | null;
    expect(brandImg).toBeTruthy();
    expect(brandImg?.getAttribute('src')).toBe(APP_BRAND_MARK_SRC);
    expect(text).toContain('Cookbook');
    expect(text).toContain('Meal Plan');
    expect(text).toContain('Shop');
    expect(text).toContain('Ingest');
    expect(text).toContain('Matcher');
    expect(text).toContain('Food Catalog');
  });

  it('renders review inbox count badge when count > 0', () => {
    const badge = fixture.nativeElement.querySelector('.inbox-count-badge');
    expect(badge).toBeTruthy();
    expect(badge.textContent.trim()).toBe('2');
  });

  it('opens search dialog on openGlobalSearch()', () => {
    component.openGlobalSearch();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('handles Ctrl+K shortcut to open search', () => {
    const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true });
    vi.spyOn(event, 'preventDefault');

    component.onGlobalKeydown(event);
    expect(event.preventDefault).toHaveBeenCalled();
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('calls authService.logout on onLogout', () => {
    component.onLogout();
    expect(mockAuthService.logout).toHaveBeenCalled();
  });

  it('renders active timer nav pill when timers exist and hides when empty', () => {
    // When no timers
    let pill = fixture.nativeElement.querySelector('.active-timer-nav-pill');
    expect(pill).toBeNull();

    // When timer is active
    mockTimers.set([
      {
        id: 'timer-1',
        recipeId: 'rec-123',
        recipeTitle: 'Carnitas',
        stepNumber: 2,
        label: 'Sear Pork',
        totalDurationSeconds: 300,
        remainingSeconds: 125,
        state: 'running',
        endsAtMs: Date.now() + 125000,
      },
    ]);
    fixture.detectChanges();

    pill = fixture.nativeElement.querySelector('.active-timer-nav-pill');
    expect(pill).toBeTruthy();
    expect(pill.textContent).toContain('02:05');
    expect(pill.textContent).toContain('Sear Pork');
    expect(pill.getAttribute('href')).toBe('/recipes/rec-123/cook');
  });

  it('shows reconnection banner when backend is unreachable and retries on button click', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="connection-banner"]')).toBeNull();

    mockIsBackendUnreachable.set(true);
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('[data-testid="connection-banner"]');
    expect(banner).toBeTruthy();
    expect(banner.textContent).toContain("you're still signed in");

    const retryBtn = banner.querySelector('button') as HTMLButtonElement;
    retryBtn.click();
    expect(mockAuthService.retryConnectionNow).toHaveBeenCalledTimes(1);
  });

  it('renders account menu trigger as a mat-icon-button matching the theme switcher', () => {
    const accountBtn = fixture.nativeElement.querySelector(
      'button.user-profile-btn',
    ) as HTMLButtonElement | null;
    expect(accountBtn).toBeTruthy();
    expect(accountBtn?.hasAttribute('mat-icon-button')).toBe(true);
    expect(accountBtn?.querySelector('mat-icon')?.textContent?.trim()).toBe('account_circle');
    expect(accountBtn?.querySelector('.user-name')?.textContent?.trim()).toBe('chef');
  });

  it('refreshes ingestion summary when backend recovers', () => {
    mockIngestionService.refreshSummary.mockClear();

    mockBackendRecovered$.next(false);

    expect(mockIngestionService.refreshSummary).toHaveBeenCalledTimes(1);
  });

  it('shows Install and Update actions in user menu when PWA service exposes them', () => {
    mockCanShowInstallOption.set(true);
    mockUpdateAvailable.set(true);
    fixture.detectChanges();

    const userMenuBtn = fixture.nativeElement.querySelector(
      'button.user-profile-btn',
    ) as HTMLButtonElement;
    userMenuBtn.click();
    fixture.detectChanges();

    const installBtn = document.querySelector(
      '[data-testid="pwa-install-menu-item"]',
    ) as HTMLButtonElement | null;
    const updateBtn = document.querySelector(
      '[data-testid="pwa-update-menu-item"]',
    ) as HTMLButtonElement | null;

    expect(installBtn?.textContent).toContain(`Install ${APP_NAME}`);
    expect(updateBtn?.textContent).toContain('Update App');

    installBtn?.click();
    expect(mockPwaService.promptInstall).toHaveBeenCalledTimes(1);

    userMenuBtn.click();
    fixture.detectChanges();
    const updateBtnAgain = document.querySelector(
      '[data-testid="pwa-update-menu-item"]',
    ) as HTMLButtonElement | null;
    updateBtnAgain?.click();
    expect(mockPwaService.applyUpdate).toHaveBeenCalledTimes(1);
  });

  it('opens Kitchen Reference from top bar button and opens Ratios tab for standalone utility timers', () => {
    const refBtn = fixture.nativeElement.querySelector(
      '[data-testid="kitchen-reference-btn"]',
    ) as HTMLButtonElement;
    expect(refBtn).toBeTruthy();
    refBtn.click();
    expect(mockKitchenReferenceService.openDialog).toHaveBeenCalledTimes(1);

    mockTimers.set([
      {
        id: 'timer-util',
        recipeId: '',
        recipeTitle: 'Kitchen Utility',
        stepNumber: null,
        label: 'Soft Boiled Egg',
        totalDurationSeconds: 360,
        remainingSeconds: 300,
        state: 'running',
        endsAtMs: Date.now() + 300000,
      },
    ]);
    fixture.detectChanges();

    const pillBtn = fixture.nativeElement.querySelector(
      'button.active-timer-nav-pill',
    ) as HTMLButtonElement;
    expect(pillBtn).toBeTruthy();
    expect(pillBtn.textContent).toContain('05:00');
    pillBtn.click();
    expect(mockKitchenReferenceService.openDialog).toHaveBeenCalledWith({ tab: 'ratios' });
  });
});
