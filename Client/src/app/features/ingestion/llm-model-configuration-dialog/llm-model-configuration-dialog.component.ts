import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
  UpsertLlmModelConfigurationRequest,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  LLM_TIMEZONE_OPTIONS,
  filterModels,
  providerLabel,
} from '../llm-settings-dialog/llm-settings.utils';

export interface LlmModelConfigurationDialogData {
  connections: LlmConnectionDto[];
  existingConfigurations?: LlmModelConfigurationDto[];
  configuration?: LlmModelConfigurationDto | null;
}

@Component({
  selector: 'app-llm-model-configuration-dialog',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
  ],
  templateUrl: './llm-model-configuration-dialog.component.html',
  styleUrl: './llm-model-configuration-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmModelConfigurationDialogComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly llmSettingsService = inject(LlmSettingsService);
  private readonly dialogRef = inject(MatDialogRef<LlmModelConfigurationDialogComponent>);
  private readonly data = inject<LlmModelConfigurationDialogData>(MAT_DIALOG_DATA);

  readonly providerLabel = providerLabel;
  readonly timezoneOptions = LLM_TIMEZONE_OPTIONS;

  readonly connections = signal<LlmConnectionDto[]>(this.data.connections);
  readonly existingConfigurations = signal<LlmModelConfigurationDto[]>(
    this.data.existingConfigurations ?? [],
  );
  readonly editingConfiguration = signal<LlmModelConfigurationDto | null>(
    this.data.configuration ?? null,
  );
  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly discoveredModels = signal<Readonly<Record<string, string[]>>>({});
  readonly loadingModelsFor = signal<string | null>(null);
  readonly modelsError = signal<string | null>(null);

  private readonly sourceConfig = this.data.configuration ?? null;
  private readonly initialConnectionId =
    this.sourceConfig?.connectionId ?? this.data.connections[0]?.id ?? '';

  readonly form = this.fb.nonNullable.group({
    name: this.fb.nonNullable.control(this.sourceConfig?.name ?? '', [Validators.maxLength(150)]),
    connectionId: this.fb.nonNullable.control(this.initialConnectionId, [Validators.required]),
    model: this.fb.nonNullable.control(this.sourceConfig?.model ?? '', [
      Validators.required,
      Validators.maxLength(200),
    ]),
    ollamaGpuLayers: this.fb.control<number | null>(this.sourceConfig?.ollamaGpuLayers ?? null, [
      Validators.min(0),
      Validators.max(2147483647),
      Validators.pattern(/^\d+$/),
    ]),
    maxConcurrentRequests: this.fb.control<number | null>(
      this.sourceConfig?.maxConcurrentRequests ?? null,
      [Validators.min(1), Validators.max(5), Validators.pattern(/^\d+$/)],
    ),
    requestsPerMinute: this.fb.control<number | null>(
      this.sourceConfig?.requestsPerMinute ?? null,
      [Validators.min(1), Validators.max(1000), Validators.pattern(/^\d+$/)],
    ),
    dailyRequestQuota: this.fb.control<number | null>(
      this.sourceConfig?.dailyRequestQuota ?? null,
      [Validators.min(1), Validators.max(100000), Validators.pattern(/^\d+$/)],
    ),
    dailyQuotaResetTimezone: this.fb.nonNullable.control(
      this.sourceConfig?.dailyQuotaResetTimezone ?? '',
      [Validators.maxLength(64)],
    ),
    dailyQuotaResetTime: this.fb.nonNullable.control(this.sourceConfig?.dailyQuotaResetTime ?? '', [
      Validators.pattern(/^$|^([01]\d|2[0-3]):[0-5]\d$/),
    ]),
  });

  private readonly connectionIdValue = toSignal(this.form.controls.connectionId.valueChanges, {
    initialValue: this.form.controls.connectionId.value,
  });
  private readonly modelQuery = toSignal(this.form.controls.model.valueChanges, {
    initialValue: this.form.controls.model.value,
  });
  private readonly nameValue = toSignal(this.form.controls.name.valueChanges, {
    initialValue: this.form.controls.name.value,
  });

  readonly selectedConnection = computed(
    () => this.connections().find((c) => c.id === this.connectionIdValue()) ?? null,
  );
  readonly availableModels = computed(
    () => this.discoveredModels()[this.connectionIdValue()] ?? [],
  );
  readonly filteredModels = computed(() => filterModels(this.availableModels(), this.modelQuery()));
  readonly savedModelsOnSelectedConnection = computed(() => {
    const connId = this.connectionIdValue();
    const editingId = this.editingConfiguration()?.id ?? null;
    return new Set(
      this.existingConfigurations()
        .filter((c) => c.connectionId === connId && c.id !== editingId)
        .map((c) => c.model.trim().toLowerCase()),
    );
  });
  readonly isDuplicateModelOnConnection = computed(() => {
    const normalizedModel = this.modelQuery().trim().toLowerCase();
    return !!normalizedModel && this.savedModelsOnSelectedConnection().has(normalizedModel);
  });
  readonly defaultName = computed(() => {
    const model = this.modelQuery().trim();
    const connection = this.selectedConnection();
    return model && connection ? `${model} @ ${connection.name}` : '';
  });
  readonly effectiveName = computed(() => this.nameValue().trim() || this.defaultName());
  readonly isLoadingModels = computed(
    () => !!this.connectionIdValue() && this.loadingModelsFor() === this.connectionIdValue(),
  );

  constructor() {
    this.form.controls.connectionId.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((id) => {
        this.modelsError.set(null);
        if (id && !this.discoveredModels()[id]) {
          this.refreshModels();
        }
      });
  }

  ngOnInit(): void {
    const connectionId = this.form.controls.connectionId.value;
    if (connectionId && !this.discoveredModels()[connectionId]) {
      this.refreshModels();
    }
  }

  isModelAlreadySaved(model: string): boolean {
    return this.savedModelsOnSelectedConnection().has(model.trim().toLowerCase());
  }

  refreshModels(): void {
    const id = this.form.controls.connectionId.value;
    if (!id || this.loadingModelsFor() === id) {
      return;
    }
    this.loadingModelsFor.set(id);
    this.modelsError.set(null);
    const isCurrent = () => this.form.controls.connectionId.value === id;
    this.llmSettingsService
      .testSavedConnection(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          if (this.loadingModelsFor() === id) {
            this.loadingModelsFor.set(null);
          }
          if (result.isReachable) {
            this.discoveredModels.update((models) => ({ ...models, [id]: result.models }));
          } else if (isCurrent()) {
            this.modelsError.set(result.errorMessage || 'The connection is unreachable.');
          }
        },
        error: (err) => {
          if (this.loadingModelsFor() === id) {
            this.loadingModelsFor.set(null);
          }
          if (isCurrent()) {
            this.modelsError.set(err?.error?.detail || 'Failed to list models.');
          }
        },
      });
  }

  save(): void {
    if (this.form.invalid || this.isDuplicateModelOnConnection() || this.isSaving()) {
      this.form.markAllAsTouched();
      return;
    }
    this.isSaving.set(true);
    this.errorMessage.set(null);

    const values = this.form.getRawValue();
    const request: UpsertLlmModelConfigurationRequest = {
      name: this.effectiveName(),
      connectionId: values.connectionId,
      model: values.model.trim(),
      ollamaGpuLayers: values.ollamaGpuLayers,
      maxConcurrentRequests: values.maxConcurrentRequests,
      requestsPerMinute: values.requestsPerMinute,
      dailyRequestQuota: values.dailyRequestQuota,
      dailyQuotaResetTimezone: values.dailyQuotaResetTimezone.trim() || null,
      dailyQuotaResetTime: values.dailyQuotaResetTime.trim() || null,
    };
    const editing = this.editingConfiguration();
    const request$ = editing
      ? this.llmSettingsService.updateModelConfiguration(editing.id, request)
      : this.llmSettingsService.createModelConfiguration(request);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => {
        this.isSaving.set(false);
        this.dialogRef.close(saved);
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail || 'Failed to save the model configuration.');
        this.isSaving.set(false);
      },
    });
  }

  cancel(): void {
    this.dialogRef.close();
  }
}
