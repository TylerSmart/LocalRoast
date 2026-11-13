import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTabsModule } from '@angular/material/tabs';
import { catchError, forkJoin, of } from 'rxjs';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
  UpsertLlmModelProfileRequest,
} from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { LlmConnectionsPanelComponent } from '../llm-connections-panel/llm-connections-panel.component';
import { LlmModelConfigurationsPanelComponent } from '../llm-model-configurations-panel/llm-model-configurations-panel.component';

export { filterModels, matchModelProfile } from './llm-settings.utils';

export const LLM_SETTINGS_TABS = {
  workflows: 0,
  connections: 1,
  models: 2,
  profiles: 3,
} as const;

interface ConnectionGroup {
  connection: LlmConnectionDto;
  configurations: LlmModelConfigurationDto[];
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
    MatSlideToggleModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatTabsModule,
    LlmConnectionsPanelComponent,
    LlmModelConfigurationsPanelComponent,
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
  private readonly modelsPanel = viewChild(LlmModelConfigurationsPanelComponent);

  readonly tabs = LLM_SETTINGS_TABS;
  readonly isLoading = signal(true);
  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly selectedTabIndex = signal<number>(LLM_SETTINGS_TABS.workflows);
  readonly connections = signal<LlmConnectionDto[]>([]);
  readonly configurations = signal<LlmModelConfigurationDto[]>([]);
  readonly profiles = signal<LlmModelProfile[]>([]);

  readonly isEditingProfile = signal(false);
  readonly editingProfileId = signal<string | null>(null);
  readonly isSavingProfile = signal(false);
  readonly profileCrudError = signal<string | null>(null);

  readonly workflowForm = this.fb.nonNullable.group({
    textModelConfigurationId: this.fb.nonNullable.control(''),
    visionModelConfigurationId: this.fb.nonNullable.control(''),
    refineModelConfigurationId: this.fb.nonNullable.control(''),
    enableIngestion: this.fb.nonNullable.control(true),
    autoPublishHighConfidenceText: this.fb.nonNullable.control(false),
    autoResumeQueue: this.fb.nonNullable.control(true),
    maxIngestionRetries: this.fb.nonNullable.control(3, [
      Validators.required,
      Validators.min(0),
      Validators.max(10),
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

  private readonly workflowValues = toSignal(this.workflowForm.valueChanges, {
    initialValue: this.workflowForm.getRawValue(),
  });

  /** Saved configurations grouped by connection for the workflow dropdowns. */
  readonly configurationGroups = computed<ConnectionGroup[]>(() =>
    this.connections()
      .map((connection) => ({
        connection,
        configurations: this.configurations().filter((c) => c.connectionId === connection.id),
      }))
      .filter((group) => group.configurations.length > 0),
  );
  readonly textConfiguration = computed(() =>
    this.findConfiguration(this.workflowValues().textModelConfigurationId),
  );
  readonly visionConfiguration = computed(() =>
    this.findConfiguration(this.workflowValues().visionModelConfigurationId),
  );
  readonly refineConfiguration = computed(() =>
    this.findConfiguration(this.workflowValues().refineModelConfigurationId),
  );
  readonly refineMatchesText = computed(() => {
    const text = this.textConfiguration();
    const refine = this.refineConfiguration();
    return (
      !!text && !!refine && text.connectionId === refine.connectionId && text.model === refine.model
    );
  });

  ngOnInit(): void {
    this.loadAll();
  }

  loadAll(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);
    forkJoin({
      settings: this.llmSettingsService.getSettings(),
      connections: this.llmSettingsService.getConnections(),
      configurations: this.llmSettingsService.getModelConfigurations(),
      profiles: this.llmSettingsService
        .getModelProfiles()
        .pipe(catchError(() => of([] as LlmModelProfile[]))),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ settings, connections, configurations, profiles }) => {
          this.connections.set(connections);
          this.configurations.set(configurations);
          this.profiles.set(profiles);
          this.workflowForm.reset({
            textModelConfigurationId: settings.textModelConfigurationId ?? '',
            visionModelConfigurationId: settings.visionModelConfigurationId ?? '',
            refineModelConfigurationId: settings.refineModelConfigurationId ?? '',
            enableIngestion: settings.enableIngestion ?? true,
            autoPublishHighConfidenceText: settings.autoPublishHighConfidenceText,
            autoResumeQueue: settings.autoResumeQueue ?? true,
            maxIngestionRetries: settings.maxIngestionRetries ?? 3,
          });
          if (connections.length === 0) {
            this.selectedTabIndex.set(LLM_SETTINGS_TABS.connections);
          }
          this.isLoading.set(false);
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to load LLM settings.');
          this.isLoading.set(false);
        },
      });
  }

  /** Reloads connections and configurations after a panel saves or deletes one. */
  reloadCatalog(): void {
    forkJoin({
      connections: this.llmSettingsService.getConnections(),
      configurations: this.llmSettingsService.getModelConfigurations(),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ connections, configurations }) => {
          this.connections.set(connections);
          this.configurations.set(configurations);
          this.dropMissingAssignments(configurations);
        },
        error: (err) =>
          this.errorMessage.set(err?.error?.detail || 'Failed to reload LLM connections.'),
      });
  }

  onSave(): void {
    if (this.workflowForm.invalid || this.isSaving()) {
      return;
    }
    this.isSaving.set(true);
    this.errorMessage.set(null);

    const values = this.workflowForm.getRawValue();
    this.llmSettingsService
      .updateWorkflows({
        textModelConfigurationId: values.textModelConfigurationId || null,
        visionModelConfigurationId: values.visionModelConfigurationId || null,
        refineModelConfigurationId: values.refineModelConfigurationId || null,
        autoPublishHighConfidenceText: values.autoPublishHighConfidenceText,
        maxIngestionRetries: values.maxIngestionRetries,
        enableIngestion: values.enableIngestion,
        autoResumeQueue: values.autoResumeQueue,
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
          this.selectedTabIndex.set(LLM_SETTINGS_TABS.workflows);
        },
      });
  }

  onCancel(): void {
    this.dialogRef.close(false);
  }

  loadProfiles(): void {
    this.llmSettingsService
      .getModelProfiles()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (profiles) => this.profiles.set(profiles),
        error: () => this.profiles.set([]),
      });
  }

  /** Opens the Models tab with the profile's recommendations in the configuration editor. */
  applyProfileToModel(profile: LlmModelProfile): void {
    this.selectedTabIndex.set(LLM_SETTINGS_TABS.models);
    this.modelsPanel()?.applyProfile(profile);
  }

  /** Starts a new profile pre-filled from a saved model configuration. */
  saveConfigurationAsProfile(configuration: LlmModelConfigurationDto): void {
    this.profileForm.reset({
      displayName: `${configuration.model} Profile`,
      modelTag: configuration.model,
      matchPrefixes: configuration.model,
      summary: `Saved from ${configuration.name}.`,
      contextWindowTokens: configuration.contextWindowTokens,
      requestTimeoutSeconds: configuration.requestTimeoutSeconds,
      enableThinking: configuration.enableThinking,
      recommendedAsRefineModel: false,
      temperature: configuration.temperature,
      topP: configuration.topP,
      topK: configuration.topK,
      notes: '',
    });
    this.editingProfileId.set(null);
    this.isEditingProfile.set(true);
    this.profileCrudError.set(null);
    this.selectedTabIndex.set(LLM_SETTINGS_TABS.profiles);
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
      next: () => {
        this.isSavingProfile.set(false);
        this.isEditingProfile.set(false);
        this.editingProfileId.set(null);
        this.loadProfiles();
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

  private findConfiguration(id: string | undefined): LlmModelConfigurationDto | null {
    return id ? (this.configurations().find((c) => c.id === id) ?? null) : null;
  }

  /**
   * Clears selections whose configuration was deleted, and pre-selects the text workflow when the
   * first configuration is created so a new setup works after a single save.
   */
  private dropMissingAssignments(configurations: LlmModelConfigurationDto[]): void {
    const ids = new Set(configurations.map((c) => c.id));
    const controls = this.workflowForm.controls;
    for (const control of [
      controls.textModelConfigurationId,
      controls.visionModelConfigurationId,
      controls.refineModelConfigurationId,
    ]) {
      if (control.value && !ids.has(control.value)) {
        control.setValue('');
      }
    }
    if (!controls.textModelConfigurationId.value && configurations.length === 1) {
      controls.textModelConfigurationId.setValue(configurations[0].id);
      this.workflowForm.markAsDirty();
    }
  }
}
