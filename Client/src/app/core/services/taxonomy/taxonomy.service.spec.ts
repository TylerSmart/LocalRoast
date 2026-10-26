import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  CollectionDto,
  TagDto,
  UpsertCollectionRequest,
  UpsertTagRequest,
} from '../../models/taxonomy.models';
import { TaxonomyService } from './taxonomy.service';

describe('TaxonomyService', () => {
  let service: TaxonomyService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), TaxonomyService],
    });

    service = TestBed.inject(TaxonomyService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches collections via GET /api/collections', async () => {
    const mockCollections: CollectionDto[] = [
      {
        id: 'c1',
        name: 'Weeknight Staples',
        slug: 'weeknight-staples',
        description: 'Quick dinners',
        sortOrder: 10,
        isSystemSeeded: true,
        recipeCount: 3,
      },
    ];

    const promise = firstValueFrom(service.getCollections());
    const req = httpMock.expectOne('/api/collections');
    expect(req.request.method).toBe('GET');
    req.flush(mockCollections);

    const result = await promise;
    expect(result).toEqual(mockCollections);
  });

  it('creates collection via POST /api/collections', async () => {
    const request: UpsertCollectionRequest = {
      name: 'Summer Grilling',
      description: 'BBQ favorites',
      sortOrder: 1,
    };
    const response: CollectionDto = {
      id: 'c2',
      name: 'Summer Grilling',
      slug: 'summer-grilling',
      description: 'BBQ favorites',
      sortOrder: 1,
      isSystemSeeded: false,
      recipeCount: 0,
    };

    const promise = firstValueFrom(service.createCollection(request));
    const req = httpMock.expectOne('/api/collections');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(response);

    const result = await promise;
    expect(result).toEqual(response);
  });

  it('updates collection via PUT /api/collections/:id', async () => {
    const request: UpsertCollectionRequest = {
      name: 'Updated Name',
      sortOrder: 5,
    };
    const response: CollectionDto = {
      id: 'c1',
      name: 'Updated Name',
      slug: 'updated-name',
      description: null,
      sortOrder: 5,
      isSystemSeeded: false,
      recipeCount: 2,
    };

    const promise = firstValueFrom(service.updateCollection('c1', request));
    const req = httpMock.expectOne('/api/collections/c1');
    expect(req.request.method).toBe('PUT');
    req.flush(response);

    const result = await promise;
    expect(result.name).toBe('Updated Name');
  });

  it('deletes collection via DELETE /api/collections/:id', async () => {
    const promise = firstValueFrom(service.deleteCollection('c1'));
    const req = httpMock.expectOne('/api/collections/c1');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await promise;
  });

  it('fetches tags with optional category via GET /api/tags', async () => {
    const mockTags: TagDto[] = [
      {
        id: 't1',
        name: 'Vegetarian',
        slug: 'dietary-vegetarian',
        category: 'Dietary',
        sortOrder: 1,
        isSystemSeeded: true,
        recipeCount: 5,
      },
    ];

    const promise = firstValueFrom(service.getTags('Dietary'));
    const req = httpMock.expectOne('/api/tags?category=Dietary');
    expect(req.request.method).toBe('GET');
    req.flush(mockTags);

    const result = await promise;
    expect(result).toEqual(mockTags);
  });

  it('creates tag via POST /api/tags', async () => {
    const request: UpsertTagRequest = {
      name: 'Air Fryer',
      category: 'Equipment',
      sortOrder: 10,
    };
    const response: TagDto = {
      id: 't2',
      name: 'Air Fryer',
      slug: 'equipment-air-fryer',
      category: 'Equipment',
      sortOrder: 10,
      isSystemSeeded: false,
      recipeCount: 0,
    };

    const promise = firstValueFrom(service.createTag(request));
    const req = httpMock.expectOne('/api/tags');
    expect(req.request.method).toBe('POST');
    req.flush(response);

    const result = await promise;
    expect(result).toEqual(response);
  });

  it('updates tag via PUT /api/tags/:id', async () => {
    const request: UpsertTagRequest = {
      name: 'Gluten-Free',
      category: 'Dietary',
      sortOrder: 2,
    };
    const response: TagDto = {
      id: 't1',
      name: 'Gluten-Free',
      slug: 'dietary-gluten-free',
      category: 'Dietary',
      sortOrder: 2,
      isSystemSeeded: false,
      recipeCount: 1,
    };

    const promise = firstValueFrom(service.updateTag('t1', request));
    const req = httpMock.expectOne('/api/tags/t1');
    expect(req.request.method).toBe('PUT');
    req.flush(response);

    const result = await promise;
    expect(result.name).toBe('Gluten-Free');
  });

  it('deletes tag via DELETE /api/tags/:id', async () => {
    const promise = firstValueFrom(service.deleteTag('t1'));
    const req = httpMock.expectOne('/api/tags/t1');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await promise;
  });
});
