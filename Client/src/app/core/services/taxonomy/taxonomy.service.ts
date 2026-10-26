import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  CollectionDto,
  TagCategory,
  TagDto,
  UpsertCollectionRequest,
  UpsertTagRequest,
} from '../../models/taxonomy.models';

@Service()
export class TaxonomyService {
  private readonly http = inject(HttpClient);

  getCollections(): Observable<CollectionDto[]> {
    return this.http.get<CollectionDto[]>(apiUrl('/collections'));
  }

  createCollection(request: UpsertCollectionRequest): Observable<CollectionDto> {
    return this.http.post<CollectionDto>(apiUrl('/collections'), request);
  }

  updateCollection(id: string, request: UpsertCollectionRequest): Observable<CollectionDto> {
    return this.http.put<CollectionDto>(apiUrl(`/collections/${id}`), request);
  }

  deleteCollection(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/collections/${id}`));
  }

  getTags(category?: TagCategory): Observable<TagDto[]> {
    let params = new HttpParams();
    if (category) {
      params = params.set('category', category);
    }
    return this.http.get<TagDto[]>(apiUrl('/tags'), { params });
  }

  createTag(request: UpsertTagRequest): Observable<TagDto> {
    return this.http.post<TagDto>(apiUrl('/tags'), request);
  }

  updateTag(id: string, request: UpsertTagRequest): Observable<TagDto> {
    return this.http.put<TagDto>(apiUrl(`/tags/${id}`), request);
  }

  deleteTag(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/tags/${id}`));
  }
}
