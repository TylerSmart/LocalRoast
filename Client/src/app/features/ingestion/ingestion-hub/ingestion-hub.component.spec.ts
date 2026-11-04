import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import type { IngestionJobSummaryDto } from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { IngestionHubComponent } from './ingestion-hub.component';

describe('IngestionHubComponent', () => {
  let component: IngestionHubComponent;
  let fixture: ComponentFixture<IngestionHubComponent>;
  let mockIngestionService: {
    getJobs: ReturnType<typeof vi.fn>;
    enqueueUrls: ReturnType<typeof vi.fn>;
    enqueueRawText: ReturnType<typeof vi.fn>;
    retryJob: ReturnType<typeof vi.fn>;
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
      submittedByUsername: 'alice',
      createdAt: '2026-10-26T12:10:00Z',
      modifiedAt: '2026-10-26T12:10:00Z',
    },
  ];

  beforeEach(async () => {
    localStorage.clear();
    mockIngestionService = {
      getJobs: vi.fn().mockReturnValue(of(sampleJobs)),
      enqueueUrls: vi.fn().mockReturnValue(of([sampleJobs[0]])),
      enqueueRawText: vi.fn().mockReturnValue(of(sampleJobs[1])),
      retryJob: vi.fn().mockReturnValue(of(sampleJobs[1])),
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

    await TestBed.configureTestingModule({
      imports: [IngestionHubComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: IngestionService, useValue: mockIngestionService },
        { provide: LlmSettingsService, useValue: mockLlmSettingsService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(IngestionHubComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders loaded jobs and computes review and waiting counts', () => {
    expect(mockIngestionService.getJobs).toHaveBeenCalled();
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
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-1');

    // Search by URL (specific to /tofu)
    component.searchQuery.set('/tofu');
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-1');

    // Search by shared domain (example.com)
    component.searchQuery.set('example.com');
    expect(component.filteredJobs()).toHaveLength(2);

    // Search by model
    component.searchQuery.set('llama3:8b');
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-3');

    // Search with non-matching query
    component.searchQuery.set('NonExistentJob');
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

  it('discards waiting, review, and published jobs and renders remove buttons for all statuses', () => {
    const discardButtons = fixture.nativeElement.querySelectorAll('button.discard-btn');
    expect(discardButtons.length).toBe(3);

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
    mockIngestionService.getJobs.mockClear();
    mockIngestionService.refreshSummary.mockClear();

    component.remainingSeconds.set(5);
    component.manualRefresh();

    expect(mockIngestionService.getJobs).toHaveBeenCalled();
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
      jobIds: ['job-1', 'job-2', 'job-3'],
      filter: 'All',
    });
    expect(component.feedbackMessage()).toContain('Requeued 3 job(s) for extraction.');
  });

  it('retries all jobs in review tab with only review job IDs', () => {
    component.setFilter('ReadyForReview');
    component.retryAllInCurrentTab();

    expect(mockIngestionService.retryBatchJobs).toHaveBeenCalledWith({
      jobIds: ['job-1'],
      filter: 'ReadyForReview',
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
});
