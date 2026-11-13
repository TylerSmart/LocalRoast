import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { Observable, tap } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import type {
  LlmConnectionDto,
  LlmConnectionTestResultDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
  LlmSettingsDto,
  TestLlmConnectionRequest,
  UpdateLlmSettingsRequest,
  UpdateLlmWorkflowsRequest,
  UpsertLlmConnectionRequest,
  UpsertLlmModelConfigurationRequest,
  UpsertLlmModelProfileRequest,
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

  getModelProfiles(): Observable<LlmModelProfile[]> {
    return this.http.get<LlmModelProfile[]>(apiUrl('/llm-settings/model-profiles'));
  }

  createModelProfile(request: UpsertLlmModelProfileRequest): Observable<LlmModelProfile> {
    return this.http.post<LlmModelProfile>(apiUrl('/llm-settings/model-profiles'), request);
  }

  updateModelProfile(
    id: string,
    request: UpsertLlmModelProfileRequest,
  ): Observable<LlmModelProfile> {
    return this.http.put<LlmModelProfile>(apiUrl(`/llm-settings/model-profiles/${id}`), request);
  }

  deleteModelProfile(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/llm-settings/model-profiles/${id}`));
  }

  restoreDefaultModelProfiles(): Observable<LlmModelProfile[]> {
    return this.http.post<LlmModelProfile[]>(
      apiUrl('/llm-settings/model-profiles/restore-defaults'),
      null,
    );
  }

  updateSettings(request: UpdateLlmSettingsRequest): Observable<LlmSettingsDto> {
    return this.http.put<LlmSettingsDto>(apiUrl('/llm-settings'), request).pipe(
      tap(() => {
        this.aiHelper?.checkStatus(true).subscribe();
      }),
    );
  }

  updateWorkflows(request: UpdateLlmWorkflowsRequest): Observable<LlmSettingsDto> {
    return this.http.put<LlmSettingsDto>(apiUrl('/llm-settings/workflows'), request).pipe(
      tap(() => {
        this.aiHelper?.checkStatus(true).subscribe();
      }),
    );
  }

  getConnections(): Observable<LlmConnectionDto[]> {
    return this.http.get<LlmConnectionDto[]>(apiUrl('/llm-settings/connections'));
  }

  createConnection(request: UpsertLlmConnectionRequest): Observable<LlmConnectionDto> {
    return this.http.post<LlmConnectionDto>(apiUrl('/llm-settings/connections'), request);
  }

  updateConnection(id: string, request: UpsertLlmConnectionRequest): Observable<LlmConnectionDto> {
    return this.http.put<LlmConnectionDto>(apiUrl(`/llm-settings/connections/${id}`), request);
  }

  deleteConnection(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/llm-settings/connections/${id}`));
  }

  /** Probes a saved connection and lists the models it serves. */
  testSavedConnection(id: string): Observable<LlmConnectionTestResultDto> {
    return this.http.post<LlmConnectionTestResultDto>(
      apiUrl(`/llm-settings/connections/${id}/test`),
      null,
    );
  }

  getModelConfigurations(): Observable<LlmModelConfigurationDto[]> {
    return this.http.get<LlmModelConfigurationDto[]>(apiUrl('/llm-settings/model-configurations'));
  }

  createModelConfiguration(
    request: UpsertLlmModelConfigurationRequest,
  ): Observable<LlmModelConfigurationDto> {
    return this.http.post<LlmModelConfigurationDto>(
      apiUrl('/llm-settings/model-configurations'),
      request,
    );
  }

  updateModelConfiguration(
    id: string,
    request: UpsertLlmModelConfigurationRequest,
  ): Observable<LlmModelConfigurationDto> {
    return this.http.put<LlmModelConfigurationDto>(
      apiUrl(`/llm-settings/model-configurations/${id}`),
      request,
    );
  }

  deleteModelConfiguration(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/llm-settings/model-configurations/${id}`));
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
      width: '680px',
      maxWidth: '95vw',
      panelClass: 'llm-settings-dialog-panel',
      autoFocus: false,
    });
  }
}
