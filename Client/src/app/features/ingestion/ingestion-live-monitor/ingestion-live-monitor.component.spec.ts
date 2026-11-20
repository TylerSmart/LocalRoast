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

  it('trims leading blank lines in thinking prose without losing internal formatting', () => {
    const workload = sampleWorkload({
      calls: [
        {
          callId: 'c-done',
          stageKey: 'stage1',
          stageLabel: 'Stage 1 · Extract',
          model: 'gemma4:e4b',
          thinkingEnabled: true,
          phase: 'Completed',
          thinkingText: '\n\nFirst line.\nSecond line.\n\n',
          outputText: '\n{"title":"Focaccia"}\n',
          startedAtUtc: new Date().toISOString(),
          completedAtUtc: new Date().toISOString(),
        },
        {
          callId: 'c-live',
          stageKey: 'stage2',
          stageLabel: 'Stage 2 · Condense',
          model: 'gemma4:e4b',
          thinkingEnabled: true,
          phase: 'Thinking',
          thinkingText: '\n\nLive thought line ',
          outputText: '',
          startedAtUtc: new Date().toISOString(),
        },
      ],
    });

    realtime.applySnapshot({ activeWorkloads: [workload], recentWorkloads: [] });
    component.toggleShowJsonOutput();
    fixture.detectChanges();

    const proseEls = fixture.nativeElement.querySelectorAll('.thinking-prose-text');
    expect(proseEls.length).toBe(2);
    expect(proseEls[0].textContent).toBe('First line.\nSecond line.');
    expect(proseEls[1].textContent).toBe('Live thought line ');

    const jsonEl = fixture.nativeElement.querySelector('.output-json-code');
    expect(jsonEl?.textContent).toBe('{"title":"Focaccia"}');
  });

  it('pauses auto-scroll when user scrolls up and resumes on jump to live', () => {
    realtime.applySnapshot({ activeWorkloads: [sampleWorkload()], recentWorkloads: [] });
    fixture.detectChanges();

    const viewport = fixture.nativeElement.querySelector('.stream-viewport') as HTMLDivElement;
    expect(viewport).toBeTruthy();

    Object.defineProperty(viewport, 'scrollHeight', { value: 900, configurable: true });
    Object.defineProperty(viewport, 'clientHeight', { value: 300, configurable: true });

    // Start scrolled to bottom, then simulate user scrolling upward
    viewport.scrollTop = 600;
    component.onViewportScroll();
    expect(component.isAutoScrollPaused()).toBe(false);

    viewport.scrollTop = 120;
    component.onViewportScroll();
    fixture.detectChanges();
    expect(component.isAutoScrollPaused()).toBe(true);
    expect(fixture.nativeElement.querySelector('.jump-to-live-btn')).toBeTruthy();

    // Clicking Jump to live resumes live following and scrolls back to bottom
    component.jumpToLive();
    fixture.detectChanges();
    expect(component.isAutoScrollPaused()).toBe(false);
    expect(viewport.scrollTop).toBe(900);
    expect(fixture.nativeElement.querySelector('.jump-to-live-btn')).toBeNull();
  });

  it('toggles fixed tall class on stream viewport via height toggle button', () => {
    realtime.applySnapshot({ activeWorkloads: [sampleWorkload()], recentWorkloads: [] });
    fixture.detectChanges();

    const viewport = fixture.nativeElement.querySelector('.stream-viewport') as HTMLDivElement;
    const toggleBtn = fixture.nativeElement.querySelector(
      '.height-toggle-btn',
    ) as HTMLButtonElement;

    expect(viewport.classList.contains('tall')).toBe(false);
    expect(toggleBtn.getAttribute('aria-label')).toBe('Expand stream height');

    toggleBtn.click();
    fixture.detectChanges();

    expect(viewport.classList.contains('tall')).toBe(true);
    expect(toggleBtn.getAttribute('aria-label')).toBe('Compact stream height');

    toggleBtn.click();
    fixture.detectChanges();

    expect(viewport.classList.contains('tall')).toBe(false);
  });
});
