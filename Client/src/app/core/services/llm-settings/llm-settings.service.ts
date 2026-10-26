import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import type {
  LlmConnectionTestResultDto,
  LlmSettingsDto,
  TestLlmConnectionRequest,
  UpdateLlmSettingsRequest,
} from '../../models/ingestion.models';

@Service()
export class LlmSettingsService {
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog, { optional: true });

  getSettings(): Observable<LlmSettingsDto> {
    return this.http.get<LlmSettingsDto>(apiUrl('/llm-settings'));
  }

  updateSettings(request: UpdateLlmSettingsRequest): Observable<LlmSettingsDto> {
    return this.http.put<LlmSettingsDto>(apiUrl('/llm-settings'), request);
  }

  testConnection(request: TestLlmConnectionRequest): Observable<LlmConnectionTestResultDto> {
    return this.http.post<LlmConnectionTestResultDto>(apiUrl('/llm-settings/test'), request);
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
