import type { PagedResultDto } from '../../models/pagination.models';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  CreateFoodItemRequest,
  FoodItemDto,
  FoodItemQueryParameters,
  FoodItemRecipeUsageDto,
  MergeFoodItemsRequest,
  MergeFoodItemsResultDto,
  UnlinkFoodItemRecipesResultDto,
  UpdateFoodItemRequest,
} from '../../models/food-catalog.models';

@Service()
export class FoodCatalogService {
  private readonly http = inject(HttpClient);

  getFoodItemsPaged(query?: FoodItemQueryParameters): Observable<PagedResultDto<FoodItemDto>> {
    let params = new HttpParams();
    if (query?.page !== undefined && query?.page !== null) {
      params = params.set('page', query.page.toString());
    }
    if (query?.pageSize !== undefined && query?.pageSize !== null) {
      params = params.set('pageSize', query.pageSize.toString());
    }
    if (query?.search) {
      params = params.set('search', query.search);
    }
    if (query?.isPantryStaple !== undefined && query?.isPantryStaple !== null) {
      params = params.set('isPantryStaple', query.isPantryStaple.toString());
    }
    if (query?.category) {
      params = params.set('category', query.category);
    }

    return this.http.get<PagedResultDto<FoodItemDto>>(apiUrl('/food-items'), { params });
  }

  getFoodItems(query?: FoodItemQueryParameters): Observable<FoodItemDto[]> {
    let params = new HttpParams();
    if (query?.search) {
      params = params.set('search', query.search);
    }
    if (query?.isPantryStaple !== undefined && query?.isPantryStaple !== null) {
      params = params.set('isPantryStaple', query.isPantryStaple.toString());
    }
    if (query?.category) {
      params = params.set('category', query.category);
    }

    return this.http.get<FoodItemDto[]>(apiUrl('/food-items'), { params });
  }

  getFoodItemById(id: string): Observable<FoodItemDto> {
    return this.http.get<FoodItemDto>(apiUrl(`/food-items/${id}`));
  }

  createFoodItem(request: CreateFoodItemRequest): Observable<FoodItemDto> {
    return this.http.post<FoodItemDto>(apiUrl('/food-items'), request);
  }

  updateFoodItem(id: string, request: UpdateFoodItemRequest): Observable<FoodItemDto> {
    return this.http.put<FoodItemDto>(apiUrl(`/food-items/${id}`), request);
  }

  deleteFoodItem(id: string, unlinkFromRecipes = false): Observable<void> {
    let params = new HttpParams();
    if (unlinkFromRecipes) {
      params = params.set('unlinkFromRecipes', 'true');
    }
    return this.http.delete<void>(apiUrl(`/food-items/${id}`), { params });
  }

  mergeFoodItems(request: MergeFoodItemsRequest): Observable<MergeFoodItemsResultDto> {
    return this.http.post<MergeFoodItemsResultDto>(apiUrl('/food-items/merge'), request);
  }

  getFoodItemRecipesPaged(
    id: string,
    page = 1,
    pageSize = 20,
  ): Observable<PagedResultDto<FoodItemRecipeUsageDto>> {
    const params = new HttpParams()
      .set('page', page.toString())
      .set('pageSize', pageSize.toString());

    return this.http.get<PagedResultDto<FoodItemRecipeUsageDto>>(
      apiUrl(`/food-items/${id}/recipes`),
      { params },
    );
  }

  getFoodItemRecipes(id: string): Observable<FoodItemRecipeUsageDto[]> {
    return this.http.get<FoodItemRecipeUsageDto[]>(apiUrl(`/food-items/${id}/recipes`));
  }

  unlinkFoodItemRecipes(id: string): Observable<UnlinkFoodItemRecipesResultDto> {
    return this.http.post<UnlinkFoodItemRecipesResultDto>(
      apiUrl(`/food-items/${id}/unlink-recipes`),
      {},
    );
  }
}
