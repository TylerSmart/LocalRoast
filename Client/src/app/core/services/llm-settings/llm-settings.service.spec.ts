import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom, of } from 'rxjs';
import { AiKitchenHelperService } from '../ai-helpers/ai-kitchen-helper.service';
import {
  LlmConnectionTestResultDto,
  LlmSettingsDto,
  TestLlmConnectionRequest,
  UpdateLlmSettingsRequest,
} from '../../models/ingestion.models';
import { LlmSettingsService } from './llm-settings.service';

describe('LlmSettingsService', () => {
  let service: LlmSettingsService;
  let httpMock: HttpTestingController;
  let mockDialog: { open: ReturnType<typeof vi.fn> };
  let mockAiHelperService: { checkStatus: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    mockDialog = {
      open: vi.fn(),
    };
    mockAiHelperService = {
      checkStatus: vi.fn().mockReturnValue(of({ isAvailable: true })),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        LlmSettingsService,
        { provide: MatDialog, useValue: mockDialog },
        { provide: AiKitchenHelperService, useValue: mockAiHelperService },
      ],
    });

    service = TestBed.inject(LlmSettingsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('gets model profiles via GET /api/llm-settings/model-profiles', async () => {
    const promise = firstValueFrom(service.getModelProfiles());
    const req = httpMock.expectOne('/api/llm-settings/model-profiles');
    expect(req.request.method).toBe('GET');
    req.flush([{ id: 'gemma4-e4b' }]);
    expect((await promise)[0].id).toBe('gemma4-e4b');
  });

  it('gets LLM settings via GET /api/llm-settings', async () => {
    const mockSettings: LlmSettingsDto = {
      provider: 'Ollama',
      baseUrl: 'http://localhost:11434',
      hasApiKey: false,
      apiKeyMasked: null,
      textModel: 'qwen2.5:14b',
      visionModel: null,
      ollamaKeepAlive: '5m',
      contextWindowTokens: 8192,
      requestTimeoutSeconds: 120,
      autoPublishHighConfidenceText: false,
      enableThinking: false,
      enableIngestion: true,
      maxIngestionRetries: 3,
      modifiedAt: null,
    };

    const promise = firstValueFrom(service.getSettings());
    const req = httpMock.expectOne('/api/llm-settings');
    expect(req.request.method).toBe('GET');
    req.flush(mockSettings);

    const result = await promise;
    expect(result).toEqual(mockSettings);
  });

  it('updates LLM settings via PUT /api/llm-settings', async () => {
    const request: UpdateLlmSettingsRequest = {
      provider: 'Ollama',
      baseUrl: 'http://localhost:11434',
      textModel: 'llama3:8b',
    };

    const mockResponse: LlmSettingsDto = {
      provider: 'Ollama',
      baseUrl: 'http://localhost:11434',
      hasApiKey: false,
      apiKeyMasked: null,
      textModel: 'llama3:8b',
      visionModel: null,
      ollamaKeepAlive: '5m',
      contextWindowTokens: 8192,
      requestTimeoutSeconds: 120,
      autoPublishHighConfidenceText: false,
      enableThinking: false,
      enableIngestion: true,
      maxIngestionRetries: 3,
      modifiedAt: '2026-10-26T12:00:00Z',
    };

    const promise = firstValueFrom(service.updateSettings(request));
    const req = httpMock.expectOne('/api/llm-settings');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(request);
    req.flush(mockResponse);

    const result = await promise;
    expect(result).toEqual(mockResponse);
  });

  it('sets ingestion enabled via POST /api/llm-settings/set-ingestion-enabled', async () => {
    const mockResponse: LlmSettingsDto = {
      provider: 'Ollama',
      baseUrl: 'http://localhost:11434',
      hasApiKey: false,
      apiKeyMasked: null,
      textModel: 'qwen2.5:14b',
      visionModel: null,
      ollamaKeepAlive: '5m',
      contextWindowTokens: 8192,
      requestTimeoutSeconds: 120,
      autoPublishHighConfidenceText: false,
      enableThinking: false,
      enableIngestion: false,
      maxIngestionRetries: 3,
      modifiedAt: '2026-10-26T12:00:00Z',
    };

    const promise = firstValueFrom(service.setIngestionEnabled(false));
    const req = httpMock.expectOne('/api/llm-settings/set-ingestion-enabled?enabled=false');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    req.flush(mockResponse);

    const result = await promise;
    expect(result).toEqual(mockResponse);
  });

  it('tests LLM connection via POST /api/llm-settings/test', async () => {
    const request: TestLlmConnectionRequest = {
      provider: 'Ollama',
      baseUrl: 'http://localhost:11434',
    };

    const mockResult: LlmConnectionTestResultDto = {
      isReachable: true,
      provider: 'Ollama',
      normalizedBaseUrl: 'http://localhost:11434',
      latencyMs: 15,
      models: ['qwen2.5:14b', 'llama3:8b'],
      errorMessage: null,
    };

    const promise = firstValueFrom(service.testConnection(request));
    const req = httpMock.expectOne('/api/llm-settings/test');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(mockResult);

    const result = await promise;
    expect(result).toEqual(mockResult);
  });

  it('saves workflow assignments via PUT /api/llm-settings/workflows and rechecks AI status', async () => {
    const request = {
      textModelConfigurationId: 'cfg-text',
      visionModelConfigurationId: null,
      refineModelConfigurationId: null,
      autoPublishHighConfidenceText: false,
      maxIngestionRetries: 3,
      enableIngestion: true,
      autoResumeQueue: true,
    };

    const promise = firstValueFrom(service.updateWorkflows(request));
    const req = httpMock.expectOne('/api/llm-settings/workflows');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(request);
    req.flush({});
    await promise;

    expect(mockAiHelperService.checkStatus).toHaveBeenCalledWith(true);
  });

  it('calls the connection and model configuration endpoints', () => {
    service.getConnections().subscribe();
    httpMock.expectOne({ method: 'GET', url: '/api/llm-settings/connections' }).flush([]);

    service.testSavedConnection('desk').subscribe();
    httpMock
      .expectOne({ method: 'POST', url: '/api/llm-settings/connections/desk/test' })
      .flush({});

    service.deleteConnection('desk').subscribe();
    httpMock.expectOne({ method: 'DELETE', url: '/api/llm-settings/connections/desk' }).flush(null);

    service.getModelConfigurations().subscribe();
    httpMock.expectOne({ method: 'GET', url: '/api/llm-settings/model-configurations' }).flush([]);

    service.deleteModelConfiguration('cfg-1').subscribe();
    httpMock
      .expectOne({ method: 'DELETE', url: '/api/llm-settings/model-configurations/cfg-1' })
      .flush(null);
  });

  it('creates model profile via POST /api/llm-settings/model-profiles', async () => {
    const request = {
      displayName: 'Qwen Custom',
      modelTag: 'qwen:14b',
    };
    const mockCreated = {
      id: 'qwen-custom',
      displayName: 'Qwen Custom',
      modelTag: 'qwen:14b',
      matchPrefixes: [],
      summary: '',
      enableThinking: false,
      temperature: null,
      topP: null,
      topK: null,
      contextWindowTokens: 32768,
      requestTimeoutSeconds: 600,
      recommendedAsRefineModel: false,
      notes: [],
    };

    const promise = firstValueFrom(service.createModelProfile(request));
    const req = httpMock.expectOne('/api/llm-settings/model-profiles');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(mockCreated);

    const result = await promise;
    expect(result.id).toBe('qwen-custom');
  });

  it('updates model profile via PUT /api/llm-settings/model-profiles/:id', async () => {
    const request = {
      displayName: 'Updated Profile',
      modelTag: 'updated:tag',
    };
    const mockUpdated = {
      id: 'existing-profile',
      displayName: 'Updated Profile',
      modelTag: 'updated:tag',
      matchPrefixes: [],
      summary: '',
      enableThinking: false,
      temperature: null,
      topP: null,
      topK: null,
      contextWindowTokens: 32768,
      requestTimeoutSeconds: 600,
      recommendedAsRefineModel: false,
      notes: [],
    };

    const promise = firstValueFrom(service.updateModelProfile('existing-profile', request));
    const req = httpMock.expectOne('/api/llm-settings/model-profiles/existing-profile');
    expect(req.request.method).toBe('PUT');
    req.flush(mockUpdated);

    const result = await promise;
    expect(result.displayName).toBe('Updated Profile');
  });

  it('deletes model profile via DELETE /api/llm-settings/model-profiles/:id', async () => {
    const promise = firstValueFrom(service.deleteModelProfile('to-delete'));
    const req = httpMock.expectOne('/api/llm-settings/model-profiles/to-delete');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await promise;
  });

  it('restores default model profiles via POST /api/llm-settings/model-profiles/restore-defaults', async () => {
    const mockList = [{ id: 'gemma4-e4b', displayName: 'Gemma 4 E4B' }];
    const promise = firstValueFrom(service.restoreDefaultModelProfiles());
    const req = httpMock.expectOne('/api/llm-settings/model-profiles/restore-defaults');
    expect(req.request.method).toBe('POST');
    req.flush(mockList);

    const result = await promise;
    expect(result.length).toBe(1);
    expect(result[0].id).toBe('gemma4-e4b');
  });
});
