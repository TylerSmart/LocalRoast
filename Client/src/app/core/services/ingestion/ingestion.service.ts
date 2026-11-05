import type { PagedResultDto } from '../../models/pagination.models';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import type {
  EnqueueRawTextIngestionRequest,
  EnqueueWebUrlIngestionRequest,
  IngestionExtractedDraftDto,
  IngestionJobDetailDto,
  IngestionJobStatus,
  IngestionJobSummaryDto,
  IngestionQueueSummaryDto,
  RetryBatchJobsRequest,
  RetryBatchJobsResultDto,
  RetryWaitingJobsResultDto,
} from '../../models/ingestion.models';
import type { RecipeDetailDto } from '../../models/recipe.models';

@Service()
export class IngestionService {
  private readonly http = inject(HttpClient);

  readonly summary = signal<IngestionQueueSummaryDto>({
    readyForReviewCount: 0,
    activeProcessingCount: 0,
    waitingForLlmHostCount: 0,
    failedCount: 0,
  });

  readonly reviewInboxCount = computed(() => this.summary().readyForReviewCount);
  readonly waitingForHostCount = computed(() => this.summary().waitingForLlmHostCount);

  refreshSummary(): void {
    this.getSummary().subscribe({
      next: (summary) => this.summary.set(summary),
      error: () => undefined,
    });
  }

  getSummary(): Observable<IngestionQueueSummaryDto> {
    return this.http
      .get<IngestionQueueSummaryDto>(apiUrl('/ingestion/jobs/summary'))
      .pipe(tap((summary) => this.summary.set(summary)));
  }

  getJobsPaged(
    status?: IngestionJobStatus | null,
    search?: string | null,
    page = 1,
    pageSize = 25,
    activeWaiting = false,
  ): Observable<PagedResultDto<IngestionJobSummaryDto>> {
    let params = new HttpParams().set('page', page.toString()).set('pageSize', pageSize.toString());

    if (activeWaiting) {
      params = params.set('activeWaiting', 'true');
    } else if (status) {
      params = params.set('status', status);
    }
    if (search && search.trim()) {
      params = params.set('search', search.trim());
    }

    return this.http.get<PagedResultDto<IngestionJobSummaryDto>>(apiUrl('/ingestion/jobs'), {
      params,
    });
  }

  getJobs(status?: IngestionJobStatus): Observable<IngestionJobSummaryDto[]> {
    let params = new HttpParams();
    if (status) {
      params = params.set('status', status);
    }
    return this.http.get<IngestionJobSummaryDto[]>(apiUrl('/ingestion/jobs'), { params });
  }

  getJobById(id: string): Observable<IngestionJobDetailDto> {
    return this.http.get<IngestionJobDetailDto>(apiUrl(`/ingestion/jobs/${id}`));
  }

  enqueueUrls(request: EnqueueWebUrlIngestionRequest): Observable<IngestionJobSummaryDto[]> {
    return this.http
      .post<IngestionJobSummaryDto[]>(apiUrl('/ingestion/jobs/urls'), request)
      .pipe(tap(() => this.refreshSummary()));
  }

  enqueueRawText(request: EnqueueRawTextIngestionRequest): Observable<IngestionJobSummaryDto> {
    return this.http
      .post<IngestionJobSummaryDto>(apiUrl('/ingestion/jobs/text'), request)
      .pipe(tap(() => this.refreshSummary()));
  }

  saveDraft(id: string, draft: IngestionExtractedDraftDto): Observable<IngestionJobDetailDto> {
    return this.http
      .put<IngestionJobDetailDto>(apiUrl(`/ingestion/jobs/${id}/draft`), draft)
      .pipe(tap(() => this.refreshSummary()));
  }

  approveAndPublish(id: string, draft: IngestionExtractedDraftDto): Observable<RecipeDetailDto> {
    return this.http
      .post<RecipeDetailDto>(apiUrl(`/ingestion/jobs/${id}/approve`), draft)
      .pipe(tap(() => this.refreshSummary()));
  }

  retryJob(id: string): Observable<IngestionJobSummaryDto> {
    return this.http
      .post<IngestionJobSummaryDto>(apiUrl(`/ingestion/jobs/${id}/retry`), {})
      .pipe(tap(() => this.refreshSummary()));
  }

  retryAllWaitingJobs(): Observable<RetryWaitingJobsResultDto> {
    return this.http
      .post<RetryWaitingJobsResultDto>(apiUrl('/ingestion/jobs/retry-waiting'), {})
      .pipe(tap(() => this.refreshSummary()));
  }

  retryBatchJobs(request: RetryBatchJobsRequest): Observable<RetryBatchJobsResultDto> {
    return this.http
      .post<RetryBatchJobsResultDto>(apiUrl('/ingestion/jobs/retry-batch'), request)
      .pipe(tap(() => this.refreshSummary()));
  }

  discardJob(id: string): Observable<void> {
    return this.http
      .delete<void>(apiUrl(`/ingestion/jobs/${id}`))
      .pipe(tap(() => this.refreshSummary()));
  }
  enqueueImages(
    files: File[],
    metadata?: { titleHint?: string; sourceBookTitle?: string; sourceBookPage?: number },
  ): Observable<IngestionJobSummaryDto> {
    const formData = new FormData();
    for (const file of files) {
      formData.append('files', file, file.name);
    }
    if (metadata?.titleHint) {
      formData.append('titleHint', metadata.titleHint);
    }
    if (metadata?.sourceBookTitle) {
      formData.append('sourceBookTitle', metadata.sourceBookTitle);
    }
    if (metadata?.sourceBookPage !== undefined && metadata?.sourceBookPage !== null) {
      formData.append('sourceBookPage', metadata.sourceBookPage.toString());
    }
    return this.http
      .post<IngestionJobSummaryDto>(apiUrl('/ingestion/jobs/images'), formData)
      .pipe(tap(() => this.refreshSummary()));
  }

  enqueuePdf(
    file: File,
    metadata?: { titleHint?: string; sourceBookTitle?: string; sourceBookPage?: number },
  ): Observable<IngestionJobSummaryDto> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    if (metadata?.titleHint) {
      formData.append('titleHint', metadata.titleHint);
    }
    if (metadata?.sourceBookTitle) {
      formData.append('sourceBookTitle', metadata.sourceBookTitle);
    }
    if (metadata?.sourceBookPage !== undefined && metadata?.sourceBookPage !== null) {
      formData.append('sourceBookPage', metadata.sourceBookPage.toString());
    }
    return this.http
      .post<IngestionJobSummaryDto>(apiUrl('/ingestion/jobs/pdf'), formData)
      .pipe(tap(() => this.refreshSummary()));
  }

  enqueuePdfBatch(
    files: File[],
    metadata?: { sourceBookTitle?: string; splitMultiPage?: boolean },
  ): Observable<IngestionJobSummaryDto[]> {
    const formData = new FormData();
    for (const file of files) {
      formData.append('files', file, file.name);
    }
    if (metadata?.sourceBookTitle) {
      formData.append('sourceBookTitle', metadata.sourceBookTitle);
    }
    if (metadata?.splitMultiPage) {
      formData.append('splitMultiPage', 'true');
    }
    return this.http
      .post<IngestionJobSummaryDto[]>(apiUrl('/ingestion/jobs/pdf/bulk'), formData)
      .pipe(tap(() => this.refreshSummary()));
  }

  promotePageToCover(id: string, pageNumber: number): Observable<IngestionJobDetailDto> {
    return this.http.post<IngestionJobDetailDto>(apiUrl(`/ingestion/jobs/${id}/cover-from-page`), {
      pageNumber,
    });
  }
}
