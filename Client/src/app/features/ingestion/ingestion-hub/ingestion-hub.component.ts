import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { Subject, Subscription, debounceTime, interval } from 'rxjs';
import { APP_STORAGE_PREFIX } from '../../../app.branding';
import type {
  IngestionChannel,
  IngestionJobSortBy,
  IngestionJobSortDirection,
  IngestionJobStatus,
  IngestionJobSummaryDto,
} from '../../../core/models/ingestion.models';
import { IngestionRealtimeService } from '../../../core/services/ingestion/ingestion-realtime.service';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  parseEnumParam,
  parsePositiveIntParam,
  queryParamsMatch,
} from '../../../core/utils/query-param-state';
import { IngestionLiveMonitorComponent } from '../ingestion-live-monitor/ingestion-live-monitor.component';

export type IngestionFilter = 'All' | 'ReadyForReview' | 'ActiveWaiting' | 'Published' | 'Failed';

export interface SortByOption {
  readonly value: IngestionJobSortBy;
  readonly label: string;
}

export const SORT_OPTIONS: SortByOption[] = [
  { value: 'CreatedAt', label: 'Created' },
  { value: 'ModifiedAt', label: 'Updated' },
  { value: 'Name', label: 'Name' },
];

export interface RefreshOption {
  readonly label: string;
  readonly value: number;
}

export const REFRESH_OPTIONS: RefreshOption[] = [
  { label: '15 seconds', value: 15 },
  { label: '30 seconds', value: 30 },
  { label: '1 minute', value: 60 },
  { label: '2 minutes', value: 120 },
  { label: '5 minutes', value: 300 },
  { label: 'Manual (Off)', value: 0 },
];

@Component({
  selector: 'app-ingestion-hub',
  imports: [
    ReactiveFormsModule,
    DatePipe,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatCheckboxModule,
    MatChipsModule,
    MatDividerModule,
    MatFormFieldModule,
    MatInputModule,
    MatIconModule,
    MatMenuModule,
    MatPaginatorModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTooltipModule,
    IngestionLiveMonitorComponent,
  ],
  templateUrl: './ingestion-hub.component.html',
  styleUrl: './ingestion-hub.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IngestionHubComponent implements OnInit, OnDestroy {
  private static readonly DEFAULT_PAGE_SIZE = 25;
  private static readonly ALLOWED_PAGE_SIZES = new Set([10, 25, 50, 100]);

  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly ingestionService = inject(IngestionService);
  private readonly ingestionRealtime = inject(IngestionRealtimeService);
  private readonly llmSettingsService = inject(LlmSettingsService);
  private initializedFromRoute = false;
  private lastRouteTabParam: string | null = null;

  private readonly liveRefreshTrigger$ = new Subject<void>();

  readonly syncState = this.ingestionRealtime.syncState;
  readonly isLiveSynced = computed(() => this.syncState() === 'live');

  readonly refreshOptions = REFRESH_OPTIONS;
  readonly sortOptions = SORT_OPTIONS;
  private readonly refreshStorageKey = `${APP_STORAGE_PREFIX}:ingestion-refresh-interval`;
  private lastActiveInterval = 15;

  readonly refreshInterval = signal<number>(this.getInitialRefreshInterval());
  readonly remainingSeconds = signal<number>(this.getInitialRefreshInterval());
  readonly isRefreshing = signal<boolean>(false);
  readonly isAutoRefreshEnabled = computed(() => this.refreshInterval() > 0);

  readonly activeTab = signal<'urls' | 'text' | 'images' | 'pdf'>('urls');
  readonly filter = signal<IngestionFilter>('All');
  readonly pageIndex = signal<number>(0);
  readonly pageSize = signal<number>(25);
  readonly totalCount = signal<number>(0);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private jobsRequest: Subscription | null = null;
  readonly jobs = signal<IngestionJobSummaryDto[]>([]);
  readonly isLoading = signal<boolean>(true);
  readonly isSubmitting = signal<boolean>(false);
  readonly isRetryingAll = signal<boolean>(false);
  readonly isIngestionEnabled = signal<boolean>(true);
  readonly refineModel = signal<string | null>(null);
  readonly hasRefinementModel = computed(() => !!this.refineModel()?.trim());
  readonly pausedUntilUtc = signal<string | null>(null);
  readonly pauseReason = signal<string | null>(null);
  readonly isTogglingIngestion = signal<boolean>(false);
  readonly feedbackMessage = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);

  readonly countdownProgress = computed(() => {
    const total = this.refreshInterval();
    if (total <= 0) return 0;
    const remaining = this.remainingSeconds();
    return Math.max(0, Math.min(100, (remaining / total) * 100));
  });

  readonly remainingTimeDisplay = computed(() => {
    const total = this.refreshInterval();
    if (total <= 0) return 'Manual';
    const remaining = this.remainingSeconds();
    if (remaining >= 60) {
      const minutes = Math.floor(remaining / 60);
      const seconds = remaining % 60;
      return seconds > 0 ? `${minutes}m ${Math.round(seconds)}s` : `${minutes}m`;
    }
    return `${Math.round(remaining)}s`;
  });

  readonly urlForm = this.fb.nonNullable.group({
    urlsText: this.fb.nonNullable.control<string>('', [Validators.required]),
  });

  readonly textForm = this.fb.nonNullable.group({
    titleHint: this.fb.nonNullable.control<string>(''),
    rawText: this.fb.nonNullable.control<string>('', [Validators.required]),
  });

  readonly imagesForm = this.fb.group({
    titleHint: this.fb.control<string>(''),
    sourceBookTitle: this.fb.control<string>(''),
    sourceBookPage: this.fb.control<number | null>(null),
  });

  readonly pdfForm = this.fb.group({
    titleHint: this.fb.control<string>(''),
    sourceBookTitle: this.fb.control<string>(''),
    sourceBookPage: this.fb.control<number | null>(null),
    splitMultiPage: this.fb.control<boolean>(false),
  });

  readonly selectedImages = signal<{ file: File; previewUrl: string }[]>([]);
  readonly selectedPdfs = signal<File[]>([]);
  readonly selectedPdf = computed(() =>
    this.selectedPdfs().length > 0 ? this.selectedPdfs()[0] : null,
  );
  readonly totalPdfSizeBytes = computed(() =>
    this.selectedPdfs().reduce((acc, f) => acc + f.size, 0),
  );
  readonly failedThumbJobIds = signal<Set<string>>(new Set());

  onJobThumbError(jobId: string): void {
    this.failedThumbJobIds.update((prev) => {
      const next = new Set(prev);
      next.add(jobId);
      return next;
    });
  }

  readonly expandedDetailJobIds = signal<Set<string>>(new Set());
  readonly detailExpandThreshold = 80;

  toggleJobDetail(jobId: string): void {
    this.expandedDetailJobIds.update((prev) => {
      const next = new Set(prev);
      if (next.has(jobId)) {
        next.delete(jobId);
      } else {
        next.add(jobId);
      }
      return next;
    });
  }

  readonly allCount = computed(() => this.ingestionService.summary().allCount ?? 0);
  readonly reviewCount = computed(() => this.ingestionService.summary().readyForReviewCount);
  readonly waitingCount = computed(() => this.ingestionService.summary().waitingForLlmHostCount);
  readonly waitingJobs = computed(() =>
    this.jobs().filter((j) => j.status === 'WaitingForLlmHost'),
  );
  readonly activeCount = computed(
    () =>
      this.ingestionService.summary().activeProcessingCount +
      this.ingestionService.summary().waitingForLlmHostCount,
  );
  readonly activeJobs = computed(() =>
    this.jobs().filter(
      (j) =>
        j.status === 'Queued' ||
        j.status === 'Preprocessing' ||
        j.status === 'Extracting' ||
        j.status === 'WaitingForLlmHost',
    ),
  );
  readonly failedCount = computed(() => this.ingestionService.summary().failedCount);
  readonly publishedCount = computed(() => this.ingestionService.summary().publishedCount ?? 0);

  readonly searchQuery = signal<string>('');
  readonly sortBy = signal<IngestionJobSortBy>('CreatedAt');
  readonly sortDirection = signal<IngestionJobSortDirection>('Desc');

  readonly filteredJobs = computed(() => this.jobs());

  onSearchInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.searchQuery.set(input.value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      this.pageIndex.set(0);
      this.syncQueryParams();
      this.executeRefresh();
    }, 300);
  }

  clearSearch(): void {
    if (this.searchTimer) {
      clearTimeout(this.searchTimer);
      this.searchTimer = null;
    }
    this.searchQuery.set('');
    this.pageIndex.set(0);
    this.syncQueryParams();
    this.executeRefresh();
  }

  onSortByChange(value: IngestionJobSortBy): void {
    this.sortBy.set(value);
    this.pageIndex.set(0);
    this.syncQueryParams();
    this.executeRefresh();
  }

  toggleSortDirection(): void {
    this.sortDirection.update((dir) => (dir === 'Asc' ? 'Desc' : 'Asc'));
    this.pageIndex.set(0);
    this.syncQueryParams();
    this.executeRefresh();
  }

  onPageChange(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.syncQueryParams();
    this.executeRefresh();
  }

  ngOnInit(): void {
    this.setupRouteQueryParams();
    this.loadLlmSettings();
    this.setupPolling();
    this.setupRealtime();
  }

  private setupRouteQueryParams(): void {
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const changed = this.applyViewQueryParams(params);
      this.applyShareTargetParams(params);
      if (!this.initializedFromRoute || changed) {
        this.initializedFromRoute = true;
        this.loadJobs();
      }
    });
  }

  private applyViewQueryParams(params: ParamMap): boolean {
    const rawTabParam = params.get('tab');
    const nextTab = parseEnumParam<'urls' | 'text' | 'images' | 'pdf'>(
      rawTabParam,
      ['urls', 'text', 'images', 'pdf'] as const,
      'urls',
    );
    const nextFilter = parseEnumParam<IngestionFilter>(
      params.get('filter'),
      ['All', 'ReadyForReview', 'ActiveWaiting', 'Published', 'Failed'] as const,
      'All',
    );
    const nextSearch = params.get('q') ?? '';
    const nextSortBy = parseEnumParam<IngestionJobSortBy>(
      params.get('sort'),
      ['CreatedAt', 'ModifiedAt', 'Name'] as const,
      'CreatedAt',
    );
    const nextSortDir = parseEnumParam<IngestionJobSortDirection>(
      params.get('dir'),
      ['Asc', 'Desc'] as const,
      'Desc',
    );
    const parsedPage = parsePositiveIntParam(params.get('page'));
    const nextPageIndex = parsedPage !== null ? parsedPage - 1 : 0;
    const parsedPageSize = parsePositiveIntParam(params.get('pageSize'));
    const nextPageSize =
      parsedPageSize !== null && IngestionHubComponent.ALLOWED_PAGE_SIZES.has(parsedPageSize)
        ? parsedPageSize
        : IngestionHubComponent.DEFAULT_PAGE_SIZE;

    const effectiveNextSearch =
      this.initializedFromRoute && this.searchTimer !== null ? this.searchQuery() : nextSearch;
    const queryChanged =
      this.filter() !== nextFilter ||
      this.searchQuery().trim() !== effectiveNextSearch.trim() ||
      this.sortBy() !== nextSortBy ||
      this.sortDirection() !== nextSortDir ||
      this.pageIndex() !== nextPageIndex ||
      this.pageSize() !== nextPageSize;

    const tabParamChanged = !this.initializedFromRoute || rawTabParam !== this.lastRouteTabParam;
    this.lastRouteTabParam = rawTabParam;
    if (tabParamChanged) {
      this.activeTab.set(nextTab);
    }

    if (!this.initializedFromRoute || queryChanged) {
      this.filter.set(nextFilter);
      if (!this.initializedFromRoute || this.searchTimer === null) {
        this.searchQuery.set(nextSearch);
      }
      this.sortBy.set(nextSortBy);
      this.sortDirection.set(nextSortDir);
      this.pageIndex.set(nextPageIndex);
      this.pageSize.set(nextPageSize);
    }

    return queryChanged;
  }

  private syncQueryParams(): void {
    const trimmedSearch = this.searchQuery().trim();
    const queryParams: Record<string, string | number | null> = {
      tab: this.activeTab() !== 'urls' ? this.activeTab() : null,
      filter: this.filter() !== 'All' ? this.filter() : null,
      q: trimmedSearch || null,
      sort: this.sortBy() !== 'CreatedAt' ? this.sortBy() : null,
      dir: this.sortDirection() !== 'Desc' ? this.sortDirection() : null,
      page: this.pageIndex() > 0 ? this.pageIndex() + 1 : null,
      pageSize:
        this.pageSize() !== IngestionHubComponent.DEFAULT_PAGE_SIZE ? this.pageSize() : null,
    };

    const current = this.route.snapshot?.queryParamMap;
    if (current && queryParamsMatch(current, queryParams)) {
      return;
    }

    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams,
      queryParamsHandling: 'merge',
      replaceUrl: true,
      scroll: 'manual',
    });
  }

  private applyShareTargetParams(params: ParamMap): void {
    const sharedUrl = params.get('url')?.trim() ?? '';
    const sharedText = params.get('text')?.trim() ?? '';
    const sharedTitle = params.get('title')?.trim() ?? '';

    if (!sharedUrl && !sharedText && !sharedTitle) {
      return;
    }

    const combinedForUrls = [sharedUrl, sharedText].filter(Boolean).join('\n');
    const urlMatches = combinedForUrls.match(/https?:\/\/[^\s<>"{}|\\^`[\]]+/gi) ?? [];
    const extractedUrls = Array.from(
      new Set(urlMatches.map((match) => match.replace(/[.,;!?)]+$/, ''))),
    );

    const remainingTextWithoutUrls = sharedText
      .replace(/https?:\/\/[^\s<>"{}|\\^`[\]]+/gi, '')
      .trim();
    const hasSubstantialRecipeBody =
      remainingTextWithoutUrls.length >= 80 || remainingTextWithoutUrls.includes('\n');

    if (extractedUrls.length > 0 && (!sharedText || !hasSubstantialRecipeBody)) {
      this.activeTab.set('urls');
      this.urlForm.controls.urlsText.setValue(extractedUrls.join('\n'));
      this.feedbackMessage.set('Shared recipe link loaded — tap Queue URL(s) to import.');
    } else if (sharedText || sharedTitle) {
      this.activeTab.set('text');
      if (sharedTitle) {
        this.textForm.controls.titleHint.setValue(sharedTitle);
      }
      this.textForm.controls.rawText.setValue(sharedText || sharedUrl);
      this.feedbackMessage.set(
        'Shared recipe text loaded — review and tap Extract Recipe to import.',
      );
    }

    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { url: null, text: null, title: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  ngOnDestroy(): void {
    this.jobsRequest?.unsubscribe();
    if (this.searchTimer) {
      clearTimeout(this.searchTimer);
      this.searchTimer = null;
    }
    void this.ingestionRealtime.disconnect();
  }

  reconnectRealtime(): void {
    void this.ingestionRealtime.connect();
  }

  private setupRealtime(): void {
    this.liveRefreshTrigger$
      .pipe(debounceTime(250), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.executeRefresh(true);
      });

    this.ingestionRealtime.queueChanged$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((event) => {
        if (event.enableIngestion !== undefined && event.enableIngestion !== null) {
          this.isIngestionEnabled.set(event.enableIngestion);
          this.pausedUntilUtc.set(event.pausedUntilUtc ?? null);
          this.pauseReason.set(event.pauseReason ?? null);
        }
        this.liveRefreshTrigger$.next();
      });

    this.ingestionRealtime.reconnected$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.loadLlmSettings();
      this.executeRefresh(true);
    });

    void this.ingestionRealtime.connect();
  }

  private getInitialRefreshInterval(): number {
    try {
      if (typeof window !== 'undefined' && window.localStorage) {
        const stored = window.localStorage.getItem(this.refreshStorageKey);
        if (stored !== null) {
          const parsed = parseInt(stored, 10);
          if (REFRESH_OPTIONS.some((opt) => opt.value === parsed)) {
            return parsed;
          }
        }
      }
    } catch {
      // Ignore storage errors in restricted contexts
    }
    return 15;
  }

  setRefreshInterval(seconds: number): void {
    if (seconds > 0) {
      this.lastActiveInterval = seconds;
    }
    this.refreshInterval.set(seconds);
    this.remainingSeconds.set(seconds);
    try {
      if (typeof window !== 'undefined' && window.localStorage) {
        window.localStorage.setItem(this.refreshStorageKey, seconds.toString());
      }
    } catch {
      // Ignore storage errors
    }
  }

  toggleAutoRefresh(): void {
    if (this.refreshInterval() > 0) {
      this.lastActiveInterval = this.refreshInterval();
      this.setRefreshInterval(0);
    } else {
      this.setRefreshInterval(this.lastActiveInterval || 15);
    }
  }

  manualRefresh(): void {
    if (this.isRefreshing()) {
      return;
    }
    this.executeRefresh();
    if (this.refreshInterval() > 0) {
      this.remainingSeconds.set(this.refreshInterval());
    }
  }

  loadJobs(): void {
    this.executeRefresh();
    if (this.refreshInterval() > 0) {
      this.remainingSeconds.set(this.refreshInterval());
    }
  }

  private executeRefresh(silent = false): void {
    if (!silent) {
      this.isRefreshing.set(true);
    }
    let status: IngestionJobStatus | null = null;
    let activeWaiting = false;

    switch (this.filter()) {
      case 'ReadyForReview':
        status = 'ReadyForReview';
        break;
      case 'ActiveWaiting':
        activeWaiting = true;
        break;
      case 'Published':
        status = 'Published';
        break;
      case 'Failed':
        status = 'Failed';
        break;
      default:
        break;
    }

    this.jobsRequest?.unsubscribe();
    this.jobsRequest = this.ingestionService
      .getJobsPaged(
        status,
        this.searchQuery().trim() || null,
        this.pageIndex() + 1,
        this.pageSize(),
        activeWaiting,
        this.sortBy(),
        this.sortDirection(),
      )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (res) => {
          this.jobs.set(res.items);
          this.totalCount.set(res.totalCount);
          this.isLoading.set(false);
          if (!silent) {
            this.isRefreshing.set(false);
          }
          this.ingestionService.refreshSummary();
        },
        error: (err) => {
          if (!silent) {
            this.errorMessage.set(err?.error?.detail || 'Failed to load ingestion jobs.');
            this.isRefreshing.set(false);
          }
          this.isLoading.set(false);
        },
      });
  }

  private setupPolling(): void {
    interval(100)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.isLiveSynced()) {
          // SignalR live sync handles updates; polling acts as an automatic offline fallback
          return;
        }

        const intervalVal = this.refreshInterval();
        if (intervalVal <= 0) {
          // Manual mode: do not count down or auto-refresh
          return;
        }

        if (this.isRefreshing()) {
          // Keep countdown steady while refresh request is in-flight
          return;
        }

        const remaining = this.remainingSeconds();
        if (remaining <= 1) {
          this.remainingSeconds.set(intervalVal);
          this.executeRefresh();
        } else {
          this.remainingSeconds.set(remaining - 0.1);
        }
      });
  }

  setFilter(filter: IngestionFilter): void {
    this.filter.set(filter);
    this.pageIndex.set(0);
    this.syncQueryParams();
    this.executeRefresh();
  }

  setActiveTab(tab: 'urls' | 'text' | 'images' | 'pdf'): void {
    this.activeTab.set(tab);
    this.syncQueryParams();
  }

  loadLlmSettings(): void {
    this.llmSettingsService
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.isIngestionEnabled.set(settings.enableIngestion ?? true);
          this.refineModel.set(settings.refineModel?.trim() || null);
          this.pausedUntilUtc.set(settings.pausedUntilUtc ?? null);
          this.pauseReason.set(settings.pauseReason ?? null);
        },
        error: () => {
          // Fallback or leave default true
        },
      });
  }

  toggleIngestionState(): void {
    if (this.isTogglingIngestion()) {
      return;
    }

    const nextState = !this.isIngestionEnabled();
    this.isTogglingIngestion.set(true);

    this.llmSettingsService
      .setIngestionEnabled(nextState)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings) => {
          this.isIngestionEnabled.set(settings.enableIngestion ?? nextState);
          this.pausedUntilUtc.set(settings.pausedUntilUtc ?? null);
          this.pauseReason.set(settings.pauseReason ?? null);
          this.isTogglingIngestion.set(false);
          this.feedbackMessage.set(
            nextState
              ? 'LLM ingestion resumed. Background processor will process queued recipes.'
              : 'LLM ingestion paused. Active jobs have been safely paused and returned to the queue.',
          );
          this.loadJobs();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to update ingestion state.');
          this.isTogglingIngestion.set(false);
        },
      });
  }

  async openLlmSettings(): Promise<void> {
    const dialogRef = await this.llmSettingsService.openLlmSettingsDialog();
    if (dialogRef) {
      dialogRef
        .afterClosed()
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe((saved) => {
          if (saved) {
            this.loadLlmSettings();
            this.loadJobs();
          }
        });
    }
  }

  onImagesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0) return;

    const current = [...this.selectedImages()];
    const remainingSlots = 6 - current.length;

    if (remainingSlots <= 0) {
      this.errorMessage.set('Maximum 6 images can be uploaded per recipe scan.');
      return;
    }

    const filesToAdd = Array.from(input.files).slice(0, remainingSlots);
    for (const f of filesToAdd) {
      current.push({
        file: f,
        previewUrl: URL.createObjectURL(f),
      });
    }

    this.selectedImages.set(current);
    input.value = '';
  }

  removeSelectedImage(index: number): void {
    const current = [...this.selectedImages()];
    if (index >= 0 && index < current.length) {
      URL.revokeObjectURL(current[index].previewUrl);
      current.splice(index, 1);
      this.selectedImages.set(current);
    }
  }

  submitImages(): void {
    const images = this.selectedImages();
    if (images.length === 0 || this.isSubmitting()) return;

    this.isSubmitting.set(true);
    this.errorMessage.set(null);
    this.feedbackMessage.set(null);

    const files = images.map((img) => img.file);
    const formVal = this.imagesForm.value;

    this.ingestionService
      .enqueueImages(files, {
        titleHint: formVal.titleHint || undefined,
        sourceBookTitle: formVal.sourceBookTitle || undefined,
        sourceBookPage: formVal.sourceBookPage || undefined,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (job) => {
          for (const img of this.selectedImages()) {
            URL.revokeObjectURL(img.previewUrl);
          }
          this.selectedImages.set([]);
          this.imagesForm.reset();
          this.isSubmitting.set(false);
          this.feedbackMessage.set(`Enqueued photo scan job "${job.displayTitle}".`);
          this.loadJobs();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to enqueue image scan.');
          this.isSubmitting.set(false);
        },
      });
  }

  onPdfSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (!input.files || input.files.length === 0) return;

    const files = Array.from(input.files);
    const validFiles: File[] = [];
    for (const file of files) {
      if (file.size > 25 * 1024 * 1024) {
        this.errorMessage.set(`File "${file.name}" exceeds the maximum allowed size of 25 MB.`);
        continue;
      }
      validFiles.push(file);
    }

    if (validFiles.length > 0) {
      this.selectedPdfs.update((prev) => {
        const combined = [...prev, ...validFiles];
        return combined.slice(0, 50);
      });
    }
    input.value = '';
  }

  removeSelectedPdf(index: number): void {
    this.selectedPdfs.update((prev) => prev.filter((_, i) => i !== index));
  }

  clearSelectedPdf(): void {
    this.selectedPdfs.set([]);
  }

  submitPdf(): void {
    const pdfs = this.selectedPdfs();
    if (pdfs.length === 0 || this.isSubmitting()) return;

    this.isSubmitting.set(true);
    this.errorMessage.set(null);
    this.feedbackMessage.set(null);

    const formVal = this.pdfForm.value;

    if (pdfs.length === 1 && !formVal.splitMultiPage) {
      this.ingestionService
        .enqueuePdf(pdfs[0], {
          titleHint: formVal.titleHint || undefined,
          sourceBookTitle: formVal.sourceBookTitle || undefined,
          sourceBookPage: formVal.sourceBookPage || undefined,
        })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: (job) => {
            this.selectedPdfs.set([]);
            this.pdfForm.reset();
            this.isSubmitting.set(false);
            this.feedbackMessage.set(`Enqueued PDF document job "${job.displayTitle}".`);
            this.loadJobs();
          },
          error: (err) => {
            this.errorMessage.set(err?.error?.detail || 'Failed to enqueue PDF document.');
            this.isSubmitting.set(false);
          },
        });
    } else {
      this.ingestionService
        .enqueuePdfBatch(pdfs, {
          sourceBookTitle: formVal.sourceBookTitle || undefined,
          splitMultiPage: formVal.splitMultiPage || false,
        })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: (jobs) => {
            this.selectedPdfs.set([]);
            this.pdfForm.reset();
            this.isSubmitting.set(false);
            this.feedbackMessage.set(`Enqueued ${jobs.length} PDF recipe job(s).`);
            this.loadJobs();
          },
          error: (err) => {
            this.errorMessage.set(err?.error?.detail || 'Failed to enqueue PDF batch.');
            this.isSubmitting.set(false);
          },
        });
    }
  }

  submitWebUrls(): void {
    if (this.urlForm.invalid || this.isSubmitting()) {
      return;
    }

    const raw = this.urlForm.controls.urlsText.value;
    const urls = raw
      .split('\n')
      .map((u) => u.trim())
      .filter((u) => u.length > 0);

    if (urls.length === 0) {
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);
    this.feedbackMessage.set(null);

    this.ingestionService
      .enqueueUrls({ urls })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (enqueued) => {
          this.urlForm.reset();
          this.isSubmitting.set(false);
          this.feedbackMessage.set(`Enqueued ${enqueued.length} URL(s) for extraction.`);
          this.loadJobs();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to enqueue URLs.');
          this.isSubmitting.set(false);
        },
      });
  }

  submitRawText(): void {
    if (this.textForm.invalid || this.isSubmitting()) {
      return;
    }

    const { rawText, titleHint } = this.textForm.getRawValue();
    if (!rawText.trim()) {
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set(null);
    this.feedbackMessage.set(null);

    this.ingestionService
      .enqueueRawText({
        rawText: rawText.trim(),
        titleHint: titleHint.trim() || undefined,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.textForm.reset();
          this.isSubmitting.set(false);
          this.feedbackMessage.set('Recipe text queued for extraction.');
          this.loadJobs();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to enqueue recipe text.');
          this.isSubmitting.set(false);
        },
      });
  }

  retryJob(job: IngestionJobSummaryDto): void {
    this.errorMessage.set(null);
    this.ingestionService
      .retryJob(job.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.feedbackMessage.set(
            'Re-queued "' + job.displayTitle + '" for extraction with current LLM model.',
          );
          this.loadJobs();
        },
        error: (err) =>
          this.errorMessage.set(err?.error?.detail || `Failed to retry job "${job.displayTitle}".`),
      });
  }

  runJobNow(job: IngestionJobSummaryDto): void {
    this.errorMessage.set(null);
    const paused = !this.isIngestionEnabled();
    this.ingestionService
      .runJobNow(job.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.feedbackMessage.set(
            paused
              ? `Running "${job.displayTitle}" now while the rest of the queue stays paused.`
              : `Moved "${job.displayTitle}" to the front of the queue.`,
          );
          this.loadJobs();
        },
        error: (err) =>
          this.errorMessage.set(
            err?.error?.detail || `Failed to run job "${job.displayTitle}" now.`,
          ),
      });
  }

  isJobRefining(job: IngestionJobSummaryDto): boolean {
    return !!job.refinementRunNowRequested || (job.statusDetail?.startsWith('Refining ') ?? false);
  }

  refineJobNow(job: IngestionJobSummaryDto): void {
    this.errorMessage.set(null);
    const paused = !this.isIngestionEnabled();
    this.ingestionService
      .refineJobNow(job.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.feedbackMessage.set(
            paused
              ? `Running refinement for "${job.displayTitle}" now while the rest of the queue stays paused.`
              : `Queued refinement for "${job.displayTitle}" to run next.`,
          );
          this.loadJobs();
        },
        error: (err) =>
          this.errorMessage.set(
            err?.error?.detail || `Failed to refine "${job.displayTitle}" now.`,
          ),
      });
  }

  retryAllWaiting(): void {
    if (this.isRetryingAll()) {
      return;
    }

    this.isRetryingAll.set(true);
    this.errorMessage.set(null);

    this.ingestionService
      .retryAllWaitingJobs()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (res) => {
          this.isRetryingAll.set(false);
          this.feedbackMessage.set(`Requeued ${res.requeuedCount} waiting job(s) for extraction.`);
          this.loadJobs();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to retry waiting jobs.');
          this.isRetryingAll.set(false);
        },
      });
  }

  retryAllInCurrentTab(): void {
    if (this.isRetryingAll() || this.totalCount() === 0) {
      return;
    }

    this.isRetryingAll.set(true);
    this.errorMessage.set(null);
    this.feedbackMessage.set(null);

    const currentFilter = this.filter();

    this.ingestionService
      .retryBatchJobs({
        jobIds: [],
        filter: currentFilter,
        search: this.searchQuery().trim() || undefined,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (res) => {
          this.isRetryingAll.set(false);
          this.feedbackMessage.set(`Requeued ${res.requeuedCount} job(s) for extraction.`);
          this.loadJobs();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to retry jobs.');
          this.isRetryingAll.set(false);
        },
      });
  }

  discardJob(job: IngestionJobSummaryDto): void {
    this.ingestionService
      .discardJob(job.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.loadJobs(),
        error: (err) =>
          this.errorMessage.set(
            err?.error?.detail || `Failed to discard job "${job.displayTitle}".`,
          ),
      });
  }

  getChannelIcon(channel: IngestionChannel): string {
    switch (channel) {
      case 'WebUrl':
        return 'link';
      case 'RawText':
        return 'description';
      case 'Images':
        return 'photo_camera';
      case 'Pdf':
        return 'picture_as_pdf';
      default:
        return 'receipt_long';
    }
  }

  getStatusClass(status: IngestionJobStatus): string {
    switch (status) {
      case 'ReadyForReview':
        return 'status-ready';
      case 'WaitingForLlmHost':
        return 'status-waiting';
      case 'Queued':
      case 'Preprocessing':
      case 'Extracting':
        return 'status-active';
      case 'Published':
        return 'status-published';
      case 'Failed':
        return 'status-failed';
      default:
        return 'status-default';
    }
  }

  getStatusLabel(status: IngestionJobStatus): string {
    switch (status) {
      case 'ReadyForReview':
        return 'Ready for Review';
      case 'WaitingForLlmHost':
        return 'Waiting for LLM Host';
      case 'Preprocessing':
        return 'Preprocessing...';
      case 'Extracting':
        return 'Extracting with LLM...';
      case 'Queued':
        return 'Queued';
      case 'Published':
        return 'Published';
      case 'Failed':
        return 'Failed';
      case 'Discarded':
        return 'Discarded';
      default:
        return status;
    }
  }
}
