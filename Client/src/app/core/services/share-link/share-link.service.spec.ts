import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  CreateShareLinkRequest,
  PublicSharedRecipeDto,
  ShareLinkDto,
} from '../../models/share-link.models';
import { ShareLinkService } from './share-link.service';

describe('ShareLinkService', () => {
  let service: ShareLinkService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), ShareLinkService],
    });

    service = TestBed.inject(ShareLinkService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches share links for a recipe via GET /api/recipes/:recipeId/shares', async () => {
    const mockLinks: ShareLinkDto[] = [
      {
        id: 's1',
        recipeId: 'r1',
        token: 'token12345678901234567890123456',
        label: 'Family Link',
        includeCookLogs: true,
        expiresAt: null,
        revokedAt: null,
        isActive: true,
        viewCount: 3,
        lastAccessedAt: '2026-10-26T20:00:00Z',
        createdAt: '2026-10-26T19:00:00Z',
        createdBy: 'user1',
      },
    ];

    const promise = firstValueFrom(service.getShareLinks('r1'));
    const req = httpMock.expectOne('/api/recipes/r1/shares');
    expect(req.request.method).toBe('GET');
    req.flush(mockLinks);

    const result = await promise;
    expect(result).toEqual(mockLinks);
  });

  it('creates a share link via POST /api/recipes/:recipeId/shares', async () => {
    const payload: CreateShareLinkRequest = {
      label: 'New Link',
      includeCookLogs: false,
      expiresAt: '2026-11-01T00:00:00Z',
    };

    const mockCreated: ShareLinkDto = {
      id: 's2',
      recipeId: 'r1',
      token: 'newtoken123456789012345678901234',
      label: 'New Link',
      includeCookLogs: false,
      expiresAt: '2026-11-01T00:00:00Z',
      revokedAt: null,
      isActive: true,
      viewCount: 0,
      lastAccessedAt: null,
      createdAt: '2026-10-26T20:00:00Z',
      createdBy: 'user1',
    };

    const promise = firstValueFrom(service.createShareLink('r1', payload));
    const req = httpMock.expectOne('/api/recipes/r1/shares');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(payload);
    req.flush(mockCreated);

    const result = await promise;
    expect(result).toEqual(mockCreated);
  });

  it('revokes a share link via POST /api/recipes/:recipeId/shares/:shareId/revoke', async () => {
    const mockRevoked: ShareLinkDto = {
      id: 's1',
      recipeId: 'r1',
      token: 'token12345678901234567890123456',
      label: 'Family Link',
      includeCookLogs: false,
      expiresAt: null,
      revokedAt: '2026-10-26T21:00:00Z',
      isActive: false,
      viewCount: 3,
      lastAccessedAt: null,
      createdAt: '2026-10-26T19:00:00Z',
      createdBy: 'user1',
    };

    const promise = firstValueFrom(service.revokeShareLink('r1', 's1'));
    const req = httpMock.expectOne('/api/recipes/r1/shares/s1/revoke');
    expect(req.request.method).toBe('POST');
    req.flush(mockRevoked);

    const result = await promise;
    expect(result.isActive).toBe(false);
  });

  it('deletes a share link via DELETE /api/recipes/:recipeId/shares/:shareId', async () => {
    const promise = firstValueFrom(service.deleteShareLink('r1', 's1'));
    const req = httpMock.expectOne('/api/recipes/r1/shares/s1');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await promise;
  });

  it('fetches a public shared recipe via GET /api/public/shares/:token', async () => {
    const mockPublicRecipe: PublicSharedRecipeDto = {
      title: 'Shared Tacos',
      description: 'Quick tacos',
      baseServings: 4,
      yieldDisplay: null,
      prepTimeMinutes: 15,
      cookTimeMinutes: 15,
      totalTimeMinutes: 30,
      difficulty: 'Easy',
      sourceUrl: null,
      sourceName: null,
      sourceAuthor: null,
      sourceBookTitle: null,
      sourceBookPage: null,
      coverImagePath: null,
      tags: [],
      ingredients: [],
      steps: [],
      linkedSubRecipes: [],
      includeCookLogs: false,
      expiresAt: null,
    };

    const promise = firstValueFrom(service.getPublicSharedRecipe('tok_123'));
    const req = httpMock.expectOne('/api/public/shares/tok_123');
    expect(req.request.method).toBe('GET');
    req.flush(mockPublicRecipe);

    const result = await promise;
    expect(result.title).toBe('Shared Tacos');
  });

  it('buildPublicShareUrl formats current origin url with token', () => {
    const url = service.buildPublicShareUrl('xyz789');
    expect(url).toContain('/share/xyz789');
  });
});
