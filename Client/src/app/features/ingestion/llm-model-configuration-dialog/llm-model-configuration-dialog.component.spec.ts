import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  LlmModelConfigurationDialogComponent,
  type LlmModelConfigurationDialogData,
} from './llm-model-configuration-dialog.component';

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

const modelsFor = (models: string[]) =>
  of({
    isReachable: true,
    provider: 'Ollama' as const,
    normalizedBaseUrl: 'http://desk:11434',
    latencyMs: 5,
    models,
    errorMessage: null,
  });

describe('LlmModelConfigurationDialogComponent', () => {
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  async function create(overrides: Partial<LlmModelConfigurationDialogData> = {}): Promise<{
    component: LlmModelConfigurationDialogComponent;
    fixture: ComponentFixture<LlmModelConfigurationDialogComponent>;
  }> {
    const data: LlmModelConfigurationDialogData = {
      connections: [connection('desk', 'Desk GPU'), connection('laptop', 'Laptop')],
      profiles: [profile],
      ...overrides,
    };

    await TestBed.configureTestingModule({
      imports: [LlmModelConfigurationDialogComponent],
      providers: [
        { provide: LlmSettingsService, useValue: service },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: data },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(LlmModelConfigurationDialogComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    return { component, fixture };
  }

  beforeEach(() => {
    dialogRef = { close: vi.fn() };
    service = {
      testSavedConnection: vi
        .fn()
        .mockImplementation((id: string) =>
          modelsFor(id === 'desk' ? ['gemma4:12b', 'qwen2.5:14b'] : ['llama3:8b']),
        ),
      createModelConfiguration: vi.fn().mockReturnValue(of(savedConfiguration)),
      updateModelConfiguration: vi.fn().mockReturnValue(of(savedConfiguration)),
    };
  });

  it('lists models from the chosen connection when adding a model', async () => {
    const { component } = await create();

    expect(component.form.controls.connectionId.value).toBe('desk');
    expect(service['testSavedConnection']).toHaveBeenCalledWith('desk');
    expect(component.availableModels()).toEqual(['gemma4:12b', 'qwen2.5:14b']);

    component.form.controls.connectionId.setValue('laptop');
    expect(service['testSavedConnection']).toHaveBeenCalledWith('laptop');
    expect(component.availableModels()).toEqual(['llama3:8b']);

    const callsBeforeReturning = service['testSavedConnection'].mock.calls.length;
    component.form.controls.connectionId.setValue('desk');
    expect(service['testSavedConnection']).toHaveBeenCalledTimes(callsBeforeReturning);
    expect(component.availableModels()).toEqual(['gemma4:12b', 'qwen2.5:14b']);
  });

  it('suggests the matching profile when a model is picked', async () => {
    const { component } = await create();
    component.form.controls.model.setValue('gemma4:12b-it-q8');

    component.onModelSelected('gemma4:12b-it-q8');

    expect(component.selectedProfileId()).toBe(profile.id);
    expect(component.appliedProfileId()).toBeNull();
  });

  it('applies a profile without replacing an already chosen model', async () => {
    const { component } = await create();
    component.form.controls.model.setValue('my-gemma-gpu');
    component.form.controls.ollamaGpuLayers.setValue(24);

    component.applyProfile(profile);

    const values = component.form.getRawValue();
    expect(values.model).toBe('my-gemma-gpu');
    expect(values.enableThinking).toBe(true);
    expect(values.temperature).toBe(1);
    expect(values.topK).toBe(64);
    expect(values.requestTimeoutSeconds).toBe(1200);
    expect(values.ollamaGpuLayers).toBe(24);
    expect(component.appliedProfileId()).toBe(profile.id);
  });

  it('opens a new model from an initial profile using its model tag', async () => {
    const { component } = await create({ initialProfile: profile });

    expect(component.editingConfiguration()).toBeNull();
    expect(component.form.controls.model.value).toBe('gemma4:12b');
    expect(component.appliedProfileId()).toBe(profile.id);
  });

  it('saves a new model with a default name and the applied profile', async () => {
    const { component } = await create();
    component.form.controls.model.setValue('gemma4:12b');
    component.applyProfile(profile);

    component.save();

    expect(service['createModelConfiguration']).toHaveBeenCalledWith({
      name: 'gemma4:12b @ Desk GPU',
      connectionId: 'desk',
      model: 'gemma4:12b',
      profileId: profile.id,
      enableThinking: true,
      temperature: 1,
      topP: 0.95,
      topK: 64,
      contextWindowTokens: 16384,
      requestTimeoutSeconds: 1200,
      ollamaGpuLayers: null,
    });
    expect(dialogRef.close).toHaveBeenCalledWith(savedConfiguration);
  });

  it('updates an existing model and keeps a custom name', async () => {
    const { component } = await create({ configuration: savedConfiguration });
    component.form.controls.temperature.setValue(0.4);

    component.save();

    expect(service['updateModelConfiguration']).toHaveBeenCalledWith(
      'cfg-1',
      expect.objectContaining({
        name: 'Desk text',
        model: 'qwen2.5:14b',
        temperature: 0.4,
        ollamaGpuLayers: 24,
      }),
    );
    expect(dialogRef.close).toHaveBeenCalledWith(savedConfiguration);
  });

  it('duplicates a model as a new configuration', async () => {
    const { component } = await create({ duplicateFrom: savedConfiguration });
    component.save();

    expect(service['createModelConfiguration']).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'Desk text (copy)',
        model: 'qwen2.5:14b',
        ollamaGpuLayers: 24,
      }),
    );
  });

  it.each([
    ['model', ''],
    ['temperature', 2.5],
    ['topK', 12.5],
    ['ollamaGpuLayers', -1],
    ['ollamaGpuLayers', 1.5],
    ['contextWindowTokens', 1024],
    ['requestTimeoutSeconds', 1801],
  ] as const)('blocks saving when %s is %s', async (control, value) => {
    const { component } = await create();
    component.form.controls.model.setValue('gemma4:12b');
    component.form.controls[control].setValue(value as never);

    component.save();

    expect(service['createModelConfiguration']).not.toHaveBeenCalled();
  });

  it.each([0, null])(
    'saves GPU layers as %s without retaining the previous override',
    async (layers) => {
      const { component } = await create({ configuration: savedConfiguration });
      component.form.controls.ollamaGpuLayers.setValue(layers);
      component.save();
      expect(service['updateModelConfiguration']).toHaveBeenCalledWith(
        savedConfiguration.id,
        expect.objectContaining({ ollamaGpuLayers: layers }),
      );
    },
  );

  it('only shows GPU layers for Ollama and explains the CPU-only override', async () => {
    const { component, fixture } = await create({
      connections: [
        { ...connection('desk', 'Desk GPU'), ollamaCpuOnly: true },
        { ...connection('cloud', 'Cloud'), provider: 'OpenAiCompatible' },
      ],
    });
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('[data-testid="model-config-gpu-layers"]')).not.toBeNull();
    expect(element.textContent).toContain('Connection CPU-only mode overrides');
    component.form.controls.connectionId.setValue('cloud');
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="model-config-gpu-layers"]')).toBeNull();
  });
});
