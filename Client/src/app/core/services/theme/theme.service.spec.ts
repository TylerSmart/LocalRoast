import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { APP_STORAGE_PREFIX } from '../../../app.branding';
import { CurrentUserDto } from '../../models/auth.models';
import { AuthService } from '../auth/auth.service';
import { DEFAULT_THEME_CONFIG, ThemeService } from './theme.service';

describe('ThemeService', () => {
  let service: ThemeService;
  let dialogSpy: { open: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    dialogSpy = { open: vi.fn() };

    TestBed.configureTestingModule({
      providers: [ThemeService, { provide: MatDialog, useValue: dialogSpy }],
    });
    service = TestBed.inject(ThemeService);
  });

  afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    vi.useRealTimers();
  });

  it('should initialize with system mode when no preference stored', () => {
    expect(service.mode()).toBe('system');
    expect(document.documentElement.getAttribute('data-theme')).toBe('system');
    expect(service.primaryPalette()).toBe(DEFAULT_THEME_CONFIG.primaryPalette);
    expect(service.secondaryPalette()).toBe(DEFAULT_THEME_CONFIG.secondaryPalette);
    expect(service.tertiaryPalette()).toBe(DEFAULT_THEME_CONFIG.tertiaryPalette);
    expect(service.fontFamily()).toBe(DEFAULT_THEME_CONFIG.fontFamily);
  });

  it('should update mode and persist to localStorage on setMode', () => {
    service.setMode('dark');
    expect(service.mode()).toBe('dark');
    expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-theme`)).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(service.effectiveTheme()).toBe('dark');
    expect(service.isDark()).toBe(true);

    service.setMode('light');
    expect(service.mode()).toBe('light');
    expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-theme`)).toBe('light');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(service.effectiveTheme()).toBe('light');
    expect(service.isDark()).toBe(false);
  });

  it('should restore stored mode from legacy localStorage key', () => {
    localStorage.setItem(`${APP_STORAGE_PREFIX}-theme`, 'dark');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [ThemeService],
    });
    const restoredService = TestBed.inject(ThemeService);
    expect(restoredService.mode()).toBe('dark');
    expect(restoredService.effectiveTheme()).toBe('dark');
  });

  it('should update primary, secondary, and tertiary palettes and set CSS variables', () => {
    service.setPrimaryPalette('azure');
    expect(service.primaryPalette()).toBe('azure');
    expect(service.primaryPaletteDef().name).toBe('Azure');

    service.setSecondaryPalette('rose');
    expect(service.secondaryPalette()).toBe('rose');
    expect(service.secondaryPaletteDef().name).toBe('Rose');

    service.setTertiaryPalette('chartreuse');
    expect(service.tertiaryPalette()).toBe('chartreuse');
    expect(service.tertiaryPaletteDef().name).toBe('Chartreuse');

    expect(document.documentElement.style.getPropertyValue('--mat-sys-primary')).toBeTruthy();
    expect(document.documentElement.style.getPropertyValue('--mat-sys-secondary')).toBeTruthy();
    expect(document.documentElement.style.getPropertyValue('--mat-sys-tertiary')).toBeTruthy();
  });

  it('should ignore invalid palette ids', () => {
    const original = service.primaryPalette();
    service.setPrimaryPalette('non-existent-palette');
    expect(service.primaryPalette()).toBe(original);
  });

  it('should update font family and set font CSS variables', () => {
    service.setFontFamily('caveat');
    expect(service.fontFamily()).toBe('caveat');
    expect(service.fontFamilyDef().label).toBe('Caveat');
    expect(document.documentElement.style.getPropertyValue('--app-font-family')).toContain(
      'Caveat',
    );

    service.setFontFamily('roboto-mono');
    expect(service.fontFamily()).toBe('roboto-mono');
    expect(document.documentElement.style.getPropertyValue('--app-font-family')).toContain(
      'Roboto Mono',
    );
  });

  it('should ignore invalid font ids', () => {
    const original = service.fontFamily();
    service.setFontFamily('comic-sans');
    expect(service.fontFamily()).toBe(original);
  });

  it('should reset defaults properly', () => {
    service.setMode('dark');
    service.setPrimaryPalette('green');
    service.setSecondaryPalette('yellow');
    service.setTertiaryPalette('magenta');
    service.setFontFamily('pacifico');

    service.resetDefaults();

    expect(service.mode()).toBe(DEFAULT_THEME_CONFIG.mode);
    expect(service.primaryPalette()).toBe(DEFAULT_THEME_CONFIG.primaryPalette);
    expect(service.secondaryPalette()).toBe(DEFAULT_THEME_CONFIG.secondaryPalette);
    expect(service.tertiaryPalette()).toBe(DEFAULT_THEME_CONFIG.tertiaryPalette);
    expect(service.fontFamily()).toBe(DEFAULT_THEME_CONFIG.fontFamily);
  });

  it('should restore full config from stored theme config in localStorage', () => {
    const customConfig = {
      mode: 'dark',
      primaryPalette: 'blue',
      secondaryPalette: 'orange',
      tertiaryPalette: 'red',
      fontFamily: 'share-tech',
    };
    localStorage.setItem(`${APP_STORAGE_PREFIX}-theme-config`, JSON.stringify(customConfig));

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [ThemeService],
    });
    const restoredService = TestBed.inject(ThemeService);

    expect(restoredService.mode()).toBe('dark');
    expect(restoredService.primaryPalette()).toBe('blue');
    expect(restoredService.secondaryPalette()).toBe('orange');
    expect(restoredService.tertiaryPalette()).toBe('red');
    expect(restoredService.fontFamily()).toBe('share-tech');
  });

  it('should open theme settings dialog when openThemeSettingsDialog is called', async () => {
    await service.openThemeSettingsDialog();
    expect(dialogSpy.open).toHaveBeenCalled();
  });

  it('applies remote theme settings from authenticated user without triggering a PUT loop', () => {
    vi.useFakeTimers();
    const userSignal = signal<CurrentUserDto | null>(null);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        ThemeService,
        {
          provide: AuthService,
          useValue: {
            currentUser: userSignal.asReadonly(),
            isAuthenticated: () => !!userSignal(),
            updateCurrentUserThemeSettings: vi.fn(),
          },
        },
      ],
    });

    const syncedService = TestBed.inject(ThemeService);
    const httpMock = TestBed.inject(HttpTestingController);

    userSignal.set({
      id: 'u-1',
      email: 'alice@example.com',
      username: 'alice',
      themeSettings: JSON.stringify({
        mode: 'dark',
        primaryPalette: 'azure',
        secondaryPalette: 'rose',
        tertiaryPalette: 'chartreuse',
        fontFamily: 'caveat',
      }),
    });

    TestBed.tick();
    vi.advanceTimersByTime(1000);

    expect(syncedService.mode()).toBe('dark');
    expect(syncedService.primaryPalette()).toBe('azure');
    expect(syncedService.fontFamily()).toBe('caveat');
    httpMock.expectNone('/api/account/theme');
    httpMock.verify();
  });

  it('debounces PUT /api/account/theme when authenticated user changes theme', () => {
    vi.useFakeTimers();
    const userSignal = signal<CurrentUserDto | null>({
      id: 'u-1',
      email: 'alice@example.com',
      username: 'alice',
      themeSettings: null,
    });
    const updateUserSpy = vi.fn();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        ThemeService,
        {
          provide: AuthService,
          useValue: {
            currentUser: userSignal.asReadonly(),
            isAuthenticated: () => !!userSignal(),
            updateCurrentUserThemeSettings: updateUserSpy,
          },
        },
      ],
    });

    const syncedService = TestBed.inject(ThemeService);
    const httpMock = TestBed.inject(HttpTestingController);

    syncedService.setMode('dark');
    syncedService.setPrimaryPalette('azure');

    vi.advanceTimersByTime(499);
    httpMock.expectNone('/api/account/theme');

    vi.advanceTimersByTime(1);
    const req = httpMock.expectOne('/api/account/theme');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.settings.mode).toBe('dark');
    expect(req.request.body.settings.primaryPalette).toBe('azure');
    req.flush({ themeSettings: JSON.stringify(req.request.body.settings) });

    expect(updateUserSpy).toHaveBeenCalledTimes(1);
    httpMock.verify();
  });

  it('does not call PUT /api/account/theme when user is unauthenticated', () => {
    vi.useFakeTimers();
    const userSignal = signal<CurrentUserDto | null>(null);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        ThemeService,
        {
          provide: AuthService,
          useValue: {
            currentUser: userSignal.asReadonly(),
            isAuthenticated: () => false,
            updateCurrentUserThemeSettings: vi.fn(),
          },
        },
      ],
    });

    const syncedService = TestBed.inject(ThemeService);
    const httpMock = TestBed.inject(HttpTestingController);

    syncedService.setMode('dark');
    vi.advanceTimersByTime(1000);

    httpMock.expectNone('/api/account/theme');
    httpMock.verify();
  });

  it('syncs meta[name="theme-color"] when toggling between light and dark themes', () => {
    const meta = document.createElement('meta');
    meta.name = 'theme-color';
    meta.content = '#1a1715';
    document.head.appendChild(meta);

    try {
      service.setMode('light');
      expect(meta.content).toBe('#f0edf1');

      service.setMode('dark');
      expect(meta.content).toBe('#1a1715');
    } finally {
      meta.remove();
    }
  });
});
