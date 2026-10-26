import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
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

  beforeEach(() => {
    mockDialog = {
      open: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        LlmSettingsService,
        { provide: MatDialog, useValue: mockDialog },
      ],
    });

    service = TestBed.inject(LlmSettingsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
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
      requestTimeoutSeconds: 120,
      autoPublishHighConfidenceText: false,
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
      requestTimeoutSeconds: 120,
      autoPublishHighConfidenceText: false,
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
});
