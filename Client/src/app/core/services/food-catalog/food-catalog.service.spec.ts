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
});
