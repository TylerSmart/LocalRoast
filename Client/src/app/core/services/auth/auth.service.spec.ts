import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { APP_STORAGE_PREFIX } from '../../../app.branding';
import { AuthService } from './auth.service';

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;
  let mockRouter: { navigate: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    localStorage.clear();
    mockRouter = { navigate: vi.fn() };

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: mockRouter },
      ],
    });

    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('should initialize unauthenticated when no token in storage', () => {
    expect(service.token()).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
  });

  it('should login successfully and set session', () => {
    const mockResponse = {
      token: 'jwt-token-123',
      id: 'user-guid-1',
      email: 'user@example.com',
      username: 'sampleuser',
      expiresAt: '2026-10-11T00:00:00Z',
    };

    service.login({ email: 'user@example.com', password: 'password123' }).subscribe((res) => {
      expect(res).toEqual(mockResponse);
    });

    const req = httpMock.expectOne('/api/auth/login');
    expect(req.request.method).toBe('POST');
    req.flush(mockResponse);

    expect(service.token()).toBe('jwt-token-123');
    expect(service.currentUser()?.email).toBe('user@example.com');
    expect(service.isAuthenticated()).toBe(true);
    expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-token`)).toBe('jwt-token-123');
  });

  it('should register successfully and set session', () => {
    const mockResponse = {
      token: 'jwt-token-456',
      id: 'user-guid-2',
      email: 'new@example.com',
      username: 'newuser',
      expiresAt: '2026-10-11T00:00:00Z',
    };

    service
      .register({
        email: 'new@example.com',
        username: 'newuser',
        password: 'password123',
      })
      .subscribe((res) => {
        expect(res).toEqual(mockResponse);
      });

    const req = httpMock.expectOne('/api/auth/register');
    expect(req.request.method).toBe('POST');
    req.flush(mockResponse);

    expect(service.token()).toBe('jwt-token-456');
    expect(service.currentUser()?.username).toBe('newuser');
    expect(service.isAuthenticated()).toBe(true);
  });

  it('should handle externalCallback successfully and set session', () => {
    const mockResponse = {
      token: 'jwt-oauth-token',
      id: 'oauth-user-id',
      email: 'oauth@example.com',
      username: 'googleuser',
      expiresAt: '2026-10-11T00:00:00Z',
    };

    service
      .externalCallback({
        code: 'oauth-code-123',
        idToken: null,
        accessToken: null,
        redirectUri: 'http://localhost:4200/login',
      })
      .subscribe((res) => {
        expect(res).toEqual(mockResponse);
      });

    const req = httpMock.expectOne('/api/auth/external-callback');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      code: 'oauth-code-123',
      idToken: null,
      accessToken: null,
      redirectUri: 'http://localhost:4200/login',
    });
    req.flush(mockResponse);

    expect(service.token()).toBe('jwt-oauth-token');
    expect(service.currentUser()?.email).toBe('oauth@example.com');
    expect(service.isAuthenticated()).toBe(true);
  });

  it('should load current user', () => {
    const mockUser = {
      id: 'user-guid-1',
      email: 'user@example.com',
      username: 'sampleuser',
    };

    service.loadCurrentUser().subscribe((user) => {
      expect(user).toEqual(mockUser);
    });

    const req = httpMock.expectOne('/api/auth/me');
    expect(req.request.method).toBe('GET');
    req.flush(mockUser);

    expect(service.currentUser()).toEqual(mockUser);
    expect(service.isAuthenticated()).toBe(true);
  });

  it('should logout and clear session and redirect', () => {
    service.setSession({
      token: 'test-token',
      id: 'u1',
      email: 'test@example.com',
      username: 'tester',
      expiresAt: '2026-10-11T00:00:00Z',
    });

    expect(service.isAuthenticated()).toBe(true);

    service.logout();

    const req = httpMock.expectOne('/api/auth/logout');
    expect(req.request.method).toBe('POST');
    req.flush({});

    expect(service.token()).toBeNull();
    expect(service.currentUser()).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-token`)).toBeNull();
    expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-user`)).toBeNull();
    expect(mockRouter.navigate).toHaveBeenCalledWith(['/login']);
  });

  it('should persist cached user on setSession and hydrate from storage on startup', () => {
    service.setSession({
      token: 'persisted-token',
      id: 'user-1',
      email: 'chef@example.com',
      username: 'chef',
      expiresAt: null,
      themeSettings: '{"mode":"dark"}',
    });

    const cachedRaw = localStorage.getItem(`${APP_STORAGE_PREFIX}-user`);
    expect(cachedRaw).toBeTruthy();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: mockRouter },
      ],
    });
    const rehydrated = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);

    expect(rehydrated.token()).toBe('persisted-token');
    expect(rehydrated.currentUser()).toEqual({
      id: 'user-1',
      email: 'chef@example.com',
      username: 'chef',
      themeSettings: '{"mode":"dark"}',
    });
    expect(rehydrated.isAuthenticated()).toBe(true);
  });

  it('should preserve token and cached user when initAuth encounters a transient proxy error and recover once API returns', async () => {
    vi.useFakeTimers();
    try {
      localStorage.setItem(`${APP_STORAGE_PREFIX}-token`, 'persisted-token');
      localStorage.setItem(
        `${APP_STORAGE_PREFIX}-user`,
        JSON.stringify({
          id: 'user-1',
          email: 'chef@example.com',
          username: 'chef',
          themeSettings: null,
        }),
      );

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [
          AuthService,
          provideHttpClient(),
          provideHttpClientTesting(),
          { provide: Router, useValue: mockRouter },
        ],
      });
      service = TestBed.inject(AuthService);
      httpMock = TestBed.inject(HttpTestingController);

      const recoveredEvents: boolean[] = [];
      service.backendRecovered$.subscribe((bootedOffline) => recoveredEvents.push(bootedOffline));

      const initPromise = service.initAuth();
      const firstReq = httpMock.expectOne('/api/auth/me');
      firstReq.flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
      await initPromise;

      expect(service.isInitialized()).toBe(true);
      expect(service.token()).toBe('persisted-token');
      expect(service.currentUser()?.username).toBe('chef');
      expect(service.isAuthenticated()).toBe(true);
      expect(service.isBackendUnreachable()).toBe(true);
      expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-token`)).toBe('persisted-token');

      // Advance backoff timer to trigger background recovery probe
      vi.advanceTimersByTime(2000);
      const retryReq = httpMock.expectOne('/api/auth/me');
      retryReq.flush({
        id: 'user-1',
        email: 'chef@example.com',
        username: 'chef-updated',
        themeSettings: null,
      });
      await Promise.resolve();

      expect(service.isBackendUnreachable()).toBe(false);
      expect(service.currentUser()?.username).toBe('chef-updated');
      expect(recoveredEvents).toEqual([true]);
    } finally {
      vi.useRealTimers();
    }
  });

  it('should clear session when initAuth receives 401 Unauthorized', async () => {
    localStorage.setItem(`${APP_STORAGE_PREFIX}-token`, 'invalid-token');
    localStorage.setItem(
      `${APP_STORAGE_PREFIX}-user`,
      JSON.stringify({
        id: 'user-1',
        email: 'chef@example.com',
        username: 'chef',
        themeSettings: null,
      }),
    );

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: mockRouter },
      ],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);

    const initPromise = service.initAuth();
    const req = httpMock.expectOne('/api/auth/me');
    req.flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });
    await initPromise;

    expect(service.isInitialized()).toBe(true);
    expect(service.token()).toBeNull();
    expect(service.currentUser()).toBeNull();
    expect(service.isBackendUnreachable()).toBe(false);
    expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-token`)).toBeNull();
    expect(localStorage.getItem(`${APP_STORAGE_PREFIX}-user`)).toBeNull();
  });

  it('should decode fallback user info from a JWT when cached user is absent', () => {
    const payload = btoa(
      JSON.stringify({
        sub: 'jwt-user-id',
        email: 'jwt@example.com',
        unique_name: 'jwtchef',
      }),
    )
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=+$/, '');
    localStorage.setItem(`${APP_STORAGE_PREFIX}-token`, `header.${payload}.sig`);

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        AuthService,
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: mockRouter },
      ],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);

    expect(service.currentUser()).toEqual({
      id: 'jwt-user-id',
      email: 'jwt@example.com',
      username: 'jwtchef',
      themeSettings: null,
    });
  });
});
