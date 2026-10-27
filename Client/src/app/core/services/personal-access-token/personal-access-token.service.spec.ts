import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  CreatePersonalAccessTokenRequest,
  CreatedPersonalAccessTokenDto,
  PersonalAccessTokenDto,
} from '../../models/personal-access-token.models';
import { PersonalAccessTokenService } from './personal-access-token.service';

describe('PersonalAccessTokenService', () => {
  let service: PersonalAccessTokenService;
  let httpMock: HttpTestingController;

  const mockToken: PersonalAccessTokenDto = {
    id: 'tok-1',
    name: 'CLI Agent',
    tokenPrefix: 'lr_pat_abcd1234',
    createdAt: '2026-10-27T00:00:00Z',
    expiresAt: null,
    lastUsedAt: null,
    revokedAt: null,
    isActive: true,
    isRevoked: false,
    isExpired: false,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), PersonalAccessTokenService],
    });

    service = TestBed.inject(PersonalAccessTokenService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches personal access tokens via GET /api/account/tokens', async () => {
    const promise = firstValueFrom(service.getTokens());
    const req = httpMock.expectOne('/api/account/tokens');
    expect(req.request.method).toBe('GET');
    req.flush([mockToken]);

    await expect(promise).resolves.toEqual([mockToken]);
  });

  it('creates token via POST /api/account/tokens', async () => {
    const request: CreatePersonalAccessTokenRequest = {
      name: 'New Agent',
      expiresAt: '2026-11-27T00:00:00Z',
    };
    const createdDto: CreatedPersonalAccessTokenDto = {
      token: mockToken,
      plaintextToken: 'lr_pat_secret1234567890abcdef',
    };

    const promise = firstValueFrom(service.createToken(request));
    const req = httpMock.expectOne('/api/account/tokens');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(createdDto);

    await expect(promise).resolves.toEqual(createdDto);
  });

  it('revokes token via POST /api/account/tokens/:id/revoke', async () => {
    const revokedToken = { ...mockToken, isRevoked: true, revokedAt: '2026-10-27T01:00:00Z' };

    const promise = firstValueFrom(service.revokeToken('tok-1'));
    const req = httpMock.expectOne('/api/account/tokens/tok-1/revoke');
    expect(req.request.method).toBe('POST');
    req.flush(revokedToken);

    await expect(promise).resolves.toEqual(revokedToken);
  });

  it('deletes token via DELETE /api/account/tokens/:id', async () => {
    const promise = firstValueFrom(service.deleteToken('tok-1'));
    const req = httpMock.expectOne('/api/account/tokens/tok-1');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await expect(promise).resolves.toBeNull();
  });
});
