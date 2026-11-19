import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
  LlmModelProfile,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  LlmModelProfileDialogComponent,
  type LlmModelProfileDialogData,
} from './llm-model-profile-dialog.component';

const ollamaConnection: LlmConnectionDto = {
  id: 'conn-ollama',
  name: 'ThinkStation',
  provider: 'Ollama',
  baseUrl: 'http://desk:11434',
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
  modifiedAt: '2026-11-19T00:00:00Z',
};

const cloudConnection: LlmConnectionDto = {
  ...ollamaConnection,
  id: 'conn-cloud',
  name: 'Cloud',
  provider: 'OpenAiCompatible',
  baseUrl: 'https://api.example.com/v1',
};

const ollamaConfig: LlmModelConfigurationDto = {
  id: 'cfg-ollama',
  name: 'gemma4:e4b @ ThinkStation',
  connectionId: 'conn-ollama',
  connectionName: 'ThinkStation',
  provider: 'Ollama',
  model: 'gemma4:e4b',
  ollamaGpuLayers: null,
  maxConcurrentRequests: null,
  requestsPerMinute: null,
  dailyRequestQuota: null,
  dailyQuotaResetTimezone: null,
  dailyQuotaResetTime: null,
  defaultProfileId: 'prof-default',
  profileCount: 2,
  assignedWorkflows: [],
  modifiedAt: '2026-11-19T00:00:00Z',
};

const cloudConfig: LlmModelConfigurationDto = {
  ...ollamaConfig,
  id: 'cfg-cloud',
  name: 'gemini-flash @ Cloud',
  connectionId: 'conn-cloud',
  connectionName: 'Cloud',
  provider: 'OpenAiCompatible',
  model: 'gemini-flash',
  defaultProfileId: 'prof-cloud-default',
  profileCount: 1,
};

const defaultProfile: LlmModelProfile = {
  id: 'prof-default',
  modelConfigurationId: 'cfg-ollama',
  modelConfigurationName: 'gemma4:e4b @ ThinkStation',
  connectionId: 'conn-ollama',
  connectionName: 'ThinkStation',
  provider: 'Ollama',
  model: 'gemma4:e4b',
  displayName: 'Defaults',
  summary: 'Default tuning for this model.',
  enableThinking: false,
  temperature: null,
  topP: null,
  topK: null,
  contextWindowTokens: 8192,
  requestTimeoutSeconds: 120,
  notes: [],
  isDefault: true,
  assignedWorkflows: [],
  modifiedAt: '2026-11-19T00:00:00Z',
};

const existingProfile: LlmModelProfile = {
  ...defaultProfile,
  id: 'prof-thinking',
  displayName: 'Thinking',
  summary: 'Deep reasoning preset',
  enableThinking: true,
  temperature: 0.2,
  topP: 0.95,
  topK: 64,
  contextWindowTokens: 16384,
  requestTimeoutSeconds: 600,
  notes: ['Good for tricky recipes.'],
  isDefault: false,
};

describe('LlmModelProfileDialogComponent', () => {
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  async function create(overrides: Partial<LlmModelProfileDialogData> = {}): Promise<{
    component: LlmModelProfileDialogComponent;
    fixture: ComponentFixture<LlmModelProfileDialogComponent>;
  }> {
    const data: LlmModelProfileDialogData = {
      connections: [ollamaConnection, cloudConnection],
      configurations: [ollamaConfig, cloudConfig],
      existingProfiles: [defaultProfile, existingProfile],
      ...overrides,
    };

    await TestBed.configureTestingModule({
      imports: [LlmModelProfileDialogComponent],
      providers: [
        { provide: LlmSettingsService, useValue: service },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: data },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(LlmModelProfileDialogComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    return { component, fixture };
  }

  beforeEach(() => {
    dialogRef = { close: vi.fn() };
    service = {
      createModelProfile: vi.fn().mockImplementation((req) => of({ ...req, id: 'prof-new' })),
      updateModelProfile: vi.fn().mockImplementation((id, req) => of({ ...req, id })),
    };
  });

  it('creates a new profile bound to a model and closes with the saved result', async () => {
    const { component } = await create({ initialModelConfigurationId: 'cfg-ollama' });
    component.form.controls.displayName.setValue('Low Temp');
    component.form.controls.enableThinking.setValue(true);

    component.save();

    expect(service['createModelProfile']).toHaveBeenCalledWith(
      expect.objectContaining({
        modelConfigurationId: 'cfg-ollama',
        displayName: 'Low Temp',
        enableThinking: true,
        contextWindowTokens: 8192,
        requestTimeoutSeconds: 120,
      }),
    );
    expect(dialogRef.close).toHaveBeenCalledWith(expect.objectContaining({ id: 'prof-new' }));
  });

  it('updates an existing custom profile', async () => {
    const { component } = await create({ profile: existingProfile });
    component.form.controls.summary.setValue('Updated');

    component.save();

    expect(service['updateModelProfile']).toHaveBeenCalledWith(
      'prof-thinking',
      expect.objectContaining({
        modelConfigurationId: 'cfg-ollama',
        displayName: 'Thinking',
        summary: 'Updated',
      }),
    );
    expect(dialogRef.close).toHaveBeenCalledWith(expect.objectContaining({ id: 'prof-thinking' }));
  });

  it('locks model and displayName when editing a Defaults profile while allowing tuning edits', async () => {
    const { component } = await create({ profile: defaultProfile });

    expect(component.isEditingDefault()).toBe(true);
    expect(component.form.controls.modelConfigurationId.disabled).toBe(true);
    expect(component.form.controls.displayName.disabled).toBe(true);

    component.form.controls.enableThinking.setValue(true);
    component.form.controls.requestTimeoutSeconds.setValue(300);
    component.save();

    expect(service['updateModelProfile']).toHaveBeenCalledWith(
      'prof-default',
      expect.objectContaining({
        modelConfigurationId: 'cfg-ollama',
        displayName: 'Defaults',
        enableThinking: true,
        requestTimeoutSeconds: 300,
      }),
    );
  });

  it('pre-fills a new profile bound to the same model when duplicating an existing profile', async () => {
    const { component } = await create({ duplicateFrom: existingProfile });

    expect(component.editingProfile()).toBeNull();
    const values = component.form.getRawValue();
    expect(values.modelConfigurationId).toBe('cfg-ollama');
    expect(values.displayName).toBe('Thinking (copy)');
    expect(values.temperature).toBe(0.2);
    expect(values.contextWindowTokens).toBe(16384);
    expect(values.requestTimeoutSeconds).toBe(600);
  });

  it('shows context window section only for Ollama models', async () => {
    const { component, fixture } = await create({ initialModelConfigurationId: 'cfg-ollama' });
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="profile-context-section"]')).not.toBeNull();

    component.form.controls.modelConfigurationId.setValue('cfg-cloud');
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="profile-context-section"]')).toBeNull();
  });

  it('blocks saving duplicate displayName on the same model, while allowing the same name on another model', async () => {
    const { component } = await create({ initialModelConfigurationId: 'cfg-ollama' });
    component.form.controls.displayName.setValue('  thinking  ');

    expect(component.isDuplicateName()).toBe(true);
    component.save();
    expect(service['createModelProfile']).not.toHaveBeenCalled();

    component.form.controls.modelConfigurationId.setValue('cfg-cloud');
    expect(component.isDuplicateName()).toBe(false);
  });

  it('shows API error when saving fails', async () => {
    service['createModelProfile'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Profile already exists.' } })),
    );
    const { component } = await create({ initialModelConfigurationId: 'cfg-ollama' });
    component.form.controls.displayName.setValue('Unique Name');

    component.save();

    expect(component.errorMessage()).toBe('Profile already exists.');
    expect(dialogRef.close).not.toHaveBeenCalled();
  });
});
