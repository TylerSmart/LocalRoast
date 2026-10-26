import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import type {
  LlmConnectionTestResultDto,
  LlmProvider,
  LlmSettingsDto,
} from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';

@Component({
  selector: 'app-llm-settings-dialog',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatAutocompleteModule,
    MatSlideToggleModule,
    MatCheckboxModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  templateUrl: './llm-settings-dialog.component.html',
  styleUrl: './llm-settings-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmSettingsDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly llmSettingsService = inject(LlmSettingsService);
  private readonly ingestionService = inject(IngestionService);
  private readonly dialogRef = inject(MatDialogRef<LlmSettingsDialogComponent>);

  readonly isLoading = signal<boolean>(true);
  readonly isTesting = signal<boolean>(false);
  readonly isSaving = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);
  readonly testResult = signal<LlmConnectionTestResultDto | null>(null);
  readonly discoveredModels = signal<string[]>([]);
  readonly hasSavedApiKey = signal<boolean>(false);
  readonly apiKeyMasked = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    provider: this.fb.nonNullable.control<LlmProvider>('Ollama', [Validators.required]),
    baseUrl: this.fb.nonNullable.control<string>('http://localhost:11434', [
      Validators.required,
      Validators.maxLength(500),
    ]),
    apiKey: this.fb.nonNullable.control<string>(''),
    clearApiKey: this.fb.nonNullable.control<boolean>(false),
    textModel: this.fb.nonNullable.control<string>(''),
    visionModel: this.fb.nonNullable.control<string>(''),
    ollamaKeepAlive: this.fb.nonNullable.control<string>('5m'),
    requestTimeoutSeconds: this.fb.nonNullable.control<number>(120, [
      Validators.required,
      Validators.min(5),
      Validators.max(600),
    ]),
    autoPublishHighConfidenceText: this.fb.nonNullable.control<boolean>(false),
  });

  ngOnInit(): void {
    this.loadSettings();

    this.form.controls.provider.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((provider) => {
        const currentUrl = this.form.controls.baseUrl.value.trim();
        if (provider === 'Ollama' && (currentUrl === '' || currentUrl.includes(':1234'))) {
          this.form.controls.baseUrl.setValue('http://localhost:11434');
        } else if (
          provider === 'OpenAiCompatible' &&
          (currentUrl === '' || currentUrl === 'http://localhost:11434')
        ) {
          this.form.controls.baseUrl.setValue('http://localhost:1234/v1');
        }
      });
  }

  loadSettings(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.llmSettingsService
      .getSettings()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (settings: LlmSettingsDto) => {
          this.hasSavedApiKey.set(settings.hasApiKey);
          this.apiKeyMasked.set(settings.apiKeyMasked);

          this.form.patchValue({
            provider: settings.provider,
            baseUrl: settings.baseUrl,
            apiKey: '',
            clearApiKey: false,
            textModel: settings.textModel ?? '',
            visionModel: settings.visionModel ?? '',
            ollamaKeepAlive: settings.ollamaKeepAlive,
            requestTimeoutSeconds: settings.requestTimeoutSeconds,
            autoPublishHighConfidenceText: settings.autoPublishHighConfidenceText,
          });

          this.isLoading.set(false);
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to load LLM settings.');
          this.isLoading.set(false);
        },
      });
  }

  testConnection(): void {
    if (this.isTesting()) {
      return;
    }

    this.isTesting.set(true);
    this.testResult.set(null);
    this.errorMessage.set(null);

    const values = this.form.getRawValue();
    this.llmSettingsService
      .testConnection({
        provider: values.provider,
        baseUrl: values.baseUrl.trim(),
        apiKey: values.apiKey.trim() || undefined,
        clearApiKey: values.clearApiKey,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.testResult.set(result);
          if (result.isReachable && result.models.length > 0) {
            this.discoveredModels.set(result.models);
            if (!this.form.controls.textModel.value.trim() && result.models.length > 0) {
              this.form.controls.textModel.setValue(result.models[0]);
            }
          }
          this.isTesting.set(false);
        },
        error: (err) => {
          this.testResult.set({
            isReachable: false,
            provider: values.provider,
            normalizedBaseUrl: values.baseUrl.trim(),
            latencyMs: 0,
            models: [],
            errorMessage: err?.error?.detail || err?.message || 'Connection test failed.',
          });
          this.isTesting.set(false);
        },
      });
  }

  onSave(): void {
    if (this.form.invalid || this.isSaving()) {
      return;
    }

    this.isSaving.set(true);
    this.errorMessage.set(null);

    const values = this.form.getRawValue();
    this.llmSettingsService
      .updateSettings({
        provider: values.provider,
        baseUrl: values.baseUrl.trim(),
        apiKey: values.apiKey.trim() || undefined,
        clearApiKey: values.clearApiKey,
        textModel: values.textModel.trim() || null,
        visionModel: values.visionModel.trim() || null,
        ollamaKeepAlive: values.ollamaKeepAlive.trim() || '5m',
        requestTimeoutSeconds: values.requestTimeoutSeconds,
        autoPublishHighConfidenceText: values.autoPublishHighConfidenceText,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSaving.set(false);
          this.ingestionService.refreshSummary();
          this.dialogRef.close(true);
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to save settings.');
          this.isSaving.set(false);
        },
      });
  }

  onCancel(): void {
    this.dialogRef.close(false);
  }
}
