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
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
  UpsertLlmModelConfigurationRequest,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  filterModels,
  matchModelProfile,
  providerLabel,
} from '../llm-settings-dialog/llm-settings.utils';

const DEFAULT_CONTEXT_WINDOW = 8192;
const DEFAULT_TIMEOUT_SECONDS = 300;

export interface LlmModelConfigurationDialogData {
  connections: LlmConnectionDto[];
  profiles: LlmModelProfile[];
  configuration?: LlmModelConfigurationDto | null;
  duplicateFrom?: LlmModelConfigurationDto | null;
  initialProfile?: LlmModelProfile | null;
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
    MatSlideToggleModule,
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

  readonly connections = signal<LlmConnectionDto[]>(this.data.connections);
  readonly profiles = signal<LlmModelProfile[]>(this.data.profiles);
  readonly editingConfiguration = signal<LlmModelConfigurationDto | null>(
    this.data.configuration ?? null,
  );
  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly discoveredModels = signal<Readonly<Record<string, string[]>>>({});
  readonly loadingModelsFor = signal<string | null>(null);
  readonly modelsError = signal<string | null>(null);
  readonly selectedProfileId = signal('');
  readonly appliedProfileId = signal<string | null>(null);
  readonly profileMessage = signal<string | null>(null);

  private readonly sourceConfig = this.data.configuration ?? this.data.duplicateFrom ?? null;
  private readonly initialConnectionId =
    this.sourceConfig?.connectionId ?? this.data.connections[0]?.id ?? '';

  readonly form = this.fb.nonNullable.group({
    name: this.fb.nonNullable.control(
      this.data.configuration
        ? this.data.configuration.name
        : this.data.duplicateFrom
          ? `${this.data.duplicateFrom.name} (copy)`
          : '',
      [Validators.maxLength(150)],
    ),
    connectionId: this.fb.nonNullable.control(this.initialConnectionId, [Validators.required]),
    model: this.fb.nonNullable.control(this.sourceConfig?.model ?? '', [
      Validators.required,
      Validators.maxLength(200),
    ]),
    enableThinking: this.fb.nonNullable.control(this.sourceConfig?.enableThinking ?? false),
    temperature: this.fb.control<number | null>(this.sourceConfig?.temperature ?? null, [
      Validators.min(0),
      Validators.max(2),
    ]),
    topP: this.fb.control<number | null>(this.sourceConfig?.topP ?? null, [
      Validators.min(0),
      Validators.max(1),
    ]),
    topK: this.fb.control<number | null>(this.sourceConfig?.topK ?? null, [
      Validators.min(1),
      Validators.max(1000),
      Validators.pattern(/^\d+$/),
    ]),
    contextWindowTokens: this.fb.nonNullable.control(
      this.sourceConfig?.contextWindowTokens ?? DEFAULT_CONTEXT_WINDOW,
      [Validators.required, Validators.min(2048), Validators.max(65536)],
    ),
    requestTimeoutSeconds: this.fb.nonNullable.control(
      this.sourceConfig?.requestTimeoutSeconds ?? DEFAULT_TIMEOUT_SECONDS,
      [Validators.required, Validators.min(5), Validators.max(1800)],
    ),
    ollamaGpuLayers: this.fb.control<number | null>(this.sourceConfig?.ollamaGpuLayers ?? null, [
      Validators.min(0),
      Validators.max(2147483647),
      Validators.pattern(/^\d+$/),
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
  readonly selectedProfile = computed(
    () => this.profiles().find((profile) => profile.id === this.selectedProfileId()) ?? null,
  );
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
    this.appliedProfileId.set(this.sourceConfig?.profileId ?? null);
    const profiles = this.profiles();
    this.selectedProfileId.set(
      this.sourceConfig?.profileId ??
        matchModelProfile(profiles, this.form.controls.model.value)?.id ??
        profiles[0]?.id ??
        '',
    );

    if (this.data.initialProfile) {
      this.applyProfile(this.data.initialProfile);
    }

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

  applyProfile(profile: LlmModelProfile): void {
    if (!this.form.controls.model.value.trim()) {
      this.form.controls.model.setValue(profile.modelTag);
    }
    this.form.patchValue({
      enableThinking: profile.enableThinking,
      temperature: profile.temperature,
      topP: profile.topP,
      topK: profile.topK,
      contextWindowTokens: profile.contextWindowTokens,
      requestTimeoutSeconds: profile.requestTimeoutSeconds,
    });
    this.form.markAsDirty();
    this.selectedProfileId.set(profile.id);
    this.appliedProfileId.set(profile.id);
    this.profileMessage.set(
      `Applied ${profile.displayName} recommendations. Save to keep them. ${profile.notes.join(' ')}`.trim(),
    );
  }

  applySelectedProfile(): void {
    const profile = this.selectedProfile();
    if (profile) {
      this.applyProfile(profile);
    }
  }

  onProfileSelected(id: string): void {
    this.selectedProfileId.set(id);
    this.profileMessage.set(null);
  }

  onModelSelected(model: string): void {
    const match = matchModelProfile(this.profiles(), model);
    if (match) {
      this.selectedProfileId.set(match.id);
      this.profileMessage.set(
        `${match.displayName} matches this model. Apply it to use its tuning.`,
      );
    }
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

  onContextWindowSliderInput(event: Event): void {
    this.form.controls.contextWindowTokens.setValue(
      (event.target as HTMLInputElement).valueAsNumber,
    );
  }

  save(): void {
    if (this.form.invalid || this.isSaving()) {
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
      profileId: this.appliedProfileId(),
      enableThinking: values.enableThinking,
      temperature: values.temperature,
      topP: values.topP,
      topK: values.topK,
      contextWindowTokens: values.contextWindowTokens,
      requestTimeoutSeconds: values.requestTimeoutSeconds,
      ollamaGpuLayers: values.ollamaGpuLayers,
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
