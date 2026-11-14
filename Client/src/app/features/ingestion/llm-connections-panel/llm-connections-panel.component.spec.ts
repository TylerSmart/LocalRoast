import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type { LlmConnectionDto } from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
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

  it('shows the conflict message when a connection is still in use', () => {
    service['deleteConnection'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Connection is used by 2 saved models.' } })),
    );

    component.delete(savedConnection);

    expect(component.errorMessage()).toBe('Connection is used by 2 saved models.');
    expect(changed).not.toHaveBeenCalled();
  });
});
