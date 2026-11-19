import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../core/services/auth/auth.service';
import { ThemeService } from '../../core/services/theme/theme.service';
import { LoginComponent } from './login.component';

describe('LoginComponent', () => {
  let component: LoginComponent;
  let fixture: ComponentFixture<LoginComponent>;
  let mockAuthService: {
    isLoading: ReturnType<typeof vi.fn>;
    login: ReturnType<typeof vi.fn>;
    register: ReturnType<typeof vi.fn>;
    externalCallback: ReturnType<typeof vi.fn>;
    getProviders: ReturnType<typeof vi.fn>;
  };
  let mockRouter: {
    navigateByUrl: ReturnType<typeof vi.fn>;
  };
  let mockSnackBar: {
    open: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    mockAuthService = {
      isLoading: vi.fn().mockReturnValue(false),
      login: vi.fn().mockReturnValue(
        of({
          token: 'token123',
          id: 'u1',
          email: 'test@example.com',
          username: 'testuser',
          expiresAt: '2026-10-11T00:00:00Z',
        }),
      ),
      register: vi.fn().mockReturnValue(
        of({
          token: 'token123',
          id: 'u1',
          email: 'test@example.com',
          username: 'testuser',
          expiresAt: '2026-10-11T00:00:00Z',
        }),
      ),
      externalCallback: vi.fn().mockReturnValue(
        of({
          token: 'token123',
          id: 'u1',
          email: 'test@example.com',
          username: 'testuser',
          expiresAt: '2026-10-11T00:00:00Z',
        }),
      ),
      getProviders: vi.fn().mockReturnValue(of([])),
    };

    mockRouter = {
      navigateByUrl: vi.fn(),
    };

    mockSnackBar = {
      open: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        { provide: AuthService, useValue: mockAuthService },
        { provide: Router, useValue: mockRouter },
        { provide: MatSnackBar, useValue: mockSnackBar },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParams: { returnUrl: '/dashboard' },
            },
          },
        },
        ThemeService,
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LoginComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create the component', () => {
    expect(component).toBeTruthy();
  });

  it('should have invalid login form when empty', () => {
    expect(component['loginForm'].valid).toBe(false);
  });

  it('should submit login when valid', () => {
    component['loginForm'].setValue({
      email: 'user@example.com',
      password: 'password123',
    });
    expect(component['loginForm'].valid).toBe(true);

    component.onLoginSubmit();
    expect(mockAuthService.login).toHaveBeenCalledWith({
      email: 'user@example.com',
      password: 'password123',
    });
    expect(mockRouter.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });

  it('should display error message on failed login', () => {
    mockAuthService.login.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 401,
            error: { detail: 'Invalid credentials provided' },
          }),
      ),
    );

    component['loginForm'].setValue({
      email: 'wrong@example.com',
      password: 'wrongpassword',
    });
    component.onLoginSubmit();

    expect(component['errorMessage']()).toBe('Invalid credentials provided');
  });

  it('should validate matching passwords in register form', () => {
    component['registerForm'].patchValue({
      username: 'tester',
      email: 'test@example.com',
      password: 'password123',
      confirmPassword: 'differentpassword',
    });
    expect(component['registerForm'].hasError('passwordMismatch')).toBe(true);

    component['registerForm'].patchValue({
      confirmPassword: 'password123',
    });
    expect(component['registerForm'].hasError('passwordMismatch')).toBe(false);
    expect(component['registerForm'].valid).toBe(true);
  });

  it('should submit register form and navigate', () => {
    component['registerForm'].setValue({
      username: 'newuser',
      email: 'new@example.com',
      password: 'secretpassword',
      confirmPassword: 'secretpassword',
    });

    component.onRegisterSubmit();
    expect(mockAuthService.register).toHaveBeenCalledWith({
      username: 'newuser',
      email: 'new@example.com',
      password: 'secretpassword',
    });
    expect(mockRouter.navigateByUrl).toHaveBeenCalledWith('/dashboard');
  });

  it('should display error if provider has missing oauth configuration', () => {
    component.onExternalLogin({
      id: 'google',
      name: 'Google',
      isExternal: true,
    });

    expect(component['errorMessage']()).toContain(
      'OAuth authorization endpoint or client ID is not configured',
    );
  });

  it('should construct oauth authorization url and redirect on external login', () => {
    const originalLocation = window.location;
    try {
      delete (window as unknown as { location?: unknown }).location;
      (window as unknown as { location: unknown }).location = {
        href: '',
        origin: 'http://localhost:4200',
      };

      component.onExternalLogin({
        id: 'google',
        name: 'Google',
        isExternal: true,
        oidcConfig: {
          enabled: true,
          providerName: 'Google',
          authority: 'https://accounts.google.com',
          clientId: 'my-google-client-id',
          scope: 'openid profile email',
        },
      });

      expect(window.location.href).toContain('https://accounts.google.com/o/oauth2/v2/auth');
      expect(window.location.href).toContain('client_id=my-google-client-id');
      expect(window.location.href).toContain('redirect_uri=http%3A%2F%2Flocalhost%3A4200%2Flogin');
      expect(sessionStorage.getItem('oauth_return_url')).toBe('/dashboard');
    } finally {
      (window as unknown as { location: unknown }).location = originalLocation;
    }
  });

  it('should handle OAuth callback error param', () => {
    (
      component as unknown as { route: { snapshot: { queryParams: Record<string, string> } } }
    ).route.snapshot.queryParams = {
      error: 'access_denied',
      error_description: 'User canceled authorization',
    };

    component['checkExternalAuthCallback']();

    expect(component['errorMessage']()).toBe('User canceled authorization');
  });

  it('should handle OAuth callback code and navigate to saved return url', () => {
    sessionStorage.setItem('oauth_return_url', '/boards/123');
    mockAuthService.externalCallback.mockReturnValue(
      of({
        token: 'oauth-token',
        id: 'u-google',
        email: 'user@google.com',
        username: 'googler',
        expiresAt: '2026-10-11T00:00:00Z',
      }),
    );
    (
      component as unknown as { route: { snapshot: { queryParams: Record<string, string> } } }
    ).route.snapshot.queryParams = {
      code: 'google-auth-code',
    };

    component['checkExternalAuthCallback']();

    expect(mockAuthService.externalCallback).toHaveBeenCalledWith({
      code: 'google-auth-code',
      idToken: null,
      accessToken: null,
      redirectUri: `${window.location.origin}/login`,
    });
    expect(mockSnackBar.open).toHaveBeenCalledWith('Signed in successfully', 'Close', {
      duration: 3000,
    });
    expect(mockRouter.navigateByUrl).toHaveBeenCalledWith('/boards/123');
    expect(sessionStorage.getItem('oauth_return_url')).toBeNull();
  });

  it('should display error when externalCallback fails', () => {
    mockAuthService.externalCallback.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { detail: 'Invalid authorization code' },
          }),
      ),
    );
    (
      component as unknown as { route: { snapshot: { queryParams: Record<string, string> } } }
    ).route.snapshot.queryParams = {
      code: 'invalid-code',
    };

    component['checkExternalAuthCallback']();

    expect(component['errorMessage']()).toBe('Invalid authorization code');
    expect(component['isProcessingOAuth']()).toBe(false);
  });

  it('should return SVG icon name for Google and Microsoft', () => {
    expect(component['getProviderSvg']({ id: 'google', name: 'Google', isExternal: true })).toBe(
      'google',
    );
    expect(
      component['getProviderSvg']({ id: 'ms', name: 'Microsoft Entra', isExternal: true }),
    ).toBe('microsoft');
    expect(
      component['getProviderSvg']({ id: 'custom', name: 'Custom OIDC', isExternal: true }),
    ).toBeNull();
  });

  it('should surface confirm password mismatch through custom error state matcher', () => {
    component['registerForm'].patchValue({
      username: 'tester',
      email: 'test@example.com',
      password: 'password123',
      confirmPassword: 'differentpassword',
    });
    const confirmControl = component['registerForm'].controls.confirmPassword;
    confirmControl.markAsTouched();

    expect(component['confirmPasswordErrorMatcher'].isErrorState(confirmControl, null)).toBe(true);

    confirmControl.setValue('password123');
    expect(component['confirmPasswordErrorMatcher'].isErrorState(confirmControl, null)).toBe(false);
  });

  it('should update card subtitle when switching between Sign In and Create Account tabs', () => {
    const signInSubtitle = component['cardSubtitle']();
    component.onTabChange(1);
    const registerSubtitle = component['cardSubtitle']();

    expect(registerSubtitle).not.toBe(signInSubtitle);
    expect(registerSubtitle).toContain('Create an account');
  });

  it('should mark controls touched instead of submitting when login form is invalid', () => {
    component.onLoginSubmit();

    expect(mockAuthService.login).not.toHaveBeenCalled();
    expect(component['loginForm'].controls.email.touched).toBe(true);
    expect(component['loginForm'].controls.password.touched).toBe(true);
  });
});
