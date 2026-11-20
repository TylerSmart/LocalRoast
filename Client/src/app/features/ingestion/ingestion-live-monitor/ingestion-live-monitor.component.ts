import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { interval } from 'rxjs';
import { APP_STORAGE_PREFIX } from '../../../app.branding';
import type {
  IngestionChannel,
  LlmActiveWorkloadDto,
  LlmCallTraceDto,
  LlmWorkflowSegmentDto,
  LlmWorkloadKind,
} from '../../../core/models/ingestion.models';
import { IngestionRealtimeService } from '../../../core/services/ingestion/ingestion-realtime.service';

@Component({
  selector: 'app-ingestion-live-monitor',
  imports: [MatButtonModule, MatCardModule, MatIconModule, MatTooltipModule],
  templateUrl: './ingestion-live-monitor.component.html',
  styleUrl: './ingestion-live-monitor.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IngestionLiveMonitorComponent {
  private readonly realtime = inject(IngestionRealtimeService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly thoughtsOpenStorageKey = `${APP_STORAGE_PREFIX}:ingestion-live-thoughts-open`;
  private readonly showJsonStorageKey = `${APP_STORAGE_PREFIX}:ingestion-live-show-json`;

  readonly streamViewport = viewChild<ElementRef<HTMLDivElement>>('streamViewport');

  readonly activeWorkloads = this.realtime.activeWorkloads;
  readonly recentWorkloads = this.realtime.recentWorkloads;
  readonly allWorkloads = computed<LlmActiveWorkloadDto[]>(() => [
    ...this.activeWorkloads(),
    ...this.recentWorkloads(),
  ]);
  readonly hasAnyWorkload = computed(() => this.allWorkloads().length > 0);
  readonly showWorkloadSelector = computed(() => this.allWorkloads().length > 1);

  readonly userSelectedWorkloadId = signal<string | null>(null);
  readonly selectedWorkload = computed<LlmActiveWorkloadDto | null>(() => {
    const all = this.allWorkloads();
    if (all.length === 0) {
      return null;
    }

    const pinnedId = this.userSelectedWorkloadId();
    if (pinnedId) {
      const match = all.find((w) => w.workloadId === pinnedId);
      if (match) {
        return match;
      }
    }

    return this.activeWorkloads()[0] ?? this.recentWorkloads()[0] ?? null;
  });

  readonly isThoughtsOpen = signal<boolean>(
    this.readBoolStorage(this.thoughtsOpenStorageKey, true),
  );
  readonly showJsonOutput = signal<boolean>(this.readBoolStorage(this.showJsonStorageKey, false));
  readonly isTallViewport = signal<boolean>(false);
  readonly isAutoScrollPaused = signal<boolean>(false);
  readonly manualCallExpandState = signal<Record<string, boolean>>({});
  readonly failedThumbIds = signal<Set<string>>(new Set());
  readonly nowMs = signal<number>(Date.now());

  readonly activeSegment = computed<LlmWorkflowSegmentDto | null>(() => {
    const workload = this.selectedWorkload();
    if (!workload || workload.segments.length === 0) {
      return null;
    }
    return (
      workload.segments.find((s) => s.state === 'Active') ??
      workload.segments.find((s) => s.state === 'Failed') ??
      workload.segments[workload.segments.length - 1]
    );
  });

  readonly activeSegmentIndex = computed<number>(() => {
    const workload = this.selectedWorkload();
    const active = this.activeSegment();
    if (!workload || !active) {
      return 0;
    }
    return Math.max(
      0,
      workload.segments.findIndex((s) => s.key === active.key),
    );
  });

  readonly completedSegmentCount = computed<number>(() => {
    const workload = this.selectedWorkload();
    if (!workload) {
      return 0;
    }
    return workload.segments.filter((s) => s.state === 'Completed' || s.state === 'Skipped').length;
  });

  readonly progressPercent = computed<number>(() => {
    const workload = this.selectedWorkload();
    if (!workload || workload.segments.length === 0) {
      return 0;
    }
    if (workload.status === 'Completed') {
      return 100;
    }
    const done = this.completedSegmentCount();
    const hasActive = workload.segments.some((s) => s.state === 'Active') ? 0.5 : 0;
    return Math.min(100, Math.round(((done + hasActive) / workload.segments.length) * 100));
  });

  readonly activeThroughput = computed<number | null>(() => {
    const workload = this.selectedWorkload();
    if (!workload || workload.calls.length === 0) {
      return null;
    }
    for (let i = workload.calls.length - 1; i >= 0; i--) {
      const tps = workload.calls[i].tokensPerSecond;
      if (tps && tps > 0) {
        return tps;
      }
    }
    return null;
  });

  readonly elapsedDisplay = computed<string>(() => {
    const workload = this.selectedWorkload();
    if (!workload) {
      return '';
    }
    const startMs = Date.parse(workload.startedAtUtc);
    if (Number.isNaN(startMs)) {
      return '';
    }
    const endMs =
      workload.status !== 'Running' && workload.completedAtUtc
        ? Date.parse(workload.completedAtUtc)
        : this.nowMs();
    const diffSeconds = Math.max(0, Math.floor((endMs - startMs) / 1000));
    return this.formatSeconds(diffSeconds);
  });

  private readonly streamFingerprint = computed(() => {
    const w = this.selectedWorkload();
    if (!w) {
      return '';
    }
    const last = w.calls[w.calls.length - 1];
    const totalChars = w.calls.reduce(
      (sum, call) => sum + call.thinkingText.length + call.outputText.length,
      0,
    );
    return `${w.workloadId}:${w.calls.length}:${last?.callId ?? ''}:${totalChars}:${last?.phase ?? ''}`;
  });

  private pendingProgrammaticScroll = false;
  private lastKnownScrollTop = 0;
  private scrollGuardFrameId: number | null = null;

  constructor() {
    interval(1000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.activeWorkloads().length > 0) {
          this.nowMs.set(Date.now());
        }
      });

    this.destroyRef.onDestroy(() => {
      if (this.scrollGuardFrameId !== null && typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(this.scrollGuardFrameId);
        this.scrollGuardFrameId = null;
      }
    });

    // Follow newly arrived stream tokens after Angular commits DOM layout,
    // unless the user scrolled up to read earlier thoughts.
    afterRenderEffect(() => {
      const fingerprint = this.streamFingerprint();
      const open = this.isThoughtsOpen();
      const paused = this.isAutoScrollPaused();
      this.showJsonOutput();
      this.isTallViewport();
      if (!fingerprint || !open || paused) {
        return;
      }
      this.scrollViewportToBottom();
    });
  }

  selectWorkload(workloadId: string): void {
    this.userSelectedWorkloadId.set(workloadId);
    this.isAutoScrollPaused.set(false);
  }

  dismissWorkload(workloadId: string): void {
    if (this.userSelectedWorkloadId() === workloadId) {
      this.userSelectedWorkloadId.set(null);
    }
    this.realtime.dismissRecentWorkload(workloadId);
  }

  toggleThoughtsOpen(): void {
    const next = !this.isThoughtsOpen();
    this.isThoughtsOpen.set(next);
    this.writeBoolStorage(this.thoughtsOpenStorageKey, next);
    if (next) {
      this.isAutoScrollPaused.set(false);
    }
  }

  toggleShowJsonOutput(): void {
    const next = !this.showJsonOutput();
    this.showJsonOutput.set(next);
    this.writeBoolStorage(this.showJsonStorageKey, next);
  }

  toggleViewportHeight(): void {
    this.isTallViewport.update((v) => !v);
  }

  isCallExpanded(call: LlmCallTraceDto, isLatest: boolean): boolean {
    const manual = this.manualCallExpandState()[call.callId];
    if (manual !== undefined) {
      return manual;
    }

    if (call.phase === 'Thinking' || call.phase === 'WaitingForModel') {
      return true;
    }

    if (call.phase === 'GeneratingOutput') {
      return !!call.thinkingText || this.showJsonOutput();
    }

    if (call.phase === 'Failed') {
      return true;
    }

    // Completed calls stay expanded if they contain thinking prose so the user can scroll through history
    if (call.thinkingText) {
      return true;
    }

    return isLatest && this.showJsonOutput();
  }

  toggleCallExpanded(call: LlmCallTraceDto, isLatest: boolean): void {
    const current = this.isCallExpanded(call, isLatest);
    this.manualCallExpandState.update((prev) => ({
      ...prev,
      [call.callId]: !current,
    }));
  }

  onViewportScroll(): void {
    const el = this.streamViewport()?.nativeElement;
    if (!el) {
      return;
    }
    const currentTop = el.scrollTop;
    const distanceFromBottom = el.scrollHeight - currentTop - el.clientHeight;

    if (this.pendingProgrammaticScroll) {
      if (currentTop >= this.lastKnownScrollTop - 2) {
        this.lastKnownScrollTop = currentTop;
        return;
      }
      this.pendingProgrammaticScroll = false;
    }

    this.lastKnownScrollTop = currentTop;
    this.isAutoScrollPaused.set(distanceFromBottom > 48);
  }

  jumpToLive(): void {
    this.isAutoScrollPaused.set(false);
    this.scrollViewportToBottom();
  }

  formatThinkingText(text: string, phase: LlmCallTraceDto['phase']): string {
    return phase === 'Thinking' ? text.trimStart() : text.trim();
  }

  formatOutputText(text: string, phase: LlmCallTraceDto['phase']): string {
    return phase === 'GeneratingOutput' ? text.trimStart() : text.trim();
  }

  onThumbError(workloadId: string): void {
    this.failedThumbIds.update((prev) => {
      const next = new Set(prev);
      next.add(workloadId);
      return next;
    });
  }

  getWorkloadIcon(kind: LlmWorkloadKind, channel?: IngestionChannel | null): string {
    switch (kind) {
      case 'JobRefinement':
      case 'RecipeRefinement':
        return 'auto_awesome';
      case 'EmbeddingSync':
        return 'hub';
      case 'MealPlanDraft':
        return 'calendar_month';
      case 'KitchenHelper':
        return 'chat';
      case 'RecipeIngestion':
      default:
        switch (channel) {
          case 'WebUrl':
            return 'link';
          case 'Images':
            return 'photo_camera';
          case 'Pdf':
            return 'picture_as_pdf';
          default:
            return 'description';
        }
    }
  }

  getKindLabel(kind: LlmWorkloadKind): string {
    switch (kind) {
      case 'RecipeIngestion':
        return 'Ingestion';
      case 'JobRefinement':
        return 'Job Refinement';
      case 'RecipeRefinement':
        return 'Recipe Refinement';
      case 'EmbeddingSync':
        return 'Embeddings';
      case 'MealPlanDraft':
        return 'Meal Planner';
      case 'KitchenHelper':
        return 'Kitchen AI';
    }
  }

  getSegmentTooltip(segment: LlmWorkflowSegmentDto): string {
    const stateLabel =
      segment.state === 'Active'
        ? 'In progress'
        : segment.state === 'Completed'
          ? 'Completed'
          : segment.state === 'Skipped'
            ? 'Skipped (not needed)'
            : segment.state === 'Failed'
              ? 'Failed'
              : 'Pending';
    return segment.subStatus
      ? `${segment.label} — ${stateLabel} (${segment.subStatus})`
      : `${segment.label} — ${stateLabel}`;
  }

  getCallPhaseSummary(call: LlmCallTraceDto): string {
    const startMs = Date.parse(call.startedAtUtc);
    const now = this.nowMs();
    const tpsSuffix = call.tokensPerSecond ? ` · ${call.tokensPerSecond.toFixed(1)} tok/s` : '';

    const thinkDur =
      call.thinkingCompletedAtUtc && !Number.isNaN(startMs)
        ? this.formatDurationDecimal(Date.parse(call.thinkingCompletedAtUtc) - startMs)
        : null;

    const totalDur =
      call.completedAtUtc && !Number.isNaN(startMs)
        ? this.formatDurationDecimal(Date.parse(call.completedAtUtc) - startMs)
        : !Number.isNaN(startMs)
          ? this.formatDurationDecimal(Math.max(0, now - startMs))
          : null;

    switch (call.phase) {
      case 'WaitingForModel':
        return totalDur ? `Waiting for model… (${totalDur})` : 'Waiting for model…';
      case 'Thinking':
        return totalDur ? `Thinking… (${totalDur})${tpsSuffix}` : `Thinking…${tpsSuffix}`;
      case 'GeneratingOutput': {
        const thoughtPrefix = thinkDur ? `Thought for ${thinkDur} · ` : '';
        const tokenInfo = call.evalCount ? `${call.evalCount} tokens` : 'structured output';
        return `${thoughtPrefix}Generating ${tokenInfo}…${tpsSuffix}`;
      }
      case 'Completed': {
        const thoughtPrefix = thinkDur && call.thinkingText ? `Thought for ${thinkDur} · ` : '';
        return `${thoughtPrefix}Done in ${totalDur ?? '0s'}${tpsSuffix}`;
      }
      case 'Failed':
        return call.errorMessage ? `Failed: ${call.errorMessage}` : 'Failed';
    }
  }

  private scrollViewportToBottom(): void {
    const el = this.streamViewport()?.nativeElement;
    if (!el) {
      return;
    }
    this.pendingProgrammaticScroll = true;
    el.scrollTop = el.scrollHeight;
    this.lastKnownScrollTop = el.scrollTop;

    if (typeof requestAnimationFrame === 'function') {
      if (this.scrollGuardFrameId !== null && typeof cancelAnimationFrame === 'function') {
        cancelAnimationFrame(this.scrollGuardFrameId);
      }
      this.scrollGuardFrameId = requestAnimationFrame(() => {
        this.scrollGuardFrameId = null;
        this.pendingProgrammaticScroll = false;
      });
    } else {
      this.pendingProgrammaticScroll = false;
    }
  }

  private formatSeconds(totalSeconds: number): string {
    if (totalSeconds >= 60) {
      const m = Math.floor(totalSeconds / 60);
      const s = totalSeconds % 60;
      return `${m}m ${s}s`;
    }
    return `${totalSeconds}s`;
  }

  private formatDurationDecimal(ms: number): string {
    const clamped = Math.max(0, ms);
    if (clamped < 10000) {
      return `${(clamped / 1000).toFixed(1)}s`;
    }
    return this.formatSeconds(Math.round(clamped / 1000));
  }

  private readBoolStorage(key: string, fallback: boolean): boolean {
    try {
      if (typeof window !== 'undefined' && window.localStorage) {
        const val = window.localStorage.getItem(key);
        if (val === 'true') return true;
        if (val === 'false') return false;
      }
    } catch {
      // Ignore storage errors
    }
    return fallback;
  }

  private writeBoolStorage(key: string, value: boolean): void {
    try {
      if (typeof window !== 'undefined' && window.localStorage) {
        window.localStorage.setItem(key, value ? 'true' : 'false');
      }
    } catch {
      // Ignore storage errors
    }
  }
}
