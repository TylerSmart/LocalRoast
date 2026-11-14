import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  LlmModelConfigurationDialogComponent,
  type LlmModelConfigurationDialogData,
} from '../llm-model-configuration-dialog/llm-model-configuration-dialog.component';

/** Saves a model on a connection together with its tuning so workflows can select it by name. */
@Component({
  selector: 'app-llm-model-configurations-panel',
  imports: [MatButtonModule, MatIconModule],
  templateUrl: './llm-model-configurations-panel.component.html',
  styleUrl: './llm-model-configurations-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmModelConfigurationsPanelComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
  private readonly llmSettingsService = inject(LlmSettingsService);

  readonly connections = input.required<LlmConnectionDto[]>();
  readonly configurations = input.required<LlmModelConfigurationDto[]>();
  readonly profiles = input.required<LlmModelProfile[]>();
  /** Emits after a configuration is created, updated or deleted. */
  readonly changed = output<void>();
  readonly addConnectionRequested = output<void>();
  readonly saveAsProfileRequested = output<LlmModelConfigurationDto>();

  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  startAdd(): void {
    this.openDialog({});
  }

  startEdit(configuration: LlmModelConfigurationDto): void {
    this.openDialog({ configuration });
  }

  duplicate(configuration: LlmModelConfigurationDto): void {
    this.openDialog({ duplicateFrom: configuration });
  }

  /** Opens a new model configuration dialog pre-filled with a profile's recommendations. */
  applyProfile(profile: LlmModelProfile): void {
    this.openDialog({ initialProfile: profile });
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

  private openDialog(
    data: Omit<LlmModelConfigurationDialogData, 'connections' | 'profiles'>,
  ): void {
    this.errorMessage.set(null);
    this.dialog
      .open<
        LlmModelConfigurationDialogComponent,
        LlmModelConfigurationDialogData,
        LlmModelConfigurationDto | undefined
      >(LlmModelConfigurationDialogComponent, {
        width: '640px',
        maxWidth: '95vw',
        autoFocus: false,
        data: {
          ...data,
          connections: this.connections(),
          profiles: this.profiles(),
        },
      })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((saved) => {
        if (saved) {
          this.changed.emit();
        }
      });
  }
}
