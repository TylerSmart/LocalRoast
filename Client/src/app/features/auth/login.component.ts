import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormControl,
  FormGroup,
  FormGroupDirective,
  NgForm,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatDialogModule } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule, MatIconRegistry } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DomSanitizer } from '@angular/platform-browser';
import { ActivatedRoute, Router } from '@angular/router';
import { APP_BRAND_MARK_SRC, APP_LOGIN_SUBTITLE, APP_NAME } from '../../app.branding';
import { AuthProviderDto } from '../../core/models/auth.models';
import { AuthService } from '../../core/services/auth/auth.service';
import { ThemeMode, ThemeService } from '../../core/services/theme/theme.service';

const GOOGLE_ICON_SVG =
  '<svg version="1.1" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48"><path fill="#EA4335" d="M24 9.5c3.54 0 6.71 1.22 9.21 3.6l6.85-6.85C35.9 2.38 30.47 0 24 0 14.62 0 6.51 5.38 2.56 13.22l7.98 6.19C12.43 13.72 17.74 9.5 24 9.5z"></path><path fill="#4285F4" d="M46.98 24.55c0-1.57-.15-3.09-.38-4.55H24v9.02h12.94c-.58 2.96-2.26 5.48-4.78 7.18l7.73 6c4.51-4.18 7.09-10.36 7.09-17.65z"></path><path fill="#FBBC05" d="M10.53 28.59c-.48-1.45-.76-2.99-.76-4.59s.27-3.14.76-4.59l-7.98-6.19C.92 16.46 0 20.12 0 24c0 3.88.92 7.54 2.56 10.78l7.97-6.19z"></path><path fill="#34A853" d="M24 48c6.48 0 11.93-2.13 15.89-5.81l-7.73-6c-2.15 1.45-4.92 2.3-8.16 2.3-6.26 0-11.57-4.22-13.47-9.91l-7.98 6.19C6.51 42.62 14.62 48 24 48z"></path><path fill="none" d="M0 0h48v48H0z"></path></svg>';

const MICROSOFT_ICON_SVG =
  '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 21 21"><rect x="1" y="1" width="9" height="9" fill="#f25022"/><rect x="1" y="11" width="9" height="9" fill="#00a4ef"/><rect x="11" y="1" width="9" height="9" fill="#7fba00"/><rect x="11" y="11" width="9" height="9" fill="#ffb900"/></svg>';

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

const passwordMatchValidator: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const password = control.get('password')?.value;
  const confirmPassword = control.get('confirmPassword')?.value;
  return password && confirmPassword && password !== confirmPassword
    ? { passwordMismatch: true }
    : null;
};

class ConfirmPasswordErrorStateMatcher implements ErrorStateMatcher {
  isErrorState(control: FormControl | null, form: FormGroupDirective | NgForm | null): boolean {
    if (!control) {
      return false;
    }
    const isInteracted = Boolean(control.touched || control.dirty || form?.submitted);
    const hasMismatch = Boolean(control.parent?.hasError('passwordMismatch'));
    return isInteracted && (control.invalid || hasMismatch);
  }
}

@Component({
  imports: [
    ReactiveFormsModule,
    MatCardModule,
    MatDialogModule,
    MatDividerModule,
    MatTabsModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  selector: 'app-login',
  styleUrl: './login.component.scss',
  templateUrl: './login.component.html',
})
export class LoginComponent implements OnInit {
  protected readonly appName = APP_NAME;
  protected readonly brandMarkSrc = APP_BRAND_MARK_SRC;
  protected readonly loginSubtitle = APP_LOGIN_SUBTITLE;
  protected readonly confirmPasswordErrorMatcher = new ConfirmPasswordErrorStateMatcher();
  protected readonly authService = inject(AuthService);
  protected readonly themeService = inject(ThemeService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly selectedTabIndex = signal<number>(0);
  protected readonly cardSubtitle = computed(() =>
    this.selectedTabIndex() === 0
      ? this.loginSubtitle
      : 'Create an account to join your household cookbook, meal plans, and shopping lists',
  );
  protected readonly hideLoginPassword = signal<boolean>(true);
  protected readonly hideRegisterPassword = signal<boolean>(true);
  protected readonly hideConfirmPassword = signal<boolean>(true);
  protected readonly errorMessage = signal<string | null>(null);
  protected readonly providers = signal<AuthProviderDto[]>([]);
  protected readonly isProcessingOAuth = signal<boolean>(false);

  protected readonly externalProviders = computed(() =>
    this.providers().filter((p) => p.isExternal),
  );

  protected readonly themeIcon = computed(() => THEME_ICONS[this.themeService.mode()]);
  protected readonly themeLabel = computed(() => THEME_LABELS[this.themeService.mode()]);

  protected readonly loginForm = new FormGroup({
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.email],
    }),
    password: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required],
    }),
  });

  protected readonly registerForm = new FormGroup(
    {
      username: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.minLength(3)],
      }),
      email: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.email],
      }),
      password: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required, Validators.minLength(6)],
      }),
      confirmPassword: new FormControl('', {
        nonNullable: true,
        validators: [Validators.required],
      }),
    },
    { validators: [passwordMatchValidator] },
  );

  constructor() {
    const iconRegistry = inject(MatIconRegistry);
    const sanitizer = inject(DomSanitizer);

    iconRegistry.addSvgIconLiteral('google', sanitizer.bypassSecurityTrustHtml(GOOGLE_ICON_SVG));
    iconRegistry.addSvgIconLiteral(
      'microsoft',
      sanitizer.bypassSecurityTrustHtml(MICROSOFT_ICON_SVG),
    );
  }

  ngOnInit(): void {
    this.checkExternalAuthCallback();

    this.authService.getProviders().subscribe({
      next: (list) => this.providers.set(list),
      error: () => undefined,
    });
  }

  onTabChange(index: number): void {
    this.selectedTabIndex.set(index);
    this.errorMessage.set(null);
  }

  onLoginSubmit(): void {
    if (this.loginForm.invalid) {
      this.loginForm.markAllAsTouched();
      return;
    }

    this.errorMessage.set(null);
    const formVal = this.loginForm.getRawValue();

    this.authService
      .login({
        email: formVal.email.trim(),
        password: formVal.password,
      })
      .subscribe({
        next: () => {
          this.snackBar.open('Signed in successfully', 'Close', {
            duration: 3000,
          });
          this.navigateToTarget();
        },
        error: (err: unknown) => {
          this.errorMessage.set(this.extractErrorMessage(err, 'Invalid email or password.'));
        },
      });
  }

  onRegisterSubmit(): void {
    if (this.registerForm.invalid) {
      this.registerForm.markAllAsTouched();
      return;
    }

    this.errorMessage.set(null);
    const formVal = this.registerForm.getRawValue();

    this.authService
      .register({
        email: formVal.email.trim(),
        username: formVal.username.trim(),
        password: formVal.password,
      })
      .subscribe({
        next: () => {
          this.snackBar.open('Account created successfully!', 'Close', {
            duration: 3000,
          });
          this.navigateToTarget();
        },
        error: (err: unknown) => {
          this.errorMessage.set(
            this.extractErrorMessage(err, 'Could not create account. Please check your details.'),
          );
        },
      });
  }

  onExternalLogin(provider: AuthProviderDto): void {
    this.errorMessage.set(null);
    const authUrl = this.buildAuthorizationUrl(provider);

    if (!authUrl) {
      this.errorMessage.set(
        `Unable to sign in with ${provider.name}: OAuth authorization endpoint or client ID is not configured.`,
      );
      return;
    }

    const returnUrl = this.route.snapshot.queryParams['returnUrl'] || '/dashboard';
    if (typeof sessionStorage !== 'undefined') {
      sessionStorage.setItem('oauth_return_url', returnUrl);
    }

    window.location.href = authUrl;
  }

  protected getProviderIcon(provider: AuthProviderDto): string {
    const name = provider.name.toLowerCase();
    if (name.includes('google')) {
      return 'account_circle';
    }
    if (name.includes('microsoft')) {
      return 'window';
    }
    if (name.includes('github')) {
      return 'code';
    }
    return 'login';
  }

  protected getProviderSvg(provider: AuthProviderDto): string | null {
    const name = provider.name.toLowerCase();
    if (name.includes('google')) {
      return 'google';
    }
    if (name.includes('microsoft')) {
      return 'microsoft';
    }
    return null;
  }

  private buildAuthorizationUrl(provider: AuthProviderDto): string | null {
    if (provider.authorizationUrl && provider.authorizationUrl.includes('?')) {
      return provider.authorizationUrl;
    }

    const config = provider.oidcConfig;
    let endpoint = config?.authorizationEndpoint || provider.authorizationUrl;

    if (!endpoint && config?.authority) {
      const authority = config.authority.replace(/\/$/, '');
      if (authority.toLowerCase().includes('accounts.google.com')) {
        endpoint = 'https://accounts.google.com/o/oauth2/v2/auth';
      } else {
        endpoint = `${authority}/authorize`;
      }
    }

    // Google authority fallback when authority was set to accounts.google.com without custom endpoint
    if (endpoint && endpoint.includes('accounts.google.com') && endpoint.endsWith('/authorize')) {
      endpoint = 'https://accounts.google.com/o/oauth2/v2/auth';
    }

    const clientId = config?.clientId;
    if (!endpoint || !clientId) {
      return null;
    }

    try {
      const redirectUri =
        typeof window !== 'undefined'
          ? `${window.location.origin}/login`
          : 'http://localhost:4200/login';
      const url = new URL(endpoint);
      url.searchParams.set('client_id', clientId);
      url.searchParams.set('redirect_uri', redirectUri);
      url.searchParams.set('response_type', 'code');
      url.searchParams.set('scope', config?.scope || 'openid profile email');
      url.searchParams.set('state', 'oidc');
      url.searchParams.set('prompt', 'select_account');

      return url.toString();
    } catch {
      return null;
    }
  }

  private checkExternalAuthCallback(): void {
    const queryParams = this.route.snapshot.queryParams;
    const code = queryParams['code'];
    const error = queryParams['error'];
    const errorDescription = queryParams['error_description'];

    const hashParams =
      typeof window !== 'undefined' && window.location.hash
        ? new URLSearchParams(window.location.hash.replace(/^#/, ''))
        : null;
    const idToken = queryParams['id_token'] || hashParams?.get('id_token') || null;
    const accessToken = queryParams['access_token'] || hashParams?.get('access_token') || null;

    if (error) {
      this.errorMessage.set(errorDescription || `External sign in failed: ${error}`);
      return;
    }

    if (code || idToken || accessToken) {
      this.isProcessingOAuth.set(true);
      const redirectUri =
        typeof window !== 'undefined'
          ? `${window.location.origin}/login`
          : 'http://localhost:4200/login';

      this.authService
        .externalCallback({
          code: code || null,
          idToken: idToken || null,
          accessToken: accessToken || null,
          redirectUri,
        })
        .subscribe({
          next: () => {
            this.isProcessingOAuth.set(false);
            this.snackBar.open('Signed in successfully', 'Close', {
              duration: 3000,
            });
            const returnUrl =
              typeof sessionStorage !== 'undefined'
                ? sessionStorage.getItem('oauth_return_url') || '/dashboard'
                : '/dashboard';
            if (typeof sessionStorage !== 'undefined') {
              sessionStorage.removeItem('oauth_return_url');
            }
            this.router.navigateByUrl(returnUrl);
          },
          error: (err: unknown) => {
            this.isProcessingOAuth.set(false);
            this.errorMessage.set(
              this.extractErrorMessage(
                err,
                'Could not complete external sign in. Please try again.',
              ),
            );
          },
        });
    }
  }

  private navigateToTarget(): void {
    const returnUrl = this.route.snapshot.queryParams['returnUrl'] || '/dashboard';
    this.router.navigateByUrl(returnUrl);
  }

  private extractErrorMessage(err: unknown, fallback: string): string {
    if (err instanceof HttpErrorResponse) {
      if (err.error?.detail) {
        return err.error.detail;
      }
      if (err.error?.title) {
        return err.error.title;
      }
      if (typeof err.error === 'string') {
        return err.error;
      }
    }
    return fallback;
  }
}
