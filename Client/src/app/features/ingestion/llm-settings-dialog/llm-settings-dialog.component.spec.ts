import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type {
  LlmConnectionTestResultDto,
  LlmModelProfile,
  LlmSettingsDto,
} from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
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
    extractionRepairRounds: 2,
    useStagedExtraction: true,
    recommendedAsRefineModel: false,
    notes: ['Small model.'],
  },
  {
    id: 'gemma4-12b-thinking',
    displayName: 'Gemma 4 12B (thinking)',
    modelTag: 'gemma4:12b',
    matchPrefixes: [],
    summary: 'Best quality',
    enableThinking: true,
    temperature: 1,
    topP: 0.95,
    topK: 64,
    contextWindowTokens: 16384,
    requestTimeoutSeconds: 1200,
    extractionRepairRounds: 1,
    useStagedExtraction: false,
    recommendedAsRefineModel: true,
    notes: [],
  },
];

describe('LlmSettingsDialogComponent', () => {
  let component: LlmSettingsDialogComponent;
  let fixture: ComponentFixture<LlmSettingsDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };
  let mockLlmSettingsService: {
    getSettings: ReturnType<typeof vi.fn>;
    getModelProfiles: ReturnType<typeof vi.fn>;
    updateSettings: ReturnType<typeof vi.fn>;
    testConnection: ReturnType<typeof vi.fn>;
  };
  let mockIngestionService: {
    refreshSummary: ReturnType<typeof vi.fn>;
  };

  const initialSettings: LlmSettingsDto = {
    provider: 'Ollama',
    baseUrl: 'http://localhost:11434',
    hasApiKey: false,
    apiKeyMasked: null,
    textModel: 'qwen2.5:14b',
    visionModel: 'qwen2.5vl:7b',
    ollamaKeepAlive: '5m',
    contextWindowTokens: 8192,
    requestTimeoutSeconds: 120,
    autoPublishHighConfidenceText: false,
    enableThinking: true,
    enableIngestion: true,
    maxIngestionRetries: 5,
    modifiedAt: null,
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };
    mockLlmSettingsService = {
      getSettings: vi.fn().mockReturnValue(of(initialSettings)),
      getModelProfiles: vi.fn().mockReturnValue(of(testProfiles)),
      updateSettings: vi.fn().mockReturnValue(of(initialSettings)),
      testConnection: vi.fn().mockReturnValue(
        of({
          isReachable: true,
          provider: 'Ollama',
          normalizedBaseUrl: 'http://localhost:11434',
          latencyMs: 15,
          models: ['qwen2.5:14b'],
          errorMessage: null,
        }),
      ),
    };
    mockIngestionService = {
      refreshSummary: vi.fn(),
    };

    await TestBed.configureTestingModule({
      imports: [LlmSettingsDialogComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: LlmSettingsService, useValue: mockLlmSettingsService },
        { provide: IngestionService, useValue: mockIngestionService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LlmSettingsDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads initial settings and populates form', () => {
    expect(mockLlmSettingsService.getSettings).toHaveBeenCalled();
    expect(component.form.controls.provider.value).toBe('Ollama');
    expect(component.form.controls.baseUrl.value).toBe('http://localhost:11434');
    expect(component.form.controls.textModel.value).toBe('qwen2.5:14b');
    expect(component.form.controls.visionModel.value).toBe('qwen2.5vl:7b');
    expect(component.form.controls.contextWindowTokens.value).toBe(8192);
    expect(component.form.controls.enableThinking.value).toBe(true);
    expect(component.form.controls.enableIngestion.value).toBe(true);
    expect(component.form.controls.maxIngestionRetries.value).toBe(5);
    expect(component.form.controls.ollamaCpuOnly.value).toBe(false);
    expect(component.form.controls.temperature.value).toBeNull();
    expect(component.form.controls.topP.value).toBeNull();
    expect(component.form.controls.topK.value).toBeNull();
    expect(component.form.controls.extractionRepairRounds.value).toBe(1);
  });

  it('loads saved repair rounds and long timeouts into the form', () => {
    mockLlmSettingsService.getSettings.mockReturnValue(
      of({ ...initialSettings, requestTimeoutSeconds: 1800, extractionRepairRounds: 0 }),
    );

    component.loadSettings();

    expect(component.form.controls.requestTimeoutSeconds.value).toBe(1800);
    expect(component.form.controls.extractionRepairRounds.value).toBe(0);
    expect(component.form.valid).toBe(true);
  });

  it('loads saved sampling overrides into the form', () => {
    mockLlmSettingsService.getSettings.mockReturnValue(
      of({ ...initialSettings, temperature: 1, topP: 0.95, topK: 64 }),
    );

    component.loadSettings();

    expect(component.form.controls.temperature.value).toBe(1);
    expect(component.form.controls.topP.value).toBe(0.95);
    expect(component.form.controls.topK.value).toBe(64);
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

  it('narrows each model autocomplete independently as the user types', () => {
    component.discoveredModels.set(['gemma4:e4b', 'gemma4:12b', 'qwen2.5:14b']);
    component.form.controls.textModel.setValue('qwen');
    component.form.controls.visionModel.setValue('');
    component.form.controls.refineModel.setValue('12b');

    expect(component.filteredTextModels()).toEqual(['qwen2.5:14b']);
    expect(component.filteredVisionModels()).toEqual(['gemma4:e4b', 'gemma4:12b', 'qwen2.5:14b']);
    expect(component.filteredRefineModels()).toEqual(['gemma4:12b']);
  });

  it('applies a profile without renaming the model and sets the strategy explicitly', () => {
    component.form.controls.textModel.setValue('my-gemma-e4b-gpu');
    component.onProfileSelected('gemma4-e4b');
    component.applyProfile();

    const values = component.form.getRawValue();
    expect(values.textModel).toBe('my-gemma-e4b-gpu');
    expect(values.enableThinking).toBe(false);
    expect(values.temperature).toBe(0.2);
    expect(values.contextWindowTokens).toBe(16384);
    expect(values.extractionRepairRounds).toBe(2);
    expect(values.extractionStrategy).toBe('Staged');
    expect(component.profileMessage()).toContain('Gemma 4 E4B');

    component.form.controls.textModel.setValue('gemma4:e4b');
    component.applyProfile();
    expect(component.form.controls.extractionStrategy.value).toBe('Auto');
  });

  it('fills a blank refine model from the refine profile and saves refine settings', () => {
    expect(component.refineProfiles().map((p) => p.id)).toEqual(['gemma4-12b-thinking']);
    component.form.controls.refineModel.setValue('');
    component.onRefineProfileSelected('gemma4-12b-thinking');
    component.applyRefineProfile();

    expect(component.form.controls.refineModel.value).toBe('gemma4:12b');
    expect(component.form.controls.refineEnableThinking.value).toBe(true);
    expect(component.form.controls.refineRequestTimeoutSeconds.value).toBe(1200);

    component.onSave();
    expect(mockLlmSettingsService.updateSettings).toHaveBeenLastCalledWith(
      expect.objectContaining({
        refineModel: 'gemma4:12b',
        refineEnableThinking: true,
        refineTemperature: 1,
        refineRequestTimeoutSeconds: 1200,
      }),
    );
  });

  it('renders the profile picker and hides it when profiles cannot load', () => {
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('[data-testid="llm-profile-select"]')).toBeTruthy();
    expect(element.querySelector('[data-testid="llm-extraction-strategy"]')).toBeTruthy();

    mockLlmSettingsService.getModelProfiles.mockReturnValue(throwError(() => new Error('down')));
    component.loadProfiles();
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="llm-profile-select"]')).toBeNull();
  });

  it('saves sampling overrides and sends blanks as null', () => {
    component.form.controls.temperature.setValue(1);
    component.form.controls.topP.setValue(0.95);
    component.form.controls.topK.setValue(64);
    component.onSave();

    expect(mockLlmSettingsService.updateSettings).toHaveBeenLastCalledWith(
      expect.objectContaining({ temperature: 1, topP: 0.95, topK: 64 }),
    );

    component.form.controls.temperature.setValue(null);
    component.form.controls.topP.setValue(null);
    component.form.controls.topK.setValue(null);
    component.onSave();

    expect(mockLlmSettingsService.updateSettings).toHaveBeenLastCalledWith(
      expect.objectContaining({ temperature: null, topP: null, topK: null }),
    );
  });

  it.each([
    ['temperature', 2.5],
    ['temperature', -0.1],
    ['topP', 1.5],
    ['topK', 0],
    ['topK', 12.5],
    ['topK', 1001],
    ['extractionRepairRounds', -1],
    ['extractionRepairRounds', 1.5],
    ['extractionRepairRounds', 6],
    ['requestTimeoutSeconds', 1801],
  ] as const)('blocks saving when %s is %s', (control, value) => {
    component.form.controls[control].setValue(value);

    component.onSave();

    expect(component.form.invalid).toBe(true);
    expect(mockLlmSettingsService.updateSettings).not.toHaveBeenCalled();
  });

  it('automatically attempts to pull models on dialog init when baseUrl is configured', () => {
    expect(mockLlmSettingsService.testConnection).toHaveBeenCalledWith(
      expect.objectContaining({
        baseUrl: 'http://localhost:11434',
        provider: 'Ollama',
      }),
    );
  });

  it('switches provider and updates default base url', () => {
    component.form.controls.provider.setValue('OpenAiCompatible');
    expect(component.form.controls.baseUrl.value).toBe('http://localhost:1234/v1');

    component.form.controls.provider.setValue('Ollama');
    expect(component.form.controls.baseUrl.value).toBe('http://localhost:11434');
  });

  it('runs connection test and surfaces discovered models', () => {
    const testResult: LlmConnectionTestResultDto = {
      isReachable: true,
      provider: 'Ollama',
      normalizedBaseUrl: 'http://localhost:11434',
      latencyMs: 18,
      models: ['qwen2.5:14b', 'llama3:8b'],
      errorMessage: null,
    };
    mockLlmSettingsService.testConnection.mockReturnValue(of(testResult));

    component.testConnection();
    expect(mockLlmSettingsService.testConnection).toHaveBeenCalled();
    expect(component.testResult()).toEqual(testResult);
    expect(component.discoveredModels()).toEqual(['qwen2.5:14b', 'llama3:8b']);
  });

  it('handles connection test error gracefully', () => {
    mockLlmSettingsService.testConnection.mockReturnValue(
      throwError(() => ({ error: { detail: 'Host refused connection' } })),
    );

    component.testConnection();
    expect(component.testResult()?.isReachable).toBe(false);
    expect(component.testResult()?.errorMessage).toBe('Host refused connection');
  });

  it('saves settings and closes dialog', () => {
    component.form.controls.textModel.setValue('mistral:7b');
    component.form.controls.contextWindowTokens.setValue(16384);
    component.form.controls.enableThinking.setValue(false);
    component.form.controls.maxIngestionRetries.setValue(2);
    component.form.controls.extractionRepairRounds.setValue(3);
    component.form.controls.requestTimeoutSeconds.setValue(1800);
    component.form.controls.ollamaCpuOnly.setValue(true);
    component.onSave();

    expect(mockLlmSettingsService.updateSettings).toHaveBeenCalledWith(
      expect.objectContaining({
        textModel: 'mistral:7b',
        contextWindowTokens: 16384,
        enableThinking: false,
        maxIngestionRetries: 2,
        extractionRepairRounds: 3,
        requestTimeoutSeconds: 1800,
        ollamaCpuOnly: true,
      }),
    );
    expect(mockIngestionService.refreshSummary).toHaveBeenCalled();
    expect(mockDialogRef.close).toHaveBeenCalledWith(true);
  });

  it('cancels dialog without saving', () => {
    component.onCancel();
    expect(mockDialogRef.close).toHaveBeenCalledWith(false);
    expect(mockLlmSettingsService.updateSettings).not.toHaveBeenCalled();
  });
});
