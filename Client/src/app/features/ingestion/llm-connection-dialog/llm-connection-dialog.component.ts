import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSliderModule } from '@angular/material/slider';
import type {
  LlmConnectionDto,
  LlmConnectionTestResultDto,
  LlmProvider,
  UpsertLlmConnectionRequest,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  LLM_TIMEZONE_OPTIONS,
  defaultBaseUrlFor,
  failedTestResult,
} from '../llm-settings-dialog/llm-settings.utils';

export interface LlmConnectionDialogData {
  connection?: LlmConnectionDto | null;
  defaultName?: string;
}

@Component({
  selector: 'app-llm-connection-dialog',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatSliderModule,
  ],
  templateUrl: './llm-connection-dialog.component.html',
  styleUrl: './llm-connection-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmConnectionDialogComponent {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly llmSettingsService = inject(LlmSettingsService);
  private readonly dialogRef = inject(MatDialogRef<LlmConnectionDialogComponent>);
  private readonly data = inject<LlmConnectionDialogData | null>(MAT_DIALOG_DATA, {
    optional: true,
  });

  readonly timezoneOptions = LLM_TIMEZONE_OPTIONS;
  readonly editingConnection = signal<LlmConnectionDto | null>(this.data?.connection ?? null);
  readonly isSaving = signal(false);
  readonly isTestingEditor = signal(false);
  readonly editorTestResult = signal<LlmConnectionTestResultDto | null>(null);
  readonly errorMessage = signal<string | null>(null);
  readonly hasSavedApiKey = computed(() => this.editingConnection()?.hasApiKey ?? false);

  readonly form = this.fb.nonNullable.group({
    name: this.fb.nonNullable.control(this.data?.connection?.name ?? this.data?.defaultName ?? '', [
      Validators.required,
      Validators.maxLength(100),
    ]),
    provider: this.fb.nonNullable.control<LlmProvider>(
      this.data?.connection?.provider ?? 'Ollama',
      [Validators.required],
    ),
    baseUrl: this.fb.nonNullable.control(
      this.data?.connection?.baseUrl ?? 'http://localhost:11434',
      [Validators.required, Validators.maxLength(500)],
    ),
    apiKey: this.fb.nonNullable.control('', [Validators.maxLength(500)]),
    clearApiKey: this.fb.nonNullable.control(false),
    ollamaKeepAlive: this.fb.nonNullable.control(this.data?.connection?.ollamaKeepAlive ?? '5m', [
      Validators.maxLength(32),
    ]),
    ollamaCpuOnly: this.fb.nonNullable.control(this.data?.connection?.ollamaCpuOnly ?? false),
    enableRateLimitSafeguards: this.fb.nonNullable.control(
      this.data?.connection?.enableRateLimitSafeguards ?? true,
    ),
    maxConcurrentRequests: this.fb.nonNullable.control(
      this.data?.connection?.maxConcurrentRequests ?? 1,
      [Validators.required, Validators.min(1), Validators.max(5)],
    ),
    requestsPerMinute: this.fb.control<number | null>(
      this.data?.connection?.requestsPerMinute ?? null,
      [Validators.min(1), Validators.max(1000), Validators.pattern(/^\d+$/)],
    ),
    dailyRequestQuota: this.fb.control<number | null>(
      this.data?.connection?.dailyRequestQuota ?? null,
      [Validators.min(1), Validators.max(100000), Validators.pattern(/^\d+$/)],
    ),
    dailyQuotaResetTimezone: this.fb.nonNullable.control(
      this.data?.connection?.dailyQuotaResetTimezone ?? 'UTC',
      [Validators.maxLength(64)],
    ),
    dailyQuotaResetTime: this.fb.nonNullable.control(
      this.data?.connection?.dailyQuotaResetTime ?? '00:00',
      [Validators.pattern(/^([01]\d|2[0-3]):[0-5]\d$/)],
    ),
  });

  constructor() {
    this.form.controls.provider.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((provider) => {
        const url = defaultBaseUrlFor(provider, this.form.controls.baseUrl.value);
        if (url) {
          this.form.controls.baseUrl.setValue(url);
        }
      });
  }

  /** Probes the values in the editor; a saved connection's stored API key is used when left blank. */
  testEditor(): void {
    if (this.isTestingEditor()) {
      return;
    }
    const values = this.form.getRawValue();
    const editing = this.editingConnection();
    const apiKey = values.apiKey.trim();
    this.isTestingEditor.set(true);
    this.editorTestResult.set(null);
    this.llmSettingsService
      .testConnection({
        connectionId: editing?.id ?? null,
        provider: values.provider,
        baseUrl: values.baseUrl.trim(),
        apiKey: apiKey || null,
        // A new connection must never borrow the API key of whichever connection is currently active.
        clearApiKey: values.clearApiKey || (!editing && !apiKey),
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.editorTestResult.set(result);
          this.isTestingEditor.set(false);
        },
        error: (err) => {
          this.editorTestResult.set(failedTestResult(values.provider, values.baseUrl, err));
          this.isTestingEditor.set(false);
        },
      });
  }

  save(): void {
    if (this.form.invalid || this.isSaving()) {
      this.form.markAllAsTouched();
      return;
    }
    this.isSaving.set(true);
    this.errorMessage.set(null);

    const values = this.form.getRawValue();
    const request: UpsertLlmConnectionRequest = {
      name: values.name.trim(),
      provider: values.provider,
      baseUrl: values.baseUrl.trim(),
      apiKey: values.apiKey.trim() || null,
      clearApiKey: values.clearApiKey,
      ollamaKeepAlive: values.ollamaKeepAlive.trim() || '5m',
      ollamaCpuOnly: values.ollamaCpuOnly,
      enableRateLimitSafeguards: values.enableRateLimitSafeguards,
      maxConcurrentRequests: values.maxConcurrentRequests,
      requestsPerMinute: values.requestsPerMinute,
      dailyRequestQuota: values.dailyRequestQuota,
      dailyQuotaResetTimezone: values.dailyQuotaResetTimezone.trim() || 'UTC',
      dailyQuotaResetTime: values.dailyQuotaResetTime.trim() || '00:00',
    };
    const editing = this.editingConnection();
    const request$ = editing
      ? this.llmSettingsService.updateConnection(editing.id, request)
      : this.llmSettingsService.createConnection(request);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => {
        this.isSaving.set(false);
        this.dialogRef.close(saved);
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail || 'Failed to save the connection.');
        this.isSaving.set(false);
      },
    });
  }

  cancel(): void {
    this.dialogRef.close();
  }
}
