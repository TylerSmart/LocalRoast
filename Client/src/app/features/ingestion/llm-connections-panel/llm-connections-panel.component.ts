import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
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
  providerLabel,
} from '../llm-settings-dialog/llm-settings.utils';

/** Manages saved LLM machines/providers: endpoint, authentication and request limits. */
@Component({
  selector: 'app-llm-connections-panel',
  imports: [
    ReactiveFormsModule,
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
  templateUrl: './llm-connections-panel.component.html',
  styleUrl: './llm-connections-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmConnectionsPanelComponent {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly llmSettingsService = inject(LlmSettingsService);

  readonly connections = input.required<LlmConnectionDto[]>();
  /** Emits after a connection is created, updated or deleted. */
  readonly changed = output<void>();

  readonly timezoneOptions = LLM_TIMEZONE_OPTIONS;
  readonly providerLabel = providerLabel;

  readonly isEditing = signal(false);
  readonly editingConnection = signal<LlmConnectionDto | null>(null);
  readonly isSaving = signal(false);
  readonly isTestingEditor = signal(false);
  readonly editorTestResult = signal<LlmConnectionTestResultDto | null>(null);
  readonly cardTestResults = signal<Readonly<Record<string, LlmConnectionTestResultDto>>>({});
  readonly testingIds = signal<ReadonlySet<string>>(new Set());
  readonly errorMessage = signal<string | null>(null);
  readonly hasSavedApiKey = computed(() => this.editingConnection()?.hasApiKey ?? false);

  readonly form = this.fb.nonNullable.group({
    name: this.fb.nonNullable.control('', [Validators.required, Validators.maxLength(100)]),
    provider: this.fb.nonNullable.control<LlmProvider>('Ollama', [Validators.required]),
    baseUrl: this.fb.nonNullable.control('http://localhost:11434', [
      Validators.required,
      Validators.maxLength(500),
    ]),
    apiKey: this.fb.nonNullable.control('', [Validators.maxLength(500)]),
    clearApiKey: this.fb.nonNullable.control(false),
    ollamaKeepAlive: this.fb.nonNullable.control('5m', [Validators.maxLength(32)]),
    ollamaCpuOnly: this.fb.nonNullable.control(false),
    enableRateLimitSafeguards: this.fb.nonNullable.control(true),
    maxConcurrentRequests: this.fb.nonNullable.control(1, [
      Validators.required,
      Validators.min(1),
      Validators.max(5),
    ]),
    requestsPerMinute: this.fb.control<number | null>(null, [
      Validators.min(1),
      Validators.max(1000),
      Validators.pattern(/^\d+$/),
    ]),
    dailyRequestQuota: this.fb.control<number | null>(null, [
      Validators.min(1),
      Validators.max(100000),
      Validators.pattern(/^\d+$/),
    ]),
    dailyQuotaResetTimezone: this.fb.nonNullable.control('UTC', [Validators.maxLength(64)]),
    dailyQuotaResetTime: this.fb.nonNullable.control('00:00', [
      Validators.pattern(/^([01]\d|2[0-3]):[0-5]\d$/),
    ]),
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

  startAdd(): void {
    this.form.reset(
      {
        name: this.connections().length === 0 ? 'Local Ollama' : '',
        provider: 'Ollama',
        baseUrl: 'http://localhost:11434',
        apiKey: '',
        clearApiKey: false,
        ollamaKeepAlive: '5m',
        ollamaCpuOnly: false,
        enableRateLimitSafeguards: true,
        maxConcurrentRequests: 1,
        requestsPerMinute: null,
        dailyRequestQuota: null,
        dailyQuotaResetTimezone: 'UTC',
        dailyQuotaResetTime: '00:00',
      },
      { emitEvent: false },
    );
    this.openEditor(null);
  }

  startEdit(connection: LlmConnectionDto): void {
    this.form.reset(
      {
        name: connection.name,
        provider: connection.provider,
        baseUrl: connection.baseUrl,
        apiKey: '',
        clearApiKey: false,
        ollamaKeepAlive: connection.ollamaKeepAlive,
        ollamaCpuOnly: connection.ollamaCpuOnly,
        enableRateLimitSafeguards: connection.enableRateLimitSafeguards,
        maxConcurrentRequests: connection.maxConcurrentRequests,
        requestsPerMinute: connection.requestsPerMinute,
        dailyRequestQuota: connection.dailyRequestQuota,
        dailyQuotaResetTimezone: connection.dailyQuotaResetTimezone,
        dailyQuotaResetTime: connection.dailyQuotaResetTime,
      },
      { emitEvent: false },
    );
    this.openEditor(connection);
  }

  cancelEdit(): void {
    this.isEditing.set(false);
    this.editingConnection.set(null);
    this.editorTestResult.set(null);
    this.errorMessage.set(null);
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
      next: () => {
        this.isSaving.set(false);
        this.cancelEdit();
        this.changed.emit();
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail || 'Failed to save the connection.');
        this.isSaving.set(false);
      },
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

  testSaved(connection: LlmConnectionDto): void {
    if (this.testingIds().has(connection.id)) {
      return;
    }
    this.testingIds.update((ids) => new Set(ids).add(connection.id));
    this.llmSettingsService
      .testSavedConnection(connection.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.finishCardTest(connection.id, result),
        error: (err) =>
          this.finishCardTest(
            connection.id,
            failedTestResult(connection.provider, connection.baseUrl, err),
          ),
      });
  }

  delete(connection: LlmConnectionDto): void {
    if (this.isSaving()) {
      return;
    }
    this.isSaving.set(true);
    this.errorMessage.set(null);
    this.llmSettingsService
      .deleteConnection(connection.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSaving.set(false);
          if (this.editingConnection()?.id === connection.id) {
            this.cancelEdit();
          }
          this.changed.emit();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to delete the connection.');
          this.isSaving.set(false);
        },
      });
  }

  private openEditor(connection: LlmConnectionDto | null): void {
    this.editingConnection.set(connection);
    this.editorTestResult.set(null);
    this.errorMessage.set(null);
    this.isEditing.set(true);
  }

  private finishCardTest(id: string, result: LlmConnectionTestResultDto): void {
    this.cardTestResults.update((results) => ({ ...results, [id]: result }));
    this.testingIds.update((ids) => {
      const next = new Set(ids);
      next.delete(id);
      return next;
    });
  }
}
