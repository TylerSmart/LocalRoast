import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  CreateFoodItemRequest,
  FoodItemDto,
  FoodItemQueryParameters,
  MergeFoodItemsRequest,
  MergeFoodItemsResultDto,
  StoreAisleDto,
  UpdateFoodItemRequest,
} from '../../models/food-catalog.models';

@Service()
export class FoodCatalogService {
  private readonly http = inject(HttpClient);

  getStoreAisles(): Observable<StoreAisleDto[]> {
    return this.http.get<StoreAisleDto[]>(apiUrl('/store-aisles'));
  }

  getFoodItems(query?: FoodItemQueryParameters): Observable<FoodItemDto[]> {
    let params = new HttpParams();
    if (query?.search) {
      params = params.set('search', query.search);
    }
    if (query?.storeAisleId) {
      params = params.set('storeAisleId', query.storeAisleId);
    }
    if (query?.isPantryStaple !== undefined && query?.isPantryStaple !== null) {
      params = params.set('isPantryStaple', query.isPantryStaple.toString());
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

  deleteFoodItem(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/food-items/${id}`));
  }

  mergeFoodItems(request: MergeFoodItemsRequest): Observable<MergeFoodItemsResultDto> {
    return this.http.post<MergeFoodItemsResultDto>(apiUrl('/food-items/merge'), request);
  }
}
