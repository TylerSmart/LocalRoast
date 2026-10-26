import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type {
  LlmConnectionTestResultDto,
  LlmSettingsDto,
} from '../../../core/models/ingestion.models';
import { IngestionService } from '../../../core/services/ingestion/ingestion.service';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { LlmSettingsDialogComponent } from './llm-settings-dialog.component';

describe('LlmSettingsDialogComponent', () => {
  let component: LlmSettingsDialogComponent;
  let fixture: ComponentFixture<LlmSettingsDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };
  let mockLlmSettingsService: {
    getSettings: ReturnType<typeof vi.fn>;
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
    requestTimeoutSeconds: 120,
    autoPublishHighConfidenceText: false,
    modifiedAt: null,
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };
    mockLlmSettingsService = {
      getSettings: vi.fn().mockReturnValue(of(initialSettings)),
      updateSettings: vi.fn().mockReturnValue(of(initialSettings)),
      testConnection: vi.fn(),
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
    component.onSave();

    expect(mockLlmSettingsService.updateSettings).toHaveBeenCalled();
    expect(mockIngestionService.refreshSummary).toHaveBeenCalled();
    expect(mockDialogRef.close).toHaveBeenCalledWith(true);
  });

  it('cancels dialog without saving', () => {
    component.onCancel();
    expect(mockDialogRef.close).toHaveBeenCalledWith(false);
    expect(mockLlmSettingsService.updateSettings).not.toHaveBeenCalled();
  });
});
