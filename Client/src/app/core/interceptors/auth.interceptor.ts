import { HttpErrorResponse, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, tap, throwError } from 'rxjs';
import { AuthService } from '../services/auth/auth.service';

const SW_BYPASS_PREFIXES = [
  '/api/auth/',
  '/api/account',
  '/api/hubs/',
  '/api/ingestion/',
  '/api/llm-settings',
  '/api/ai-helpers',
  '/api/ai/',
  '/api/media',
  '/api/version',
  '/api/mcp',
];

export function shouldBypassServiceWorker(method: string, url: string): boolean {
  const upperMethod = method.toUpperCase();
  if (upperMethod !== 'GET' && upperMethod !== 'HEAD') {
    return true;
  }

  return SW_BYPASS_PREFIXES.some((prefix) => url.includes(prefix));
}

function isServiceWorkerControlled(): boolean {
  return (
    typeof navigator !== 'undefined' &&
    'serviceWorker' in navigator &&
    Boolean(navigator.serviceWorker?.controller)
  );
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const authService = inject(AuthService);
  const token = authService.token();
  const isApiRequest = req.url.includes('/api/');
  const bypassSw = isApiRequest && shouldBypassServiceWorker(req.method, req.url);

  const setHeaders: Record<string, string> = {};
  if (token) {
    setHeaders['Authorization'] = `Bearer ${token}`;
  }
  if (bypassSw) {
    setHeaders['ngsw-bypass'] = 'true';
  }

  const authReq = Object.keys(setHeaders).length > 0 ? req.clone({ setHeaders }) : req;

  return next(authReq).pipe(
    tap((event) => {
      const isOnline = typeof navigator === 'undefined' || navigator.onLine !== false;
      const canTrustLiveReachability = isOnline && (bypassSw || !isServiceWorkerControlled());
      if (
        isApiRequest &&
        canTrustLiveReachability &&
        event instanceof HttpResponse &&
        event.headers.has('X-Api-Version')
      ) {
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
          const isOnline = typeof navigator === 'undefined' || navigator.onLine !== false;
          if (isOnline && (bypassSw || !isServiceWorkerControlled())) {
            authService.reportBackendReachable?.();
          }
        }
      }
      return throwError(() => error);
    }),
  );
};
