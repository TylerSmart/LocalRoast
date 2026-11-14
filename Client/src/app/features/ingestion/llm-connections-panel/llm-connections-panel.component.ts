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
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import type {
  LlmConnectionDto,
  LlmConnectionTestResultDto,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  LlmConnectionDialogComponent,
  type LlmConnectionDialogData,
} from '../llm-connection-dialog/llm-connection-dialog.component';
import { failedTestResult, providerLabel } from '../llm-settings-dialog/llm-settings.utils';

/** Manages saved LLM machines/providers: endpoint, authentication and request limits. */
@Component({
  selector: 'app-llm-connections-panel',
  imports: [MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './llm-connections-panel.component.html',
  styleUrl: './llm-connections-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LlmConnectionsPanelComponent {
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
  private readonly llmSettingsService = inject(LlmSettingsService);

  readonly connections = input.required<LlmConnectionDto[]>();
  /** Emits after a connection is created, updated or deleted. */
  readonly changed = output<void>();

  readonly providerLabel = providerLabel;

  readonly isSaving = signal(false);
  readonly cardTestResults = signal<Readonly<Record<string, LlmConnectionTestResultDto>>>({});
  readonly testingIds = signal<ReadonlySet<string>>(new Set());
  readonly errorMessage = signal<string | null>(null);

  startAdd(): void {
    this.openDialog({
      defaultName: this.connections().length === 0 ? 'Local Ollama' : '',
    });
  }

  startEdit(connection: LlmConnectionDto): void {
    this.openDialog({ connection });
  }

  testSaved(connection: LlmConnectionDto): void {
    if (this.testingIds().has(connection.id)) {
      return;
    }
    this.testingIds.update((ids) => new Set(ids).add(connection.id));
    this.llmSettingsService
      .testSavedConnection(connection.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.finishCardTest(connection.id, result),
        error: (err) =>
          this.finishCardTest(
            connection.id,
            failedTestResult(connection.provider, connection.baseUrl, err),
          ),
      });
  }

  delete(connection: LlmConnectionDto): void {
    if (this.isSaving()) {
      return;
    }
    this.isSaving.set(true);
    this.errorMessage.set(null);
    this.llmSettingsService
      .deleteConnection(connection.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.isSaving.set(false);
          this.changed.emit();
        },
        error: (err) => {
          this.errorMessage.set(err?.error?.detail || 'Failed to delete the connection.');
          this.isSaving.set(false);
        },
      });
  }

  private openDialog(data: LlmConnectionDialogData): void {
    this.errorMessage.set(null);
    this.dialog
      .open<LlmConnectionDialogComponent, LlmConnectionDialogData, LlmConnectionDto | undefined>(
        LlmConnectionDialogComponent,
        {
          width: '620px',
          maxWidth: '95vw',
          autoFocus: false,
          data,
        },
      )
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((saved) => {
        if (saved) {
          this.changed.emit();
        }
      });
  }

  private finishCardTest(id: string, result: LlmConnectionTestResultDto): void {
    this.cardTestResults.update((results) => ({ ...results, [id]: result }));
    this.testingIds.update((ids) => {
      const next = new Set(ids);
      next.delete(id);
      return next;
    });
  }
}
