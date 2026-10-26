import { HttpBackend, HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  CreateShareLinkRequest,
  PublicSharedRecipeDto,
  ShareLinkDto,
} from '../../models/share-link.models';

@Service()
export class ShareLinkService {
  private readonly http = inject(HttpClient);
  private readonly handler = inject(HttpBackend);
  private readonly publicHttp = new HttpClient(this.handler);

  getShareLinks(recipeId: string): Observable<ShareLinkDto[]> {
    return this.http.get<ShareLinkDto[]>(apiUrl(`/recipes/${recipeId}/shares`));
  }

  createShareLink(recipeId: string, request: CreateShareLinkRequest): Observable<ShareLinkDto> {
    return this.http.post<ShareLinkDto>(apiUrl(`/recipes/${recipeId}/shares`), request);
  }

  revokeShareLink(recipeId: string, shareId: string): Observable<ShareLinkDto> {
    return this.http.post<ShareLinkDto>(
      apiUrl(`/recipes/${recipeId}/shares/${shareId}/revoke`),
      {},
    );
  }

  deleteShareLink(recipeId: string, shareId: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/recipes/${recipeId}/shares/${shareId}`));
  }

  getPublicSharedRecipe(token: string): Observable<PublicSharedRecipeDto> {
    return this.publicHttp.get<PublicSharedRecipeDto>(
      apiUrl(`/public/shares/${encodeURIComponent(token)}`),
    );
  }

  buildPublicShareUrl(token: string): string {
    const origin =
      typeof window !== 'undefined' && window.location?.origin
        ? window.location.origin
        : 'http://localhost:4200';
    return `${origin}/share/${token}`;
  }
}
