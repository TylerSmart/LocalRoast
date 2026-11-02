import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  CreateShoppingListItemRequest,
  CreateShoppingListRequest,
  ShoppingListDetailDto,
  ShoppingListItemDto,
  ShoppingListSummaryDto,
} from '../../models/shopping.models';
import { ShoppingService } from './shopping.service';

describe('ShoppingService', () => {
  let service: ShoppingService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), ShoppingService],
    });

    service = TestBed.inject(ShoppingService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches shopping lists via GET /api/shopping-lists', async () => {
    const mockLists: ShoppingListSummaryDto[] = [
      {
        id: 'list-1',
        name: 'Weekly Groceries',
        isPinned: true,
        activeUncheckedCount: 3,
        checkedCount: 1,
        excludedPantryCount: 2,
        createdAt: '2026-11-02T00:00:00Z',
        modifiedAt: '2026-11-02T00:00:00Z',
      },
    ];

    const promise = firstValueFrom(service.getShoppingLists());
    const req = httpMock.expectOne('/api/shopping-lists');
    expect(req.request.method).toBe('GET');
    req.flush(mockLists);

    await expect(promise).resolves.toEqual(mockLists);
  });

  it('creates shopping list via POST /api/shopping-lists', async () => {
    const payload: CreateShoppingListRequest = { name: 'Costco', isPinned: true };
    const mockCreated: ShoppingListDetailDto = {
      id: 'list-2',
      name: 'Costco',
      isPinned: true,
      activeItems: [],
      checkedItems: [],
      excludedPantryItems: [],
      createdAt: '2026-11-02T00:00:00Z',
      modifiedAt: '2026-11-02T00:00:00Z',
    };

    const promise = firstValueFrom(service.createShoppingList(payload));
    const req = httpMock.expectOne('/api/shopping-lists');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(payload);
    req.flush(mockCreated);

    await expect(promise).resolves.toEqual(mockCreated);
  });

  it('adds item via POST /api/shopping-lists/:id/items', async () => {
    const payload: CreateShoppingListItemRequest = { displayName: 'Avocados', quantity: 3 };
    const mockItem: ShoppingListItemDto = {
      id: 'item-1',
      shoppingListId: 'list-1',
      displayName: 'Avocados',
      quantity: 3,
      unitLabel: '',
      formattedAmount: '3',
      dimension: 'Discrete',
      isPantryStapleExcluded: false,
      isChecked: false,
      sortOrder: 0,
      sources: [],
      createdAt: '2026-11-02T00:00:00Z',
      modifiedAt: '2026-11-02T00:00:00Z',
    };

    const promise = firstValueFrom(service.addItem('list-1', payload));
    const req = httpMock.expectOne('/api/shopping-lists/list-1/items');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(payload);
    req.flush(mockItem);

    await expect(promise).resolves.toEqual(mockItem);
  });

  it('sets item checked via PATCH /api/shopping-lists/:id/items/:itemId/checked', async () => {
    const mockItem: ShoppingListItemDto = {
      id: 'item-1',
      shoppingListId: 'list-1',
      displayName: 'Avocados',
      quantity: 3,
      unitLabel: '',
      formattedAmount: '3',
      dimension: 'Discrete',
      isPantryStapleExcluded: false,
      isChecked: true,
      checkedAt: '2026-11-02T12:00:00Z',
      checkedByDisplayName: 'Chef Tyler',
      sortOrder: 0,
      sources: [],
      createdAt: '2026-11-02T00:00:00Z',
      modifiedAt: '2026-11-02T12:00:00Z',
    };

    const promise = firstValueFrom(service.setItemChecked('list-1', 'item-1', true));
    const req = httpMock.expectOne('/api/shopping-lists/list-1/items/item-1/checked');
    expect(req.request.method).toBe('PATCH');
    expect(req.request.body).toEqual({ isChecked: true });
    req.flush(mockItem);

    await expect(promise).resolves.toEqual(mockItem);
  });
});
