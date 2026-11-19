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
import { MatButtonModule } from '@angular/material/button';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
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
} from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { LlmConnectionsPanelComponent } from '../llm-connections-panel/llm-connections-panel.component';
import { LlmModelConfigurationsPanelComponent } from '../llm-model-configurations-panel/llm-model-configurations-panel.component';
import {
  LlmModelProfileDialogComponent,
  type LlmModelProfileDialogData,
} from '../llm-model-profile-dialog/llm-model-profile-dialog.component';

export { filterModels } from './llm-settings.utils';

export const LLM_SETTINGS_TABS = {
  workflows: 0,
  connections: 1,
  models: 2,
  profiles: 3,
} as const;

export interface ModelProfileGroup {
  configuration: LlmModelConfigurationDto;
  profiles: LlmModelProfile[];
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
  private readonly dialog = inject(MatDialog);
  private readonly llmSettingsService = inject(LlmSettingsService);
  private readonly ingestionService = inject(IngestionService);
  private readonly dialogRef = inject(MatDialogRef<LlmSettingsDialogComponent>);

  readonly tabs = LLM_SETTINGS_TABS;
  readonly isLoading = signal(true);
  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly selectedTabIndex = signal<number>(LLM_SETTINGS_TABS.workflows);
  readonly connections = signal<LlmConnectionDto[]>([]);
  readonly configurations = signal<LlmModelConfigurationDto[]>([]);
  readonly profiles = signal<LlmModelProfile[]>([]);

  readonly isSavingProfile = signal(false);
  readonly profileCrudError = signal<string | null>(null);

  readonly workflowForm = this.fb.nonNullable.group({
    textModelProfileId: this.fb.nonNullable.control(''),
    visionModelProfileId: this.fb.nonNullable.control(''),
    refineModelProfileId: this.fb.nonNullable.control(''),
    refineDuringIngestion: this.fb.nonNullable.control(false),
    refineDuringIdle: this.fb.nonNullable.control(false),
    embeddingModelProfileId: this.fb.nonNullable.control(''),
    enableIngestion: this.fb.nonNullable.control(true),
    autoPublishHighConfidenceText: this.fb.nonNullable.control(false),
    autoResumeQueue: this.fb.nonNullable.control(true),
    maxIngestionRetries: this.fb.nonNullable.control(3, [
      Validators.required,
      Validators.min(0),
      Validators.max(10),
    ]),
  });

  private readonly workflowValues = toSignal(this.workflowForm.valueChanges, {
    initialValue: this.workflowForm.getRawValue(),
  });

  /** Saved models paired with their child profiles (Defaults first), ordered by connection and model name. */
  readonly modelProfileSections = computed<ModelProfileGroup[]>(() => {
    const connectionOrder = new Map(this.connections().map((c, idx) => [c.id, idx]));
    const sortedConfigs = [...this.configurations()].sort((a, b) => {
      const connDiff =
        (connectionOrder.get(a.connectionId) ?? Number.MAX_SAFE_INTEGER) -
        (connectionOrder.get(b.connectionId) ?? Number.MAX_SAFE_INTEGER);
      return connDiff !== 0 ? connDiff : a.name.localeCompare(b.name);
    });

    const profilesByConfig = new Map<string, LlmModelProfile[]>();
    for (const profile of this.profiles()) {
      const list = profilesByConfig.get(profile.modelConfigurationId) ?? [];
      list.push(profile);
      profilesByConfig.set(profile.modelConfigurationId, list);
    }

    return sortedConfigs.map((configuration) => {
      const items = [...(profilesByConfig.get(configuration.id) ?? [])].sort((a, b) => {
        if (a.isDefault !== b.isDefault) {
          return a.isDefault ? -1 : 1;
        }
        return a.displayName.localeCompare(b.displayName);
      });
      return { configuration, profiles: items };
    });
  });

  /** Non-empty model profile groups used in workflow <mat-optgroup> selects. */
  readonly profileGroups = computed<ModelProfileGroup[]>(() =>
    this.modelProfileSections().filter((group) => group.profiles.length > 0),
  );

  readonly textProfile = computed(() => this.findProfile(this.workflowValues().textModelProfileId));
  readonly visionProfile = computed(() =>
    this.findProfile(this.workflowValues().visionModelProfileId),
  );
  readonly refineProfile = computed(() =>
    this.findProfile(this.workflowValues().refineModelProfileId),
  );
  readonly embeddingProfile = computed(() =>
    this.findProfile(this.workflowValues().embeddingModelProfileId),
  );

  readonly refineMatchesText = computed(() => {
    const text = this.textProfile();
    const refine = this.refineProfile();
    return !!text && !!refine && text.id === refine.id;
  });

  ngOnInit(): void {
    this.workflowForm.controls.refineModelProfileId.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncRefinementToggleState());
    this.syncRefinementToggleState();
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
            textModelProfileId: settings.textModelProfileId ?? '',
            visionModelProfileId: settings.visionModelProfileId ?? '',
            refineModelProfileId: settings.refineModelProfileId ?? '',
            refineDuringIngestion: settings.refineDuringIngestion ?? false,
            refineDuringIdle: settings.refineDuringIdle ?? false,
            embeddingModelProfileId: settings.embeddingModelProfileId ?? '',
            enableIngestion: settings.enableIngestion ?? true,
            autoPublishHighConfidenceText: settings.autoPublishHighConfidenceText,
            autoResumeQueue: settings.autoResumeQueue ?? true,
            maxIngestionRetries: settings.maxIngestionRetries ?? 3,
          });
          this.syncRefinementToggleState();
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

  /** Reloads connections, models, and profiles after a panel saves or deletes an item. */
  reloadCatalog(): void {
    forkJoin({
      connections: this.llmSettingsService.getConnections(),
      configurations: this.llmSettingsService.getModelConfigurations(),
      profiles: this.llmSettingsService
        .getModelProfiles()
        .pipe(catchError(() => of([] as LlmModelProfile[]))),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ connections, configurations, profiles }) => {
          this.connections.set(connections);
          this.configurations.set(configurations);
          this.profiles.set(profiles);
          this.syncWorkflowAssignments(configurations, profiles);
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
        textModelProfileId: values.textModelProfileId || null,
        visionModelProfileId: values.visionModelProfileId || null,
        refineModelProfileId: values.refineModelProfileId || null,
        refineDuringIngestion: values.refineDuringIngestion,
        refineDuringIdle: values.refineDuringIdle,
        embeddingModelProfileId: values.embeddingModelProfileId || null,
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
    forkJoin({
      configurations: this.llmSettingsService
        .getModelConfigurations()
        .pipe(catchError(() => of(this.configurations()))),
      profiles: this.llmSettingsService
        .getModelProfiles()
        .pipe(catchError(() => of([] as LlmModelProfile[]))),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ configurations, profiles }) => {
          this.configurations.set(configurations);
          this.profiles.set(profiles);
          this.dropMissingProfiles(profiles);
        },
      });
  }

  startAddProfile(initialModelConfigurationId?: string): void {
    this.openProfileDialog({ initialModelConfigurationId: initialModelConfigurationId ?? null });
  }

  startEditProfile(profile: LlmModelProfile): void {
    this.openProfileDialog({ profile });
  }

  duplicateProfile(profile: LlmModelProfile): void {
    this.openProfileDialog({ duplicateFrom: profile });
  }

  deleteProfile(profile: LlmModelProfile): void {
    if (this.isSavingProfile() || profile.isDefault) {
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

  private openProfileDialog(
    data: Omit<LlmModelProfileDialogData, 'connections' | 'configurations' | 'existingProfiles'>,
  ): void {
    this.profileCrudError.set(null);
    this.dialog
      .open<LlmModelProfileDialogComponent, LlmModelProfileDialogData, LlmModelProfile | undefined>(
        LlmModelProfileDialogComponent,
        {
          width: '620px',
          maxWidth: '95vw',
          autoFocus: false,
          data: {
            ...data,
            connections: this.connections(),
            configurations: this.configurations(),
            existingProfiles: this.profiles(),
          },
        },
      )
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((saved) => {
        if (saved) {
          this.loadProfiles();
        }
      });
  }

  private findProfile(id: string | undefined): LlmModelProfile | null {
    return id ? (this.profiles().find((p) => p.id === id) ?? null) : null;
  }

  /**
   * Clears selections whose profile was deleted, and pre-selects the first model's Defaults profile
   * for Text when the first saved model is created so a new setup works after a single save.
   */
  private syncWorkflowAssignments(
    configurations: LlmModelConfigurationDto[],
    profiles: LlmModelProfile[],
  ): void {
    this.dropMissingProfiles(profiles);
    const controls = this.workflowForm.controls;
    if (!controls.textModelProfileId.value && configurations.length === 1) {
      const defaultProfileId =
        configurations[0].defaultProfileId ||
        profiles.find((p) => p.modelConfigurationId === configurations[0].id && p.isDefault)?.id;
      if (defaultProfileId && profiles.some((p) => p.id === defaultProfileId)) {
        controls.textModelProfileId.setValue(defaultProfileId);
        this.workflowForm.markAsDirty();
      }
    }
  }

  private syncRefinementToggleState(): void {
    const hasRefineProfile = !!this.workflowForm.controls.refineModelProfileId.value;
    const controls = [
      this.workflowForm.controls.refineDuringIngestion,
      this.workflowForm.controls.refineDuringIdle,
    ];
    for (const ctrl of controls) {
      if (hasRefineProfile && ctrl.disabled) {
        ctrl.enable({ emitEvent: false });
      } else if (!hasRefineProfile && ctrl.enabled) {
        ctrl.disable({ emitEvent: false });
      }
    }
  }

  private dropMissingProfiles(profiles: LlmModelProfile[]): void {
    const ids = new Set(profiles.map((p) => p.id));
    const controls = this.workflowForm.controls;
    for (const control of [
      controls.textModelProfileId,
      controls.visionModelProfileId,
      controls.refineModelProfileId,
      controls.embeddingModelProfileId,
    ]) {
      if (control.value && !ids.has(control.value)) {
        control.setValue('');
      }
    }
    this.syncRefinementToggleState();
  }
}
