import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  EnqueueRawTextIngestionRequest,
  EnqueueWebUrlIngestionRequest,
  IngestionExtractedDraftDto,
  IngestionJobDetailDto,
  IngestionJobSummaryDto,
  IngestionQueueSummaryDto,
  RetryWaitingJobsResultDto,
} from '../../models/ingestion.models';
import { RecipeDetailDto } from '../../models/recipe.models';
import { IngestionService } from './ingestion.service';

describe('IngestionService', () => {
  let service: IngestionService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), IngestionService],
    });

    service = TestBed.inject(IngestionService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('gets queue summary and updates reactive signals', async () => {
    const mockSummary: IngestionQueueSummaryDto = {
      readyForReviewCount: 3,
      activeProcessingCount: 1,
      waitingForLlmHostCount: 2,
      failedCount: 0,
    };

    const promise = firstValueFrom(service.getSummary());
    const req = httpMock.expectOne('/api/ingestion/jobs/summary');
    expect(req.request.method).toBe('GET');
    req.flush(mockSummary);

    const result = await promise;
    expect(result).toEqual(mockSummary);
    expect(service.summary()).toEqual(mockSummary);
    expect(service.reviewInboxCount()).toBe(3);
    expect(service.waitingForHostCount()).toBe(2);
  });

  it('gets jobs filtered by status', async () => {
    const mockJobs: IngestionJobSummaryDto[] = [
      {
        id: 'job-1',
        channel: 'WebUrl',
        status: 'ReadyForReview',
        displayTitle: 'Crispy Sesame Tofu',
        sourceUrl: 'https://example.com/tofu',
        thumbnailImagePath: null,
        confidenceScore: 0.9,
        statusDetail: 'Ready for review',
        attemptCount: 1,
        nextRetryAt: null,
        resultingRecipeId: null,
        resultingRecipeSlug: null,
        submittedByUsername: 'test',
        createdAt: '2026-10-26T12:00:00Z',
        modifiedAt: '2026-10-26T12:00:00Z',
      },
    ];

    const promise = firstValueFrom(service.getJobs('ReadyForReview'));
    const req = httpMock.expectOne('/api/ingestion/jobs?status=ReadyForReview');
    expect(req.request.method).toBe('GET');
    req.flush(mockJobs);

    const result = await promise;
    expect(result).toHaveLength(1);
    expect(result[0].id).toBe('job-1');
  });

  it('enqueues web URLs and refreshes summary', async () => {
    const request: EnqueueWebUrlIngestionRequest = {
      urls: ['https://example.com/recipe-1'],
    };

    const mockResponse: IngestionJobSummaryDto[] = [
      {
        id: 'job-2',
        channel: 'WebUrl',
        status: 'Queued',
        displayTitle: 'example.com/recipe-1',
        sourceUrl: 'https://example.com/recipe-1',
        thumbnailImagePath: null,
        confidenceScore: null,
        statusDetail: 'Queued',
        attemptCount: 0,
        nextRetryAt: null,
        resultingRecipeId: null,
        resultingRecipeSlug: null,
        submittedByUsername: 'test',
        createdAt: '2026-10-26T12:00:00Z',
        modifiedAt: '2026-10-26T12:00:00Z',
      },
    ];

    const promise = firstValueFrom(service.enqueueUrls(request));
    const req = httpMock.expectOne('/api/ingestion/jobs/urls');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(mockResponse);

    // After mutating call, refreshSummary() triggers GET /api/ingestion/jobs/summary
    const summaryReq = httpMock.expectOne('/api/ingestion/jobs/summary');
    summaryReq.flush({
      readyForReviewCount: 0,
      activeProcessingCount: 1,
      waitingForLlmHostCount: 0,
      failedCount: 0,
    });

    const result = await promise;
    expect(result).toHaveLength(1);
    expect(service.summary().activeProcessingCount).toBe(1);
  });

  it('enqueues raw text and refreshes summary', async () => {
    const request: EnqueueRawTextIngestionRequest = {
      rawText: 'Grandma Italian Meatballs\n1 lb beef',
      titleHint: 'Grandma Meatballs',
    };

    const mockResponse: IngestionJobSummaryDto = {
      id: 'job-3',
      channel: 'RawText',
      status: 'Queued',
      displayTitle: 'Grandma Meatballs',
      sourceUrl: null,
      thumbnailImagePath: null,
      confidenceScore: null,
      statusDetail: 'Queued',
      attemptCount: 0,
      nextRetryAt: null,
      resultingRecipeId: null,
      resultingRecipeSlug: null,
      submittedByUsername: 'test',
      createdAt: '2026-10-26T12:00:00Z',
      modifiedAt: '2026-10-26T12:00:00Z',
    };

    const promise = firstValueFrom(service.enqueueRawText(request));
    const req = httpMock.expectOne('/api/ingestion/jobs/text');
    expect(req.request.method).toBe('POST');
    req.flush(mockResponse);

    const summaryReq = httpMock.expectOne('/api/ingestion/jobs/summary');
    summaryReq.flush({
      readyForReviewCount: 0,
      activeProcessingCount: 1,
      waitingForLlmHostCount: 0,
      failedCount: 0,
    });

    const result = await promise;
    expect(result.id).toBe('job-3');
  });

  it('saves draft and approves/publishes', async () => {
    const draft: IngestionExtractedDraftDto = {
      title: 'Tofu Scramble',
      description: null,
      baseServings: 2,
      yieldDisplay: '2 servings',
      prepTimeMinutes: 5,
      cookTimeMinutes: 10,
      totalTimeMinutes: 15,
      difficulty: 'Easy',
      sourceUrl: null,
      sourceName: null,
      sourceAuthor: null,
      coverImagePath: null,
      thumbnailImagePath: null,
      suggestedCollectionIds: [],
      suggestedTagIds: [],
      ingredients: [],
      steps: [],
      confidenceScore: 0.95,
    };

    // Save draft
    const savePromise = firstValueFrom(service.saveDraft('job-1', draft));
    const saveReq = httpMock.expectOne('/api/ingestion/jobs/job-1/draft');
    expect(saveReq.request.method).toBe('PUT');
    saveReq.flush({
      id: 'job-1',
      channel: 'RawText',
      status: 'ReadyForReview',
      displayTitle: 'Tofu Scramble',
      sourceUrl: null,
      rawText: '...',
      scrapedPage: null,
      normalizedSourceText: '...',
      downloadedCoverImagePath: null,
      downloadedThumbnailImagePath: null,
      extractedDraft: draft,
      confidenceScore: 0.95,
      statusDetail: 'Draft saved',
      attemptCount: 1,
      nextRetryAt: null,
      processedAt: '2026-10-26T12:00:00Z',
      resultingRecipeId: null,
      resultingRecipeSlug: null,
      submittedByUsername: 'test',
      createdAt: '2026-10-26T12:00:00Z',
      modifiedAt: '2026-10-26T12:00:00Z',
    } as IngestionJobDetailDto);

    httpMock.expectOne('/api/ingestion/jobs/summary').flush({
      readyForReviewCount: 1,
      activeProcessingCount: 0,
      waitingForLlmHostCount: 0,
      failedCount: 0,
    });

    await savePromise;

    // Approve & publish
    const publishPromise = firstValueFrom(service.approveAndPublish('job-1', draft));
    const pubReq = httpMock.expectOne('/api/ingestion/jobs/job-1/approve');
    expect(pubReq.request.method).toBe('POST');
    pubReq.flush({
      id: 'rec-1',
      title: 'Tofu Scramble',
      slug: 'tofu-scramble',
    } as RecipeDetailDto);

    httpMock.expectOne('/api/ingestion/jobs/summary').flush({
      readyForReviewCount: 0,
      activeProcessingCount: 0,
      waitingForLlmHostCount: 0,
      failedCount: 0,
    });

    const publishedRecipe = await publishPromise;
    expect(publishedRecipe.id).toBe('rec-1');
  });

  it('retries all waiting jobs and discards job', async () => {
    const retryPromise = firstValueFrom(service.retryAllWaitingJobs());
    const retryReq = httpMock.expectOne('/api/ingestion/jobs/retry-waiting');
    expect(retryReq.request.method).toBe('POST');
    retryReq.flush({ requeuedCount: 2 } as RetryWaitingJobsResultDto);

    httpMock.expectOne('/api/ingestion/jobs/summary').flush({
      readyForReviewCount: 0,
      activeProcessingCount: 2,
      waitingForLlmHostCount: 0,
      failedCount: 0,
    });

    const retryResult = await retryPromise;
    expect(retryResult.requeuedCount).toBe(2);

    const discardPromise = firstValueFrom(service.discardJob('job-1'));
    const discardReq = httpMock.expectOne('/api/ingestion/jobs/job-1');
    expect(discardReq.request.method).toBe('DELETE');
    discardReq.flush(null);

    httpMock.expectOne('/api/ingestion/jobs/summary').flush({
      readyForReviewCount: 0,
      activeProcessingCount: 1,
      waitingForLlmHostCount: 0,
      failedCount: 0,
    });

    await discardPromise;
  });
});
