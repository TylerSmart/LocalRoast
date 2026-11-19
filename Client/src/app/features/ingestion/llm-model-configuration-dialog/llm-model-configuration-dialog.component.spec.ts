import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
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
  maxConcurrentRequests: 2,
  requestsPerMinute: 30,
  dailyRequestQuota: 1000,
  dailyQuotaResetTimezone: 'UTC',
  dailyQuotaResetTime: '00:00',
  modelConfigurationCount: 0,
  modifiedAt: '2026-11-13T00:00:00Z',
});

const savedConfiguration: LlmModelConfigurationDto = {
  id: 'cfg-1',
  name: 'Desk text',
  connectionId: 'desk',
  connectionName: 'Desk GPU',
  provider: 'Ollama',
  model: 'qwen2.5:14b',
  ollamaGpuLayers: 24,
  maxConcurrentRequests: 1,
  requestsPerMinute: 10,
  dailyRequestQuota: 250,
  dailyQuotaResetTimezone: 'America/Los_Angeles',
  dailyQuotaResetTime: '00:00',
  defaultProfileId: 'prof-1',
  profileCount: 1,
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
      existingConfigurations: [],
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

  it('saves a new model with a default name and optional per-model rate limits', async () => {
    const { component } = await create();
    component.form.controls.model.setValue('gemma4:12b');
    component.form.controls.maxConcurrentRequests.setValue(1);
    component.form.controls.requestsPerMinute.setValue(10);
    component.form.controls.dailyRequestQuota.setValue(250);
    component.form.controls.dailyQuotaResetTimezone.setValue('America/Los_Angeles');
    component.form.controls.dailyQuotaResetTime.setValue('00:00');

    component.save();

    expect(service['createModelConfiguration']).toHaveBeenCalledWith({
      name: 'gemma4:12b @ Desk GPU',
      connectionId: 'desk',
      model: 'gemma4:12b',
      ollamaGpuLayers: null,
      maxConcurrentRequests: 1,
      requestsPerMinute: 10,
      dailyRequestQuota: 250,
      dailyQuotaResetTimezone: 'America/Los_Angeles',
      dailyQuotaResetTime: '00:00',
    });
    expect(dialogRef.close).toHaveBeenCalledWith(savedConfiguration);
  });

  it('updates an existing model and preserves its custom name and rate limits', async () => {
    const { component } = await create({
      configuration: savedConfiguration,
      existingConfigurations: [savedConfiguration],
    });
    component.form.controls.requestsPerMinute.setValue(15);

    component.save();

    expect(service['updateModelConfiguration']).toHaveBeenCalledWith(
      'cfg-1',
      expect.objectContaining({
        name: 'Desk text',
        model: 'qwen2.5:14b',
        ollamaGpuLayers: 24,
        requestsPerMinute: 15,
        dailyRequestQuota: 250,
      }),
    );
    expect(dialogRef.close).toHaveBeenCalledWith(savedConfiguration);
  });

  it('blocks saving duplicate (connection, model) pairs case-insensitively', async () => {
    const { component } = await create({ existingConfigurations: [savedConfiguration] });
    component.form.controls.model.setValue('  QWEN2.5:14B  ');

    expect(component.isDuplicateModelOnConnection()).toBe(true);
    component.save();
    expect(service['createModelConfiguration']).not.toHaveBeenCalled();

    // Same model on a different connection is allowed
    component.form.controls.connectionId.setValue('laptop');
    expect(component.isDuplicateModelOnConnection()).toBe(false);
  });

  it.each([
    ['model', ''],
    ['ollamaGpuLayers', -1],
    ['ollamaGpuLayers', 1.5],
    ['maxConcurrentRequests', 0],
    ['maxConcurrentRequests', 6],
    ['requestsPerMinute', 0],
    ['requestsPerMinute', 1001],
    ['dailyRequestQuota', 0],
    ['dailyQuotaResetTime', '25:00'],
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
