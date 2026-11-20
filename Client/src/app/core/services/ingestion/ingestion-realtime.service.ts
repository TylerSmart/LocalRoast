import { Service, inject, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import type {
  IngestionLiveSnapshotDto,
  IngestionQueueChangedEventDto,
  IngestionRealtimeSyncState,
  LlmActiveWorkloadDto,
  LlmCallCompletedDto,
  LlmCallStartedDto,
  LlmCallStreamDeltaDto,
  LlmWorkloadMetaUpdatedDto,
} from '../../models/ingestion.models';
import { AuthService } from '../auth/auth.service';
import { IngestionService } from './ingestion.service';

const MAX_RECENT_WORKLOADS = 3;

@Service()
export class IngestionRealtimeService {
  private readonly authService = inject(AuthService);
  private readonly ingestionService = inject(IngestionService);

  private connection: HubConnection | null = null;
  private readonly dismissedRecentIds = new Set<string>();

  readonly syncState = signal<IngestionRealtimeSyncState>('disconnected');
  readonly activeWorkloads = signal<LlmActiveWorkloadDto[]>([]);
  readonly recentWorkloads = signal<LlmActiveWorkloadDto[]>([]);
  readonly queueChanged$ = new Subject<IngestionQueueChangedEventDto>();
  readonly reconnected$ = new Subject<void>();

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

    const reconnectDelaysMs = [0, 1000, 2000, 5000, 10000, 15000];
    const hub = new HubConnectionBuilder()
      .withUrl(apiUrl('/hubs/ingestion'), {
        accessTokenFactory: () => this.authService.token() ?? '',
        headers: { 'ngsw-bypass': 'true' },
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (!this.authService.token()) {
            return null;
          }
          const idx = Math.min(retryContext.previousRetryCount, reconnectDelaysMs.length - 1);
          return reconnectDelaysMs[idx];
        },
      })
      .configureLogging(LogLevel.None)
      .build();

    this.connection = hub;

    hub.on('LiveSnapshot', (snapshot: IngestionLiveSnapshotDto) => {
      this.applySnapshot(snapshot);
    });

    hub.on('WorkloadUpserted', (workload: LlmActiveWorkloadDto) => {
      this.applyWorkloadUpserted(workload);
    });

    hub.on('WorkloadMetaUpdated', (update: LlmWorkloadMetaUpdatedDto) => {
      this.applyWorkloadMetaUpdated(update);
    });

    hub.on('CallStarted', (started: LlmCallStartedDto) => {
      this.applyCallStarted(started);
    });

    hub.on('CallStreamDelta', (delta: LlmCallStreamDeltaDto) => {
      this.applyCallStreamDelta(delta);
    });

    hub.on('CallCompleted', (completed: LlmCallCompletedDto) => {
      this.applyCallCompleted(completed);
    });

    hub.on('QueueChanged', (event: IngestionQueueChangedEventDto) => {
      this.applyQueueChanged(event);
    });

    hub.onreconnecting(() => {
      this.syncState.set('reconnecting');
    });

    hub.onreconnected(async () => {
      this.syncState.set('live');
      if (this.connection?.state === HubConnectionState.Connected) {
        try {
          const snapshot =
            await this.connection.invoke<IngestionLiveSnapshotDto>('RequestSnapshot');
          if (snapshot) {
            this.applySnapshot(snapshot);
          }
        } catch {
          // Non-fatal if snapshot refresh fails during reconnect
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
    } catch {
      this.syncState.set('disconnected');
    }
  }

  async disconnect(): Promise<void> {
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

  applySnapshot(snapshot: IngestionLiveSnapshotDto): void {
    const active = (snapshot.activeWorkloads ?? []).map((w) => this.normalizeWorkload(w));
    const recent = (snapshot.recentWorkloads ?? [])
      .map((w) => this.normalizeWorkload(w))
      .filter((w) => !this.dismissedRecentIds.has(w.workloadId))
      .slice(0, MAX_RECENT_WORKLOADS);

    this.activeWorkloads.set(active);
    this.recentWorkloads.set(recent);
  }

  applyWorkloadUpserted(workload: LlmActiveWorkloadDto): void {
    const normalized = this.normalizeWorkload(workload);
    this.dismissedRecentIds.delete(normalized.workloadId);

    if (normalized.status === 'Running') {
      this.recentWorkloads.update((list) =>
        list.filter((w) => w.workloadId !== normalized.workloadId),
      );
      this.activeWorkloads.update((list) => {
        const idx = list.findIndex((w) => w.workloadId === normalized.workloadId);
        if (idx >= 0) {
          const next = [...list];
          next[idx] = normalized;
          return next;
        }
        return [...list, normalized];
      });
    } else {
      this.activeWorkloads.update((list) =>
        list.filter((w) => w.workloadId !== normalized.workloadId),
      );
      this.prependRecent(normalized);
    }
  }

  applyWorkloadMetaUpdated(update: LlmWorkloadMetaUpdatedDto): void {
    let finishedWorkload: LlmActiveWorkloadDto | null = null;

    this.activeWorkloads.update((list) => {
      const idx = list.findIndex((w) => w.workloadId === update.workloadId);
      if (idx < 0) {
        return list;
      }

      const existing = list[idx];
      const merged: LlmActiveWorkloadDto = {
        ...existing,
        status: update.status,
        title: update.title || existing.title,
        subtitle: update.subtitle ?? existing.subtitle,
        thumbnailUrl: update.thumbnailUrl ?? existing.thumbnailUrl,
        activeModel: update.activeModel ?? existing.activeModel,
        statusDetail: update.statusDetail ?? existing.statusDetail,
        completedAtUtc: update.completedAtUtc ?? existing.completedAtUtc,
        segments: update.segments ?? existing.segments,
      };

      if (merged.status !== 'Running') {
        finishedWorkload = merged;
        return list.filter((_, i) => i !== idx);
      }

      const next = [...list];
      next[idx] = merged;
      return next;
    });

    if (finishedWorkload) {
      this.prependRecent(finishedWorkload);
      return;
    }

    // Also update if already in recentWorkloads
    this.recentWorkloads.update((list) => {
      const idx = list.findIndex((w) => w.workloadId === update.workloadId);
      if (idx < 0) {
        return list;
      }
      const existing = list[idx];
      const next = [...list];
      next[idx] = {
        ...existing,
        status: update.status,
        title: update.title || existing.title,
        subtitle: update.subtitle ?? existing.subtitle,
        thumbnailUrl: update.thumbnailUrl ?? existing.thumbnailUrl,
        activeModel: update.activeModel ?? existing.activeModel,
        statusDetail: update.statusDetail ?? existing.statusDetail,
        completedAtUtc: update.completedAtUtc ?? existing.completedAtUtc,
        segments: update.segments ?? existing.segments,
      };
      return next;
    });
  }

  applyCallStarted(started: LlmCallStartedDto): void {
    const updateList = (list: LlmActiveWorkloadDto[]) =>
      list.map((w) => {
        if (w.workloadId !== started.workloadId) {
          return w;
        }
        const exists = w.calls.some((c) => c.callId === started.call.callId);
        if (exists) {
          return w;
        }
        return {
          ...w,
          activeModel: started.call.model || w.activeModel,
          calls: [...w.calls, { ...started.call }],
        };
      });

    this.activeWorkloads.update(updateList);
    this.recentWorkloads.update(updateList);
  }

  applyCallStreamDelta(delta: LlmCallStreamDeltaDto): void {
    const updateList = (list: LlmActiveWorkloadDto[]) =>
      list.map((w) => {
        if (w.workloadId !== delta.workloadId) {
          return w;
        }
        return {
          ...w,
          calls: w.calls.map((c) => {
            if (c.callId !== delta.callId) {
              return c;
            }
            return {
              ...c,
              phase: delta.phase,
              thinkingText: delta.thinkingDelta
                ? c.thinkingText + delta.thinkingDelta
                : c.thinkingText,
              outputText: delta.outputDelta ? c.outputText + delta.outputDelta : c.outputText,
              thinkingCompletedAtUtc: delta.thinkingCompletedAtUtc ?? c.thinkingCompletedAtUtc,
              evalCount: delta.evalCount ?? c.evalCount,
              tokensPerSecond: delta.tokensPerSecond ?? c.tokensPerSecond,
            };
          }),
        };
      });

    this.activeWorkloads.update(updateList);
    this.recentWorkloads.update(updateList);
  }

  applyCallCompleted(completed: LlmCallCompletedDto): void {
    const updateList = (list: LlmActiveWorkloadDto[]) =>
      list.map((w) => {
        if (w.workloadId !== completed.workloadId) {
          return w;
        }
        return {
          ...w,
          calls: w.calls.map((c) => {
            if (c.callId !== completed.callId) {
              return c;
            }
            return {
              ...c,
              phase: completed.phase,
              thinkingCompletedAtUtc: completed.thinkingCompletedAtUtc ?? c.thinkingCompletedAtUtc,
              completedAtUtc: completed.completedAtUtc,
              evalCount: completed.evalCount ?? c.evalCount,
              tokensPerSecond: completed.tokensPerSecond ?? c.tokensPerSecond,
              errorMessage: completed.errorMessage ?? c.errorMessage,
            };
          }),
        };
      });

    this.activeWorkloads.update(updateList);
    this.recentWorkloads.update(updateList);
  }

  applyQueueChanged(event: IngestionQueueChangedEventDto): void {
    if (event.summary) {
      this.ingestionService.summary.set(event.summary);
    }
    this.queueChanged$.next(event);
  }

  dismissRecentWorkload(workloadId: string): void {
    this.dismissedRecentIds.add(workloadId);
    this.recentWorkloads.update((list) => list.filter((w) => w.workloadId !== workloadId));
  }

  clearRecentWorkloads(): void {
    for (const w of this.recentWorkloads()) {
      this.dismissedRecentIds.add(w.workloadId);
    }
    this.recentWorkloads.set([]);
  }

  private prependRecent(workload: LlmActiveWorkloadDto): void {
    if (this.dismissedRecentIds.has(workload.workloadId)) {
      return;
    }
    this.recentWorkloads.update((list) => {
      const filtered = list.filter((w) => w.workloadId !== workload.workloadId);
      return [workload, ...filtered].slice(0, MAX_RECENT_WORKLOADS);
    });
  }

  private normalizeWorkload(workload: LlmActiveWorkloadDto): LlmActiveWorkloadDto {
    return {
      ...workload,
      segments: workload.segments ?? [],
      calls: workload.calls ?? [],
    };
  }
}
