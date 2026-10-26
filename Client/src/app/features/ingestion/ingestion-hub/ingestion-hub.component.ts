import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
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
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { interval } from 'rxjs';
import type {
  IngestionChannel,
  IngestionJobStatus,
  IngestionJobSummaryDto,
} from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';

export type IngestionFilter = 'All' | 'ReadyForReview' | 'ActiveWaiting' | 'Published' | 'Failed';

@Component({
  selector: 'app-ingestion-hub',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  templateUrl: './ingestion-hub.component.html',
  styleUrl: './ingestion-hub.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IngestionHubComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly ingestionService = inject(IngestionService);
  private readonly llmSettingsService = inject(LlmSettingsService);

  readonly activeTab = signal<'urls' | 'text'>('urls');
  readonly filter = signal<IngestionFilter>('All');
  readonly jobs = signal<IngestionJobSummaryDto[]>([]);
  readonly isLoading = signal<boolean>(true);
  readonly isSubmitting = signal<boolean>(false);
  readonly isRetryingAll = signal<boolean>(false);
  readonly feedbackMessage = signal<string | null>(null);
  readonly errorMessage = signal<string | null>(null);

  readonly urlForm = this.fb.nonNullable.group({
    urlsText: this.fb.nonNullable.control<string>('', [Validators.required]),
  });

  readonly textForm = this.fb.nonNullable.group({
    titleHint: this.fb.nonNullable.control<string>(''),
    rawText: this.fb.nonNullable.control<string>('', [Validators.required]),
  });

  readonly reviewCount = computed(
    () => this.jobs().filter((j) => j.status === 'ReadyForReview').length,
  );

  readonly waitingJobs = computed(() =>
    this.jobs().filter((j) => j.status === 'WaitingForLlmHost'),
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

  readonly failedCount = computed(() => this.jobs().filter((j) => j.status === 'Failed').length);

  readonly publishedCount = computed(
    () => this.jobs().filter((j) => j.status === 'Published').length,
  );

  readonly filteredJobs = computed(() => {
    const all = this.jobs();
    switch (this.filter()) {
      case 'ReadyForReview':
        return all.filter((j) => j.status === 'ReadyForReview');
      case 'ActiveWaiting':
        return all.filter(
          (j) =>
            j.status === 'Queued' ||
            j.status === 'Preprocessing' ||
            j.status === 'Extracting' ||
            j.status === 'WaitingForLlmHost',
        );
      case 'Published':
        return all.filter((j) => j.status === 'Published');
      case 'Failed':
        return all.filter((j) => j.status === 'Failed');
      default:
        return all;
    }
  });

  ngOnInit(): void {
    this.loadJobs();
    this.setupPolling();
  }

  loadJobs(): void {
    this.ingestionService
      .getJobs()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (jobs) => {
          this.jobs.set(jobs);
          this.isLoading.set(false);
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to load ingestion jobs.');
          this.isLoading.set(false);
        },
      });
  }

  private setupPolling(): void {
    interval(2000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        const hasProcessing = this.jobs().some(
          (j) => j.status === 'Queued' || j.status === 'Preprocessing' || j.status === 'Extracting',
        );
        if (hasProcessing) {
          this.loadJobs();
          this.ingestionService.refreshSummary();
        }
      });
  }

  setFilter(filter: IngestionFilter): void {
    this.filter.set(filter);
  }

  setActiveTab(tab: 'urls' | 'text'): void {
    this.activeTab.set(tab);
  }

  openLlmSettings(): void {
    this.llmSettingsService.openLlmSettingsDialog();
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
    this.ingestionService
      .retryJob(job.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.loadJobs(),
        error: (err) =>
          this.errorMessage.set(err?.error?.detail || `Failed to retry job "${job.displayTitle}".`),
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
