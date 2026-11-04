import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Observable, tap } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import type {
  LlmConnectionTestResultDto,
  LlmSettingsDto,
  TestLlmConnectionRequest,
  UpdateLlmSettingsRequest,
} from '../../models/ingestion.models';
import { AiKitchenHelperService } from '../ai-helpers/ai-kitchen-helper.service';

@Service()
export class LlmSettingsService {
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog, { optional: true });
  private readonly aiHelper = inject(AiKitchenHelperService, { optional: true });

  getSettings(): Observable<LlmSettingsDto> {
    return this.http.get<LlmSettingsDto>(apiUrl('/llm-settings'));
  }

  updateSettings(request: UpdateLlmSettingsRequest): Observable<LlmSettingsDto> {
    return this.http.put<LlmSettingsDto>(apiUrl('/llm-settings'), request).pipe(
      tap(() => {
        this.aiHelper?.checkStatus(true).subscribe();
      }),
    );
  }

  testConnection(request: TestLlmConnectionRequest): Observable<LlmConnectionTestResultDto> {
    return this.http.post<LlmConnectionTestResultDto>(apiUrl('/llm-settings/test'), request).pipe(
      tap(() => {
        this.aiHelper?.checkStatus(true).subscribe();
      }),
    );
  }

  setIngestionEnabled(enabled: boolean): Observable<LlmSettingsDto> {
    return this.http.post<LlmSettingsDto>(apiUrl('/llm-settings/set-ingestion-enabled'), null, {
      params: { enabled: String(enabled) },
    });
  }

  async openLlmSettingsDialog(): Promise<MatDialogRef<unknown> | undefined> {
    if (!this.dialog) {
      return undefined;
    }
    const { LlmSettingsDialogComponent } =
      await import('../../../features/ingestion/llm-settings-dialog/llm-settings-dialog.component');
    return this.dialog.open(LlmSettingsDialogComponent, {
      width: '620px',
      maxWidth: '95vw',
      panelClass: 'llm-settings-dialog-panel',
      autoFocus: false,
    });
  }
}
