import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import type {
  IngestionQueueChangedEventDto,
  LlmActiveWorkloadDto,
} from '../../models/ingestion.models';
import { AuthService } from '../auth/auth.service';
import { IngestionRealtimeService } from './ingestion-realtime.service';
import { IngestionService } from './ingestion.service';

describe('IngestionRealtimeService', () => {
  let service: IngestionRealtimeService;
  let ingestionService: IngestionService;

  const mockAuthService = {
    token: signal<string | null>('test-jwt'),
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        IngestionService,
        IngestionRealtimeService,
        { provide: AuthService, useValue: mockAuthService },
      ],
    });
    service = TestBed.inject(IngestionRealtimeService);
    ingestionService = TestBed.inject(IngestionService);
  });

  const makeWorkload = (overrides: Partial<LlmActiveWorkloadDto> = {}): LlmActiveWorkloadDto => ({
    workloadId: 'w-1',
    kind: 'RecipeIngestion',
    status: 'Running',
    title: 'Crispy Sesame Tofu',
    startedAtUtc: '2026-11-19T12:00:00Z',
    segments: [
      { key: 'preprocess', label: 'Preprocess', state: 'Completed' },
      { key: 'stage1', label: '1. Extract', state: 'Active' },
    ],
    calls: [],
    ...overrides,
  });

  it('applies snapshots, call starts, streamed thinking/output deltas, and completion cleanly', () => {
    service.applySnapshot({
      activeWorkloads: [makeWorkload()],
      recentWorkloads: [],
    });

    expect(service.activeWorkloads().length).toBe(1);

    service.applyCallStarted({
      workloadId: 'w-1',
      call: {
        callId: 'c-1',
        stageKey: 'stage1',
        stageLabel: 'Stage 1 · Extract Recipe Structure',
        model: 'gemma4:e4b',
        thinkingEnabled: true,
        phase: 'WaitingForModel',
        thinkingText: '',
        outputText: '',
        startedAtUtc: '2026-11-19T12:00:01Z',
      },
    });

    service.applyCallStreamDelta({
      workloadId: 'w-1',
      callId: 'c-1',
      phase: 'Thinking',
      thinkingDelta: 'Checking ingredient list. ',
      evalCount: 12,
      tokensPerSecond: 64.5,
    });

    service.applyCallStreamDelta({
      workloadId: 'w-1',
      callId: 'c-1',
      phase: 'GeneratingOutput',
      thinkingDelta: 'Looks complete.',
      outputDelta: '{"title":"Crispy Sesame Tofu"}',
      thinkingCompletedAtUtc: '2026-11-19T12:00:03Z',
      evalCount: 40,
      tokensPerSecond: 72.1,
    });

    service.applyCallCompleted({
      workloadId: 'w-1',
      callId: 'c-1',
      phase: 'Completed',
      thinkingCompletedAtUtc: '2026-11-19T12:00:03Z',
      completedAtUtc: '2026-11-19T12:00:05Z',
      evalCount: 45,
      tokensPerSecond: 75.0,
    });

    const call = service.activeWorkloads()[0].calls[0];
    expect(call.thinkingText).toBe('Checking ingredient list. Looks complete.');
    expect(call.outputText).toBe('{"title":"Crispy Sesame Tofu"}');
    expect(call.phase).toBe('Completed');
    expect(call.tokensPerSecond).toBe(75.0);

    service.applyWorkloadMetaUpdated({
      workloadId: 'w-1',
      status: 'Completed',
      title: 'Crispy Sesame Tofu',
      completedAtUtc: '2026-11-19T12:00:06Z',
      segments: [
        { key: 'preprocess', label: 'Preprocess', state: 'Completed' },
        { key: 'stage1', label: '1. Extract', state: 'Completed' },
      ],
    });

    expect(service.activeWorkloads().length).toBe(0);
    expect(service.recentWorkloads().length).toBe(1);
    expect(service.recentWorkloads()[0].calls[0].thinkingText).toContain(
      'Checking ingredient list.',
    );
  });

  it('updates summary signal and emits queueChanged$ events', () => {
    const received: IngestionQueueChangedEventDto[] = [];
    service.queueChanged$.subscribe((e) => received.push(e));

    service.applyQueueChanged({
      reason: 'completed',
      jobId: 'job-1',
      summary: {
        readyForReviewCount: 3,
        activeProcessingCount: 1,
        waitingForLlmHostCount: 0,
        failedCount: 0,
        allCount: 10,
        publishedCount: 6,
      },
    });

    expect(ingestionService.summary().readyForReviewCount).toBe(3);
    expect(ingestionService.summary().publishedCount).toBe(6);
    expect(received.length).toBe(1);
    expect(received[0].reason).toBe('completed');
  });

  it('dismisses recent workloads and prevents re-showing them from subsequent snapshots', () => {
    const recent = makeWorkload({ workloadId: 'w-done', status: 'Completed' });
    service.applySnapshot({ activeWorkloads: [], recentWorkloads: [recent] });
    expect(service.recentWorkloads().length).toBe(1);

    service.dismissRecentWorkload('w-done');
    expect(service.recentWorkloads().length).toBe(0);

    service.applySnapshot({ activeWorkloads: [], recentWorkloads: [recent] });
    expect(service.recentWorkloads().length).toBe(0);
  });
});
