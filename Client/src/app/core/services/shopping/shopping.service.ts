import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  AddRecipeToShoppingListRequest,
  ClearCheckedItemsResultDto,
  CreateShoppingListItemRequest,
  CreateShoppingListRequest,
  GenerateFromMealPlanRequest,
  SetShoppingListItemCheckedRequest,
  ShoppingListDetailDto,
  ShoppingListGenerationPreviewDto,
  ShoppingListGenerationResultDto,
  ShoppingListItemDto,
  ShoppingListSummaryDto,
  UpdateShoppingListItemRequest,
  UpdateShoppingListRequest,
} from '../../models/shopping.models';

@Service()
export class ShoppingService {
  private readonly http = inject(HttpClient);

  getShoppingLists(): Observable<ShoppingListSummaryDto[]> {
    return this.http.get<ShoppingListSummaryDto[]>(apiUrl('/shopping-lists'));
  }

  getShoppingListById(id: string): Observable<ShoppingListDetailDto> {
    return this.http.get<ShoppingListDetailDto>(apiUrl(`/shopping-lists/${id}`));
  }

  createShoppingList(request: CreateShoppingListRequest): Observable<ShoppingListDetailDto> {
    return this.http.post<ShoppingListDetailDto>(apiUrl('/shopping-lists'), request);
  }

  updateShoppingList(
    id: string,
    request: UpdateShoppingListRequest,
  ): Observable<ShoppingListDetailDto> {
    return this.http.put<ShoppingListDetailDto>(apiUrl(`/shopping-lists/${id}`), request);
  }

  deleteShoppingList(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/shopping-lists/${id}`));
  }

  addItem(listId: string, request: CreateShoppingListItemRequest): Observable<ShoppingListItemDto> {
    return this.http.post<ShoppingListItemDto>(apiUrl(`/shopping-lists/${listId}/items`), request);
  }

  updateItem(
    listId: string,
    itemId: string,
    request: UpdateShoppingListItemRequest,
  ): Observable<ShoppingListItemDto> {
    return this.http.put<ShoppingListItemDto>(
      apiUrl(`/shopping-lists/${listId}/items/${itemId}`),
      request,
    );
  }

  setItemChecked(
    listId: string,
    itemId: string,
    isChecked: boolean,
  ): Observable<ShoppingListItemDto> {
    const payload: SetShoppingListItemCheckedRequest = { isChecked };
    return this.http.patch<ShoppingListItemDto>(
      apiUrl(`/shopping-lists/${listId}/items/${itemId}/checked`),
      payload,
    );
  }

  promotePantryStaple(listId: string, itemId: string): Observable<ShoppingListItemDto> {
    return this.http.post<ShoppingListItemDto>(
      apiUrl(`/shopping-lists/${listId}/items/${itemId}/promote-staple`),
      {},
    );
  }

  deleteItem(listId: string, itemId: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/shopping-lists/${listId}/items/${itemId}`));
  }

  clearCheckedItems(listId: string): Observable<ClearCheckedItemsResultDto> {
    return this.http.delete<ClearCheckedItemsResultDto>(
      apiUrl(`/shopping-lists/${listId}/items/checked`),
    );
  }

  previewGenerateFromMealPlan(
    listId: string,
    request: GenerateFromMealPlanRequest,
  ): Observable<ShoppingListGenerationPreviewDto> {
    return this.http.post<ShoppingListGenerationPreviewDto>(
      apiUrl(`/shopping-lists/${listId}/generate/meal-plan/preview`),
      request,
    );
  }

  generateFromMealPlan(
    listId: string,
    request: GenerateFromMealPlanRequest,
  ): Observable<ShoppingListGenerationResultDto> {
    return this.http.post<ShoppingListGenerationResultDto>(
      apiUrl(`/shopping-lists/${listId}/generate/meal-plan`),
      request,
    );
  }

  addRecipeToList(
    listId: string,
    request: AddRecipeToShoppingListRequest,
  ): Observable<ShoppingListGenerationResultDto> {
    return this.http.post<ShoppingListGenerationResultDto>(
      apiUrl(`/shopping-lists/${listId}/generate/recipe`),
      request,
    );
  }
}
