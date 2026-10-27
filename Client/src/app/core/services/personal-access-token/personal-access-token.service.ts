import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  CreatePersonalAccessTokenRequest,
  CreatedPersonalAccessTokenDto,
  PersonalAccessTokenDto,
} from '../../models/personal-access-token.models';

@Service()
export class PersonalAccessTokenService {
  private readonly http = inject(HttpClient);

  getTokens(): Observable<PersonalAccessTokenDto[]> {
    return this.http.get<PersonalAccessTokenDto[]>(apiUrl('/account/tokens'));
  }

  createToken(
    request: CreatePersonalAccessTokenRequest,
  ): Observable<CreatedPersonalAccessTokenDto> {
    return this.http.post<CreatedPersonalAccessTokenDto>(apiUrl('/account/tokens'), request);
  }

  revokeToken(id: string): Observable<PersonalAccessTokenDto> {
    return this.http.post<PersonalAccessTokenDto>(apiUrl(`/account/tokens/${id}/revoke`), {});
  }

  deleteToken(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/account/tokens/${id}`));
  }
}
