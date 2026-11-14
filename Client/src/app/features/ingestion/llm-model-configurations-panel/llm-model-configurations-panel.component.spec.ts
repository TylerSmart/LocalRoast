import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { LlmModelConfigurationDialogComponent } from '../llm-model-configuration-dialog/llm-model-configuration-dialog.component';
import { LlmModelConfigurationsPanelComponent } from './llm-model-configurations-panel.component';

const connection = (id: string, name: string): LlmConnectionDto => ({
  id,
  name,
  provider: 'Ollama',
  baseUrl: `http://${id}:11434`,
  hasApiKey: false,
  apiKeyMasked: null,
  ollamaKeepAlive: '5m',
  ollamaCpuOnly: false,
  enableRateLimitSafeguards: true,
  maxConcurrentRequests: 1,
  requestsPerMinute: null,
  dailyRequestQuota: null,
  dailyQuotaResetTimezone: 'UTC',
  dailyQuotaResetTime: '00:00',
  modelConfigurationCount: 0,
  modifiedAt: '2026-11-13T00:00:00Z',
});

const profile: LlmModelProfile = {
  id: 'gemma4-12b-thinking',
  displayName: 'Gemma 4 12B (thinking)',
  modelTag: 'gemma4:12b',
  matchPrefixes: ['gemma4:12b'],
  summary: 'Best quality',
  enableThinking: true,
  temperature: 1,
  topP: 0.95,
  topK: 64,
  contextWindowTokens: 16384,
  requestTimeoutSeconds: 1200,
  recommendedAsRefineModel: true,
  notes: [],
};

const savedConfiguration: LlmModelConfigurationDto = {
  id: 'cfg-1',
  name: 'Desk text',
  connectionId: 'desk',
  connectionName: 'Desk GPU',
  provider: 'Ollama',
  model: 'qwen2.5:14b',
  profileId: null,
  enableThinking: false,
  temperature: 0.2,
  topP: null,
  topK: null,
  contextWindowTokens: 8192,
  requestTimeoutSeconds: 300,
  ollamaGpuLayers: 24,
  assignedWorkflows: ['Text'],
  modifiedAt: '2026-11-13T00:00:00Z',
};

describe('LlmModelConfigurationsPanelComponent', () => {
  let component: LlmModelConfigurationsPanelComponent;
  let fixture: ComponentFixture<LlmModelConfigurationsPanelComponent>;
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let dialog: { open: ReturnType<typeof vi.fn> };
  let changed: ReturnType<typeof vi.fn<() => void>>;
  const connections = [connection('desk', 'Desk GPU'), connection('laptop', 'Laptop')];

  beforeEach(async () => {
    service = {
      deleteModelConfiguration: vi.fn().mockReturnValue(of(undefined)),
    };
    dialog = {
      open: vi.fn().mockReturnValue({ afterClosed: () => of(savedConfiguration) }),
    };

    await TestBed.configureTestingModule({
      imports: [LlmModelConfigurationsPanelComponent],
      providers: [
        { provide: LlmSettingsService, useValue: service },
        { provide: MatDialog, useValue: dialog },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LlmModelConfigurationsPanelComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('connections', connections);
    fixture.componentRef.setInput('configurations', [savedConfiguration]);
    fixture.componentRef.setInput('profiles', [profile]);
    changed = vi.fn<() => void>();
    component.changed.subscribe(() => changed());
    fixture.detectChanges();
  });

  it('opens the model configuration dialog to add a model and emits changed on save', () => {
    component.startAdd();

    expect(dialog.open).toHaveBeenCalledWith(
      LlmModelConfigurationDialogComponent,
      expect.objectContaining({
        data: { connections, profiles: [profile] },
      }),
    );
    expect(changed).toHaveBeenCalled();
  });

  it('opens the model configuration dialog to edit a saved model', () => {
    component.startEdit(savedConfiguration);

    expect(dialog.open).toHaveBeenCalledWith(
      LlmModelConfigurationDialogComponent,
      expect.objectContaining({
        data: { configuration: savedConfiguration, connections, profiles: [profile] },
      }),
    );
    expect(changed).toHaveBeenCalled();
  });

  it('opens the model configuration dialog to duplicate a saved model', () => {
    component.duplicate(savedConfiguration);

    expect(dialog.open).toHaveBeenCalledWith(
      LlmModelConfigurationDialogComponent,
      expect.objectContaining({
        data: { duplicateFrom: savedConfiguration, connections, profiles: [profile] },
      }),
    );
    expect(changed).toHaveBeenCalled();
  });

  it('opens the model configuration dialog with an initial profile', () => {
    component.applyProfile(profile);

    expect(dialog.open).toHaveBeenCalledWith(
      LlmModelConfigurationDialogComponent,
      expect.objectContaining({
        data: { initialProfile: profile, connections, profiles: [profile] },
      }),
    );
    expect(changed).toHaveBeenCalled();
  });

  it('does not emit changed when the dialog is cancelled', () => {
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    component.startAdd();

    expect(changed).not.toHaveBeenCalled();
  });

  it('deletes a model configuration and emits changed', () => {
    component.delete(savedConfiguration);

    expect(service['deleteModelConfiguration']).toHaveBeenCalledWith('cfg-1');
    expect(changed).toHaveBeenCalled();
  });

  it('displays API error when deleting fails', () => {
    service['deleteModelConfiguration'].mockReturnValue(
      throwError(() => ({ error: { detail: 'In use by workflow' } })),
    );

    component.delete(savedConfiguration);

    expect(component.errorMessage()).toBe('In use by workflow');
    expect(changed).not.toHaveBeenCalled();
  });
});
