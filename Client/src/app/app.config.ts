import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { CDK_DRAG_CONFIG, DragDropConfig } from '@angular/cdk/drag-drop';
import { MatIconRegistry } from '@angular/material/icon';
import { provideRouter, withInMemoryScrolling } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { AuthService } from './core/services/auth/auth.service';
import { ThemeService } from './core/services/theme/theme.service';

export const appDragDropConfig: DragDropConfig = {
  dragStartDelay: {
    touch: 250,
    mouse: 0,
  },
};

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(
      routes,
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled', anchorScrolling: 'enabled' }),
    ),
    provideHttpClient(withInterceptors([authInterceptor])),
    { provide: CDK_DRAG_CONFIG, useValue: appDragDropConfig },
    provideAppInitializer(() => {
      inject(MatIconRegistry).setDefaultFontSetClass(
        'material-symbols-outlined',
        'mat-ligature-font',
      );
      inject(ThemeService);
      return inject(AuthService).initAuth();
    }),
  ],
};
