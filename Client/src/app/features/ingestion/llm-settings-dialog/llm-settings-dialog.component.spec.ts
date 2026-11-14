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
import { LlmModelConfigurationDialogComponent } from '../llm-model-configuration-dialog/llm-model-configuration-dialog.component';
import { LlmModelProfileDialogComponent } from '../llm-model-profile-dialog/llm-model-profile-dialog.component';
import {
  LLM_SETTINGS_TABS,
  LlmSettingsDialogComponent,
  filterModels,
  matchModelProfile,
} from './llm-settings-dialog.component';

const testProfiles: LlmModelProfile[] = [
  {
    id: 'gemma4-e4b',
    displayName: 'Gemma 4 E4B',
    modelTag: 'gemma4:e4b',
    matchPrefixes: ['gemma4:e4b'],
    summary: 'Fast default',
    enableThinking: false,
    temperature: 0.2,
    topP: 0.95,
    topK: 64,
    contextWindowTokens: 16384,
    requestTimeoutSeconds: 600,
    recommendedAsRefineModel: false,
    notes: ['Small model.'],
  },
];

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
  model: string,
): LlmModelConfigurationDto => ({
  id,
  name: `${model} @ ${connectionId}`,
  connectionId,
  connectionName: connectionId,
  provider: 'Ollama',
  model,
  profileId: null,
  enableThinking: true,
  temperature: 0.7,
  topP: 0.9,
  topK: 40,
  contextWindowTokens: 16384,
  requestTimeoutSeconds: 450,
  ollamaGpuLayers: null,
  assignedWorkflows: [],
  modifiedAt: '2026-11-13T00:00:00Z',
});

describe('LlmSettingsDialogComponent', () => {
  let component: LlmSettingsDialogComponent;
  let fixture: ComponentFixture<LlmSettingsDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };
  let mockDialog: { open: ReturnType<typeof vi.fn> };
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let mockIngestionService: { refreshSummary: ReturnType<typeof vi.fn> };

  const connections = [connection('desk', 'Desk GPU'), connection('laptop', 'Laptop')];
  const configurations = [
    configuration('cfg-text', 'desk', 'gemma4:12b'),
    configuration('cfg-vision', 'laptop', 'qwen2.5vl:7b'),
    configuration('cfg-refine', 'laptop', 'gemma4:12b'),
  ];
  const settings = {
    autoPublishHighConfidenceText: true,
    enableIngestion: true,
    maxIngestionRetries: 5,
    autoResumeQueue: false,
    textModelConfigurationId: 'cfg-text',
    visionModelConfigurationId: 'cfg-vision',
    refineModelConfigurationId: null,
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
      open: vi.fn().mockReturnValue({ afterClosed: () => of(testProfiles[0]) }),
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
      restoreDefaultModelProfiles: vi.fn().mockReturnValue(of(testProfiles)),
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

  it('loads the workflow assignments and the saved catalog', async () => {
    await create();

    const values = component.workflowForm.getRawValue();
    expect(values.textModelConfigurationId).toBe('cfg-text');
    expect(values.visionModelConfigurationId).toBe('cfg-vision');
    expect(values.refineModelConfigurationId).toBe('');
    expect(values.maxIngestionRetries).toBe(5);
    expect(values.autoResumeQueue).toBe(false);
    expect(component.selectedTabIndex()).toBe(LLM_SETTINGS_TABS.workflows);
    expect(component.textConfiguration()?.model).toBe('gemma4:12b');
    expect(component.configurationGroups().map((g) => g.connection.id)).toEqual(['desk', 'laptop']);
    expect(component.configurationGroups()[1].configurations.map((c) => c.id)).toEqual([
      'cfg-vision',
      'cfg-refine',
    ]);
  });

  it('opens on the Connections tab when nothing is configured yet', async () => {
    service['getConnections'].mockReturnValue(of([]));
    service['getModelConfigurations'].mockReturnValue(of([]));
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

  it('saves workflow assignments, sending unassigned workflows as null', async () => {
    await create();
    component.workflowForm.controls.visionModelConfigurationId.setValue('');
    component.workflowForm.controls.refineModelConfigurationId.setValue('cfg-refine');

    component.onSave();

    expect(service['updateWorkflows']).toHaveBeenCalledWith({
      textModelConfigurationId: 'cfg-text',
      visionModelConfigurationId: null,
      refineModelConfigurationId: 'cfg-refine',
      autoPublishHighConfidenceText: true,
      maxIngestionRetries: 5,
      enableIngestion: true,
      autoResumeQueue: false,
    });
    expect(mockIngestionService.refreshSummary).toHaveBeenCalled();
    expect(mockDialogRef.close).toHaveBeenCalledWith(true);
  });

  it('keeps the dialog open and shows the API error when saving fails', async () => {
    await create();
    service['updateWorkflows'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Unknown model configuration.' } })),
    );
    component.selectedTabIndex.set(LLM_SETTINGS_TABS.profiles);

    component.onSave();

    expect(component.errorMessage()).toBe('Unknown model configuration.');
    expect(component.selectedTabIndex()).toBe(LLM_SETTINGS_TABS.workflows);
    expect(mockDialogRef.close).not.toHaveBeenCalled();
  });

  it('blocks saving with an out-of-range retry count', async () => {
    await create();
    component.workflowForm.controls.maxIngestionRetries.setValue(11);

    component.onSave();

    expect(service['updateWorkflows']).not.toHaveBeenCalled();
  });

  it('warns when refinement would reuse the text model on the same connection', async () => {
    await create();
    expect(component.refineMatchesText()).toBe(false);

    service['getModelConfigurations'].mockReturnValue(
      of([...configurations, configuration('cfg-same', 'desk', 'gemma4:12b')]),
    );
    component.reloadCatalog();
    component.workflowForm.controls.refineModelConfigurationId.setValue('cfg-same');
    expect(component.refineMatchesText()).toBe(true);

    component.workflowForm.controls.refineModelConfigurationId.setValue('cfg-refine');
    expect(component.refineMatchesText()).toBe(false);
  });

  it('clears assignments whose configuration was deleted', async () => {
    await create();
    service['getModelConfigurations'].mockReturnValue(of([configurations[0]]));

    component.reloadCatalog();

    expect(component.workflowForm.controls.textModelConfigurationId.value).toBe('cfg-text');
    expect(component.workflowForm.controls.visionModelConfigurationId.value).toBe('');
  });

  it('assigns the first saved configuration to the text workflow', async () => {
    service['getSettings'].mockReturnValue(of({ ...settings, textModelConfigurationId: null }));
    service['getModelConfigurations'].mockReturnValue(of([]));
    await create();

    service['getModelConfigurations'].mockReturnValue(of([configurations[0]]));
    component.reloadCatalog();

    expect(component.workflowForm.controls.textModelConfigurationId.value).toBe('cfg-text');
  });

  it('opens the model configuration dialog from a profile card on the Models tab', async () => {
    await create();
    component.selectedTabIndex.set(LLM_SETTINGS_TABS.profiles);

    component.applyProfileToModel(testProfiles[0]);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(component.selectedTabIndex()).toBe(LLM_SETTINGS_TABS.models);
    expect(mockDialog.open).toHaveBeenCalledWith(
      LlmModelConfigurationDialogComponent,
      expect.objectContaining({
        data: {
          initialProfile: testProfiles[0],
          connections,
          profiles: testProfiles,
        },
      }),
    );
  });

  it('opens the profile dialog pre-filled from a saved model configuration', async () => {
    await create();

    component.saveConfigurationAsProfile(configurations[0]);

    expect(component.selectedTabIndex()).toBe(LLM_SETTINGS_TABS.profiles);
    expect(mockDialog.open).toHaveBeenCalledWith(
      LlmModelProfileDialogComponent,
      expect.objectContaining({
        data: { fromConfiguration: configurations[0] },
      }),
    );
    expect(service['getModelProfiles']).toHaveBeenCalledTimes(2);
  });

  it('opens profile dialog for add/edit, and deletes and restores profiles', async () => {
    await create();

    component.startAddProfile();
    expect(mockDialog.open).toHaveBeenCalledWith(
      LlmModelProfileDialogComponent,
      expect.objectContaining({ data: {} }),
    );
    expect(service['getModelProfiles']).toHaveBeenCalledTimes(2);

    component.startEditProfile(testProfiles[0]);
    expect(mockDialog.open).toHaveBeenCalledWith(
      LlmModelProfileDialogComponent,
      expect.objectContaining({ data: { profile: testProfiles[0] } }),
    );
    expect(service['getModelProfiles']).toHaveBeenCalledTimes(3);

    component.deleteProfile(testProfiles[0]);
    expect(service['deleteModelProfile']).toHaveBeenCalledWith('gemma4-e4b');

    component.restoreDefaults();
    expect(service['restoreDefaultModelProfiles']).toHaveBeenCalled();
  });

  it('closes without saving on cancel', async () => {
    await create();

    component.onCancel();

    expect(mockDialogRef.close).toHaveBeenCalledWith(false);
    expect(service['updateWorkflows']).not.toHaveBeenCalled();
  });

  it('matches model profiles by the longest prefix, case-insensitively', () => {
    const extra = { ...testProfiles[0], id: 'longer', matchPrefixes: ['gemma4:e4b-it'] };
    expect(matchModelProfile(testProfiles, 'GEMMA4:E4B')?.id).toBe('gemma4-e4b');
    expect(matchModelProfile([...testProfiles, extra], 'gemma4:e4b-it-q8')?.id).toBe('longer');
    expect(matchModelProfile(testProfiles, 'gemma4:12b')).toBeNull();
    expect(matchModelProfile(testProfiles, '  ')).toBeNull();
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
