import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Observable } from 'rxjs';
import type { AccountSettingsDialogComponent } from '../../../features/account-settings-dialog/account-settings-dialog.component';
import { apiUrl } from '../../api/api-url';
import {
  AccountDto,
  ChangePasswordRequest,
  UpdateProfileRequest,
} from '../../models/account.models';
import { AuthResponse } from '../../models/auth.models';

export type AccountSettingsTab = 'profile' | 'password' | 'tokens';

@Service()
export class AccountService {
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog, { optional: true });

  getAccount(): Observable<AccountDto> {
    return this.http.get<AccountDto>(apiUrl('/account'));
  }

  updateProfile(request: UpdateProfileRequest): Observable<AuthResponse> {
    return this.http.put<AuthResponse>(apiUrl('/account/profile'), request);
  }

  changePassword(request: ChangePasswordRequest): Observable<AuthResponse> {
    return this.http.put<AuthResponse>(apiUrl('/account/password'), request);
  }

  async openAccountSettingsDialog(
    initialTab: AccountSettingsTab = 'profile',
  ): Promise<MatDialogRef<AccountSettingsDialogComponent> | undefined> {
    if (!this.dialog) {
      return undefined;
    }
    const { AccountSettingsDialogComponent } =
      await import('../../../features/account-settings-dialog/account-settings-dialog.component');
    return this.dialog.open(AccountSettingsDialogComponent, {
      data: { initialTab },
      width: '640px',
      maxWidth: '95vw',
      panelClass: 'account-settings-dialog-panel',
      autoFocus: false,
    });
  }
}
