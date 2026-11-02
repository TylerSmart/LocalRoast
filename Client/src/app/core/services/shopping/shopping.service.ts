import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable, Subject, tap } from 'rxjs';
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

export interface CrossTabShoppingSyncEvent {
  listId?: string;
  timestamp: number;
}

const SYNC_CHANNEL_NAME = 'localroast-shopping-sync';
const SYNC_STORAGE_KEY = 'localroast-shopping-sync-event';

@Service()
export class ShoppingService {
  private readonly http = inject(HttpClient);
  private broadcastChannel: BroadcastChannel | null = null;

  readonly crossTabSync$ = new Subject<CrossTabShoppingSyncEvent>();

  constructor() {
    this.initCrossTabSync();
  }

  private initCrossTabSync(): void {
    try {
      if (typeof BroadcastChannel !== 'undefined') {
        this.broadcastChannel = new BroadcastChannel(SYNC_CHANNEL_NAME);
        this.broadcastChannel.onmessage = (event: MessageEvent<CrossTabShoppingSyncEvent>) => {
          if (event?.data) {
            this.crossTabSync$.next(event.data);
          }
        };
      }
    } catch {
      // BroadcastChannel unsupported
    }

    try {
      if (typeof window !== 'undefined' && typeof window.addEventListener === 'function') {
        window.addEventListener('storage', (event: StorageEvent) => {
          if (event.key === SYNC_STORAGE_KEY && event.newValue) {
            try {
              const parsed = JSON.parse(event.newValue) as CrossTabShoppingSyncEvent;
              this.crossTabSync$.next(parsed);
            } catch {
              // Ignore malformed storage payload
            }
          }
        });
      }
    } catch {
      // Ignore storage listener errors
    }
  }

  private notifyCrossTab(listId?: string): void {
    const payload: CrossTabShoppingSyncEvent = {
      listId,
      timestamp: Date.now(),
    };

    try {
      this.broadcastChannel?.postMessage(payload);
    } catch {
      // Ignore BroadcastChannel post error
    }

    try {
      if (typeof localStorage !== 'undefined') {
        localStorage.setItem(SYNC_STORAGE_KEY, JSON.stringify(payload));
      }
    } catch {
      // Ignore localStorage quota/access errors
    }
  }

  getShoppingLists(): Observable<ShoppingListSummaryDto[]> {
    return this.http.get<ShoppingListSummaryDto[]>(apiUrl('/shopping-lists'));
  }

  getShoppingListById(id: string): Observable<ShoppingListDetailDto> {
    return this.http.get<ShoppingListDetailDto>(apiUrl(`/shopping-lists/${id}`));
  }

  createShoppingList(request: CreateShoppingListRequest): Observable<ShoppingListDetailDto> {
    return this.http
      .post<ShoppingListDetailDto>(apiUrl('/shopping-lists'), request)
      .pipe(tap((res) => this.notifyCrossTab(res.id)));
  }

  updateShoppingList(
    id: string,
    request: UpdateShoppingListRequest,
  ): Observable<ShoppingListDetailDto> {
    return this.http
      .put<ShoppingListDetailDto>(apiUrl(`/shopping-lists/${id}`), request)
      .pipe(tap(() => this.notifyCrossTab(id)));
  }

  deleteShoppingList(id: string): Observable<void> {
    return this.http
      .delete<void>(apiUrl(`/shopping-lists/${id}`))
      .pipe(tap(() => this.notifyCrossTab(id)));
  }

  addItem(listId: string, request: CreateShoppingListItemRequest): Observable<ShoppingListItemDto> {
    return this.http
      .post<ShoppingListItemDto>(apiUrl(`/shopping-lists/${listId}/items`), request)
      .pipe(tap(() => this.notifyCrossTab(listId)));
  }

  updateItem(
    listId: string,
    itemId: string,
    request: UpdateShoppingListItemRequest,
  ): Observable<ShoppingListItemDto> {
    return this.http
      .put<ShoppingListItemDto>(apiUrl(`/shopping-lists/${listId}/items/${itemId}`), request)
      .pipe(tap(() => this.notifyCrossTab(listId)));
  }

  setItemChecked(
    listId: string,
    itemId: string,
    isChecked: boolean,
  ): Observable<ShoppingListItemDto> {
    const payload: SetShoppingListItemCheckedRequest = { isChecked };
    return this.http
      .patch<ShoppingListItemDto>(
        apiUrl(`/shopping-lists/${listId}/items/${itemId}/checked`),
        payload,
      )
      .pipe(tap(() => this.notifyCrossTab(listId)));
  }

  promotePantryStaple(listId: string, itemId: string): Observable<ShoppingListItemDto> {
    return this.http
      .post<ShoppingListItemDto>(
        apiUrl(`/shopping-lists/${listId}/items/${itemId}/promote-staple`),
        {},
      )
      .pipe(tap(() => this.notifyCrossTab(listId)));
  }

  deleteItem(listId: string, itemId: string): Observable<void> {
    return this.http
      .delete<void>(apiUrl(`/shopping-lists/${listId}/items/${itemId}`))
      .pipe(tap(() => this.notifyCrossTab(listId)));
  }

  clearCheckedItems(listId: string): Observable<ClearCheckedItemsResultDto> {
    return this.http
      .delete<ClearCheckedItemsResultDto>(apiUrl(`/shopping-lists/${listId}/items/checked`))
      .pipe(tap(() => this.notifyCrossTab(listId)));
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
    return this.http
      .post<ShoppingListGenerationResultDto>(
        apiUrl(`/shopping-lists/${listId}/generate/meal-plan`),
        request,
      )
      .pipe(tap(() => this.notifyCrossTab(listId)));
  }

  addRecipeToList(
    listId: string,
    request: AddRecipeToShoppingListRequest,
  ): Observable<ShoppingListGenerationResultDto> {
    return this.http
      .post<ShoppingListGenerationResultDto>(
        apiUrl(`/shopping-lists/${listId}/generate/recipe`),
        request,
      )
      .pipe(tap(() => this.notifyCrossTab(listId)));
  }
}
