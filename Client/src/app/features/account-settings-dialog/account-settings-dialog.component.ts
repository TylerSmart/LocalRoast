import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { AccountDto } from '../../core/models/account.models';
import { AccountService, AccountSettingsTab } from '../../core/services/account/account.service';
import { AuthService } from '../../core/services/auth/auth.service';
import { PersonalAccessTokensPanelComponent } from '../mcp-tokens/personal-access-tokens-panel/personal-access-tokens-panel.component';

export interface AccountSettingsDialogData {
  initialTab?: AccountSettingsTab;
}

function passwordsMatchValidator(group: AbstractControl): ValidationErrors | null {
  const newPassword = group.get('newPassword')?.value as string | undefined;
  const confirmPassword = group.get('confirmPassword')?.value as string | undefined;
  if (newPassword && confirmPassword && newPassword !== confirmPassword) {
    return { passwordMismatch: true };
  }
  return null;
}

@Component({
  selector: 'app-account-settings-dialog',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatIconModule,
    MatTabsModule,
    MatProgressSpinnerModule,
    PersonalAccessTokensPanelComponent,
  ],
  templateUrl: './account-settings-dialog.component.html',
  styleUrl: './account-settings-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountSettingsDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly accountService = inject(AccountService);
  private readonly authService = inject(AuthService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly dialogRef = inject(MatDialogRef<AccountSettingsDialogComponent>);
  private readonly dialogData = inject<AccountSettingsDialogData | null>(MAT_DIALOG_DATA, {
    optional: true,
  });

  protected readonly selectedTabIndex = signal<number>(
    this.getInitialTabIndex(this.dialogData?.initialTab),
  );

  protected readonly account = signal<AccountDto | null>(null);
  protected readonly isLoadingAccount = signal<boolean>(true);
  protected readonly loadError = signal<string | null>(null);

  protected readonly isSavingProfile = signal<boolean>(false);
  protected readonly profileError = signal<string | null>(null);

  protected readonly isSavingPassword = signal<boolean>(false);
  protected readonly passwordError = signal<string | null>(null);

  protected readonly hideProfilePassword = signal<boolean>(true);
  protected readonly hideCurrentPassword = signal<boolean>(true);
  protected readonly hideNewPassword = signal<boolean>(true);
  protected readonly hideConfirmPassword = signal<boolean>(true);

  private readonly enteredUsername = signal<string>('');
  private readonly enteredEmail = signal<string>('');

  protected readonly hasPassword = computed<boolean>(() => this.account()?.hasPassword ?? true);

  protected readonly externalProvider = computed<string | null>(
    () => this.account()?.externalProvider ?? null,
  );

  protected readonly requiresPasswordForEmail = computed<boolean>(() => {
    if (!this.hasPassword()) {
      return false;
    }
    const baselineEmail = (this.account()?.email ?? this.authService.currentUser()?.email ?? '')
      .trim()
      .toLowerCase();
    const candidateEmail = this.enteredEmail().trim().toLowerCase();
    return candidateEmail.length > 0 && candidateEmail !== baselineEmail;
  });

  protected readonly hasProfileChanges = computed<boolean>(() => {
    const baselineUsername = (
      this.account()?.username ??
      this.authService.currentUser()?.username ??
      ''
    ).trim();
    const baselineEmail = (this.account()?.email ?? this.authService.currentUser()?.email ?? '')
      .trim()
      .toLowerCase();

    const nextUsername = this.enteredUsername().trim();
    const nextEmail = this.enteredEmail().trim().toLowerCase();

    return nextUsername !== baselineUsername || (this.hasPassword() && nextEmail !== baselineEmail);
  });

  readonly profileForm = this.fb.nonNullable.group({
    username: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(100)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    currentPassword: ['', [Validators.maxLength(100)]],
  });

  readonly passwordForm = this.fb.nonNullable.group(
    {
      currentPassword: ['', [Validators.maxLength(100)]],
      newPassword: ['', [Validators.required, Validators.minLength(6), Validators.maxLength(100)]],
      confirmPassword: ['', [Validators.required]],
    },
    { validators: passwordsMatchValidator },
  );

  constructor() {
    this.profileForm.controls.username.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((value) => this.enteredUsername.set(value));

    this.profileForm.controls.email.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((value) => this.enteredEmail.set(value));
  }

  ngOnInit(): void {
    const currentUser = this.authService.currentUser();
    if (currentUser) {
      this.applyProfileValues(currentUser.username, currentUser.email);
    }
    this.loadAccount();
  }

  loadAccount(): void {
    this.isLoadingAccount.set(true);
    this.loadError.set(null);

    this.accountService.getAccount().subscribe({
      next: (dto) => {
        this.account.set(dto);
        this.applyProfileValues(dto.username, dto.email);
        this.syncEmailControlDisabledState(dto.hasPassword);
        this.isLoadingAccount.set(false);
      },
      error: (err) => {
        this.isLoadingAccount.set(false);
        this.loadError.set(
          err?.error?.detail || err?.error?.title || 'Failed to load account settings.',
        );
      },
    });
  }

  saveProfile(): void {
    this.profileError.set(null);

    if (this.profileForm.invalid) {
      this.profileForm.markAllAsTouched();
      return;
    }

    const raw = this.profileForm.getRawValue();
    const username = raw.username.trim();
    const email = this.hasPassword()
      ? raw.email.trim()
      : (this.account()?.email ?? raw.email.trim());

    if (this.requiresPasswordForEmail() && !raw.currentPassword) {
      this.profileError.set('Enter your current password to confirm changing your email address.');
      return;
    }

    this.isSavingProfile.set(true);

    this.accountService
      .updateProfile({
        username,
        email,
        currentPassword: this.requiresPasswordForEmail() ? raw.currentPassword : null,
      })
      .subscribe({
        next: (response) => {
          this.isSavingProfile.set(false);
          this.authService.setSession(response);
          this.account.update((prev) =>
            prev
              ? { ...prev, username: response.username, email: response.email }
              : {
                  id: response.id,
                  username: response.username,
                  email: response.email,
                  hasPassword: true,
                  externalProvider: null,
                },
          );
          this.applyProfileValues(response.username, response.email);
          this.profileForm.controls.currentPassword.reset('');
          this.snackBar.open('Profile updated successfully', 'Close', { duration: 3000 });
        },
        error: (err) => {
          this.isSavingProfile.set(false);
          this.profileError.set(
            err?.error?.detail || err?.error?.title || 'Failed to update profile.',
          );
        },
      });
  }

  savePassword(): void {
    this.passwordError.set(null);

    if (this.passwordForm.invalid) {
      this.passwordForm.markAllAsTouched();
      return;
    }

    const raw = this.passwordForm.getRawValue();
    const hadPassword = this.hasPassword();

    if (hadPassword && !raw.currentPassword) {
      this.passwordError.set('Your current password is required.');
      return;
    }

    this.isSavingPassword.set(true);

    this.accountService
      .changePassword({
        currentPassword: hadPassword ? raw.currentPassword : null,
        newPassword: raw.newPassword,
      })
      .subscribe({
        next: (response) => {
          this.isSavingPassword.set(false);
          this.authService.setSession(response);
          this.account.update((prev) => (prev ? { ...prev, hasPassword: true } : prev));
          this.syncEmailControlDisabledState(true);
          this.passwordForm.reset({
            currentPassword: '',
            newPassword: '',
            confirmPassword: '',
          });
          this.snackBar.open(
            hadPassword ? 'Password updated successfully' : 'Password set successfully',
            'Close',
            { duration: 3000 },
          );
        },
        error: (err) => {
          this.isSavingPassword.set(false);
          this.passwordError.set(
            err?.error?.detail || err?.error?.title || 'Failed to update password.',
          );
        },
      });
  }

  close(): void {
    this.dialogRef.close();
  }

  private applyProfileValues(username: string, email: string): void {
    this.profileForm.patchValue({ username, email }, { emitEvent: true });
    this.enteredUsername.set(username);
    this.enteredEmail.set(email);
  }

  private syncEmailControlDisabledState(hasPassword: boolean): void {
    const emailCtrl = this.profileForm.controls.email;
    if (hasPassword) {
      emailCtrl.enable({ emitEvent: false });
    } else {
      emailCtrl.disable({ emitEvent: false });
    }
  }

  private getInitialTabIndex(tab?: AccountSettingsTab): number {
    switch (tab) {
      case 'password':
        return 1;
      case 'tokens':
        return 2;
      case 'profile':
      default:
        return 0;
    }
  }
}
