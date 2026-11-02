import { Service, inject, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  RealtimeSyncState,
  ShoppingListRealtimeEventDto,
  ShoppingListsCatalogChangedDto,
} from '../../models/shopping.models';
import { AuthService } from '../auth/auth.service';

@Service()
export class ShoppingRealtimeService {
  private readonly authService = inject(AuthService);

  private connection: HubConnection | null = null;
  private currentListId: string | null = null;

  readonly syncState = signal<RealtimeSyncState>('disconnected');
  readonly listUpdated$ = new Subject<ShoppingListRealtimeEventDto>();
  readonly catalogChanged$ = new Subject<ShoppingListsCatalogChangedDto>();
  readonly reconnected$ = new Subject<void>();

  get activeWatchedListId(): string | null {
    return this.currentListId;
  }

  async connect(): Promise<void> {
    const token = this.authService.token();
    if (!token) {
      this.syncState.set('disconnected');
      return;
    }

    if (
      this.connection &&
      (this.connection.state === HubConnectionState.Connected ||
        this.connection.state === HubConnectionState.Connecting ||
        this.connection.state === HubConnectionState.Reconnecting)
    ) {
      return;
    }

    this.syncState.set('connecting');

    const builder = new HubConnectionBuilder()
      .withUrl(apiUrl('/hubs/shopping'), {
        accessTokenFactory: () => this.authService.token() ?? '',
      })
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 15000])
      .configureLogging(LogLevel.None);

    const hub = builder.build();
    this.connection = hub;

    hub.on('ShoppingListUpdated', (payload: ShoppingListRealtimeEventDto) => {
      this.listUpdated$.next(payload);
    });

    hub.on('ShoppingListsCatalogChanged', (payload: ShoppingListsCatalogChangedDto) => {
      this.catalogChanged$.next(payload);
    });

    hub.onreconnecting(() => {
      this.syncState.set('reconnecting');
    });

    hub.onreconnected(async () => {
      this.syncState.set('live');
      if (this.currentListId && this.connection?.state === HubConnectionState.Connected) {
        try {
          await this.connection.invoke('JoinShoppingList', this.currentListId);
        } catch {
          // Non-fatal if join fails during reconnect
        }
      }
      this.reconnected$.next();
    });

    hub.onclose(() => {
      this.syncState.set('disconnected');
    });

    try {
      await hub.start();
      this.syncState.set('live');
      if (this.currentListId && hub.state === HubConnectionState.Connected) {
        try {
          await hub.invoke('JoinShoppingList', this.currentListId);
        } catch {
          // Non-fatal
        }
      }
    } catch {
      this.syncState.set('disconnected');
    }
  }

  async watchList(listId: string | null): Promise<void> {
    const previousListId = this.currentListId;
    this.currentListId = listId;

    if (this.connection?.state === HubConnectionState.Connected) {
      if (previousListId && previousListId !== listId) {
        try {
          await this.connection.invoke('LeaveShoppingList', previousListId);
        } catch {
          // Ignore transient invoke failure
        }
      }
      if (listId && listId !== previousListId) {
        try {
          await this.connection.invoke('JoinShoppingList', listId);
        } catch {
          // Ignore transient invoke failure
        }
      }
    }
  }

  async disconnect(): Promise<void> {
    this.currentListId = null;
    if (this.connection) {
      const conn = this.connection;
      this.connection = null;
      try {
        await conn.stop();
      } catch {
        // Ignore stop error
      }
    }
    this.syncState.set('disconnected');
  }
}
