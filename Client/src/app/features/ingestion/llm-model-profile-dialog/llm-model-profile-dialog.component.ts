import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import type {
  LlmModelConfigurationDto,
  LlmModelProfile,
  UpsertLlmModelProfileRequest,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';

export interface LlmModelProfileDialogData {
  profile?: LlmModelProfile | null;
  fromConfiguration?: LlmModelConfigurationDto | null;
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
  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  private readonly sourceConfig = this.data?.fromConfiguration ?? null;
  private readonly sourceProfile = this.data?.profile ?? null;

  readonly form = this.fb.nonNullable.group({
    displayName: this.fb.nonNullable.control<string>(
      this.sourceProfile?.displayName ??
        (this.sourceConfig ? `${this.sourceConfig.model} Profile` : ''),
      [Validators.required, Validators.maxLength(150)],
    ),
    modelTag: this.fb.nonNullable.control<string>(
      this.sourceProfile?.modelTag ?? this.sourceConfig?.model ?? '',
      [Validators.required, Validators.maxLength(200)],
    ),
    matchPrefixes: this.fb.nonNullable.control<string>(
      this.sourceProfile?.matchPrefixes?.join(', ') ?? this.sourceConfig?.model ?? '',
    ),
    summary: this.fb.nonNullable.control<string>(
      this.sourceProfile?.summary ??
        (this.sourceConfig ? `Saved from ${this.sourceConfig.name}.` : ''),
      [Validators.maxLength(500)],
    ),
    contextWindowTokens: this.fb.nonNullable.control<number>(
      this.sourceProfile?.contextWindowTokens ?? this.sourceConfig?.contextWindowTokens ?? 32768,
      [Validators.required, Validators.min(2048), Validators.max(65536)],
    ),
    requestTimeoutSeconds: this.fb.nonNullable.control<number>(
      this.sourceProfile?.requestTimeoutSeconds ?? this.sourceConfig?.requestTimeoutSeconds ?? 600,
      [Validators.required, Validators.min(5), Validators.max(1800)],
    ),
    enableThinking: this.fb.nonNullable.control<boolean>(
      this.sourceProfile?.enableThinking ?? this.sourceConfig?.enableThinking ?? false,
    ),
    recommendedAsRefineModel: this.fb.nonNullable.control<boolean>(
      this.sourceProfile?.recommendedAsRefineModel ?? false,
    ),
    temperature: this.fb.control<number | null>(
      this.sourceProfile?.temperature ?? this.sourceConfig?.temperature ?? null,
      [Validators.min(0), Validators.max(2)],
    ),
    topP: this.fb.control<number | null>(
      this.sourceProfile?.topP ?? this.sourceConfig?.topP ?? null,
      [Validators.min(0), Validators.max(1)],
    ),
    topK: this.fb.control<number | null>(
      this.sourceProfile?.topK ?? this.sourceConfig?.topK ?? null,
      [Validators.min(1), Validators.max(1000)],
    ),
    notes: this.fb.nonNullable.control<string>(this.sourceProfile?.notes?.join('\n') ?? ''),
  });

  save(): void {
    if (this.form.invalid || this.isSaving()) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSaving.set(true);
    this.errorMessage.set(null);

    const val = this.form.getRawValue();
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
