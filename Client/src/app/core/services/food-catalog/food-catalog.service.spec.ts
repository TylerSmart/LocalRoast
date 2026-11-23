import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { FoodItemDto, FoodItemRecipeUsageDto } from '../../models/food-catalog.models';
import { FoodCatalogService } from './food-catalog.service';

describe('FoodCatalogService', () => {
  let service: FoodCatalogService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), FoodCatalogService],
    });

    service = TestBed.inject(FoodCatalogService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches items with query parameters via GET /api/food-items', async () => {
    const promise = firstValueFrom(
      service.getFoodItems({ search: 'garlic', isPantryStaple: true }),
    );
    const req = httpMock.expectOne('/api/food-items?search=garlic&isPantryStaple=true');
    expect(req.request.method).toBe('GET');
    req.flush([]);

    const result = await promise;
    expect(result).toEqual([]);
  });

  it('passes the category filter as a query parameter', async () => {
    const promise = firstValueFrom(service.getFoodItems({ category: 'Produce' }));
    const req = httpMock.expectOne('/api/food-items?category=Produce');
    req.flush([]);
    await promise;
  });

  it('passes sortBy and sortDirection query parameters for paged and unpaged requests', async () => {
    const pagedPromise = firstValueFrom(
      service.getFoodItemsPaged({
        page: 1,
        pageSize: 30,
        sortBy: 'RecipeCount',
        sortDirection: 'Desc',
      }),
    );
    const pagedReq = httpMock.expectOne(
      '/api/food-items?page=1&pageSize=30&sortBy=RecipeCount&sortDirection=Desc',
    );
    expect(pagedReq.request.method).toBe('GET');
    pagedReq.flush({
      items: [],
      totalCount: 0,
      pageNumber: 1,
      pageSize: 30,
      totalPages: 0,
      hasPreviousPage: false,
      hasNextPage: false,
    });
    await pagedPromise;

    const listPromise = firstValueFrom(
      service.getFoodItems({
        sortBy: 'ModifiedAt',
        sortDirection: 'Asc',
      }),
    );
    const listReq = httpMock.expectOne('/api/food-items?sortBy=ModifiedAt&sortDirection=Asc');
    expect(listReq.request.method).toBe('GET');
    listReq.flush([]);
    await listPromise;
  });

  it('creates food item via POST /api/food-items', async () => {
    const item: FoodItemDto = {
      id: 'f-1',
      name: 'Shallot',
      isPantryStaple: false,
      aliases: ['French Shallot'],
      referencedRecipeCount: 0,
      createdAt: '2026-01-01T00:00:00Z',
      modifiedAt: '2026-01-01T00:00:00Z',
    };

    const promise = firstValueFrom(
      service.createFoodItem({ name: 'Shallot', aliases: ['French Shallot'] }),
    );
    const req = httpMock.expectOne('/api/food-items');
    expect(req.request.method).toBe('POST');
    req.flush(item);

    const result = await promise;
    expect(result.name).toBe('Shallot');
  });

  it('fetches referencing recipes via GET /api/food-items/:id/recipes', async () => {
    const mockUsage: FoodItemRecipeUsageDto[] = [
      {
        recipeId: 'rec-1',
        recipeTitle: 'Garlic Bread',
        recipeSlug: 'garlic-bread',
        thumbnailImagePath: null,
        status: 'Published',
        ingredientDisplayNames: ['2 cloves garlic'],
      },
    ];

    const promise = firstValueFrom(service.getFoodItemRecipes('f-1'));
    const req = httpMock.expectOne('/api/food-items/f-1/recipes');
    expect(req.request.method).toBe('GET');
    req.flush(mockUsage);

    const result = await promise;
    expect(result.length).toBe(1);
    expect(result[0].recipeTitle).toBe('Garlic Bread');
  });

  it('unlinks food item recipes via POST /api/food-items/:id/unlink-recipes', async () => {
    const promise = firstValueFrom(service.unlinkFoodItemRecipes('f-1'));
    const req = httpMock.expectOne('/api/food-items/f-1/unlink-recipes');
    expect(req.request.method).toBe('POST');
    req.flush({ foodItemId: 'f-1', unlinkedRecipeCount: 3, unlinkedIngredientCount: 4 });

    const result = await promise;
    expect(result.unlinkedRecipeCount).toBe(3);
  });

  it('deletes food item with unlinkFromRecipes flag via DELETE /api/food-items/:id?unlinkFromRecipes=true', async () => {
    const promise = firstValueFrom(service.deleteFoodItem('f-1', true));
    const req = httpMock.expectOne('/api/food-items/f-1?unlinkFromRecipes=true');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await promise;
  });

  it('bulk deletes food items via POST /api/food-items/bulk-delete', async () => {
    const promise = firstValueFrom(
      service.bulkDeleteFoodItems({
        foodItemIds: ['f-1', 'f-2'],
        unlinkFromRecipes: true,
      }),
    );
    const req = httpMock.expectOne('/api/food-items/bulk-delete');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      foodItemIds: ['f-1', 'f-2'],
      unlinkFromRecipes: true,
    });
    req.flush({
      deletedCount: 2,
      unlinkedRecipeCount: 1,
      unlinkedIngredientCount: 2,
      unlinkedShoppingListItemCount: 0,
    });

    const result = await promise;
    expect(result.deletedCount).toBe(2);
    expect(result.unlinkedRecipeCount).toBe(1);
  });
});
