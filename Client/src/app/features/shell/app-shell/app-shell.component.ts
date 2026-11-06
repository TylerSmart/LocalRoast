import { Component, OnInit, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  ActivatedRouteSnapshot,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { filter, map, startWith } from 'rxjs';
import { APP_BRAND_ICON, APP_NAME } from '../../../app.branding';
import { AccountService } from '../../../core/services/account/account.service';
import { AuthService } from '../../../core/services/auth/auth.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { KitchenTimerService } from '../../../core/services/kitchen-timer/kitchen-timer.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { ThemeMode, ThemeService } from '../../../core/services/theme/theme.service';
import { formatCountdown } from '../../../core/utils/culinary-math';
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
  protected readonly brandIcon = APP_BRAND_ICON;
  protected readonly accountService = inject(AccountService);
  protected readonly authService = inject(AuthService);
  protected readonly themeService = inject(ThemeService);
  protected readonly ingestionService = inject(IngestionService);
  protected readonly llmSettingsService = inject(LlmSettingsService);
  protected readonly kitchenTimerService = inject(KitchenTimerService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);

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
    const running = timers.filter((t) => t.state === 'running');
    if (running.length > 0) {
      return running.reduce((min, t) => (t.remainingSeconds < min.remainingSeconds ? t : min));
    }
    return timers[0];
  });

  protected readonly timerCountdown = computed(() => {
    const t = this.activeTimer();
    return t ? formatCountdown(t.remainingSeconds) : '';
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
      panelClass: 'search-dialog-panel',
    });
  }

  onLogout(): void {
    this.authService.logout();
  }
}
