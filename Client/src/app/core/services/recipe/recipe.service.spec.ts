import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  IngredientMatchRequest,
  IngredientMatchResultDto,
  RecipeAttachmentDto,
  RecipeDetailDto,
  RecipeSummaryDto,
  UpdateRecipeOrganizationRequest,
} from '../../models/recipe.models';
import { RecipeService } from './recipe.service';

describe('RecipeService', () => {
  let service: RecipeService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), RecipeService],
    });

    service = TestBed.inject(RecipeService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches recipes list with query parameters via GET /api/recipes', async () => {
    const recipes: RecipeSummaryDto[] = [];
    const promise = firstValueFrom(
      service.getRecipes({
        search: 'soup',
        status: 'Published',
        difficulty: 'Easy',
        collectionId: 'c1',
        tagIds: ['t1', 't2'],
      }),
    );
    const req = httpMock.expectOne(
      '/api/recipes?search=soup&status=Published&difficulty=Easy&collectionId=c1&tagIds=t1&tagIds=t2',
    );
    expect(req.request.method).toBe('GET');
    req.flush(recipes);

    const result = await promise;
    expect(result).toEqual([]);
  });

  it('allows recipe pagination requests to skip repeated total counts', async () => {
    const promise = firstValueFrom(
      service.getRecipesPaged({
        page: 2,
        pageSize: 24,
        search: 'soup',
        includeTotalCount: false,
        fuzzySearch: true,
      }),
    );
    const req = httpMock.expectOne(
      (request) =>
        request.url === '/api/recipes' &&
        request.params.get('page') === '2' &&
        request.params.get('includeTotalCount') === 'false' &&
        request.params.get('fuzzySearch') === 'true',
    );

    expect(req.request.method).toBe('GET');
    req.flush({
      items: [],
      totalCount: null,
      pageNumber: 2,
      pageSize: 24,
      totalPages: null,
      hasPreviousPage: true,
      hasNextPage: false,
      hasMore: false,
      isFuzzySearch: true,
    });
    expect((await promise).totalCount).toBeNull();
  });

  it('fetches recipe detail via GET /api/recipes/:id', async () => {
    const detail: RecipeDetailDto = {
      id: 'r-1',
      title: 'Lasagna',
      slug: 'lasagna',
      description: 'Yummy',
      status: 'Published',
      baseServings: 4,
      yieldDisplay: '1 pan',
      prepTimeMinutes: 20,
      cookTimeMinutes: 45,
      totalTimeMinutes: 65,
      difficulty: 'Medium',
      sourceUrl: null,
      sourceName: null,
      sourceAuthor: null,
      sourceBookTitle: null,
      sourceBookPage: null,
      coverImagePath: null,
      thumbnailImagePath: null,
      timesCooked: 0,
      lastCookedAt: null,
      averageRating: null,
      ingredients: [],
      steps: [],
      linkedSubRecipes: [],
      collections: [],
      tags: [],
      createdAt: '2026-01-01T00:00:00Z',
      createdBy: 'test-user',
      modifiedAt: '2026-01-01T00:00:00Z',
      modifiedBy: 'test-user',
    };

    const promise = firstValueFrom(service.getRecipeById('r-1'));
    const req = httpMock.expectOne('/api/recipes/r-1');
    expect(req.request.method).toBe('GET');
    req.flush(detail);

    const result = await promise;
    expect(result.title).toBe('Lasagna');
  });

  it('updates recipe organization via PUT /api/recipes/:id/organization', async () => {
    const request: UpdateRecipeOrganizationRequest = {
      collectionIds: ['c1'],
      tagIds: ['t1'],
    };
    const response: RecipeDetailDto = {
      id: 'r-1',
      title: 'Lasagna',
      slug: 'lasagna',
      description: 'Yummy',
      status: 'Published',
      baseServings: 4,
      yieldDisplay: '1 pan',
      prepTimeMinutes: 20,
      cookTimeMinutes: 45,
      totalTimeMinutes: 65,
      difficulty: 'Medium',
      sourceUrl: null,
      sourceName: null,
      sourceAuthor: null,
      sourceBookTitle: null,
      sourceBookPage: null,
      coverImagePath: null,
      thumbnailImagePath: null,
      timesCooked: 0,
      lastCookedAt: null,
      averageRating: null,
      ingredients: [],
      steps: [],
      linkedSubRecipes: [],
      collections: [{ id: 'c1', name: 'Weeknight', slug: 'weeknight' }],
      tags: [{ id: 't1', name: 'Italian', slug: 'cuisine-italian', category: 'Cuisine' }],
      createdAt: '2026-01-01T00:00:00Z',
      createdBy: 'test-user',
      modifiedAt: '2026-01-01T00:00:00Z',
      modifiedBy: 'test-user',
    };

    const promise = firstValueFrom(service.updateRecipeOrganization('r-1', request));
    const req = httpMock.expectOne('/api/recipes/r-1/organization');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(request);
    req.flush(response);

    const result = await promise;
    expect(result.collections.length).toBe(1);
    expect(result.tags.length).toBe(1);
  });

  it('matches recipes by ingredients via POST /api/recipes/match-ingredients', async () => {
    const request: IngredientMatchRequest = {
      foodItemIds: ['fi-1', 'fi-2'],
    };
    const response: IngredientMatchResultDto[] = [
      {
        recipe: {
          id: 'r-1',
          title: 'Chicken Soup',
          slug: 'chicken-soup',
          description: 'Comfort food',
          status: 'Published',
          baseServings: 4,
          yieldDisplay: null,
          prepTimeMinutes: 10,
          cookTimeMinutes: 30,
          totalTimeMinutes: 40,
          difficulty: 'Easy',
          sourceName: null,
          thumbnailImagePath: null,
          collections: [],
          tags: [],
          ingredientCount: 5,
          stepCount: 3,
          timesCooked: 1,
          averageRating: 5,
          createdAt: '2026-01-01T00:00:00Z',
          modifiedAt: '2026-01-01T00:00:00Z',
        },
        matchedCount: 2,
        selectedOnHandCount: 2,
        additionalNonStapleCount: 0,
        pantryStaplesUsedCount: 2,
        matchedIngredientNames: ['Chicken Thighs', 'Carrot'],
        missingNonStapleNames: [],
      },
    ];

    const promise = firstValueFrom(service.matchRecipesByIngredients(request));
    const req = httpMock.expectOne('/api/recipes/match-ingredients');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(response);

    const result = await promise;
    expect(result.length).toBe(1);
    expect(result[0].matchedCount).toBe(2);
  });

  it('detects step amounts via POST /api/recipes/step-amounts/detect', async () => {
    const promise = firstValueFrom(service.detectStepAmounts('Add 1 cup flour.'));
    const req = httpMock.expectOne('/api/recipes/step-amounts/detect');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ instruction: 'Add 1 cup flour.' });
    req.flush([
      {
        start: 4,
        length: 5,
        text: '1 cup',
        measurement: { quantity: 1, unit: 'Cup' },
        alternativeMeasurement: null,
      },
    ]);

    const result = await promise;
    expect(result[0].text).toBe('1 cup');
  });

  it('deletes recipe via DELETE /api/recipes/:id', async () => {
    const promise = firstValueFrom(service.deleteRecipe('r-1'));
    const req = httpMock.expectOne('/api/recipes/r-1');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await promise;
  });
  it('adds attachment via POST /api/recipes/:id/attachments', async () => {
    const file = new File(['content'], 'card.pdf', { type: 'application/pdf' });
    const promise = firstValueFrom(service.addRecipeAttachment('r-1', file, 'Scanned Recipe'));

    const req = httpMock.expectOne('/api/recipes/r-1/attachments');
    expect(req.request.method).toBe('POST');
    expect(req.request.body instanceof FormData).toBe(true);

    const mockAttachment: RecipeAttachmentDto = {
      id: 'att-1',
      sortOrder: 1,
      fileName: 'card.pdf',
      mediaUrlPath: '/api/uploads/attachments/att-1.pdf',
      contentType: 'application/pdf',
      fileSizeBytes: 1024,
      label: 'Scanned Recipe',
      createdAt: '2026-10-26T12:00:00Z',
    };
    req.flush(mockAttachment);

    const result = await promise;
    expect(result.id).toBe('att-1');
    expect(result.label).toBe('Scanned Recipe');
  });

  it('deletes attachment via DELETE /api/recipes/:id/attachments/:attachmentId', async () => {
    const promise = firstValueFrom(service.deleteRecipeAttachment('r-1', 'att-1'));
    const req = httpMock.expectOne('/api/recipes/r-1/attachments/att-1');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await promise;
  });

  it('fetches recipes with forgotten favorites & min rating parameters', async () => {
    const promise = firstValueFrom(
      service.getRecipes({
        minRating: 4.5,
        forgottenFavoritesOnly: true,
        forgottenDaysThreshold: 90,
      }),
    );
    const req = httpMock.expectOne(
      '/api/recipes?minRating=4.5&forgottenFavoritesOnly=true&forgottenDaysThreshold=90',
    );
    expect(req.request.method).toBe('GET');
    req.flush([]);

    const result = await promise;
    expect(result).toEqual([]);
  });

  it('performs cook log CRUD endpoints', async () => {
    // 1. GET cook logs
    const getPromise = firstValueFrom(service.getCookLogs('r-1'));
    const getReq = httpMock.expectOne('/api/recipes/r-1/cook-logs');
    expect(getReq.request.method).toBe('GET');
    getReq.flush([]);
    await getPromise;

    // 2. CREATE cook log
    const createPromise = firstValueFrom(
      service.createCookLog('r-1', { rating: 5, notes: 'Great' }),
    );
    const createReq = httpMock.expectOne('/api/recipes/r-1/cook-logs');
    expect(createReq.request.method).toBe('POST');
    createReq.flush({
      cookLog: { id: 'cl-1', recipeId: 'r-1', rating: 5, notes: 'Great' },
      recipeStats: { recipeId: 'r-1', timesCooked: 1, averageRating: 5 },
    });
    const createResult = await createPromise;
    expect(createResult.cookLog.id).toBe('cl-1');

    // 3. UPDATE cook log
    const updatePromise = firstValueFrom(
      service.updateCookLog('r-1', 'cl-1', { rating: 4, notes: 'Updated' }),
    );
    const updateReq = httpMock.expectOne('/api/recipes/r-1/cook-logs/cl-1');
    expect(updateReq.request.method).toBe('PUT');
    updateReq.flush({
      cookLog: { id: 'cl-1', recipeId: 'r-1', rating: 4, notes: 'Updated' },
      recipeStats: { recipeId: 'r-1', timesCooked: 1, averageRating: 4 },
    });
    const updateResult = await updatePromise;
    expect(updateResult.cookLog.rating).toBe(4);

    // 4. DELETE cook log
    const deletePromise = firstValueFrom(service.deleteCookLog('r-1', 'cl-1'));
    const deleteReq = httpMock.expectOne('/api/recipes/r-1/cook-logs/cl-1');
    expect(deleteReq.request.method).toBe('DELETE');
    deleteReq.flush(null);
    await deletePromise;
  });
});
