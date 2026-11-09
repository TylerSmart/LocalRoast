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
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import type {
  LlmConnectionTestResultDto,
  LlmModelProfile,
  LlmProvider,
  LlmSettingsDto,
  UpsertLlmModelProfileRequest,
} from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';

/** Returns the profile with the longest match prefix that starts the model tag. */
export function matchModelProfile(
  profiles: readonly LlmModelProfile[],
  model: string | null | undefined,
): LlmModelProfile | null {
  const tag = model?.trim().toLowerCase() ?? '';
  if (!tag) {
    return null;
  }
  let best: LlmModelProfile | null = null;
  let bestLength = 0;
  for (const profile of profiles) {
    for (const prefix of profile.matchPrefixes) {
      if (tag.startsWith(prefix.toLowerCase()) && prefix.length > bestLength) {
        best = profile;
        bestLength = prefix.length;
      }
    }
  }
  return best;
}

/**
 * Filters discovered model names by every whitespace-separated word in the query (case-insensitive),
 * so "gemini flash" matches "google/gemini-3.8-flash". A blank query or one that exactly names a
 * model returns the full list so the user can still switch models after picking one.
 */
export function filterModels(
  models: readonly string[],
  query: string | null | undefined,
): string[] {
  const normalized = query?.trim().toLowerCase() ?? '';
  if (!normalized || models.some((model) => model.toLowerCase() === normalized)) {
    return [...models];
  }
  const terms = normalized.split(/\s+/);
  return models.filter((model) => {
    const name = model.toLowerCase();
    return terms.every((term) => name.includes(term));
  });
}

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
    MatTabsModule,
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
  readonly profiles = signal<LlmModelProfile[]>([]);
  readonly refineProfiles = computed(() =>
    this.profiles().filter((profile) => profile.recommendedAsRefineModel),
  );
  readonly selectedProfileId = signal<string>('');
  readonly selectedRefineProfileId = signal<string>('');
  readonly selectedProfile = computed(
    () => this.profiles().find((profile) => profile.id === this.selectedProfileId()) ?? null,
  );
  readonly selectedRefineProfile = computed(
    () =>
      this.refineProfiles().find((profile) => profile.id === this.selectedRefineProfileId()) ??
      null,
  );
  readonly profileMessage = signal<string | null>(null);
  readonly refineProfileMessage = signal<string | null>(null);

  readonly selectedTabIndex = signal<number>(0);
  readonly isEditingProfile = signal<boolean>(false);
  readonly editingProfileId = signal<string | null>(null);
  readonly isSavingProfile = signal<boolean>(false);
  readonly profileCrudError = signal<string | null>(null);

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
    contextWindowTokens: this.fb.nonNullable.control<number>(8192, [
      Validators.required,
      Validators.min(2048),
      Validators.max(65536),
    ]),
    ollamaCpuOnly: this.fb.nonNullable.control<boolean>(false),
    requestTimeoutSeconds: this.fb.nonNullable.control<number>(120, [
      Validators.required,
      Validators.min(5),
      Validators.max(1800),
    ]),
    autoPublishHighConfidenceText: this.fb.nonNullable.control<boolean>(false),
    enableThinking: this.fb.nonNullable.control<boolean>(false),
    enableIngestion: this.fb.nonNullable.control<boolean>(true),
    maxIngestionRetries: this.fb.nonNullable.control<number>(3, [
      Validators.required,
      Validators.min(0),
      Validators.max(10),
    ]),
    temperature: this.fb.control<number | null>(null, [Validators.min(0), Validators.max(2)]),
    topP: this.fb.control<number | null>(null, [Validators.min(0), Validators.max(1)]),
    topK: this.fb.control<number | null>(null, [
      Validators.min(1),
      Validators.max(1000),
      Validators.pattern(/^\d+$/),
    ]),
    refineModel: this.fb.nonNullable.control<string>('', [Validators.maxLength(200)]),
    refineEnableThinking: this.fb.nonNullable.control<boolean>(true),
    refineTemperature: this.fb.control<number | null>(null, [Validators.min(0), Validators.max(2)]),
    refineTopP: this.fb.control<number | null>(null, [Validators.min(0), Validators.max(1)]),
    refineTopK: this.fb.control<number | null>(null, [
      Validators.min(1),
      Validators.max(1000),
      Validators.pattern(/^\d+$/),
    ]),
    refineRequestTimeoutSeconds: this.fb.nonNullable.control<number>(1200, [
      Validators.required,
      Validators.min(5),
      Validators.max(1800),
    ]),
  });

  readonly profileForm = this.fb.nonNullable.group({
    displayName: this.fb.nonNullable.control<string>('', [
      Validators.required,
      Validators.maxLength(150),
    ]),
    modelTag: this.fb.nonNullable.control<string>('', [
      Validators.required,
      Validators.maxLength(200),
    ]),
    matchPrefixes: this.fb.nonNullable.control<string>(''),
    summary: this.fb.nonNullable.control<string>('', [Validators.maxLength(500)]),
    contextWindowTokens: this.fb.nonNullable.control<number>(32768, [
      Validators.required,
      Validators.min(2048),
      Validators.max(65536),
    ]),
    requestTimeoutSeconds: this.fb.nonNullable.control<number>(600, [
      Validators.required,
      Validators.min(5),
      Validators.max(1800),
    ]),
    enableThinking: this.fb.nonNullable.control<boolean>(false),
    recommendedAsRefineModel: this.fb.nonNullable.control<boolean>(false),
    temperature: this.fb.control<number | null>(null, [Validators.min(0), Validators.max(2)]),
    topP: this.fb.control<number | null>(null, [Validators.min(0), Validators.max(1)]),
    topK: this.fb.control<number | null>(null, [Validators.min(1), Validators.max(1000)]),
    notes: this.fb.nonNullable.control<string>(''),
  });

  private readonly textModelQuery = toSignal(this.form.controls.textModel.valueChanges, {
    initialValue: this.form.controls.textModel.value,
  });
  private readonly visionModelQuery = toSignal(this.form.controls.visionModel.valueChanges, {
    initialValue: this.form.controls.visionModel.value,
  });
  private readonly refineModelQuery = toSignal(this.form.controls.refineModel.valueChanges, {
    initialValue: this.form.controls.refineModel.value,
  });
  readonly filteredTextModels = computed(() =>
    filterModels(this.discoveredModels(), this.textModelQuery()),
  );
  readonly filteredVisionModels = computed(() =>
    filterModels(this.discoveredModels(), this.visionModelQuery()),
  );
  readonly filteredRefineModels = computed(() =>
    filterModels(this.discoveredModels(), this.refineModelQuery()),
  );

  ngOnInit(): void {
    this.loadSettings();
    this.loadProfiles();

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
            contextWindowTokens: settings.contextWindowTokens ?? 8192,
            ollamaCpuOnly: settings.ollamaCpuOnly ?? false,
            requestTimeoutSeconds: settings.requestTimeoutSeconds,
            autoPublishHighConfidenceText: settings.autoPublishHighConfidenceText,
            enableThinking: settings.enableThinking ?? false,
            enableIngestion: settings.enableIngestion ?? true,
            maxIngestionRetries: settings.maxIngestionRetries ?? 3,
            temperature: settings.temperature ?? null,
            topP: settings.topP ?? null,
            topK: settings.topK ?? null,
            refineModel: settings.refineModel ?? '',
            refineEnableThinking: settings.refineEnableThinking ?? true,
            refineTemperature: settings.refineTemperature ?? null,
            refineTopP: settings.refineTopP ?? null,
            refineTopK: settings.refineTopK ?? null,
            refineRequestTimeoutSeconds: settings.refineRequestTimeoutSeconds ?? 1200,
          });

          this.isLoading.set(false);
          this.selectDefaultProfiles();

          if (settings.baseUrl && settings.baseUrl.trim().length > 0) {
            this.testConnection();
          }
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
        contextWindowTokens: values.contextWindowTokens,
        ollamaCpuOnly: values.ollamaCpuOnly,
        requestTimeoutSeconds: values.requestTimeoutSeconds,
        autoPublishHighConfidenceText: values.autoPublishHighConfidenceText,
        enableThinking: values.enableThinking,
        enableIngestion: values.enableIngestion,
        maxIngestionRetries: values.maxIngestionRetries,
        temperature: values.temperature,
        topP: values.topP,
        topK: values.topK,
        refineModel: values.refineModel.trim() || null,
        refineEnableThinking: values.refineEnableThinking,
        refineTemperature: values.refineTemperature,
        refineTopP: values.refineTopP,
        refineTopK: values.refineTopK,
        refineRequestTimeoutSeconds: values.refineRequestTimeoutSeconds,
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

  onContextWindowSliderInput(event: Event): void {
    const slider = event.target as HTMLInputElement;
    this.form.controls.contextWindowTokens.setValue(slider.valueAsNumber);
  }

  loadProfiles(): void {
    this.llmSettingsService
      .getModelProfiles()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (profiles) => {
          this.profiles.set(profiles);
          this.selectDefaultProfiles();
        },
        error: () => this.profiles.set([]),
      });
  }

  /**
   * Fills the model settings with a profile's measured recommendations. The model name is kept
   * so derived models (for example with a custom num_gpu) can use a profile.
   */
  applyProfile(): void {
    const profile = this.selectedProfile();
    if (!profile) {
      return;
    }
    const controls = this.form.controls;
    if (!controls.textModel.value.trim()) {
      controls.textModel.setValue(profile.modelTag);
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
    this.profileMessage.set(
      `Applied ${profile.displayName} recommendations. Save to keep them. ${profile.notes.join(' ')}`.trim(),
    );
  }

  applyRefineProfile(): void {
    const profile = this.selectedRefineProfile();
    if (!profile) {
      return;
    }
    const controls = this.form.controls;
    if (!controls.refineModel.value.trim()) {
      controls.refineModel.setValue(profile.modelTag);
    }
    this.form.patchValue({
      refineEnableThinking: profile.enableThinking,
      refineTemperature: profile.temperature,
      refineTopP: profile.topP,
      refineTopK: profile.topK,
      refineRequestTimeoutSeconds: profile.requestTimeoutSeconds,
    });
    this.form.markAsDirty();
    this.refineProfileMessage.set(
      `Applied ${profile.displayName} recommendations. Save to keep them. ${profile.notes.join(' ')}`.trim(),
    );
  }

  onProfileSelected(id: string): void {
    this.selectedProfileId.set(id);
    this.profileMessage.set(null);
  }

  onRefineProfileSelected(id: string): void {
    this.selectedRefineProfileId.set(id);
    this.refineProfileMessage.set(null);
  }

  saveCurrentSettingsAsProfile(): void {
    const values = this.form.getRawValue();
    const modelTag = values.textModel.trim() || 'custom:model';
    const displayName = values.textModel.trim()
      ? `${values.textModel.trim()} Profile`
      : 'New Custom Profile';
    this.profileForm.reset({
      displayName,
      modelTag,
      matchPrefixes: values.textModel.trim() || '',
      summary: 'Custom profile saved from current settings.',
      contextWindowTokens: values.contextWindowTokens || 32768,
      requestTimeoutSeconds: values.requestTimeoutSeconds || 600,
      enableThinking: values.enableThinking,
      recommendedAsRefineModel: false,
      temperature: values.temperature,
      topP: values.topP,
      topK: values.topK,
      notes: '',
    });
    this.editingProfileId.set(null);
    this.isEditingProfile.set(true);
    this.profileCrudError.set(null);
    this.selectedTabIndex.set(3);
  }

  startAddProfile(): void {
    this.profileForm.reset({
      displayName: '',
      modelTag: '',
      matchPrefixes: '',
      summary: '',
      contextWindowTokens: 32768,
      requestTimeoutSeconds: 600,
      enableThinking: false,
      recommendedAsRefineModel: false,
      temperature: null,
      topP: null,
      topK: null,
      notes: '',
    });
    this.editingProfileId.set(null);
    this.isEditingProfile.set(true);
    this.profileCrudError.set(null);
  }

  startEditProfile(profile: LlmModelProfile): void {
    this.profileForm.reset({
      displayName: profile.displayName,
      modelTag: profile.modelTag,
      matchPrefixes: profile.matchPrefixes?.join(', ') ?? '',
      summary: profile.summary ?? '',
      contextWindowTokens: profile.contextWindowTokens ?? 32768,
      requestTimeoutSeconds: profile.requestTimeoutSeconds ?? 600,
      enableThinking: profile.enableThinking ?? false,
      recommendedAsRefineModel: profile.recommendedAsRefineModel ?? false,
      temperature: profile.temperature ?? null,
      topP: profile.topP ?? null,
      topK: profile.topK ?? null,
      notes: profile.notes?.join('\n') ?? '',
    });
    this.editingProfileId.set(profile.id);
    this.isEditingProfile.set(true);
    this.profileCrudError.set(null);
  }

  cancelProfileEdit(): void {
    this.isEditingProfile.set(false);
    this.editingProfileId.set(null);
    this.profileCrudError.set(null);
  }

  saveProfile(): void {
    if (this.profileForm.invalid || this.isSavingProfile()) {
      return;
    }

    this.isSavingProfile.set(true);
    this.profileCrudError.set(null);

    const val = this.profileForm.getRawValue();
    const req: UpsertLlmModelProfileRequest = {
      displayName: val.displayName.trim(),
      modelTag: val.modelTag.trim(),
      matchPrefixes: val.matchPrefixes
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean),
      summary: val.summary.trim() || null,
      contextWindowTokens: val.contextWindowTokens,
      requestTimeoutSeconds: val.requestTimeoutSeconds,
      enableThinking: val.enableThinking,
      recommendedAsRefineModel: val.recommendedAsRefineModel,
      temperature: val.temperature,
      topP: val.topP,
      topK: val.topK,
      notes: val.notes
        .split('\n')
        .map((s) => s.trim())
        .filter(Boolean),
    };

    const editId = this.editingProfileId();
    const request$ = editId
      ? this.llmSettingsService.updateModelProfile(editId, req)
      : this.llmSettingsService.createModelProfile(req);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => {
        this.isSavingProfile.set(false);
        this.isEditingProfile.set(false);
        this.editingProfileId.set(null);
        this.loadProfiles();
        this.selectedProfileId.set(saved.id);
      },
      error: (err) => {
        this.profileCrudError.set(err?.error?.detail || err?.message || 'Failed to save profile.');
        this.isSavingProfile.set(false);
      },
    });
  }

  deleteProfile(profile: LlmModelProfile): void {
    if (this.isSavingProfile()) {
      return;
    }
    this.isSavingProfile.set(true);
    this.profileCrudError.set(null);

    this.llmSettingsService
      .deleteModelProfile(profile.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSavingProfile.set(false);
          if (this.editingProfileId() === profile.id) {
            this.cancelProfileEdit();
          }
          this.loadProfiles();
        },
        error: (err) => {
          this.profileCrudError.set(
            err?.error?.detail || err?.message || 'Failed to delete profile.',
          );
          this.isSavingProfile.set(false);
        },
      });
  }

  restoreDefaults(): void {
    if (this.isSavingProfile()) {
      return;
    }
    this.isSavingProfile.set(true);
    this.profileCrudError.set(null);

    this.llmSettingsService
      .restoreDefaultModelProfiles()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (restored) => {
          this.isSavingProfile.set(false);
          this.profiles.set(restored);
          this.selectDefaultProfiles();
          if (this.editingProfileId()) {
            this.cancelProfileEdit();
          }
        },
        error: (err) => {
          this.profileCrudError.set(
            err?.error?.detail || err?.message || 'Failed to restore default profiles.',
          );
          this.isSavingProfile.set(false);
        },
      });
  }

  applyProfileToPrimary(profile: LlmModelProfile): void {
    const controls = this.form.controls;
    controls.textModel.setValue(profile.modelTag);
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
    this.profileMessage.set(
      `Applied ${profile.displayName} recommendations. Save to keep them. ${profile.notes.join(' ')}`.trim(),
    );
    this.selectedTabIndex.set(0);
  }

  private selectDefaultProfiles(): void {
    const profiles = this.profiles();
    if (profiles.length === 0 || this.isLoading()) {
      return;
    }
    const values = this.form.getRawValue();
    this.selectedProfileId.set(matchModelProfile(profiles, values.textModel)?.id ?? profiles[0].id);
    const refineProfiles = this.refineProfiles();
    const refineMatch = matchModelProfile(refineProfiles, values.refineModel);
    this.selectedRefineProfileId.set(
      refineMatch?.id ??
        refineProfiles.find((profile) => profile.modelTag === values.refineModel.trim())?.id ??
        refineProfiles[0]?.id ??
        '',
    );
  }

  onCancel(): void {
    this.dialogRef.close(false);
  }
}
