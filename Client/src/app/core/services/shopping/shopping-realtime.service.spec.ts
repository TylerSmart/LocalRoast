import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import * as signalR from '@microsoft/signalr';
import {
  ShoppingListRealtimeEventDto,
  ShoppingListsCatalogChangedDto,
} from '../../models/shopping.models';
import { AuthService } from '../auth/auth.service';
import { ShoppingRealtimeService } from './shopping-realtime.service';

describe('ShoppingRealtimeService', () => {
  let service: ShoppingRealtimeService;
  let mockAuthService: { token: ReturnType<typeof signal<string | null>> };
  let mockConnection: {
    state: signalR.HubConnectionState;
    on: ReturnType<typeof vi.fn>;
    onreconnecting: ReturnType<typeof vi.fn>;
    onreconnected: ReturnType<typeof vi.fn>;
    onclose: ReturnType<typeof vi.fn>;
    start: ReturnType<typeof vi.fn>;
    stop: ReturnType<typeof vi.fn>;
    invoke: ReturnType<typeof vi.fn>;
  };

  let registeredHandlers: Record<string, (arg?: unknown) => void>;
  let reconnectingCallback: ((error?: Error) => void) | null;
  let reconnectedCallback: ((connectionId?: string) => void) | null;
  let closeCallback: ((error?: Error) => void) | null;

  beforeEach(() => {
    registeredHandlers = {};
    reconnectingCallback = null;
    reconnectedCallback = null;
    closeCallback = null;

    mockAuthService = {
      token: signal<string | null>('test-jwt-token'),
    };

    mockConnection = {
      state: signalR.HubConnectionState.Disconnected,
      on: vi.fn((event: string, handler: (arg?: unknown) => void) => {
        registeredHandlers[event] = handler;
        return mockConnection;
      }),
      onreconnecting: vi.fn((callback: (error?: Error) => void) => {
        reconnectingCallback = callback;
      }),
      onreconnected: vi.fn((callback: (connectionId?: string) => void) => {
        reconnectedCallback = callback;
      }),
      onclose: vi.fn((callback: (error?: Error) => void) => {
        closeCallback = callback;
      }),
      start: vi.fn(() => {
        mockConnection.state = signalR.HubConnectionState.Connected;
        return Promise.resolve();
      }),
      stop: vi.fn(() => {
        mockConnection.state = signalR.HubConnectionState.Disconnected;
        return Promise.resolve();
      }),
      invoke: vi.fn(() => Promise.resolve()),
    };

    vi.spyOn(signalR.HubConnectionBuilder.prototype, 'withUrl').mockReturnThis();
    vi.spyOn(signalR.HubConnectionBuilder.prototype, 'withAutomaticReconnect').mockReturnThis();
    vi.spyOn(signalR.HubConnectionBuilder.prototype, 'configureLogging').mockReturnThis();
    vi.spyOn(signalR.HubConnectionBuilder.prototype, 'build').mockReturnValue(
      mockConnection as unknown as signalR.HubConnection,
    );

    TestBed.configureTestingModule({
      providers: [
        ShoppingRealtimeService,
        {
          provide: AuthService,
          useValue: mockAuthService,
        },
      ],
    });

    service = TestBed.inject(ShoppingRealtimeService);
  });

  it('starts in disconnected state', () => {
    expect(service.syncState()).toBe('disconnected');
    expect(service.activeWatchedListId).toBeNull();
  });

  it('does not connect when unauthenticated', async () => {
    mockAuthService.token.set(null);
    await service.connect();
    expect(service.syncState()).toBe('disconnected');
    expect(mockConnection.start).not.toHaveBeenCalled();
  });

  it('connects successfully and enters live state', async () => {
    await service.connect();
    expect(service.syncState()).toBe('live');
    expect(mockConnection.start).toHaveBeenCalled();
  });

  it('dispatches ShoppingListUpdated payload through listUpdated$ Subject', async () => {
    await service.connect();

    const emittedEvents: ShoppingListRealtimeEventDto[] = [];
    service.listUpdated$.subscribe((event) => emittedEvents.push(event));

    const mockPayload: ShoppingListRealtimeEventDto = {
      eventType: 'ItemCheckedChanged',
      shoppingListId: 'list-123',
      shoppingList: null,
      changedItem: null,
      deletedItemId: null,
      actorUserId: 'user-1',
      actorDisplayName: 'Tyler',
      occurredAt: new Date().toISOString(),
    };

    registeredHandlers['ShoppingListUpdated'](mockPayload);
    expect(emittedEvents.length).toBe(1);
    expect(emittedEvents[0]).toEqual(mockPayload);
  });

  it('dispatches ShoppingListsCatalogChanged payload through catalogChanged$ Subject', async () => {
    await service.connect();

    const emittedEvents: ShoppingListsCatalogChangedDto[] = [];
    service.catalogChanged$.subscribe((event) => emittedEvents.push(event));

    const mockPayload: ShoppingListsCatalogChangedDto = {
      eventType: 'ListCreated',
      affectedShoppingListId: 'list-999',
      lists: [],
      occurredAt: new Date().toISOString(),
    };

    registeredHandlers['ShoppingListsCatalogChanged'](mockPayload);
    expect(emittedEvents.length).toBe(1);
    expect(emittedEvents[0]).toEqual(mockPayload);
  });

  it('handles reconnecting, reconnected, and onclose lifecycles', async () => {
    await service.connect();
    await service.watchList('list-abc');
    expect(mockConnection.invoke).toHaveBeenCalledWith('JoinShoppingList', 'list-abc');

    // Simulate network loss -> reconnecting
    reconnectingCallback?.();
    expect(service.syncState()).toBe('reconnecting');

    // Simulate network restored -> reconnected
    let reconnectedEmitted = false;
    service.reconnected$.subscribe(() => {
      reconnectedEmitted = true;
    });

    await reconnectedCallback?.('new-conn-id');
    expect(service.syncState()).toBe('live');
    expect(reconnectedEmitted).toBe(true);
    expect(mockConnection.invoke).toHaveBeenCalledWith('JoinShoppingList', 'list-abc');

    // Simulate connection closed
    closeCallback?.();
    expect(service.syncState()).toBe('disconnected');
  });

  it('switches watched lists leaving previous and joining next', async () => {
    await service.connect();
    await service.watchList('list-1');
    expect(mockConnection.invoke).toHaveBeenCalledWith('JoinShoppingList', 'list-1');

    await service.watchList('list-2');
    expect(mockConnection.invoke).toHaveBeenCalledWith('LeaveShoppingList', 'list-1');
    expect(mockConnection.invoke).toHaveBeenCalledWith('JoinShoppingList', 'list-2');
    expect(service.activeWatchedListId).toBe('list-2');
  });

  it('disconnects cleanly and resets syncState', async () => {
    await service.connect();
    await service.watchList('list-1');

    await service.disconnect();
    expect(mockConnection.stop).toHaveBeenCalled();
    expect(service.syncState()).toBe('disconnected');
    expect(service.activeWatchedListId).toBeNull();
  });

  it('handles connection start failure gracefully', async () => {
    mockConnection.start.mockReturnValue(Promise.reject(new Error('Connection failed')));

    await service.connect();
    expect(service.syncState()).toBe('disconnected');
  });
});
