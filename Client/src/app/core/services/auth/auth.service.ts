import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Service, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, Subject, catchError, firstValueFrom, of, tap, timeout } from 'rxjs';
import { APP_STORAGE_PREFIX } from '../../../app.branding';
import { apiUrl } from '../../api/api-url';
import {
  AuthProviderDto,
  AuthResponse,
  CurrentUserDto,
  ExternalAuthRequest,
  LoginRequest,
  RegisterRequest,
} from '../../models/auth.models';

const TOKEN_KEY = `${APP_STORAGE_PREFIX}-token`;
const USER_KEY = `${APP_STORAGE_PREFIX}-user`;
const STARTUP_AUTH_TIMEOUT_MS = 3000;
const RECOVERY_BACKOFF_MS = [2000, 4000, 8000, 15000] as const;

@Service()
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  private readonly tokenState = signal<string | null>(this.readStoredToken());
  private readonly currentUserState = signal<CurrentUserDto | null>(
    this.readInitialUser(this.tokenState()),
  );
  private readonly isInitializedState = signal<boolean>(false);
  private readonly isLoadingState = signal<boolean>(false);
  private readonly isBackendUnreachableState = signal<boolean>(false);
  private readonly isRetryingConnectionState = signal<boolean>(false);

  private bootedWhileUnreachable = false;
  private recoveryTimerId: ReturnType<typeof setTimeout> | null = null;
  private recoveryAttempt = 0;
  private recoveryInFlight = false;

  readonly token = this.tokenState.asReadonly();
  readonly currentUser = this.currentUserState.asReadonly();
  readonly isAuthenticated = computed(() => !!this.currentUserState() || !!this.tokenState());
  readonly isInitialized = this.isInitializedState.asReadonly();
  readonly isLoading = this.isLoadingState.asReadonly();
  readonly isBackendUnreachable = this.isBackendUnreachableState.asReadonly();
  readonly isRetryingConnection = this.isRetryingConnectionState.asReadonly();
  readonly backendRecovered$ = new Subject<boolean>();

  private readonly handleWindowOnline = (): void => {
    if (this.isBackendUnreachableState() && this.tokenState()) {
      void this.retryConnectionNow();
    }
  };

  private readonly handleVisibilityChange = (): void => {
    if (
      typeof document !== 'undefined' &&
      document.visibilityState === 'visible' &&
      this.isBackendUnreachableState() &&
      this.tokenState()
    ) {
      void this.retryConnectionNow();
    }
  };

  constructor() {
    if (typeof window !== 'undefined') {
      window.addEventListener('online', this.handleWindowOnline);
    }
    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', this.handleVisibilityChange);
    }

    this.destroyRef.onDestroy(() => {
      this.stopRecoveryTimer();
      if (typeof window !== 'undefined') {
        window.removeEventListener('online', this.handleWindowOnline);
      }
      if (typeof document !== 'undefined') {
        document.removeEventListener('visibilitychange', this.handleVisibilityChange);
      }
      this.backendRecovered$.complete();
    });
  }

  async initAuth(): Promise<void> {
    const savedToken = this.tokenState();
    if (!savedToken) {
      this.isInitializedState.set(true);
      return;
    }

    try {
      const user = await firstValueFrom(
        this.http.get<CurrentUserDto>(apiUrl('/auth/me')).pipe(timeout(STARTUP_AUTH_TIMEOUT_MS)),
      );
      this.applyLoadedUser(user);
      this.markBackendReachable();
    } catch (error: unknown) {
      if (this.isUnauthorizedError(error)) {
        this.clearSession();
      } else {
        // Keep token and cached user on transient API/proxy outages so restarting the API
        // never permanently wipes the user's local session.
        this.bootedWhileUnreachable = true;
        this.markBackendUnreachable();
      }
    } finally {
      this.isInitializedState.set(true);
    }
  }

  login(credentials: LoginRequest): Observable<AuthResponse> {
    this.isLoadingState.set(true);
    return this.http.post<AuthResponse>(apiUrl('/auth/login'), credentials).pipe(
      tap((response) => {
        this.setSession(response);
        this.isLoadingState.set(false);
      }),
      catchError((error) => {
        this.isLoadingState.set(false);
        throw error;
      }),
    );
  }

  register(data: RegisterRequest): Observable<AuthResponse> {
    this.isLoadingState.set(true);
    return this.http.post<AuthResponse>(apiUrl('/auth/register'), data).pipe(
      tap((response) => {
        this.setSession(response);
        this.isLoadingState.set(false);
      }),
      catchError((error) => {
        this.isLoadingState.set(false);
        throw error;
      }),
    );
  }

  externalCallback(request: ExternalAuthRequest): Observable<AuthResponse> {
    this.isLoadingState.set(true);
    return this.http.post<AuthResponse>(apiUrl('/auth/external-callback'), request).pipe(
      tap((response) => {
        this.setSession(response);
        this.isLoadingState.set(false);
      }),
      catchError((error) => {
        this.isLoadingState.set(false);
        throw error;
      }),
    );
  }

  loadCurrentUser(): Observable<CurrentUserDto | null> {
    return this.http.get<CurrentUserDto>(apiUrl('/auth/me')).pipe(
      tap((user) => {
        this.applyLoadedUser(user);
        this.markBackendReachable();
      }),
      catchError(() => {
        return of(null);
      }),
    );
  }

  getProviders(): Observable<AuthProviderDto[]> {
    return this.http
      .get<AuthProviderDto[]>(apiUrl('/auth/providers'))
      .pipe(catchError(() => of([])));
  }

  reportBackendUnreachable(): void {
    if (!this.tokenState()) {
      return;
    }
    this.markBackendUnreachable();
  }

  reportBackendReachable(): void {
    if (!this.isBackendUnreachableState()) {
      return;
    }

    const shouldRefreshUser = this.bootedWhileUnreachable;
    this.markBackendReachable();

    if (shouldRefreshUser && this.tokenState()) {
      this.loadCurrentUser().subscribe();
    }
  }

  async retryConnectionNow(): Promise<boolean> {
    if (!this.tokenState()) {
      this.stopRecoveryTimer();
      this.isBackendUnreachableState.set(false);
      return false;
    }

    if (this.recoveryInFlight) {
      return false;
    }

    this.stopRecoveryTimer();
    this.recoveryInFlight = true;
    this.isRetryingConnectionState.set(true);

    try {
      const user = await firstValueFrom(
        this.http.get<CurrentUserDto>(apiUrl('/auth/me')).pipe(timeout(STARTUP_AUTH_TIMEOUT_MS)),
      );
      this.applyLoadedUser(user);
      this.markBackendReachable();
      return true;
    } catch (error: unknown) {
      if (this.isUnauthorizedError(error)) {
        if (this.tokenState()) {
          this.logout();
        } else {
          this.stopRecoveryTimer();
          this.isBackendUnreachableState.set(false);
        }
        return false;
      }

      if (this.tokenState()) {
        this.isBackendUnreachableState.set(true);
        this.scheduleNextRecoveryAttempt();
      }
      return false;
    } finally {
      this.recoveryInFlight = false;
      this.isRetryingConnectionState.set(false);
    }
  }

  logout(): void {
    const currentToken = this.tokenState();
    this.clearSession();

    if (currentToken) {
      this.http.post(apiUrl('/auth/logout'), {}).subscribe({
        error: () => undefined,
      });
    }

    this.router.navigate(['/login']);
  }

  setSession(auth: AuthResponse): void {
    const existing = this.currentUserState();
    const fallbackThemeSettings = existing?.id === auth.id ? existing.themeSettings : null;
    const nextUser: CurrentUserDto = {
      id: auth.id,
      email: auth.email,
      username: auth.username,
      themeSettings:
        auth.themeSettings !== undefined ? auth.themeSettings : (fallbackThemeSettings ?? null),
    };

    this.tokenState.set(auth.token);
    this.currentUserState.set(nextUser);
    this.markBackendReachable();
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(TOKEN_KEY, auth.token);
      this.writeStoredUser(nextUser);
    }
  }

  updateCurrentUserThemeSettings(themeSettings: string): void {
    this.currentUserState.update((prev) => {
      if (!prev) {
        return prev;
      }
      const nextUser = { ...prev, themeSettings };
      this.writeStoredUser(nextUser);
      return nextUser;
    });
  }

  clearSession(): void {
    this.stopRecoveryTimer();
    this.bootedWhileUnreachable = false;
    this.recoveryAttempt = 0;
    this.isBackendUnreachableState.set(false);
    this.isRetryingConnectionState.set(false);
    this.tokenState.set(null);
    this.currentUserState.set(null);
    if (typeof localStorage !== 'undefined') {
      localStorage.removeItem(TOKEN_KEY);
      localStorage.removeItem(USER_KEY);
    }
  }

  private applyLoadedUser(user: CurrentUserDto): void {
    this.currentUserState.set(user);
    this.writeStoredUser(user);
  }

  private markBackendUnreachable(): void {
    const wasUnreachable = this.isBackendUnreachableState();
    this.isBackendUnreachableState.set(true);
    if (!wasUnreachable) {
      this.recoveryAttempt = 0;
    }
    if (!this.recoveryInFlight && this.recoveryTimerId === null) {
      this.scheduleNextRecoveryAttempt();
    }
  }

  private markBackendReachable(): void {
    const wasUnreachable = this.isBackendUnreachableState();
    const wasBootedOffline = this.bootedWhileUnreachable;
    this.stopRecoveryTimer();
    this.recoveryAttempt = 0;
    this.bootedWhileUnreachable = false;
    this.isBackendUnreachableState.set(false);

    if (wasUnreachable) {
      this.backendRecovered$.next(wasBootedOffline);
    }
  }

  private scheduleNextRecoveryAttempt(): void {
    if (this.recoveryTimerId !== null || !this.tokenState()) {
      return;
    }

    const delayMs =
      RECOVERY_BACKOFF_MS[Math.min(this.recoveryAttempt, RECOVERY_BACKOFF_MS.length - 1)];
    this.recoveryAttempt++;

    this.recoveryTimerId = setTimeout(() => {
      this.recoveryTimerId = null;
      void this.retryConnectionNow();
    }, delayMs);
  }

  private stopRecoveryTimer(): void {
    if (this.recoveryTimerId !== null) {
      clearTimeout(this.recoveryTimerId);
      this.recoveryTimerId = null;
    }
  }

  private isUnauthorizedError(error: unknown): boolean {
    return error instanceof HttpErrorResponse && error.status === 401;
  }

  private readStoredToken(): string | null {
    if (typeof localStorage === 'undefined') {
      return null;
    }
    return localStorage.getItem(TOKEN_KEY);
  }

  private readInitialUser(token: string | null): CurrentUserDto | null {
    if (!token) {
      if (typeof localStorage !== 'undefined') {
        localStorage.removeItem(USER_KEY);
      }
      return null;
    }

    const storedUser = this.readStoredUser();
    if (storedUser) {
      return storedUser;
    }

    return this.decodeUserFromJwt(token);
  }

  private readStoredUser(): CurrentUserDto | null {
    if (typeof localStorage === 'undefined') {
      return null;
    }

    const raw = localStorage.getItem(USER_KEY);
    if (!raw) {
      return null;
    }

    try {
      const parsed = JSON.parse(raw) as Partial<CurrentUserDto>;
      if (
        typeof parsed.id === 'string' &&
        typeof parsed.email === 'string' &&
        typeof parsed.username === 'string'
      ) {
        return {
          id: parsed.id,
          email: parsed.email,
          username: parsed.username,
          themeSettings: parsed.themeSettings ?? null,
        };
      }
    } catch {
      // Ignore malformed cached user JSON
    }

    return null;
  }

  private writeStoredUser(user: CurrentUserDto): void {
    if (typeof localStorage === 'undefined') {
      return;
    }
    try {
      localStorage.setItem(USER_KEY, JSON.stringify(user));
    } catch {
      // Ignore storage quota errors
    }
  }

  private decodeUserFromJwt(token: string): CurrentUserDto | null {
    const parts = token.split('.');
    if (parts.length !== 3 || typeof atob !== 'function') {
      return null;
    }

    try {
      const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
      const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
      const binary = atob(padded);
      const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
      const payloadJson =
        typeof TextDecoder !== 'undefined' ? new TextDecoder().decode(bytes) : binary;
      const claims = JSON.parse(payloadJson) as Record<string, unknown>;

      const id =
        this.readStringClaim(claims, 'sub') ??
        this.readStringClaim(
          claims,
          'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier',
        );
      const email =
        this.readStringClaim(claims, 'email') ??
        this.readStringClaim(
          claims,
          'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress',
        );
      const username =
        this.readStringClaim(claims, 'unique_name') ??
        this.readStringClaim(
          claims,
          'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name',
        ) ??
        email?.split('@')[0];

      if (id && email && username) {
        return {
          id,
          email,
          username,
          themeSettings: null,
        };
      }
    } catch {
      // Ignore non-JWT or malformed tokens
    }

    return null;
  }

  private readStringClaim(claims: Record<string, unknown>, key: string): string | null {
    const value = claims[key];
    return typeof value === 'string' && value.trim().length > 0 ? value : null;
  }
}
