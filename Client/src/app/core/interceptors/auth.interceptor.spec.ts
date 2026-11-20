import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../services/auth/auth.service';
import { authInterceptor } from './auth.interceptor';

describe('authInterceptor', () => {
  let httpClient: HttpClient;
  let httpMock: HttpTestingController;
  let mockAuthService: {
    token: ReturnType<typeof vi.fn>;
    isInitialized: ReturnType<typeof vi.fn>;
    logout: ReturnType<typeof vi.fn>;
    reportBackendUnreachable: ReturnType<typeof vi.fn>;
    reportBackendReachable: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    mockAuthService = {
      token: vi.fn(),
      isInitialized: vi.fn().mockReturnValue(true),
      logout: vi.fn(),
      reportBackendUnreachable: vi.fn(),
      reportBackendReachable: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: mockAuthService },
      ],
    });

    httpClient = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should attach Bearer token header when token is present', () => {
    mockAuthService.token.mockReturnValue('jwt-token-xyz');

    httpClient.get('/api/boards').subscribe();

    const req = httpMock.expectOne('/api/boards');
    expect(req.request.headers.get('Authorization')).toBe('Bearer jwt-token-xyz');
    req.flush([]);
  });

  it('should not attach Authorization header when token is null', () => {
    mockAuthService.token.mockReturnValue(null);

    httpClient.get('/api/boards').subscribe();

    const req = httpMock.expectOne('/api/boards');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush([]);
  });

  it('should call logout on 401 response for non-auth endpoints', () => {
    mockAuthService.token.mockReturnValue('expired-token');

    httpClient.get('/api/boards').subscribe({
      error: (err) => {
        expect(err).toBeTruthy();
      },
    });

    const req = httpMock.expectOne('/api/boards');
    req.flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(mockAuthService.logout).toHaveBeenCalled();
  });

  it('should not call logout during startup initAuth 401 on /api/auth/me', () => {
    mockAuthService.token.mockReturnValue('expired-token');
    mockAuthService.isInitialized.mockReturnValue(false);

    httpClient.get('/api/auth/me').subscribe({
      error: () => undefined,
    });

    const req = httpMock.expectOne('/api/auth/me');
    req.flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });

    expect(mockAuthService.logout).not.toHaveBeenCalled();
  });

  it('should report backend unreachable on proxy 500 without X-Api-Version and not logout', () => {
    mockAuthService.token.mockReturnValue('valid-token');

    httpClient.get('/api/recipes').subscribe({
      error: () => undefined,
    });

    const req = httpMock.expectOne('/api/recipes');
    req.flush('', { status: 500, statusText: 'Internal Server Error' });

    expect(mockAuthService.logout).not.toHaveBeenCalled();
    expect(mockAuthService.reportBackendUnreachable).toHaveBeenCalled();
  });

  it('should not report backend unreachable when 503 response includes X-Api-Version (e.g. Ollama offline)', () => {
    mockAuthService.token.mockReturnValue('valid-token');

    httpClient.post('/api/ai/substitutions', {}).subscribe({
      error: () => undefined,
    });

    const req = httpMock.expectOne('/api/ai/substitutions');
    req.flush('LLM unavailable', {
      status: 503,
      statusText: 'Service Unavailable',
      headers: { 'X-Api-Version': '1.0.0' },
    });

    expect(mockAuthService.reportBackendUnreachable).not.toHaveBeenCalled();
    expect(mockAuthService.reportBackendReachable).toHaveBeenCalled();
  });

  it('should report backend reachable when API responds with X-Api-Version', () => {
    mockAuthService.token.mockReturnValue('valid-token');

    httpClient.get('/api/recipes').subscribe();

    const req = httpMock.expectOne('/api/recipes');
    expect(req.request.headers.has('ngsw-bypass')).toBe(false);
    req.flush([], { headers: { 'X-Api-Version': '1.0.0' } });

    expect(mockAuthService.reportBackendReachable).toHaveBeenCalled();
  });

  it('should attach ngsw-bypass header to auth, ingestion, and mutation requests', () => {
    mockAuthService.token.mockReturnValue('valid-token');

    httpClient.get('/api/auth/me').subscribe();
    const authReq = httpMock.expectOne('/api/auth/me');
    expect(authReq.request.headers.get('ngsw-bypass')).toBe('true');
    authReq.flush({});

    httpClient.get('/api/ingestion/jobs').subscribe();
    const ingestReq = httpMock.expectOne('/api/ingestion/jobs');
    expect(ingestReq.request.headers.get('ngsw-bypass')).toBe('true');
    ingestReq.flush([]);

    httpClient.post('/api/recipes', { title: 'Chili' }).subscribe();
    const postReq = httpMock.expectOne('/api/recipes');
    expect(postReq.request.headers.get('ngsw-bypass')).toBe('true');
    postReq.flush({});
  });

  it('should not report backend reachable from cached SW GET response when controlled by service worker', () => {
    mockAuthService.token.mockReturnValue('valid-token');
    const originalSw = navigator.serviceWorker;
    Object.defineProperty(navigator, 'serviceWorker', {
      value: { controller: {} },
      configurable: true,
    });

    try {
      httpClient.get('/api/recipes').subscribe();
      const recipeReq = httpMock.expectOne('/api/recipes');
      recipeReq.flush([], { headers: { 'X-Api-Version': '1.0.0' } });
      expect(mockAuthService.reportBackendReachable).not.toHaveBeenCalled();

      httpClient.get('/api/auth/me').subscribe();
      const authReq = httpMock.expectOne('/api/auth/me');
      authReq.flush({}, { headers: { 'X-Api-Version': '1.0.0' } });
      expect(mockAuthService.reportBackendReachable).toHaveBeenCalledTimes(1);
    } finally {
      Object.defineProperty(navigator, 'serviceWorker', {
        value: originalSw,
        configurable: true,
      });
    }
  });
});
