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
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
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

/** Saves a model on a connection together with its tuning so workflows can select it by name. */
@Component({
  selector: 'app-llm-model-configurations-panel',
  imports: [
    ReactiveFormsModule,
    MatAutocompleteModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatSlideToggleModule,
  ],
  templateUrl: './llm-model-configurations-panel.component.html',
  styleUrl: './llm-model-configurations-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmModelConfigurationsPanelComponent {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly llmSettingsService = inject(LlmSettingsService);

  readonly connections = input.required<LlmConnectionDto[]>();
  readonly configurations = input.required<LlmModelConfigurationDto[]>();
  readonly profiles = input.required<LlmModelProfile[]>();
  /** Emits after a configuration is created, updated or deleted. */
  readonly changed = output<void>();
  readonly addConnectionRequested = output<void>();
  readonly saveAsProfileRequested = output<LlmModelConfigurationDto>();

  readonly providerLabel = providerLabel;

  readonly isEditing = signal(false);
  readonly editingConfiguration = signal<LlmModelConfigurationDto | null>(null);
  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly discoveredModels = signal<Readonly<Record<string, string[]>>>({});
  readonly loadingModelsFor = signal<string | null>(null);
  readonly modelsError = signal<string | null>(null);
  readonly selectedProfileId = signal('');
  readonly appliedProfileId = signal<string | null>(null);
  readonly profileMessage = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    name: this.fb.nonNullable.control('', [Validators.maxLength(150)]),
    connectionId: this.fb.nonNullable.control('', [Validators.required]),
    model: this.fb.nonNullable.control('', [Validators.required, Validators.maxLength(200)]),
    enableThinking: this.fb.nonNullable.control(false),
    temperature: this.fb.control<number | null>(null, [Validators.min(0), Validators.max(2)]),
    topP: this.fb.control<number | null>(null, [Validators.min(0), Validators.max(1)]),
    topK: this.fb.control<number | null>(null, [
      Validators.min(1),
      Validators.max(1000),
      Validators.pattern(/^\d+$/),
    ]),
    contextWindowTokens: this.fb.nonNullable.control(DEFAULT_CONTEXT_WINDOW, [
      Validators.required,
      Validators.min(2048),
      Validators.max(65536),
    ]),
    requestTimeoutSeconds: this.fb.nonNullable.control(DEFAULT_TIMEOUT_SECONDS, [
      Validators.required,
      Validators.min(5),
      Validators.max(1800),
    ]),
    ollamaGpuLayers: this.fb.control<number | null>(null, [
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
    this.form.controls.connectionId.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((id) => {
        this.modelsError.set(null);
        if (this.isEditing() && id && !this.discoveredModels()[id]) {
          this.refreshModels();
        }
      });
  }

  startAdd(): void {
    const connectionId =
      this.connectionIdValue() && this.connections().some((c) => c.id === this.connectionIdValue())
        ? this.connectionIdValue()
        : (this.connections()[0]?.id ?? '');
    this.resetForm(null, {
      name: '',
      connectionId,
      model: '',
      enableThinking: false,
      temperature: null,
      topP: null,
      topK: null,
      contextWindowTokens: DEFAULT_CONTEXT_WINDOW,
      requestTimeoutSeconds: DEFAULT_TIMEOUT_SECONDS,
      ollamaGpuLayers: null,
    });
  }

  startEdit(configuration: LlmModelConfigurationDto): void {
    this.resetForm(configuration, this.valuesFrom(configuration, configuration.name));
  }

  duplicate(configuration: LlmModelConfigurationDto): void {
    this.resetForm(null, this.valuesFrom(configuration, `${configuration.name} (copy)`));
    this.appliedProfileId.set(configuration.profileId);
  }

  cancelEdit(): void {
    this.isEditing.set(false);
    this.editingConfiguration.set(null);
    this.errorMessage.set(null);
    this.profileMessage.set(null);
    this.modelsError.set(null);
  }

  /** Fills the editor with a profile's recommendations, opening a new configuration if needed. */
  applyProfile(profile: LlmModelProfile): void {
    if (!this.isEditing()) {
      this.startAdd();
    }
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
      next: () => {
        this.isSaving.set(false);
        this.cancelEdit();
        this.changed.emit();
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail || 'Failed to save the model configuration.');
        this.isSaving.set(false);
      },
    });
  }

  delete(configuration: LlmModelConfigurationDto): void {
    if (this.isSaving()) {
      return;
    }
    this.isSaving.set(true);
    this.errorMessage.set(null);
    this.llmSettingsService
      .deleteModelConfiguration(configuration.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSaving.set(false);
          if (this.editingConfiguration()?.id === configuration.id) {
            this.cancelEdit();
          }
          this.changed.emit();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to delete the model configuration.');
          this.isSaving.set(false);
        },
      });
  }

  profileName(id: string | null): string | null {
    return id ? (this.profiles().find((profile) => profile.id === id)?.displayName ?? null) : null;
  }

  private valuesFrom(configuration: LlmModelConfigurationDto, name: string) {
    return {
      name,
      connectionId: configuration.connectionId,
      model: configuration.model,
      enableThinking: configuration.enableThinking,
      temperature: configuration.temperature,
      topP: configuration.topP,
      topK: configuration.topK,
      contextWindowTokens: configuration.contextWindowTokens,
      requestTimeoutSeconds: configuration.requestTimeoutSeconds,
      ollamaGpuLayers: configuration.ollamaGpuLayers,
    };
  }

  private resetForm(
    configuration: LlmModelConfigurationDto | null,
    values: ReturnType<LlmModelConfigurationsPanelComponent['form']['getRawValue']>,
  ): void {
    this.form.reset(values);
    this.editingConfiguration.set(configuration);
    this.appliedProfileId.set(configuration?.profileId ?? null);
    const profiles = this.profiles();
    this.selectedProfileId.set(
      configuration?.profileId ??
        matchModelProfile(profiles, values.model)?.id ??
        profiles[0]?.id ??
        '',
    );
    this.errorMessage.set(null);
    this.profileMessage.set(null);
    this.modelsError.set(null);
    this.isEditing.set(true);
    if (values.connectionId && !this.discoveredModels()[values.connectionId]) {
      this.refreshModels();
    }
  }
}
