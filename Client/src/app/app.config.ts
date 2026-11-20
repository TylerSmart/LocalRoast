import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  isDevMode,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { CDK_DRAG_CONFIG, DragDropConfig } from '@angular/cdk/drag-drop';
import { MatIconRegistry } from '@angular/material/icon';
import { MAT_TOOLTIP_DEFAULT_OPTIONS, MatTooltipDefaultOptions } from '@angular/material/tooltip';
import { provideRouter, withInMemoryScrolling } from '@angular/router';
import { provideServiceWorker } from '@angular/service-worker';
import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { AuthService } from './core/services/auth/auth.service';
import { PwaService } from './core/services/pwa/pwa.service';
import { ThemeService } from './core/services/theme/theme.service';

export const appDragDropConfig: DragDropConfig = {
  dragStartDelay: {
    touch: 250,
    mouse: 0,
  },
};

export const appTooltipConfig: MatTooltipDefaultOptions = {
  showDelay: 0,
  hideDelay: 0,
  touchendHideDelay: 1500,
  touchGestures: 'off',
};

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(
      routes,
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled', anchorScrolling: 'enabled' }),
    ),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
    { provide: CDK_DRAG_CONFIG, useValue: appDragDropConfig },
    { provide: MAT_TOOLTIP_DEFAULT_OPTIONS, useValue: appTooltipConfig },
    provideAppInitializer(() => {
      inject(MatIconRegistry).setDefaultFontSetClass(
        'material-symbols-outlined',
        'mat-ligature-font',
      );
      inject(ThemeService);
      inject(PwaService);
      return inject(AuthService).initAuth();
    }),
  ],
};
