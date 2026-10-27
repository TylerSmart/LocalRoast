import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';
import { AccountDto } from '../../core/models/account.models';
import { AuthResponse } from '../../core/models/auth.models';
import { AccountService } from '../../core/services/account/account.service';
import { AuthService } from '../../core/services/auth/auth.service';
import { PersonalAccessTokenService } from '../../core/services/personal-access-token/personal-access-token.service';
import {
  AccountSettingsDialogComponent,
  AccountSettingsDialogData,
} from './account-settings-dialog.component';

describe('AccountSettingsDialogComponent', () => {
  let component: AccountSettingsDialogComponent;
  let fixture: ComponentFixture<AccountSettingsDialogComponent>;

  let mockAccountService: {
    getAccount: ReturnType<typeof vi.fn>;
    updateProfile: ReturnType<typeof vi.fn>;
    changePassword: ReturnType<typeof vi.fn>;
  };

  let mockAuthService: {
    currentUser: ReturnType<typeof vi.fn>;
    setSession: ReturnType<typeof vi.fn>;
  };

  let mockTokenService: {
    getTokens: ReturnType<typeof vi.fn>;
    createToken: ReturnType<typeof vi.fn>;
    revokeToken: ReturnType<typeof vi.fn>;
    deleteToken: ReturnType<typeof vi.fn>;
  };

  let mockSnackBar: {
    open: ReturnType<typeof vi.fn>;
  };

  let mockDialogRef: {
    close: ReturnType<typeof vi.fn>;
  };

  let dialogData: AccountSettingsDialogData | null = null;

  const defaultAccount: AccountDto = {
    id: 'u-1',
    email: 'alice@example.com',
    username: 'alice',
    hasPassword: true,
    externalProvider: null,
  };

  beforeEach(async () => {
    dialogData = null;
    mockAccountService = {
      getAccount: vi.fn().mockReturnValue(of(defaultAccount)),
      updateProfile: vi.fn(),
      changePassword: vi.fn(),
    };

    mockAuthService = {
      currentUser: vi.fn().mockReturnValue({
        id: 'u-1',
        email: 'alice@example.com',
        username: 'alice',
      }),
      setSession: vi.fn(),
    };

    mockTokenService = {
      getTokens: vi.fn().mockReturnValue(of([])),
      createToken: vi.fn(),
      revokeToken: vi.fn(),
      deleteToken: vi.fn(),
    };

    mockSnackBar = {
      open: vi.fn(),
    };

    mockDialogRef = {
      close: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [AccountSettingsDialogComponent],
      providers: [
        provideAnimationsAsync(),
        { provide: AccountService, useValue: mockAccountService },
        { provide: AuthService, useValue: mockAuthService },
        { provide: PersonalAccessTokenService, useValue: mockTokenService },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useFactory: () => dialogData },
      ],
    }).compileComponents();
  });

  function createComponent(): void {
    fixture = TestBed.createComponent(AccountSettingsDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('loads account details and populates profile form with default profile tab selected', () => {
    createComponent();

    expect(mockAccountService.getAccount).toHaveBeenCalledTimes(1);
    expect(component.profileForm.getRawValue().username).toBe('alice');
    expect(component.profileForm.getRawValue().email).toBe('alice@example.com');
    expect(component['requiresPasswordForEmail']()).toBe(false);
    expect(component['selectedTabIndex']()).toBe(0);
  });

  it('initializes on tokens tab when MAT_DIALOG_DATA specifies tokens', () => {
    dialogData = { initialTab: 'tokens' };
    createComponent();

    expect(component['selectedTabIndex']()).toBe(2);
  });

  it('initializes on password tab when MAT_DIALOG_DATA specifies password', () => {
    dialogData = { initialTab: 'password' };
    createComponent();

    expect(component['selectedTabIndex']()).toBe(1);
  });

  it('updates username without requiring current password and refreshes session', () => {
    const authRes: AuthResponse = {
      token: 'jwt-updated',
      id: 'u-1',
      email: 'alice@example.com',
      username: 'alice_new',
      expiresAt: '2026-10-27T00:00:00Z',
    };
    mockAccountService.updateProfile.mockReturnValue(of(authRes));

    createComponent();
    component.profileForm.controls.username.setValue('alice_new');
    component.saveProfile();

    expect(mockAccountService.updateProfile).toHaveBeenCalledWith({
      username: 'alice_new',
      email: 'alice@example.com',
      currentPassword: null,
    });
    expect(mockAuthService.setSession).toHaveBeenCalledWith(authRes);
    expect(mockSnackBar.open).toHaveBeenCalled();
  });

  it('requires current password when changing email for password-backed account', () => {
    createComponent();
    component.profileForm.controls.email.setValue('new@example.com');
    expect(component['requiresPasswordForEmail']()).toBe(true);

    component.saveProfile();
    expect(mockAccountService.updateProfile).not.toHaveBeenCalled();
    expect(component['profileError']()).toContain('current password');
  });

  it('disables email field and allows setting initial password for external-only account', () => {
    mockAccountService.getAccount.mockReturnValue(
      of({
        id: 'u-2',
        email: 'sso@example.com',
        username: 'ssouser',
        hasPassword: false,
        externalProvider: 'oidc',
      } satisfies AccountDto),
    );
    const authRes: AuthResponse = {
      token: 'jwt-sso-pw',
      id: 'u-2',
      email: 'sso@example.com',
      username: 'ssouser',
      expiresAt: '2026-10-27T00:00:00Z',
    };
    mockAccountService.changePassword.mockReturnValue(of(authRes));

    createComponent();

    expect(component['hasPassword']()).toBe(false);
    expect(component.profileForm.controls.email.disabled).toBe(true);

    component.passwordForm.patchValue({
      newPassword: 'InitialPassword123!',
      confirmPassword: 'InitialPassword123!',
    });
    component.savePassword();

    expect(mockAccountService.changePassword).toHaveBeenCalledWith({
      currentPassword: null,
      newPassword: 'InitialPassword123!',
    });
    expect(mockAuthService.setSession).toHaveBeenCalledWith(authRes);
    expect(component['hasPassword']()).toBe(true);
    expect(component.profileForm.controls.email.enabled).toBe(true);
  });

  it('displays server ProblemDetails error message when password update fails', () => {
    mockAccountService.changePassword.mockReturnValue(
      throwError(() => ({
        error: { detail: 'The current password you entered is incorrect.' },
      })),
    );

    createComponent();
    component.passwordForm.patchValue({
      currentPassword: 'Wrong!',
      newPassword: 'NewPassword123!',
      confirmPassword: 'NewPassword123!',
    });
    component.savePassword();

    expect(component['passwordError']()).toBe('The current password you entered is incorrect.');
  });
});
