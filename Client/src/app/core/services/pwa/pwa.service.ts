import { ApplicationRef, DestroyRef, Service, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { NavigationEnd, Router } from '@angular/router';
import { SwUpdate } from '@angular/service-worker';
import { filter, first } from 'rxjs';
import { APP_NAME } from '../../../app.branding';

export interface BeforeInstallPromptEvent extends Event {
  readonly platforms?: string[];
  readonly userChoice: Promise<{ outcome: 'accepted' | 'dismissed'; platform: string }>;
  prompt(): Promise<void>;
}

export type PwaInstallOutcome = 'accepted' | 'dismissed' | 'instructions' | 'unavailable';

const UPDATE_CHECK_MIN_INTERVAL_MS = 15 * 60 * 1000;
const PERIODIC_UPDATE_CHECK_MS = 6 * 60 * 60 * 1000;

export async function clearPwaRuntimeDataCaches(): Promise<void> {
  if (typeof caches === 'undefined') {
    return;
  }

  try {
    const names = await caches.keys();
    const dataCaches = names.filter((name) => name.startsWith('ngsw:') && name.includes(':data:'));
    await Promise.all(dataCaches.map((name) => caches.delete(name)));
  } catch {
    // CacheStorage may be unavailable in restricted contexts
  }
}

export function detectIosSafari(
  userAgent: string,
  platform: string,
  maxTouchPoints: number,
): boolean {
  const isIosDevice =
    /iPad|iPhone|iPod/i.test(userAgent) || (platform === 'MacIntel' && maxTouchPoints > 1);
  if (!isIosDevice) {
    return false;
  }

  const isNonSafariIosBrowser = /CriOS|FxiOS|EdgiOS|OPiOS|DuckDuckGo/i.test(userAgent);
  return !isNonSafariIosBrowser;
}

@Service()
export class PwaService {
  private readonly swUpdate = inject(SwUpdate, { optional: true });
  private readonly appRef = inject(ApplicationRef, { optional: true });
  private readonly dialog = inject(MatDialog, { optional: true });
  private readonly snackBar = inject(MatSnackBar, { optional: true });
  private readonly router = inject(Router, { optional: true });
  private readonly destroyRef = inject(DestroyRef);

  private readonly standaloneMediaQuery: MediaQueryList | null =
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia('(display-mode: standalone)')
      : null;

  private readonly deferredPromptState = signal<BeforeInstallPromptEvent | null>(null);
  private readonly isStandaloneState = signal<boolean>(this.readStandaloneMode());
  private readonly isIosSafariState = signal<boolean>(this.readIosSafari());
  private readonly updateAvailableState = signal<boolean>(false);
  private readonly isApplyingUpdateState = signal<boolean>(false);

  private lastUpdateCheckAt = 0;
  private updateSnackBarShown = false;
  private periodicCheckTimerId: ReturnType<typeof setInterval> | null = null;

  readonly isStandalone = this.isStandaloneState.asReadonly();
  readonly canPromptInstall = computed(
    () => this.deferredPromptState() !== null && !this.isStandaloneState(),
  );
  readonly isIosInstallable = computed(() => this.isIosSafariState() && !this.isStandaloneState());
  readonly canShowInstallOption = computed(
    () => !this.isStandaloneState() && (this.canPromptInstall() || this.isIosInstallable()),
  );
  readonly updateAvailable = this.updateAvailableState.asReadonly();
  readonly isApplyingUpdate = this.isApplyingUpdateState.asReadonly();

  private readonly handleBeforeInstallPrompt = (event: Event): void => {
    const promptEvent = event as BeforeInstallPromptEvent;
    if (typeof promptEvent.prompt !== 'function') {
      return;
    }
    promptEvent.preventDefault();
    this.deferredPromptState.set(promptEvent);
  };

  private readonly handleAppInstalled = (): void => {
    this.deferredPromptState.set(null);
    this.isStandaloneState.set(true);
  };

  private readonly handleDisplayModeChange = (): void => {
    this.isStandaloneState.set(this.readStandaloneMode());
  };

  private readonly handleVisibilityChange = (): void => {
    if (typeof document !== 'undefined' && document.visibilityState === 'visible') {
      void this.checkForUpdate();
    }
  };

  constructor() {
    if (typeof window !== 'undefined') {
      window.addEventListener('beforeinstallprompt', this.handleBeforeInstallPrompt);
      window.addEventListener('appinstalled', this.handleAppInstalled);
    }

    if (this.standaloneMediaQuery) {
      this.standaloneMediaQuery.addEventListener('change', this.handleDisplayModeChange);
    }

    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', this.handleVisibilityChange);
    }

    this.initServiceWorkerUpdates();

    this.destroyRef.onDestroy(() => {
      if (this.periodicCheckTimerId !== null) {
        clearInterval(this.periodicCheckTimerId);
        this.periodicCheckTimerId = null;
      }
      if (typeof window !== 'undefined') {
        window.removeEventListener('beforeinstallprompt', this.handleBeforeInstallPrompt);
        window.removeEventListener('appinstalled', this.handleAppInstalled);
      }
      if (this.standaloneMediaQuery) {
        this.standaloneMediaQuery.removeEventListener('change', this.handleDisplayModeChange);
      }
      if (typeof document !== 'undefined') {
        document.removeEventListener('visibilitychange', this.handleVisibilityChange);
      }
    });
  }

  async promptInstall(): Promise<PwaInstallOutcome> {
    const promptEvent = this.deferredPromptState();
    if (promptEvent) {
      this.deferredPromptState.set(null);
      try {
        await promptEvent.prompt();
        const choice = await promptEvent.userChoice;
        if (choice.outcome === 'accepted') {
          return 'accepted';
        }
        return 'dismissed';
      } catch {
        return 'dismissed';
      }
    }

    if (this.isIosInstallable() && this.dialog) {
      const { PwaInstallDialogComponent } =
        await import('../../../features/shell/pwa-install-dialog/pwa-install-dialog.component');
      this.dialog.open(PwaInstallDialogComponent, {
        width: '440px',
        maxWidth: '95vw',
        autoFocus: false,
      });
      return 'instructions';
    }

    return 'unavailable';
  }

  async checkForUpdate(force = false): Promise<boolean> {
    if (!this.swUpdate?.isEnabled) {
      return false;
    }

    const now = Date.now();
    if (!force && now - this.lastUpdateCheckAt < UPDATE_CHECK_MIN_INTERVAL_MS) {
      return false;
    }

    this.lastUpdateCheckAt = now;
    try {
      return await this.swUpdate.checkForUpdate();
    } catch {
      return false;
    }
  }

  async applyUpdate(): Promise<void> {
    if (this.isApplyingUpdateState()) {
      return;
    }

    this.isApplyingUpdateState.set(true);
    try {
      if (this.swUpdate?.isEnabled) {
        await this.swUpdate.activateUpdate();
      }
    } catch {
      // Reload anyway to pick up the newest deployed bundle
    } finally {
      this.reloadPage();
    }
  }

  clearRuntimeDataCaches(): Promise<void> {
    return clearPwaRuntimeDataCaches();
  }

  protected reloadPage(): void {
    if (typeof window !== 'undefined') {
      window.location.reload();
    }
  }

  private initServiceWorkerUpdates(): void {
    if (!this.swUpdate?.isEnabled) {
      return;
    }

    this.swUpdate.versionUpdates.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((event) => {
      if (event.type === 'VERSION_READY') {
        this.updateAvailableState.set(true);
        this.notifyUpdateReadyIfSafe();
      }
    });

    this.swUpdate.unrecoverable.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.updateAvailableState.set(true);
      const ref = this.snackBar?.open(
        `${APP_NAME} needs a quick reload to finish updating.`,
        'Reload',
        { duration: 0 },
      );
      ref
        ?.onAction()
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => {
          this.reloadPage();
        });
    });

    this.router?.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        if (this.updateAvailableState() && !this.updateSnackBarShown) {
          this.notifyUpdateReadyIfSafe();
        }
      });

    this.appRef?.isStable
      .pipe(
        first((isStable) => isStable),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(() => {
        this.periodicCheckTimerId = setInterval(() => {
          void this.checkForUpdate(true);
        }, PERIODIC_UPDATE_CHECK_MS);
      });
  }

  private notifyUpdateReadyIfSafe(): void {
    if (this.updateSnackBarShown || !this.snackBar) {
      return;
    }

    // Never interrupt hands-free Cook Mode with an update snackbar
    if (this.router?.url.includes('/cook')) {
      return;
    }

    this.updateSnackBarShown = true;
    const ref = this.snackBar.open(`A new version of ${APP_NAME} is ready.`, 'Update', {
      duration: 15000,
    });
    ref
      .onAction()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        void this.applyUpdate();
      });
  }

  private readStandaloneMode(): boolean {
    if (typeof window === 'undefined') {
      return false;
    }

    const navStandalone = (navigator as Navigator & { standalone?: boolean }).standalone === true;
    return Boolean(this.standaloneMediaQuery?.matches || navStandalone);
  }

  private readIosSafari(): boolean {
    if (typeof navigator === 'undefined') {
      return false;
    }

    return detectIosSafari(
      navigator.userAgent || '',
      navigator.platform || '',
      navigator.maxTouchPoints || 0,
    );
  }
}
