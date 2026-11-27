import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, Subject, of } from 'rxjs';
import { signal, Signal, WritableSignal } from '@angular/core';
import type {
  IngestionJobSummaryDto,
  IngestionJobStatus,
  IngestionQueueChangedEventDto,
  IngestionQueueSummaryDto,
  IngestionRealtimeSyncState,
  LlmActiveWorkloadDto,
} from '../../../core/models/ingestion.models';
import { IngestionRealtimeService } from '../../../core/services/ingestion/ingestion-realtime.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { IngestionHubComponent } from './ingestion-hub.component';

describe('IngestionHubComponent', () => {
  let component: IngestionHubComponent;
  let fixture: ComponentFixture<IngestionHubComponent>;
  let mockIngestionService: {
    getJobs: ReturnType<typeof vi.fn>;
    getJobsPaged: ReturnType<typeof vi.fn>;
    summary: Signal<IngestionQueueSummaryDto>;
    enqueueUrls: ReturnType<typeof vi.fn>;
    enqueueRawText: ReturnType<typeof vi.fn>;
    retryJob: ReturnType<typeof vi.fn>;
    runJobNow: ReturnType<typeof vi.fn>;
    refineJobNow: ReturnType<typeof vi.fn>;
    retryAllWaitingJobs: ReturnType<typeof vi.fn>;
    retryBatchJobs: ReturnType<typeof vi.fn>;
    discardJob: ReturnType<typeof vi.fn>;
    refreshSummary: ReturnType<typeof vi.fn>;
  };
  let mockLlmSettingsService: {
    openLlmSettingsDialog: ReturnType<typeof vi.fn>;
    getSettings: ReturnType<typeof vi.fn>;
    setIngestionEnabled: ReturnType<typeof vi.fn>;
  };
  let mockIngestionRealtimeService: {
    syncState: WritableSignal<IngestionRealtimeSyncState>;
    activeWorkloads: WritableSignal<LlmActiveWorkloadDto[]>;
    recentWorkloads: WritableSignal<LlmActiveWorkloadDto[]>;
    queueChanged$: Subject<IngestionQueueChangedEventDto>;
    reconnected$: Subject<void>;
    connect: ReturnType<typeof vi.fn>;
    disconnect: ReturnType<typeof vi.fn>;
    dismissRecentWorkload: ReturnType<typeof vi.fn>;
  };

  const sampleJobs: IngestionJobSummaryDto[] = [
    {
      id: 'job-1',
      channel: 'WebUrl',
      status: 'ReadyForReview',
      displayTitle: 'Crispy Sesame Tofu',
      sourceUrl: 'https://example.com/tofu',
      thumbnailImagePath: '/api/uploads/thumb1.webp',
      confidenceScore: 0.95,
      statusDetail: 'Ready for side-by-side review.',
      attemptCount: 1,
      nextRetryAt: null,
      resultingRecipeId: null,
      resultingRecipeSlug: null,
      submittedByUsername: 'alice',
      createdAt: '2026-10-26T12:00:00Z',
      modifiedAt: '2026-10-26T12:00:00Z',
    },
    {
      id: 'job-2',
      channel: 'RawText',
      status: 'WaitingForLlmHost',
      displayTitle: 'Grandma Italian Meatballs',
      sourceUrl: null,
      thumbnailImagePath: null,
      confidenceScore: null,
      statusDetail: 'LLM host is offline.',
      attemptCount: 2,
      nextRetryAt: '2026-10-26T12:15:00Z',
      resultingRecipeId: null,
      resultingRecipeSlug: null,
      submittedByUsername: 'alice',
      createdAt: '2026-10-26T12:05:00Z',
      modifiedAt: '2026-10-26T12:05:00Z',
    },
    {
      id: 'job-3',
      channel: 'WebUrl',
      status: 'Published',
      displayTitle: 'Published Herb Pasta',
      sourceUrl: 'https://example.com/pasta',
      thumbnailImagePath: null,
      confidenceScore: 0.98,
      statusDetail: 'Approved and published to cookbook.',
      attemptCount: 1,
      nextRetryAt: null,
      resultingRecipeId: 'recipe-123',
      resultingRecipeSlug: 'published-herb-pasta',
      extractedModel: 'llama3:8b',
      refinedModel: 'gemma4:26b-a4b',
      refinedAt: '2026-10-26T12:12:00Z',
      submittedByUsername: 'alice',
      createdAt: '2026-10-26T12:10:00Z',
      modifiedAt: '2026-10-26T12:10:00Z',
    },
  ];

  beforeEach(async () => {
    localStorage.clear();
    mockIngestionService = {
      getJobs: vi.fn().mockReturnValue(of(sampleJobs)),
      getJobsPaged: vi
        .fn()
        .mockImplementation(
          (
            status?: IngestionJobStatus | null,
            search?: string | null,
            page?: number | null,
            pageSize?: number | null,
            activeWaiting?: boolean | null,
          ) => {
            let filtered = sampleJobs;
            if (status) {
              filtered = filtered.filter((j) => j.status === status);
            } else if (activeWaiting) {
              filtered = filtered.filter(
                (j) =>
                  j.status === 'Queued' ||
                  j.status === 'Preprocessing' ||
                  j.status === 'Extracting' ||
                  j.status === 'WaitingForLlmHost',
              );
            }
            if (search) {
              const s = search.toLowerCase();
              filtered = filtered.filter(
                (j) =>
                  j.displayTitle.toLowerCase().includes(s) ||
                  (j.sourceUrl && j.sourceUrl.toLowerCase().includes(s)) ||
                  (j.extractedModel && j.extractedModel.toLowerCase().includes(s)),
              );
            }
            return of({
              items: filtered,
              totalCount: filtered.length,
              pageNumber: page || 1,
              pageSize: pageSize || 25,
              totalPages: 1,
              hasPreviousPage: false,
              hasNextPage: false,
            });
          },
        ),
      summary: signal({
        readyForReviewCount: 1,
        activeProcessingCount: 0,
        waitingForLlmHostCount: 1,
        failedCount: 0,
        allCount: 3,
        publishedCount: 1,
      }),
      enqueueUrls: vi.fn().mockReturnValue(of([sampleJobs[0]])),
      enqueueRawText: vi.fn().mockReturnValue(of(sampleJobs[1])),
      retryJob: vi.fn().mockReturnValue(of(sampleJobs[1])),
      runJobNow: vi.fn().mockReturnValue(of(sampleJobs[1])),
      refineJobNow: vi
        .fn()
        .mockReturnValue(of({ ...sampleJobs[0], refinementRunNowRequested: true })),
      retryAllWaitingJobs: vi.fn().mockReturnValue(of({ requeuedCount: 1 })),
      retryBatchJobs: vi.fn().mockReturnValue(of({ requeuedCount: 3 })),
      discardJob: vi.fn().mockReturnValue(of(undefined)),
      refreshSummary: vi.fn(),
    };
    mockLlmSettingsService = {
      openLlmSettingsDialog: vi.fn(),
      getSettings: vi.fn().mockReturnValue(
        of({
          provider: 'Ollama',
          baseUrl: 'http://localhost:11434',
          hasApiKey: false,
          apiKeyMasked: null,
          textModel: 'qwen2.5:14b',
          visionModel: null,
          ollamaKeepAlive: '5m',
          requestTimeoutSeconds: 120,
          autoPublishHighConfidenceText: false,
          enableThinking: true,
          enableIngestion: true,
          maxIngestionRetries: 3,
          modifiedAt: null,
        }),
      ),
      setIngestionEnabled: vi.fn().mockImplementation((enabled: boolean) =>
        of({
          provider: 'Ollama',
          baseUrl: 'http://localhost:11434',
          hasApiKey: false,
          apiKeyMasked: null,
          textModel: 'qwen2.5:14b',
          visionModel: null,
          ollamaKeepAlive: '5m',
          requestTimeoutSeconds: 120,
          autoPublishHighConfidenceText: false,
          enableThinking: true,
          enableIngestion: enabled,
          maxIngestionRetries: 3,
          modifiedAt: null,
        }),
      ),
    };

    mockIngestionRealtimeService = {
      syncState: signal<IngestionRealtimeSyncState>('disconnected'),
      activeWorkloads: signal<LlmActiveWorkloadDto[]>([]),
      recentWorkloads: signal<LlmActiveWorkloadDto[]>([]),
      queueChanged$: new Subject<IngestionQueueChangedEventDto>(),
      reconnected$: new Subject<void>(),
      connect: vi.fn().mockResolvedValue(undefined),
      disconnect: vi.fn().mockResolvedValue(undefined),
      dismissRecentWorkload: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [IngestionHubComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: IngestionRealtimeService, useValue: mockIngestionRealtimeService },
        { provide: LlmSettingsService, useValue: mockLlmSettingsService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(IngestionHubComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders loaded jobs and computes review and waiting counts', () => {
    expect(mockIngestionService.getJobsPaged).toHaveBeenCalled();
    expect(component.jobs()).toHaveLength(3);
    expect(component.reviewCount()).toBe(1);
    expect(component.waitingJobs()).toHaveLength(1);
    expect(component.publishedCount()).toBe(1);
  });

  it('filters jobs by status pill', () => {
    component.setFilter('ReadyForReview');
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-1');

    component.setFilter('ActiveWaiting');
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-2');

    component.setFilter('Published');
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-3');

    component.setFilter('All');
    expect(component.filteredJobs()).toHaveLength(3);
  });

  it('filters jobs by search query matching title or url and clears search', () => {
    component.setFilter('All');
    expect(component.filteredJobs()).toHaveLength(3);

    // Search by title
    component.searchQuery.set('Tofu');
    component.loadJobs();
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-1');

    // Search by URL (specific to /tofu)
    component.searchQuery.set('/tofu');
    component.loadJobs();
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-1');

    // Search by shared domain (example.com)
    component.searchQuery.set('example.com');
    component.loadJobs();
    expect(component.filteredJobs()).toHaveLength(2);

    // Search by model
    component.searchQuery.set('llama3:8b');
    component.loadJobs();
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-3');

    // Search with non-matching query
    component.searchQuery.set('NonExistentJob');
    component.loadJobs();
    expect(component.filteredJobs()).toHaveLength(0);

    // Clear search
    component.clearSearch();
    expect(component.searchQuery()).toBe('');
    expect(component.filteredJobs()).toHaveLength(3);
  });

  it('enqueues web URLs and clears form', () => {
    component.urlForm.controls.urlsText.setValue(
      'https://example.com/tofu\nhttps://example.com/soup',
    );
    component.submitWebUrls();

    expect(mockIngestionService.enqueueUrls).toHaveBeenCalledWith({
      urls: ['https://example.com/tofu', 'https://example.com/soup'],
    });
    expect(component.urlForm.controls.urlsText.value).toBe('');
  });

  it('enqueues raw text recipe and clears form', () => {
    component.setActiveTab('text');
    component.textForm.controls.rawText.setValue('Raw ingredients and instructions');
    component.textForm.controls.titleHint.setValue('Sample Recipe');
    component.submitRawText();

    expect(mockIngestionService.enqueueRawText).toHaveBeenCalledWith({
      rawText: 'Raw ingredients and instructions',
      titleHint: 'Sample Recipe',
    });
    expect(component.textForm.controls.rawText.value).toBe('');
  });

  it('retries all waiting jobs via banner action', () => {
    component.retryAllWaiting();
    expect(mockIngestionService.retryAllWaitingJobs).toHaveBeenCalled();
  });

  it('retries an individual waiting or failed job', () => {
    component.retryJob(sampleJobs[1]);
    expect(mockIngestionService.retryJob).toHaveBeenCalledWith('job-2');
  });

  it('discards waiting, review, and published jobs and renders overflow menu buttons for all statuses', () => {
    const menuButtons = fixture.nativeElement.querySelectorAll('button.job-actions-menu-btn');
    expect(menuButtons.length).toBe(3);

    (menuButtons[0] as HTMLButtonElement).click();
    fixture.detectChanges();
    const discardBtn = document.querySelector<HTMLButtonElement>('button.discard-btn');
    expect(discardBtn).toBeTruthy();

    component.discardJob(sampleJobs[1]);
    expect(mockIngestionService.discardJob).toHaveBeenCalledWith('job-2');

    component.discardJob(sampleJobs[2]);
    expect(mockIngestionService.discardJob).toHaveBeenCalledWith('job-3');
  });

  it('opens LLM provider settings dialog', () => {
    component.openLlmSettings();
    expect(mockLlmSettingsService.openLlmSettingsDialog).toHaveBeenCalled();
  });
  it('displays extracted model chip when present on job', () => {
    const chips = fixture.nativeElement.querySelectorAll('.model-chip');
    expect(chips.length).toBeGreaterThan(0);
    expect(chips[0].textContent).toContain('llama3:8b');
  });

  it('retries published job when rerun button is clicked', () => {
    component.retryJob(sampleJobs[2]);
    expect(mockIngestionService.retryJob).toHaveBeenCalledWith('job-3');
  });

  it('initializes with default 15-second refresh interval and countdown progress', () => {
    expect(component.refreshInterval()).toBe(15);
    expect(component.remainingSeconds()).toBe(15);
    expect(component.countdownProgress()).toBe(100);
    expect(component.remainingTimeDisplay()).toBe('15s');
    expect(component.refreshOptions).toHaveLength(6);
  });

  it('triggers refresh and resets countdown on manualRefresh', () => {
    mockIngestionService.getJobsPaged.mockClear();
    mockIngestionService.refreshSummary.mockClear();

    component.remainingSeconds.set(5);
    component.manualRefresh();

    expect(mockIngestionService.getJobsPaged).toHaveBeenCalled();
    expect(mockIngestionService.refreshSummary).toHaveBeenCalled();
    expect(component.remainingSeconds()).toBe(15);
  });

  it('updates interval, remaining seconds, countdown progress, and persists to localStorage on setRefreshInterval', () => {
    component.setRefreshInterval(30);
    expect(component.refreshInterval()).toBe(30);
    expect(component.remainingSeconds()).toBe(30);
    expect(component.countdownProgress()).toBe(100);
    expect(component.remainingTimeDisplay()).toBe('30s');

    component.remainingSeconds.set(6);
    expect(component.countdownProgress()).toBe(20);
    expect(component.remainingTimeDisplay()).toBe('6s');

    // Switch to manual mode
    component.setRefreshInterval(0);
    expect(component.refreshInterval()).toBe(0);
    expect(component.remainingSeconds()).toBe(0);
    expect(component.countdownProgress()).toBe(0);
    expect(component.remainingTimeDisplay()).toBe('Manual');
  });

  it('toggles auto-refresh on and off via toggleAutoRefresh', () => {
    expect(component.refreshInterval()).toBe(15);
    component.toggleAutoRefresh();
    expect(component.refreshInterval()).toBe(0);
    expect(component.isAutoRefreshEnabled()).toBe(false);

    component.toggleAutoRefresh();
    expect(component.refreshInterval()).toBe(15);
    expect(component.isAutoRefreshEnabled()).toBe(true);
  });

  it('formats remaining time with minutes when interval is 1 minute or more', () => {
    component.setRefreshInterval(120);
    expect(component.remainingTimeDisplay()).toBe('2m');

    component.remainingSeconds.set(95);
    expect(component.remainingTimeDisplay()).toBe('1m 35s');
  });

  it('renders unified refresh button and config trigger in DOM', () => {
    const refreshBtn = fixture.nativeElement.querySelector(
      '.unified-refresh-group .refresh-main-btn',
    );
    expect(refreshBtn).toBeTruthy();

    const configBtn = fixture.nativeElement.querySelector(
      '.unified-refresh-group .refresh-config-btn',
    );
    expect(configBtn).toBeTruthy();

    const countdownSpinner = fixture.nativeElement.querySelector(
      '.unified-refresh-group .countdown-spinner',
    );
    expect(countdownSpinner).toBeTruthy();
  });

  it('retries all jobs in current tab and displays updated feedback message', () => {
    component.setFilter('All');
    component.retryAllInCurrentTab();

    expect(mockIngestionService.retryBatchJobs).toHaveBeenCalledWith({
      jobIds: [],
      filter: 'All',
      search: undefined,
    });
    expect(component.feedbackMessage()).toContain('Requeued 3 job(s) for extraction.');
  });

  it('retries all jobs in review tab with only review job IDs', () => {
    component.setFilter('ReadyForReview');
    component.retryAllInCurrentTab();

    expect(mockIngestionService.retryBatchJobs).toHaveBeenCalledWith({
      jobIds: [],
      filter: 'ReadyForReview',
      search: undefined,
    });
  });

  it('renders Retry All button in queue toolbar with filtered count', () => {
    component.setFilter('All');
    fixture.detectChanges();

    const retryAllBtn = fixture.nativeElement.querySelector('.queue-toolbar .retry-all-tab-btn');
    expect(retryAllBtn).toBeTruthy();
    expect(retryAllBtn.textContent).toContain('Retry All (3)');

    component.setFilter('Published');
    fixture.detectChanges();
    expect(retryAllBtn.textContent).toContain('Retry All (1)');
  });

  it('loads LLM settings on init and displays Pause button when enabled', () => {
    expect(mockLlmSettingsService.getSettings).toHaveBeenCalled();
    expect(component.isIngestionEnabled()).toBe(true);

    const toggleBtn = fixture.nativeElement.querySelector('.toggle-ingestion-btn');
    expect(toggleBtn).toBeTruthy();
    expect(toggleBtn.textContent).toContain('Pause Ingestion');

    const pausedBanner = fixture.nativeElement.querySelector('.paused-ingestion-banner');
    expect(pausedBanner).toBeNull();
  });

  it('toggles ingestion state and renders paused banner when paused', () => {
    component.toggleIngestionState();
    fixture.detectChanges();

    expect(mockLlmSettingsService.setIngestionEnabled).toHaveBeenCalledWith(false);
    expect(component.isIngestionEnabled()).toBe(false);

    const toggleBtn = fixture.nativeElement.querySelector('.toggle-ingestion-btn');
    expect(toggleBtn.textContent).toContain('Resume Ingestion');

    const pausedBanner = fixture.nativeElement.querySelector('.paused-ingestion-banner');
    expect(pausedBanner).toBeTruthy();
    expect(pausedBanner.textContent).toContain('LLM Ingestion is Paused');

    // Resume via banner button
    component.toggleIngestionState();
    fixture.detectChanges();

    expect(mockLlmSettingsService.setIngestionEnabled).toHaveBeenCalledWith(true);
    expect(component.isIngestionEnabled()).toBe(true);
  });

  it('shows Run next for waiting jobs while ingestion is enabled', () => {
    const runButtons = fixture.nativeElement.querySelectorAll('button.run-now-btn');
    expect(runButtons.length).toBe(1);
    expect(runButtons[0].textContent).toContain('Run next');

    component.runJobNow(sampleJobs[1]);
    expect(mockIngestionService.runJobNow).toHaveBeenCalledWith('job-2');
    expect(component.feedbackMessage()).toContain('front of the queue');
  });

  it('shows Run now while ingestion is paused and runs the single job', () => {
    component.toggleIngestionState();
    fixture.detectChanges();

    const runButton = fixture.nativeElement.querySelector('button.run-now-btn');
    expect(runButton.textContent).toContain('Run now');

    runButton.click();
    expect(mockIngestionService.runJobNow).toHaveBeenCalledWith('job-2');
    expect(component.feedbackMessage()).toContain('rest of the queue stays paused');
  });
  it('renders Review Side-by-Side button for Published jobs and Open Recipe in actions menu', () => {
    component.setFilter('Published');
    fixture.detectChanges();

    const reviewBtn = fixture.nativeElement.querySelector('.review-action-btn');
    expect(reviewBtn).toBeTruthy();
    expect(reviewBtn.textContent).toContain('Review Side-by-Side');

    const menuBtn = fixture.nativeElement.querySelector(
      '.job-actions-menu-btn',
    ) as HTMLButtonElement;
    expect(menuBtn).toBeTruthy();
    menuBtn.click();
    fixture.detectChanges();

    const openRecipeBtn = document.querySelector('.open-recipe-btn');
    expect(openRecipeBtn).toBeTruthy();
    expect(openRecipeBtn?.textContent).toContain('Open Recipe');
  });

  it('renders sort selector and direction toggle next to search bar and reloads on change', () => {
    const sortField = fixture.nativeElement.querySelector('.queue-search-row .sort-form-field');
    const directionBtn = fixture.nativeElement.querySelector(
      '.queue-search-row .sort-direction-btn',
    );
    expect(sortField).toBeTruthy();
    expect(directionBtn).toBeTruthy();

    expect(component.sortBy()).toBe('CreatedAt');
    expect(component.sortDirection()).toBe('Desc');
    expect(mockIngestionService.getJobsPaged).toHaveBeenCalledWith(
      null,
      null,
      1,
      25,
      false,
      'CreatedAt',
      'Desc',
    );

    component.pageIndex.set(2);
    component.onSortByChange('Name');
    fixture.detectChanges();

    expect(component.sortBy()).toBe('Name');
    expect(component.sortDirection()).toBe('Desc');
    expect(component.pageIndex()).toBe(0);
    expect(mockIngestionService.getJobsPaged).toHaveBeenLastCalledWith(
      null,
      null,
      1,
      25,
      false,
      'Name',
      'Desc',
    );

    directionBtn.click();
    fixture.detectChanges();

    expect(component.sortDirection()).toBe('Asc');
    expect(component.pageIndex()).toBe(0);
    expect(mockIngestionService.getJobsPaged).toHaveBeenLastCalledWith(
      null,
      null,
      1,
      25,
      false,
      'Name',
      'Asc',
    );
  });

  it('displays both ingested and refined model chips when a job was refined', () => {
    const el = fixture.nativeElement as HTMLElement;
    const extractedChip = el.querySelector('[data-testid="job-extracted-model-chip"]');
    const refinedChip = el.querySelector('[data-testid="job-refined-model-chip"]');

    expect(extractedChip?.textContent).toContain('llama3:8b');
    expect(refinedChip?.textContent).toContain('Refined: gemma4:26b-a4b');
  });

  it('shows Refine now action in overflow menu on review/published cards when a refinement model is configured and queues while paused', () => {
    const el = fixture.nativeElement as HTMLElement;
    const menuBtns = el.querySelectorAll<HTMLButtonElement>('.job-actions-menu-btn');
    menuBtns[0].click();
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="refine-now-btn"]')).toBeNull();
    document.querySelector<HTMLElement>('.cdk-overlay-backdrop')?.click();
    fixture.detectChanges();

    component.refineModel.set('gemma4:26b-a4b');
    component.isIngestionEnabled.set(false);
    fixture.detectChanges();

    menuBtns[0].click();
    fixture.detectChanges();
    const firstRefineBtn = Array.from(
      document.querySelectorAll<HTMLButtonElement>('[data-testid="refine-now-btn"]'),
    ).at(-1);
    expect(firstRefineBtn).toBeTruthy();
    expect(firstRefineBtn?.textContent).toContain('Refine now');

    firstRefineBtn?.click();
    fixture.detectChanges();

    expect(mockIngestionService.refineJobNow).toHaveBeenCalledWith('job-1');
    expect(component.feedbackMessage()).toContain('while the rest of the queue stays paused');

    menuBtns[2].click();
    fixture.detectChanges();
    const publishedRefineBtn = Array.from(
      document.querySelectorAll<HTMLButtonElement>('[data-testid="refine-now-btn"]'),
    ).at(-1);
    expect(publishedRefineBtn).toBeTruthy();
    expect(publishedRefineBtn?.textContent).toContain('Re-refine');
  });

  it('displays Live Sync pill when SignalR is connected and hides countdown polling spinner', () => {
    mockIngestionRealtimeService.syncState.set('live');
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const livePill = el.querySelector('.sync-status-pill.live');
    expect(livePill?.textContent).toContain('Live Sync');
    expect(el.querySelector('.unified-refresh-group .countdown-spinner')).toBeNull();
  });

  it('updates pause banner immediately when SignalR broadcasts a paused-changed event', () => {
    mockIngestionRealtimeService.queueChanged$.next({
      reason: 'paused-changed',
      enableIngestion: false,
      pausedUntilUtc: '2026-11-20T07:00:00Z',
      pauseReason: 'Provider quota exhausted',
    });
    fixture.detectChanges();

    expect(component.isIngestionEnabled()).toBe(false);
    expect(component.pauseReason()).toBe('Provider quota exhausted');
    expect(fixture.nativeElement.querySelector('.paused-ingestion-banner')).toBeTruthy();
  });

  it('pre-populates Web URLs tab from PWA Web Share Target url query param', async () => {
    const queryParamMap$ = new BehaviorSubject(
      convertToParamMap({
        url: 'https://example.com/roast-chicken',
        title: 'Sunday Roast Chicken',
      }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [IngestionHubComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: queryParamMap$.asObservable() } },
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: IngestionRealtimeService, useValue: mockIngestionRealtimeService },
        { provide: LlmSettingsService, useValue: mockLlmSettingsService },
      ],
    }).compileComponents();

    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    const shareFixture = TestBed.createComponent(IngestionHubComponent);
    const shareComponent = shareFixture.componentInstance;
    shareFixture.detectChanges();

    expect(shareComponent.activeTab()).toBe('urls');
    expect(shareComponent.urlForm.controls.urlsText.value).toBe(
      'https://example.com/roast-chicken',
    );
    expect(shareComponent.feedbackMessage()).toContain('Shared recipe link loaded');
    expect(navigateSpy).toHaveBeenCalledWith(
      [],
      expect.objectContaining({
        queryParams: { url: null, text: null, title: null },
        replaceUrl: true,
      }),
    );
  });

  it('extracts URL from Android-style shared text without recipe body into URLs tab', async () => {
    const queryParamMap$ = new BehaviorSubject(
      convertToParamMap({
        text: 'Try this pasta https://example.com/pasta-alla-vodka',
      }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [IngestionHubComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: queryParamMap$.asObservable() } },
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: IngestionRealtimeService, useValue: mockIngestionRealtimeService },
        { provide: LlmSettingsService, useValue: mockLlmSettingsService },
      ],
    }).compileComponents();

    const shareFixture = TestBed.createComponent(IngestionHubComponent);
    const shareComponent = shareFixture.componentInstance;
    shareFixture.detectChanges();

    expect(shareComponent.activeTab()).toBe('urls');
    expect(shareComponent.urlForm.controls.urlsText.value).toBe(
      'https://example.com/pasta-alla-vodka',
    );
  });

  it('pre-populates Raw Text tab when shared text contains multi-line recipe content', async () => {
    const recipeBody = 'Ingredients:\n- 2 cups flour\n- 1 cup sugar\nInstructions:\nMix and bake.';
    const queryParamMap$ = new BehaviorSubject(
      convertToParamMap({
        title: 'Grandma Cake',
        text: recipeBody,
      }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [IngestionHubComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: queryParamMap$.asObservable() } },
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: IngestionRealtimeService, useValue: mockIngestionRealtimeService },
        { provide: LlmSettingsService, useValue: mockLlmSettingsService },
      ],
    }).compileComponents();

    const shareFixture = TestBed.createComponent(IngestionHubComponent);
    const shareComponent = shareFixture.componentInstance;
    shareFixture.detectChanges();

    expect(shareComponent.activeTab()).toBe('text');
    expect(shareComponent.textForm.controls.titleHint.value).toBe('Grandma Cake');
    expect(shareComponent.textForm.controls.rawText.value).toBe(recipeBody);
    expect(shareComponent.feedbackMessage()).toContain('Shared recipe text loaded');

    // Simulating the stripped queryParamMap emission must keep Raw Text tab active
    queryParamMap$.next(convertToParamMap({}));
    shareFixture.detectChanges();
    expect(shareComponent.activeTab()).toBe('text');
  });

  it('restores tab, filter, search, sort, and pagination from query params and syncs updates back to URL', async () => {
    const queryParamMap$ = new BehaviorSubject(
      convertToParamMap({
        tab: 'pdf',
        filter: 'ReadyForReview',
        q: 'bread',
        sort: 'Name',
        dir: 'Asc',
        page: '2',
        pageSize: '50',
      }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [IngestionHubComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: queryParamMap$.asObservable(),
            snapshot: { queryParamMap: queryParamMap$.value },
          },
        },
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: IngestionRealtimeService, useValue: mockIngestionRealtimeService },
        { provide: LlmSettingsService, useValue: mockLlmSettingsService },
      ],
    }).compileComponents();

    const router = TestBed.inject(Router);
    const navSpy = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    const restoredFixture = TestBed.createComponent(IngestionHubComponent);
    const restoredComp = restoredFixture.componentInstance;
    restoredFixture.detectChanges();

    expect(restoredComp.activeTab()).toBe('pdf');
    expect(restoredComp.filter()).toBe('ReadyForReview');
    expect(restoredComp.searchQuery()).toBe('bread');
    expect(restoredComp.sortBy()).toBe('Name');
    expect(restoredComp.sortDirection()).toBe('Asc');
    expect(restoredComp.pageIndex()).toBe(1);
    expect(restoredComp.pageSize()).toBe(50);

    restoredComp.onPageChange({ pageIndex: 2, pageSize: 50, length: 150 });
    expect(navSpy).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: expect.objectContaining({
          tab: 'pdf',
          filter: 'ReadyForReview',
          q: 'bread',
          sort: 'Name',
          dir: 'Asc',
          page: 3,
          pageSize: 50,
        }),
        replaceUrl: true,
      }),
    );
  });

  it('renders expanded job statusDetail without leading or trailing template whitespace', () => {
    const longDetail = 'Extracted recipe ingredients and steps cleanly without truncation. '
      .repeat(4)
      .trim();
    const jobWithLongDetail: IngestionJobSummaryDto = {
      ...sampleJobs[0],
      id: 'job-long-detail',
      statusDetail: `  ${longDetail}  `,
    };
    mockIngestionService.getJobsPaged.mockReturnValue(
      of({
        items: [jobWithLongDetail],
        totalCount: 1,
        page: 1,
        pageSize: 25,
      }),
    );

    component.loadJobs();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const detailEl = el.querySelector('.job-detail-text') as HTMLParagraphElement | null;
    const toggleBtn = el.querySelector('.detail-toggle-btn') as HTMLButtonElement | null;
    expect(detailEl).not.toBeNull();
    expect(toggleBtn).not.toBeNull();
    expect(detailEl?.textContent).toBe(longDetail);

    toggleBtn!.click();
    fixture.detectChanges();

    expect(detailEl?.classList.contains('expanded')).toBe(true);
    expect(detailEl?.textContent).toBe(longDetail);
    expect(detailEl?.textContent).not.toMatch(/^\s|\s$/);
  });
});
