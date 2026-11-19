import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
  LlmSettingsDto,
} from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { LlmModelProfileDialogComponent } from '../llm-model-profile-dialog/llm-model-profile-dialog.component';
import {
  LLM_SETTINGS_TABS,
  LlmSettingsDialogComponent,
  filterModels,
} from './llm-settings-dialog.component';

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
  modelConfigurationCount: 1,
  modifiedAt: '2026-11-13T00:00:00Z',
});

const configuration = (
  id: string,
  connectionId: string,
  connectionName: string,
  model: string,
  defaultProfileId: string,
  profileCount = 1,
): LlmModelConfigurationDto => ({
  id,
  name: `${model} @ ${connectionName}`,
  connectionId,
  connectionName,
  provider: 'Ollama',
  model,
  ollamaGpuLayers: null,
  maxConcurrentRequests: null,
  requestsPerMinute: null,
  dailyRequestQuota: null,
  dailyQuotaResetTimezone: null,
  dailyQuotaResetTime: null,
  defaultProfileId,
  profileCount,
  assignedWorkflows: [],
  modifiedAt: '2026-11-13T00:00:00Z',
});

const profile = (
  id: string,
  config: LlmModelConfigurationDto,
  displayName: string,
  isDefault: boolean,
  overrides: Partial<LlmModelProfile> = {},
): LlmModelProfile => ({
  id,
  modelConfigurationId: config.id,
  modelConfigurationName: config.name,
  connectionId: config.connectionId,
  connectionName: config.connectionName,
  provider: config.provider,
  model: config.model,
  displayName,
  summary: isDefault ? 'Default tuning for this model.' : `${displayName} preset`,
  enableThinking: false,
  temperature: null,
  topP: null,
  topK: null,
  contextWindowTokens: 8192,
  requestTimeoutSeconds: 120,
  notes: [],
  isDefault,
  assignedWorkflows: [],
  modifiedAt: '2026-11-13T00:00:00Z',
  ...overrides,
});

describe('LlmSettingsDialogComponent', () => {
  let component: LlmSettingsDialogComponent;
  let fixture: ComponentFixture<LlmSettingsDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };
  let mockDialog: { open: ReturnType<typeof vi.fn> };
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let mockIngestionService: { refreshSummary: ReturnType<typeof vi.fn> };

  const connections = [connection('desk', 'Desk GPU'), connection('laptop', 'Laptop')];
  const cfgText = configuration('cfg-text', 'desk', 'Desk GPU', 'gemma4:12b', 'prof-text-def', 2);
  const cfgVision = configuration(
    'cfg-vision',
    'laptop',
    'Laptop',
    'qwen2.5vl:7b',
    'prof-vision-def',
    1,
  );
  const cfgRefine = configuration(
    'cfg-refine',
    'laptop',
    'Laptop',
    'gemma4:12b',
    'prof-refine-def',
    1,
  );
  const configurations = [cfgText, cfgVision, cfgRefine];

  const profTextDefault = profile('prof-text-def', cfgText, 'Defaults', true);
  const profTextThinking = profile('prof-text-thinking', cfgText, 'Thinking', false, {
    enableThinking: true,
    temperature: 1,
    contextWindowTokens: 32768,
    requestTimeoutSeconds: 900,
  });
  const profVisionDefault = profile('prof-vision-def', cfgVision, 'Defaults', true);
  const profRefineDefault = profile('prof-refine-def', cfgRefine, 'Defaults', true);
  const testProfiles = [profTextThinking, profTextDefault, profVisionDefault, profRefineDefault];

  const settings = {
    autoPublishHighConfidenceText: true,
    enableIngestion: true,
    maxIngestionRetries: 5,
    autoResumeQueue: false,
    textModelProfileId: 'prof-text-def',
    visionModelProfileId: 'prof-vision-def',
    refineModelProfileId: null,
    embeddingModelProfileId: null,
  } as unknown as LlmSettingsDto;

  async function create(): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [LlmSettingsDialogComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: LlmSettingsService, useValue: service },
        { provide: IngestionService, useValue: mockIngestionService },
      ],
    })
      .overrideProvider(MatDialog, { useValue: mockDialog })
      .compileComponents();

    fixture = TestBed.createComponent(LlmSettingsDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  beforeEach(() => {
    mockDialogRef = { close: vi.fn() };
    mockDialog = {
      open: vi.fn().mockReturnValue({ afterClosed: () => of(profTextThinking) }),
    };
    mockIngestionService = { refreshSummary: vi.fn() };
    service = {
      getSettings: vi.fn().mockReturnValue(of(settings)),
      getConnections: vi.fn().mockReturnValue(of(connections)),
      getModelConfigurations: vi.fn().mockReturnValue(of(configurations)),
      getModelProfiles: vi.fn().mockReturnValue(of(testProfiles)),
      createModelProfile: vi.fn().mockImplementation((req) => of({ ...req, id: 'custom' })),
      updateModelProfile: vi.fn().mockImplementation((id, req) => of({ ...req, id })),
      deleteModelProfile: vi.fn().mockReturnValue(of(undefined)),
      updateWorkflows: vi.fn().mockReturnValue(of(settings)),
      testSavedConnection: vi.fn().mockReturnValue(
        of({
          isReachable: true,
          provider: 'Ollama',
          normalizedBaseUrl: 'http://desk:11434',
          latencyMs: 5,
          models: ['gemma4:12b'],
          errorMessage: null,
        }),
      ),
    };
  });

  it('loads workflow profile assignments and groups profiles under each model with Defaults first', async () => {
    await create();

    const values = component.workflowForm.getRawValue();
    expect(values.textModelProfileId).toBe('prof-text-def');
    expect(values.visionModelProfileId).toBe('prof-vision-def');
    expect(values.refineModelProfileId).toBe('');
    expect(values.maxIngestionRetries).toBe(5);
    expect(values.autoResumeQueue).toBe(false);
    expect(component.selectedTabIndex()).toBe(LLM_SETTINGS_TABS.workflows);
    expect(component.textProfile()?.model).toBe('gemma4:12b');
    expect(component.profileGroups().map((g) => g.configuration.id)).toEqual([
      'cfg-text',
      'cfg-refine',
      'cfg-vision',
    ]);
    // Defaults is always sorted ahead of custom profiles inside a model group
    expect(component.profileGroups()[0].profiles.map((p) => p.id)).toEqual([
      'prof-text-def',
      'prof-text-thinking',
    ]);
  });

  it('opens on the Connections tab when nothing is configured yet', async () => {
    service['getConnections'].mockReturnValue(of([]));
    service['getModelConfigurations'].mockReturnValue(of([]));
    service['getModelProfiles'].mockReturnValue(of([]));
    await create();

    expect(component.selectedTabIndex()).toBe(LLM_SETTINGS_TABS.connections);
  });

  it('shows the load error from the API', async () => {
    service['getConnections'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Database offline' } })),
    );
    await create();

    expect(component.errorMessage()).toBe('Database offline');
    expect(component.isLoading()).toBe(false);
  });

  it('saves workflow profile assignments, sending unassigned workflows as null', async () => {
    await create();
    component.workflowForm.controls.visionModelProfileId.setValue('');
    component.workflowForm.controls.refineModelProfileId.setValue('prof-text-thinking');

    component.onSave();

    expect(service['updateWorkflows']).toHaveBeenCalledWith({
      textModelProfileId: 'prof-text-def',
      visionModelProfileId: null,
      refineModelProfileId: 'prof-text-thinking',
      embeddingModelProfileId: null,
      autoPublishHighConfidenceText: true,
      maxIngestionRetries: 5,
      enableIngestion: true,
      autoResumeQueue: false,
    });
    expect(mockIngestionService.refreshSummary).toHaveBeenCalled();
    expect(mockDialogRef.close).toHaveBeenCalledWith(true);
  });

  it('persists selected embeddingModelProfileId when assigned', async () => {
    const cfgEmbed = configuration(
      'cfg-embed',
      'desk',
      'Desk GPU',
      'embeddinggemma',
      'prof-embed-def',
    );
    const profEmbed = profile('prof-embed-def', cfgEmbed, 'Defaults', true);
    service['getModelConfigurations'].mockReturnValue(of([...configurations, cfgEmbed]));
    service['getModelProfiles'].mockReturnValue(of([...testProfiles, profEmbed]));
    await create();
    component.workflowForm.controls.embeddingModelProfileId.setValue('prof-embed-def');

    component.onSave();

    expect(service['updateWorkflows']).toHaveBeenCalledWith(
      expect.objectContaining({
        embeddingModelProfileId: 'prof-embed-def',
      }),
    );
  });

  it('keeps the dialog open and shows the API error when saving fails', async () => {
    await create();
    service['updateWorkflows'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Unknown model profile.' } })),
    );
    component.selectedTabIndex.set(LLM_SETTINGS_TABS.profiles);

    component.onSave();

    expect(component.errorMessage()).toBe('Unknown model profile.');
    expect(component.selectedTabIndex()).toBe(LLM_SETTINGS_TABS.workflows);
    expect(mockDialogRef.close).not.toHaveBeenCalled();
  });

  it('blocks saving with an out-of-range retry count', async () => {
    await create();
    component.workflowForm.controls.maxIngestionRetries.setValue(11);

    component.onSave();

    expect(service['updateWorkflows']).not.toHaveBeenCalled();
  });

  it('only warns refineMatchesText when Text and Refinement select the exact same profile ID', async () => {
    await create();
    expect(component.refineMatchesText()).toBe(false);

    // Same model + same profile -> warns
    component.workflowForm.controls.refineModelProfileId.setValue('prof-text-def');
    expect(component.refineMatchesText()).toBe(true);

    // Same model + DIFFERENT profile -> does NOT warn
    component.workflowForm.controls.refineModelProfileId.setValue('prof-text-thinking');
    expect(component.refineMatchesText()).toBe(false);

    // Different connection/model -> does NOT warn
    component.workflowForm.controls.refineModelProfileId.setValue('prof-refine-def');
    expect(component.refineMatchesText()).toBe(false);
  });

  it('clears assignments whose profile was deleted during reloadCatalog', async () => {
    await create();
    service['getModelConfigurations'].mockReturnValue(of([cfgText]));
    service['getModelProfiles'].mockReturnValue(of([profTextDefault, profTextThinking]));

    component.reloadCatalog();

    expect(component.workflowForm.controls.textModelProfileId.value).toBe('prof-text-def');
    expect(component.workflowForm.controls.visionModelProfileId.value).toBe('');
  });

  it('assigns the first saved model Defaults profile to the text workflow', async () => {
    service['getSettings'].mockReturnValue(of({ ...settings, textModelProfileId: null }));
    service['getModelConfigurations'].mockReturnValue(of([]));
    service['getModelProfiles'].mockReturnValue(of([]));
    await create();

    service['getModelConfigurations'].mockReturnValue(of([cfgText]));
    service['getModelProfiles'].mockReturnValue(of([profTextDefault]));
    component.reloadCatalog();

    expect(component.workflowForm.controls.textModelProfileId.value).toBe('prof-text-def');
  });

  it('refuses to delete a Defaults profile and opens profile dialog with catalog context', async () => {
    await create();

    component.deleteProfile(profTextDefault);
    expect(service['deleteModelProfile']).not.toHaveBeenCalled();

    component.duplicateProfile(profTextDefault);
    expect(mockDialog.open).toHaveBeenCalledWith(
      LlmModelProfileDialogComponent,
      expect.objectContaining({
        data: expect.objectContaining({
          duplicateFrom: profTextDefault,
          connections,
          configurations,
          existingProfiles: testProfiles,
        }),
      }),
    );

    component.startAddProfile('cfg-text');
    expect(mockDialog.open).toHaveBeenCalledWith(
      LlmModelProfileDialogComponent,
      expect.objectContaining({
        data: expect.objectContaining({
          initialModelConfigurationId: 'cfg-text',
          connections,
          configurations,
        }),
      }),
    );

    component.startEditProfile(profTextThinking);
    expect(mockDialog.open).toHaveBeenCalledWith(
      LlmModelProfileDialogComponent,
      expect.objectContaining({
        data: expect.objectContaining({ profile: profTextThinking }),
      }),
    );

    component.deleteProfile(profTextThinking);
    expect(service['deleteModelProfile']).toHaveBeenCalledWith('prof-text-thinking');
  });

  it('closes without saving on cancel', async () => {
    await create();

    component.onCancel();

    expect(mockDialogRef.close).toHaveBeenCalledWith(false);
    expect(service['updateWorkflows']).not.toHaveBeenCalled();
  });

  it('filters models by every typed word and shows all for a blank or exact query', () => {
    const models = ['google/gemini-3.8-flash', 'google/gemini-3.8-pro', 'openai/gpt-5-mini'];
    expect(filterModels(models, 'Gemini  FLASH')).toEqual(['google/gemini-3.8-flash']);
    expect(filterModels(models, 'gemini')).toEqual(models.slice(0, 2));
    expect(filterModels(models, 'claude')).toEqual([]);
    expect(filterModels(models, '  ')).toEqual(models);
    expect(filterModels(models, 'OPENAI/GPT-5-MINI')).toEqual(models);
  });
});
