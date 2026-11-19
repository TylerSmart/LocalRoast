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
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  ConfirmDialogComponent,
  type ConfirmDialogData,
} from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  LlmModelConfigurationDialogComponent,
  type LlmModelConfigurationDialogData,
} from '../llm-model-configuration-dialog/llm-model-configuration-dialog.component';
import { buildDeleteModelConfirmMessage } from '../llm-settings-dialog/llm-settings.utils';

/** Saves each unique (Connection, Model) pair together with optional hardware placement and rate limits. */
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
  /** Emits after a configuration is created, updated or deleted. */
  readonly changed = output<void>();
  readonly addConnectionRequested = output<void>();

  readonly isSaving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  startAdd(): void {
    this.openDialog({});
  }

  startEdit(configuration: LlmModelConfigurationDto): void {
    this.openDialog({ configuration });
  }

  delete(configuration: LlmModelConfigurationDto): void {
    if (this.isSaving()) {
      return;
    }
    const confirmMessage = buildDeleteModelConfirmMessage(configuration);
    if (confirmMessage) {
      this.dialog
        .open<ConfirmDialogComponent, ConfirmDialogData, boolean | undefined>(
          ConfirmDialogComponent,
          {
            width: '440px',
            maxWidth: '95vw',
            data: {
              title: 'Delete Model',
              message: confirmMessage,
              confirmText: 'Delete Model',
              isDestructive: true,
            },
          },
        )
        .afterClosed()
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe((confirmed) => {
          if (confirmed) {
            this.executeDelete(configuration);
          }
        });
      return;
    }
    this.executeDelete(configuration);
  }

  private executeDelete(configuration: LlmModelConfigurationDto): void {
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

  private openDialog(
    data: Omit<LlmModelConfigurationDialogData, 'connections' | 'existingConfigurations'>,
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
          existingConfigurations: this.configurations(),
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
