import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import type { LlmActiveWorkloadDto } from '../../../core/models/ingestion.models';
import { AuthService } from '../../../core/services/auth/auth.service';
import { IngestionRealtimeService } from '../../../core/services/ingestion/ingestion-realtime.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { IngestionLiveMonitorComponent } from './ingestion-live-monitor.component';

describe('IngestionLiveMonitorComponent', () => {
  let fixture: ComponentFixture<IngestionLiveMonitorComponent>;
  let component: IngestionLiveMonitorComponent;
  let realtime: IngestionRealtimeService;

  const mockAuthService = {
    token: signal<string | null>('test-token'),
  };

  beforeEach(async () => {
    window.localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [IngestionLiveMonitorComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        IngestionService,
        IngestionRealtimeService,
        { provide: AuthService, useValue: mockAuthService },
      ],
    }).compileComponents();

    realtime = TestBed.inject(IngestionRealtimeService);
    fixture = TestBed.createComponent(IngestionLiveMonitorComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  const sampleWorkload = (overrides: Partial<LlmActiveWorkloadDto> = {}): LlmActiveWorkloadDto => ({
    workloadId: 'w-1',
    kind: 'RecipeIngestion',
    status: 'Running',
    title: 'Sourdough Focaccia',
    startedAtUtc: new Date().toISOString(),
    channel: 'WebUrl',
    activeModel: 'gemma4:e4b',
    statusDetail: 'Stage 1/5: Extracting recipe structure...',
    segments: [
      { key: 'preprocess', label: 'Preprocess', state: 'Completed' },
      { key: 'stage1', label: '1. Extract', state: 'Active', subStatus: 'Attempt 1/3' },
      { key: 'stage2', label: '2. Condense', state: 'Pending' },
    ],
    calls: [
      {
        callId: 'c-1',
        stageKey: 'stage1',
        stageLabel: 'Stage 1 · Extract Recipe Structure',
        model: 'gemma4:e4b',
        thinkingEnabled: true,
        phase: 'Thinking',
        thinkingText: '1. Identify recipe title and rising times.',
        outputText: '{"title":"Sourdough Focaccia"}',
        startedAtUtc: new Date().toISOString(),
        evalCount: 24,
        tokensPerSecond: 68.4,
      },
    ],
    ...overrides,
  });

  it('hides monitor completely when there are no active or recent workloads', () => {
    expect(fixture.nativeElement.querySelector('.live-monitor-card')).toBeNull();
  });

  it('renders segmented workflow bar and streams thinking prose in chat-style history', () => {
    realtime.applySnapshot({
      activeWorkloads: [sampleWorkload()],
      recentWorkloads: [],
    });
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.monitor-title')?.textContent).toContain('Sourdough Focaccia');
    expect(host.querySelectorAll('.workflow-segment').length).toBe(3);
    expect(host.querySelector('.thinking-prose-content')?.textContent).toContain(
      'Identify recipe title',
    );
    // JSON output is hidden by default until user toggles Show JSON
    expect(host.querySelector('.output-json-pre')).toBeNull();

    component.toggleShowJsonOutput();
    fixture.detectChanges();
    expect(host.querySelector('.output-json-pre')?.textContent).toContain('Sourdough Focaccia');
  });

  it('switches between parallel active workloads via selector pills', () => {
    const w1 = sampleWorkload({ workloadId: 'w-1', title: 'Sourdough Focaccia' });
    const w2 = sampleWorkload({
      workloadId: 'w-2',
      kind: 'JobRefinement',
      title: 'Blueberry Pie',
      activeModel: 'gemma4:26b-a4b',
    });

    realtime.applySnapshot({ activeWorkloads: [w1, w2], recentWorkloads: [] });
    fixture.detectChanges();

    const pills = fixture.nativeElement.querySelectorAll('.workload-tab-pill');
    expect(pills.length).toBe(2);

    component.selectWorkload('w-2');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.monitor-title')?.textContent).toContain(
      'Blueberry Pie',
    );
  });

  it('allows dismissing a recently completed workload', () => {
    const done = sampleWorkload({
      workloadId: 'w-done',
      status: 'Completed',
      completedAtUtc: new Date().toISOString(),
    });
    realtime.applySnapshot({ activeWorkloads: [], recentWorkloads: [done] });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.dismiss-recent-btn')).toBeTruthy();
    component.dismissWorkload('w-done');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.live-monitor-card')).toBeNull();
  });
});
