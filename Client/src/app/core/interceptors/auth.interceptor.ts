import { HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, tap, throwError } from 'rxjs';
import { AuthService } from '../services/auth/auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const authService = inject(AuthService);
  const token = authService.token();
  const isApiRequest = req.url.includes('/api/');

  let authReq = req;
  if (token) {
    authReq = req.clone({
      setHeaders: {
        Authorization: `Bearer ${token}`,
      },
    });
  }

  return next(authReq).pipe(
    tap((event) => {
      if (isApiRequest && event instanceof HttpResponse && event.headers.has('X-Api-Version')) {
        authService.reportBackendReachable?.();
      }
    }),
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        if (error.status === 401) {
          const isInitialized = authService.isInitialized?.() ?? true;
          const isAuthEndpoint =
            req.url.includes('/auth/login') ||
            req.url.includes('/auth/register') ||
            req.url.includes('/auth/providers') ||
            req.url.includes('/auth/external-callback') ||
            req.url.includes('/auth/logout') ||
            (!isInitialized && req.url.includes('/auth/me'));
          if (!isAuthEndpoint && authService.token()) {
            authService.logout();
          }
        } else if (
          isApiRequest &&
          (error.status === 0 || (error.status >= 500 && !error.headers.has('X-Api-Version')))
        ) {
          authService.reportBackendUnreachable?.();
        } else if (isApiRequest && error.headers.has('X-Api-Version')) {
          authService.reportBackendReachable?.();
        }
      }
      return throwError(() => error);
    }),
  );
};
