import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { AccountDto } from '../../models/account.models';
import { AuthResponse } from '../../models/auth.models';
import { AccountService } from './account.service';

describe('AccountService', () => {
  let service: AccountService;
  let httpMock: HttpTestingController;
  let mockDialog: { open: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    mockDialog = {
      open: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        AccountService,
        { provide: MatDialog, useValue: mockDialog },
      ],
    });

    service = TestBed.inject(AccountService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches account metadata via GET /api/account', async () => {
    const expected: AccountDto = {
      id: 'u-1',
      email: 'alice@example.com',
      username: 'alice',
      hasPassword: true,
      externalProvider: null,
    };

    const promise = firstValueFrom(service.getAccount());
    const req = httpMock.expectOne('/api/account');
    expect(req.request.method).toBe('GET');
    req.flush(expected);

    await expect(promise).resolves.toEqual(expected);
  });

  it('updates profile via PUT /api/account/profile', async () => {
    const authRes: AuthResponse = {
      token: 'jwt-new',
      id: 'u-1',
      email: 'alice@example.com',
      username: 'alice_updated',
      expiresAt: '2026-10-27T00:00:00Z',
    };

    const promise = firstValueFrom(
      service.updateProfile({ username: 'alice_updated', email: 'alice@example.com' }),
    );
    const req = httpMock.expectOne('/api/account/profile');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({
      username: 'alice_updated',
      email: 'alice@example.com',
    });
    req.flush(authRes);

    await expect(promise).resolves.toEqual(authRes);
  });

  it('changes password via PUT /api/account/password', async () => {
    const authRes: AuthResponse = {
      token: 'jwt-new',
      id: 'u-1',
      email: 'alice@example.com',
      username: 'alice',
      expiresAt: '2026-10-27T00:00:00Z',
    };

    const promise = firstValueFrom(
      service.changePassword({ currentPassword: 'OldPassword1!', newPassword: 'NewPassword2!' }),
    );
    const req = httpMock.expectOne('/api/account/password');
    expect(req.request.method).toBe('PUT');
    req.flush(authRes);

    await expect(promise).resolves.toEqual(authRes);
  });

  it('opens AccountSettingsDialogComponent via MatDialog with default initialTab', async () => {
    await service.openAccountSettingsDialog();
    expect(mockDialog.open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: { initialTab: 'profile' },
        width: '640px',
      }),
    );
  });

  it('opens AccountSettingsDialogComponent via MatDialog with specific initialTab', async () => {
    await service.openAccountSettingsDialog('tokens');
    expect(mockDialog.open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: { initialTab: 'tokens' },
        width: '640px',
      }),
    );
  });
});
