import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { LlmConnectionDialogComponent } from '../llm-connection-dialog/llm-connection-dialog.component';
import { LlmConnectionsPanelComponent } from './llm-connections-panel.component';

const savedConnection: LlmConnectionDto = {
  id: 'desk',
  name: 'Desk GPU',
  provider: 'OpenAiCompatible',
  baseUrl: 'https://llm.example.com/v1',
  hasApiKey: true,
  apiKeyMasked: '****1234',
  ollamaKeepAlive: '5m',
  ollamaCpuOnly: false,
  enableRateLimitSafeguards: true,
  maxConcurrentRequests: 2,
  requestsPerMinute: 30,
  dailyRequestQuota: 1500,
  dailyQuotaResetTimezone: 'America/Los_Angeles',
  dailyQuotaResetTime: '00:00',
  modelConfigurationCount: 2,
  modifiedAt: '2026-11-13T00:00:00Z',
};

const reachable = {
  isReachable: true,
  provider: 'Ollama' as const,
  normalizedBaseUrl: 'http://gpu-box:11434',
  latencyMs: 12,
  models: ['gemma4:12b'],
  errorMessage: null,
};

describe('LlmConnectionsPanelComponent', () => {
  let component: LlmConnectionsPanelComponent;
  let fixture: ComponentFixture<LlmConnectionsPanelComponent>;
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let dialog: { open: ReturnType<typeof vi.fn> };
  let changed: ReturnType<typeof vi.fn<() => void>>;

  beforeEach(async () => {
    service = {
      deleteConnection: vi.fn().mockReturnValue(of(undefined)),
      testSavedConnection: vi.fn().mockReturnValue(of(reachable)),
    };
    dialog = {
      open: vi.fn().mockReturnValue({ afterClosed: () => of(savedConnection) }),
    };

    await TestBed.configureTestingModule({
      imports: [LlmConnectionsPanelComponent],
      providers: [
        { provide: LlmSettingsService, useValue: service },
        { provide: MatDialog, useValue: dialog },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LlmConnectionsPanelComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('connections', [savedConnection]);
    changed = vi.fn<() => void>();
    component.changed.subscribe(() => changed());
    fixture.detectChanges();
  });

  it('opens the connection dialog to add a connection and notifies on save', () => {
    component.startAdd();

    expect(dialog.open).toHaveBeenCalledWith(
      LlmConnectionDialogComponent,
      expect.objectContaining({
        data: { defaultName: '' },
      }),
    );
    expect(changed).toHaveBeenCalled();
  });

  it('opens the connection dialog with default name when no connections exist', () => {
    fixture.componentRef.setInput('connections', []);
    component.startAdd();

    expect(dialog.open).toHaveBeenCalledWith(
      LlmConnectionDialogComponent,
      expect.objectContaining({
        data: { defaultName: 'Local Ollama' },
      }),
    );
  });

  it('opens the connection dialog to edit a saved connection', () => {
    component.startEdit(savedConnection);

    expect(dialog.open).toHaveBeenCalledWith(
      LlmConnectionDialogComponent,
      expect.objectContaining({
        data: { connection: savedConnection },
      }),
    );
    expect(changed).toHaveBeenCalled();
  });

  it('does not emit changed when the connection dialog is cancelled', () => {
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    component.startEdit(savedConnection);

    expect(changed).not.toHaveBeenCalled();
  });

  it('records a failed card test with the API error', () => {
    service['testSavedConnection'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Host refused connection' } })),
    );

    component.testSaved(savedConnection);

    const result = component.cardTestResults()['desk'];
    expect(result.isReachable).toBe(false);
    expect(result.errorMessage).toBe('Host refused connection');
    expect(component.testingIds().has('desk')).toBe(false);
  });

  it('deletes a connection without dependencies immediately without confirmation', () => {
    const unusedConnection: LlmConnectionDto = {
      ...savedConnection,
      id: 'unused',
      name: 'Unused Host',
      modelConfigurationCount: 0,
    };

    component.delete(unusedConnection);

    expect(dialog.open).not.toHaveBeenCalled();
    expect(service['deleteConnection']).toHaveBeenCalledWith('unused');
    expect(changed).toHaveBeenCalled();
  });

  it('confirms before deleting a connection with saved models and assigned workflows', () => {
    const configs: LlmModelConfigurationDto[] = [
      {
        id: 'cfg-1',
        name: 'models/gemini-3.8-flash @ Desk GPU',
        connectionId: 'desk',
        connectionName: 'Desk GPU',
        provider: 'OpenAiCompatible',
        model: 'models/gemini-3.8-flash',
        ollamaGpuLayers: null,
        maxConcurrentRequests: null,
        requestsPerMinute: null,
        dailyRequestQuota: null,
        dailyQuotaResetTimezone: null,
        dailyQuotaResetTime: null,
        defaultProfileId: 'p1',
        profileCount: 1,
        assignedWorkflows: ['Text'],
        modifiedAt: '2026-11-13T00:00:00Z',
      },
      {
        id: 'cfg-2',
        name: 'models/gemini-4-argon @ Desk GPU',
        connectionId: 'desk',
        connectionName: 'Desk GPU',
        provider: 'OpenAiCompatible',
        model: 'models/gemini-4-argon',
        ollamaGpuLayers: null,
        maxConcurrentRequests: null,
        requestsPerMinute: null,
        dailyRequestQuota: null,
        dailyQuotaResetTimezone: null,
        dailyQuotaResetTime: null,
        defaultProfileId: 'p2',
        profileCount: 2,
        assignedWorkflows: ['Refinement'],
        modifiedAt: '2026-11-13T00:00:00Z',
      },
    ];
    fixture.componentRef.setInput('configurations', configs);

    // Dismissing confirmation leaves the connection untouched
    dialog.open.mockReturnValueOnce({ afterClosed: () => of(false) });
    component.delete(savedConnection);

    expect(dialog.open).toHaveBeenCalledWith(
      ConfirmDialogComponent,
      expect.objectContaining({
        data: expect.objectContaining({
          title: 'Delete Connection',
          confirmText: 'Delete Connection',
          isDestructive: true,
          message:
            'Deleting "Desk GPU" will also delete 2 saved models and their profiles, and deselect them from the Text and Refinement workflows. Continue?',
        }),
      }),
    );
    expect(service['deleteConnection']).not.toHaveBeenCalled();
    expect(changed).not.toHaveBeenCalled();

    // Confirming deletes the connection and emits changed
    dialog.open.mockReturnValueOnce({ afterClosed: () => of(true) });
    component.delete(savedConnection);

    expect(service['deleteConnection']).toHaveBeenCalledWith('desk');
    expect(changed).toHaveBeenCalled();
  });

  it('shows the API error when deleting a connection fails after confirmation', () => {
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    service['deleteConnection'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Unexpected server error.' } })),
    );

    component.delete(savedConnection);

    expect(component.errorMessage()).toBe('Unexpected server error.');
    expect(changed).not.toHaveBeenCalled();
  });
});
