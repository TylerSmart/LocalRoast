import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { shareReplay, tap } from 'rxjs/operators';
import { apiUrl } from '../../api/api-url';
import { TtlCache } from '../../cache/ttl-cache';
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
  private readonly collectionsCache = new TtlCache<Observable<CollectionDto[]>>(30_000);
  private readonly tagsCache = new TtlCache<Observable<TagDto[]>>(30_000);

  invalidateCache(): void {
    this.collectionsCache.clear();
    this.tagsCache.clear();
  }

  getCollections(): Observable<CollectionDto[]> {
    const cached = this.collectionsCache.get('all');
    if (cached) {
      return cached;
    }

    const req$ = this.http
      .get<CollectionDto[]>(apiUrl('/collections'))
      .pipe(shareReplay({ bufferSize: 1, refCount: false }));
    this.collectionsCache.set('all', req$);
    return req$;
  }

  createCollection(request: UpsertCollectionRequest): Observable<CollectionDto> {
    return this.http
      .post<CollectionDto>(apiUrl('/collections'), request)
      .pipe(tap(() => this.invalidateCache()));
  }

  updateCollection(id: string, request: UpsertCollectionRequest): Observable<CollectionDto> {
    return this.http
      .put<CollectionDto>(apiUrl(`/collections/${id}`), request)
      .pipe(tap(() => this.invalidateCache()));
  }

  deleteCollection(id: string): Observable<void> {
    return this.http
      .delete<void>(apiUrl(`/collections/${id}`))
      .pipe(tap(() => this.invalidateCache()));
  }

  getTags(category?: TagCategory): Observable<TagDto[]> {
    const cacheKey = category ?? 'all';
    const cached = this.tagsCache.get(cacheKey);
    if (cached) {
      return cached;
    }

    let params = new HttpParams();
    if (category) {
      params = params.set('category', category);
    }

    const req$ = this.http
      .get<TagDto[]>(apiUrl('/tags'), { params })
      .pipe(shareReplay({ bufferSize: 1, refCount: false }));
    this.tagsCache.set(cacheKey, req$);
    return req$;
  }

  createTag(request: UpsertTagRequest): Observable<TagDto> {
    return this.http.post<TagDto>(apiUrl('/tags'), request).pipe(tap(() => this.invalidateCache()));
  }

  updateTag(id: string, request: UpsertTagRequest): Observable<TagDto> {
    return this.http
      .put<TagDto>(apiUrl(`/tags/${id}`), request)
      .pipe(tap(() => this.invalidateCache()));
  }

  deleteTag(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/tags/${id}`)).pipe(tap(() => this.invalidateCache()));
  }
}
