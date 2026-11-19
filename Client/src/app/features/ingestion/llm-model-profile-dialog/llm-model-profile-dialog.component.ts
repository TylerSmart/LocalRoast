import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
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
  UpsertLlmModelProfileRequest,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';

export interface LlmModelProfileDialogData {
  connections?: LlmConnectionDto[];
  configurations?: LlmModelConfigurationDto[];
  existingProfiles?: LlmModelProfile[];
  profile?: LlmModelProfile | null;
  duplicateFrom?: LlmModelProfile | null;
  initialModelConfigurationId?: string | null;
}

interface ConnectionConfigurationGroup {
  connectionId: string;
  connectionName: string;
  configurations: LlmModelConfigurationDto[];
}

@Component({
  selector: 'app-llm-model-profile-dialog',
  imports: [
    ReactiveFormsModule,
    MatDialogModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatSlideToggleModule,
  ],
  templateUrl: './llm-model-profile-dialog.component.html',
  styleUrl: './llm-model-profile-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmModelProfileDialogComponent {
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly llmSettingsService = inject(LlmSettingsService);
  private readonly dialogRef = inject(MatDialogRef<LlmModelProfileDialogComponent>);
  private readonly data = inject<LlmModelProfileDialogData | null>(MAT_DIALOG_DATA, {
    optional: true,
  });

  readonly editingProfile = signal<LlmModelProfile | null>(this.data?.profile ?? null);
  readonly connections = signal<LlmConnectionDto[]>(this.data?.connections ?? []);
  readonly configurations = signal<LlmModelConfigurationDto[]>(this.data?.configurations ?? []);
  readonly existingProfiles = signal<LlmModelProfile[]>(this.data?.existingProfiles ?? []);
  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  private readonly sourceProfile = this.data?.profile ?? this.data?.duplicateFrom ?? null;
  readonly isEditingDefault = computed(() => this.editingProfile()?.isDefault === true);

  readonly configurationGroups = computed<ConnectionConfigurationGroup[]>(() => {
    const configs = this.configurations();
    const groups: ConnectionConfigurationGroup[] = [];
    const seenIds = new Set<string>();

    for (const connection of this.connections()) {
      const matching = configs.filter((c) => c.connectionId === connection.id);
      if (matching.length > 0) {
        groups.push({
          connectionId: connection.id,
          connectionName: connection.name,
          configurations: matching,
        });
        for (const c of matching) {
          seenIds.add(c.id);
        }
      }
    }

    const leftovers = configs.filter((c) => !seenIds.has(c.id));
    if (leftovers.length > 0) {
      const byConnection = new Map<string, LlmModelConfigurationDto[]>();
      for (const config of leftovers) {
        const list = byConnection.get(config.connectionId) ?? [];
        list.push(config);
        byConnection.set(config.connectionId, list);
      }
      for (const [connectionId, items] of byConnection) {
        groups.push({
          connectionId,
          connectionName: items[0].connectionName,
          configurations: items,
        });
      }
    }

    return groups;
  });

  readonly form = this.fb.nonNullable.group({
    modelConfigurationId: this.fb.nonNullable.control<string>(
      {
        value:
          this.data?.profile?.modelConfigurationId ??
          this.data?.duplicateFrom?.modelConfigurationId ??
          this.data?.initialModelConfigurationId ??
          (this.configurations().length === 1 ? this.configurations()[0].id : ''),
        disabled: this.data?.profile?.isDefault === true,
      },
      [Validators.required],
    ),
    displayName: this.fb.nonNullable.control<string>(
      {
        value: this.data?.profile
          ? this.data.profile.displayName
          : this.data?.duplicateFrom
            ? `${this.data.duplicateFrom.displayName} (copy)`
            : '',
        disabled: this.data?.profile?.isDefault === true,
      },
      [Validators.required, Validators.maxLength(150)],
    ),
    summary: this.fb.nonNullable.control<string>(this.sourceProfile?.summary ?? '', [
      Validators.maxLength(500),
    ]),
    contextWindowTokens: this.fb.nonNullable.control<number>(
      this.sourceProfile?.contextWindowTokens ?? 8192,
      [Validators.required, Validators.min(2048), Validators.max(65536)],
    ),
    requestTimeoutSeconds: this.fb.nonNullable.control<number>(
      this.sourceProfile?.requestTimeoutSeconds ?? 120,
      [Validators.required, Validators.min(5), Validators.max(1800)],
    ),
    enableThinking: this.fb.nonNullable.control<boolean>(
      this.sourceProfile?.enableThinking ?? false,
    ),
    temperature: this.fb.control<number | null>(this.sourceProfile?.temperature ?? null, [
      Validators.min(0),
      Validators.max(2),
    ]),
    topP: this.fb.control<number | null>(this.sourceProfile?.topP ?? null, [
      Validators.min(0),
      Validators.max(1),
    ]),
    topK: this.fb.control<number | null>(this.sourceProfile?.topK ?? null, [
      Validators.min(1),
      Validators.max(1000),
      Validators.pattern(/^\d+$/),
    ]),
    notes: this.fb.nonNullable.control<string>(this.sourceProfile?.notes?.join('\n') ?? ''),
  });

  private readonly formValues = toSignal(this.form.valueChanges, {
    initialValue: this.form.getRawValue(),
  });

  readonly selectedConfiguration = computed<LlmModelConfigurationDto | null>(() => {
    const raw = this.form.getRawValue();
    // Subscribe to formValues so changes trigger recomputation.
    void this.formValues();
    const id = raw.modelConfigurationId;
    return id ? (this.configurations().find((c) => c.id === id) ?? null) : null;
  });

  readonly isOllamaModel = computed<boolean>(() => {
    const config = this.selectedConfiguration();
    if (config) {
      return config.provider === 'Ollama';
    }
    const editing = this.editingProfile();
    return !editing || editing.provider === 'Ollama';
  });

  readonly isDuplicateName = computed<boolean>(() => {
    void this.formValues();
    const raw = this.form.getRawValue();
    const modelId = raw.modelConfigurationId;
    const normalizedName = raw.displayName.trim().toLowerCase();
    if (!modelId || !normalizedName) {
      return false;
    }
    const editingId = this.editingProfile()?.id;
    return this.existingProfiles().some(
      (p) =>
        p.id !== editingId &&
        p.modelConfigurationId === modelId &&
        p.displayName.trim().toLowerCase() === normalizedName,
    );
  });

  onContextWindowSliderInput(event: Event): void {
    this.form.controls.contextWindowTokens.setValue(
      (event.target as HTMLInputElement).valueAsNumber,
    );
  }

  save(): void {
    if (this.form.invalid || this.isDuplicateName() || this.isSaving()) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSaving.set(true);
    this.errorMessage.set(null);

    const val = this.form.getRawValue();
    const req: UpsertLlmModelProfileRequest = {
      modelConfigurationId: val.modelConfigurationId,
      displayName: val.displayName.trim(),
      summary: val.summary.trim() || null,
      contextWindowTokens: val.contextWindowTokens,
      requestTimeoutSeconds: val.requestTimeoutSeconds,
      enableThinking: val.enableThinking,
      temperature: val.temperature,
      topP: val.topP,
      topK: val.topK,
      notes: val.notes
        .split('\n')
        .map((s) => s.trim())
        .filter(Boolean),
    };

    const editing = this.editingProfile();
    const request$ = editing
      ? this.llmSettingsService.updateModelProfile(editing.id, req)
      : this.llmSettingsService.createModelProfile(req);

    request$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => {
        this.isSaving.set(false);
        this.dialogRef.close(saved);
      },
      error: (err) => {
        this.errorMessage.set(err?.error?.detail || err?.message || 'Failed to save profile.');
        this.isSaving.set(false);
      },
    });
  }

  cancel(): void {
    this.dialogRef.close();
  }
}
