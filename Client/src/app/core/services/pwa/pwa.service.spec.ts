import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { NavigationEnd, Router } from '@angular/router';
import { SwUpdate, UnrecoverableStateEvent, VersionEvent } from '@angular/service-worker';
import { Subject } from 'rxjs';
import {
  BeforeInstallPromptEvent,
  PwaService,
  clearPwaRuntimeDataCaches,
  detectIosSafari,
} from './pwa.service';

describe('PwaService', () => {
  let service: PwaService;
  let versionUpdates$: Subject<VersionEvent>;
  let unrecoverable$: Subject<UnrecoverableStateEvent>;
  let routerEvents$: Subject<unknown>;
  let mockRouter: { url: string; events: Subject<unknown> };
  let mockSwUpdate: {
    isEnabled: boolean;
    versionUpdates: Subject<VersionEvent>;
    unrecoverable: Subject<UnrecoverableStateEvent>;
    checkForUpdate: ReturnType<typeof vi.fn>;
    activateUpdate: ReturnType<typeof vi.fn>;
  };
  let snackBarAction$: Subject<void>;
  let mockSnackBar: { open: ReturnType<typeof vi.fn> };
  let mockDialog: { open: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    versionUpdates$ = new Subject<VersionEvent>();
    unrecoverable$ = new Subject<UnrecoverableStateEvent>();
    routerEvents$ = new Subject<unknown>();
    snackBarAction$ = new Subject<void>();

    mockRouter = {
      url: '/recipes',
      events: routerEvents$,
    };

    mockSwUpdate = {
      isEnabled: true,
      versionUpdates: versionUpdates$,
      unrecoverable: unrecoverable$,
      checkForUpdate: vi.fn().mockResolvedValue(false),
      activateUpdate: vi.fn().mockResolvedValue(true),
    };

    mockSnackBar = {
      open: vi.fn().mockReturnValue({
        onAction: () => snackBarAction$.asObservable(),
      }),
    };

    mockDialog = {
      open: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        PwaService,
        { provide: SwUpdate, useValue: mockSwUpdate },
        { provide: Router, useValue: mockRouter },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: MatDialog, useValue: mockDialog },
      ],
    });

    service = TestBed.inject(PwaService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('captures beforeinstallprompt and resolves accepted install outcome', async () => {
    expect(service.canPromptInstall()).toBe(false);

    const promptSpy = vi.fn().mockResolvedValue(undefined);
    const event = new Event('beforeinstallprompt', {
      cancelable: true,
    }) as BeforeInstallPromptEvent;
    Object.defineProperty(event, 'prompt', { value: promptSpy });
    Object.defineProperty(event, 'userChoice', {
      value: Promise.resolve({ outcome: 'accepted', platform: 'web' }),
    });

    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(service.canPromptInstall()).toBe(true);
    expect(service.canShowInstallOption()).toBe(true);

    const outcome = await service.promptInstall();
    expect(promptSpy).toHaveBeenCalledTimes(1);
    expect(outcome).toBe('accepted');
    expect(service.canPromptInstall()).toBe(false);
  });

  it('marks app standalone when appinstalled fires', () => {
    const event = new Event('beforeinstallprompt', {
      cancelable: true,
    }) as BeforeInstallPromptEvent;
    Object.defineProperty(event, 'prompt', { value: vi.fn() });
    Object.defineProperty(event, 'userChoice', {
      value: Promise.resolve({ outcome: 'dismissed', platform: 'web' }),
    });

    window.dispatchEvent(event);
    expect(service.canPromptInstall()).toBe(true);

    window.dispatchEvent(new Event('appinstalled'));
    expect(service.isStandalone()).toBe(true);
    expect(service.canPromptInstall()).toBe(false);
    expect(service.canShowInstallOption()).toBe(false);
  });

  it('shows update snackbar when VERSION_READY fires outside Cook Mode', () => {
    versionUpdates$.next({
      type: 'VERSION_READY',
      currentVersion: { hash: 'v1' },
      latestVersion: { hash: 'v2' },
    });

    expect(service.updateAvailable()).toBe(true);
    expect(mockSnackBar.open).toHaveBeenCalledWith(
      expect.stringContaining('new version'),
      'Update',
      expect.any(Object),
    );
  });

  it('defers update snackbar during Cook Mode and displays it after leaving Cook Mode', () => {
    mockRouter.url = '/recipes/123/cook';

    versionUpdates$.next({
      type: 'VERSION_READY',
      currentVersion: { hash: 'v1' },
      latestVersion: { hash: 'v2' },
    });

    expect(service.updateAvailable()).toBe(true);
    expect(mockSnackBar.open).not.toHaveBeenCalled();

    mockRouter.url = '/recipes/123';
    routerEvents$.next(new NavigationEnd(1, '/recipes/123', '/recipes/123'));

    expect(mockSnackBar.open).toHaveBeenCalledTimes(1);
  });

  it('activates update and reloads when applyUpdate() is called', async () => {
    const reloadSpy = vi
      .spyOn(service as unknown as { reloadPage: () => void }, 'reloadPage')
      .mockImplementation(() => undefined);

    await service.applyUpdate();

    expect(mockSwUpdate.activateUpdate).toHaveBeenCalledTimes(1);
    expect(reloadSpy).toHaveBeenCalledTimes(1);
  });

  it('throttles visibility update checks and allows forced checks', async () => {
    await service.checkForUpdate();
    await service.checkForUpdate();
    expect(mockSwUpdate.checkForUpdate).toHaveBeenCalledTimes(1);

    await service.checkForUpdate(true);
    expect(mockSwUpdate.checkForUpdate).toHaveBeenCalledTimes(2);
  });

  it('detects iOS Safari correctly while excluding Chrome/Firefox on iOS', () => {
    expect(
      detectIosSafari(
        'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
        'iPhone',
        5,
      ),
    ).toBe(true);
    expect(
      detectIosSafari(
        'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0.0.0 Mobile/15E148 Safari/604.1',
        'iPhone',
        5,
      ),
    ).toBe(false);
    expect(
      detectIosSafari(
        'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15',
        'MacIntel',
        5,
      ),
    ).toBe(true);
    expect(
      detectIosSafari(
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36',
        'Win32',
        0,
      ),
    ).toBe(false);
  });

  it('deletes only ngsw data caches in clearPwaRuntimeDataCaches()', async () => {
    const deleteSpy = vi.fn().mockResolvedValue(true);
    const mockCaches = {
      keys: vi
        .fn()
        .mockResolvedValue([
          'ngsw:/:1:assets:app:cache',
          'ngsw:/:1:data:household-read-models:cache',
          'ngsw:/:db:1:data:household-read-models:lru',
          'other-cache',
        ]),
      delete: deleteSpy,
    };

    const originalCaches = globalThis.caches;
    Object.defineProperty(globalThis, 'caches', {
      value: mockCaches,
      configurable: true,
    });

    try {
      await clearPwaRuntimeDataCaches();
      expect(deleteSpy).toHaveBeenCalledTimes(2);
      expect(deleteSpy).toHaveBeenCalledWith('ngsw:/:1:data:household-read-models:cache');
      expect(deleteSpy).toHaveBeenCalledWith('ngsw:/:db:1:data:household-read-models:lru');
    } finally {
      Object.defineProperty(globalThis, 'caches', {
        value: originalCaches,
        configurable: true,
      });
    }
  });
});
