import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import {
  ActivatedRouteSnapshot,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { APP_BRAND_MARK_SRC, APP_NAME } from '../../../app.branding';
import { AccountService } from '../../../core/services/account/account.service';
import { AuthService } from '../../../core/services/auth/auth.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { KitchenReferenceService } from '../../../core/services/kitchen-reference/kitchen-reference.service';
import { KitchenTimerService } from '../../../core/services/kitchen-timer/kitchen-timer.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { PwaService } from '../../../core/services/pwa/pwa.service';
import { ThemeMode, ThemeService } from '../../../core/services/theme/theme.service';
import { formatTimerDisplay } from '../../../core/utils/culinary-math';
import { KitchenReferenceTab } from '../../../core/utils/kitchen-reference-data';
import { GlobalSearchDialogComponent } from '../global-search-dialog/global-search-dialog.component';

const THEME_ICONS: Record<ThemeMode, string> = {
  light: 'light_mode',
  dark: 'dark_mode',
  system: 'brightness_auto',
};

const THEME_LABELS: Record<ThemeMode, string> = {
  light: 'Light',
  dark: 'Dark',
  system: 'System',
};

function hasFullWidthData(route: ActivatedRouteSnapshot): boolean {
  let current: ActivatedRouteSnapshot | null = route;
  while (current) {
    if (current.data['fullWidth'] === true) {
      return true;
    }
    current = current.firstChild;
  }
  return false;
}

@Component({
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatTooltipModule,
    MatDividerModule,
  ],
  selector: 'app-shell',
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.scss',
  host: {
    '(window:keydown)': 'onGlobalKeydown($event)',
  },
})
export class AppShellComponent implements OnInit {
  protected readonly appName = APP_NAME;
  protected readonly brandMarkSrc = APP_BRAND_MARK_SRC;
  protected readonly accountService = inject(AccountService);
  protected readonly authService = inject(AuthService);
  protected readonly themeService = inject(ThemeService);
  protected readonly ingestionService = inject(IngestionService);
  protected readonly llmSettingsService = inject(LlmSettingsService);
  protected readonly pwaService = inject(PwaService);
  protected readonly kitchenTimerService = inject(KitchenTimerService);
  protected readonly kitchenReferenceService = inject(KitchenReferenceService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly isRemountingOutlet = signal<boolean>(false);
  private remountTimerId: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    this.authService.backendRecovered$
      ?.pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((bootedWhileOffline) => {
        this.ingestionService.refreshSummary();
        if (
          bootedWhileOffline &&
          this.dialog.openDialogs.length === 0 &&
          !this.router.url.startsWith('/recipes/new')
        ) {
          this.remountActiveView();
        }
      });

    this.destroyRef.onDestroy(() => {
      if (this.remountTimerId !== null) {
        clearTimeout(this.remountTimerId);
        this.remountTimerId = null;
      }
    });
  }

  /** Routes opt out of the centered max-width layout with `data: { fullWidth: true }`. */
  protected readonly isFullWidthRoute = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      startWith(null),
      map(() => hasFullWidthData(this.router.routerState.snapshot.root)),
    ),
    { initialValue: false },
  );

  protected readonly themeIcon = computed(() => THEME_ICONS[this.themeService.mode()]);
  protected readonly themeLabel = computed(() => THEME_LABELS[this.themeService.mode()]);

  protected readonly activeTimer = computed(() => {
    const timers = this.kitchenTimerService.timers();
    if (timers.length === 0) {
      return null;
    }
    const completed = timers.filter((t) => t.state === 'completed');
    if (completed.length > 0) {
      return completed.reduce((max, t) =>
        (t.overtimeSeconds ?? 0) > (max.overtimeSeconds ?? 0) ? t : max,
      );
    }
    const running = timers.filter((t) => t.state === 'running');
    if (running.length > 0) {
      return running.reduce((min, t) => (t.remainingSeconds < min.remainingSeconds ? t : min));
    }
    return timers[0];
  });

  protected readonly timerCountdown = computed(() => {
    const t = this.activeTimer();
    return t ? formatTimerDisplay(t) : '';
  });

  ngOnInit(): void {
    this.ingestionService.refreshSummary();
  }

  onGlobalKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      this.openGlobalSearch();
    }
  }

  openGlobalSearch(): void {
    // Avoid opening duplicate search dialogs
    const isOpen = this.dialog.openDialogs.some(
      (d) => d.componentInstance instanceof GlobalSearchDialogComponent,
    );
    if (isOpen) {
      return;
    }

    this.dialog.open(GlobalSearchDialogComponent, {
      width: '600px',
      maxWidth: '95vw',
      position: { top: 'min(8vh, 64px)' },
      panelClass: 'search-dialog-panel',
    });
  }

  openKitchenReference(tab?: KitchenReferenceTab): void {
    this.kitchenReferenceService.openDialog(tab ? { tab } : undefined);
  }

  onLogout(): void {
    this.authService.logout();
  }

  private remountActiveView(): void {
    if (this.remountTimerId !== null) {
      clearTimeout(this.remountTimerId);
    }
    this.isRemountingOutlet.set(true);
    this.remountTimerId = setTimeout(() => {
      this.remountTimerId = null;
      this.isRemountingOutlet.set(false);
    }, 0);
  }
}
