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
    discardJob: ReturnType<typeof vi.fn>;
    refreshSummary: ReturnType<typeof vi.fn>;
  };
  let mockLlmSettingsService: {
    openLlmSettingsDialog: ReturnType<typeof vi.fn>;
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
  ];

  beforeEach(async () => {
    mockIngestionService = {
      getJobs: vi.fn().mockReturnValue(of(sampleJobs)),
      enqueueUrls: vi.fn().mockReturnValue(of([sampleJobs[0]])),
      enqueueRawText: vi.fn().mockReturnValue(of(sampleJobs[1])),
      retryJob: vi.fn().mockReturnValue(of(sampleJobs[1])),
      retryAllWaitingJobs: vi.fn().mockReturnValue(of({ requeuedCount: 1 })),
      discardJob: vi.fn().mockReturnValue(of(undefined)),
      refreshSummary: vi.fn(),
    };
    mockLlmSettingsService = {
      openLlmSettingsDialog: vi.fn(),
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
    expect(component.jobs()).toHaveLength(2);
    expect(component.reviewCount()).toBe(1);
    expect(component.waitingJobs()).toHaveLength(1);
  });

  it('filters jobs by status pill', () => {
    component.setFilter('ReadyForReview');
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-1');

    component.setFilter('ActiveWaiting');
    expect(component.filteredJobs()).toHaveLength(1);
    expect(component.filteredJobs()[0].id).toBe('job-2');

    component.setFilter('All');
    expect(component.filteredJobs()).toHaveLength(2);
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

  it('discards a job', () => {
    component.discardJob(sampleJobs[1]);
    expect(mockIngestionService.discardJob).toHaveBeenCalledWith('job-2');
  });

  it('opens LLM provider settings dialog', () => {
    component.openLlmSettings();
    expect(mockLlmSettingsService.openLlmSettingsDialog).toHaveBeenCalled();
  });
});
