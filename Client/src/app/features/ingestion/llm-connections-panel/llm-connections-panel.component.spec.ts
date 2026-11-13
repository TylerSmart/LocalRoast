import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import type { LlmConnectionDto } from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { LlmConnectionsPanelComponent } from './llm-connections-panel.component';

const savedConnection: LlmConnectionDto = {
  id: 'desk',
  name: 'Desk GPU',
  provider: 'OpenAiCompatible',
  baseUrl: 'https://llm.example.com/v1',
  hasApiKey: true,
  apiKeyMasked: '****1234',
  ollamaKeepAlive: '5m',
  ollamaCpuOnly: false,
  enableRateLimitSafeguards: true,
  maxConcurrentRequests: 2,
  requestsPerMinute: 30,
  dailyRequestQuota: 1500,
  dailyQuotaResetTimezone: 'America/Los_Angeles',
  dailyQuotaResetTime: '00:00',
  modelConfigurationCount: 2,
  modifiedAt: '2026-11-13T00:00:00Z',
};

const reachable = {
  isReachable: true,
  provider: 'Ollama' as const,
  normalizedBaseUrl: 'http://gpu-box:11434',
  latencyMs: 12,
  models: ['gemma4:12b'],
  errorMessage: null,
};

describe('LlmConnectionsPanelComponent', () => {
  let component: LlmConnectionsPanelComponent;
  let fixture: ComponentFixture<LlmConnectionsPanelComponent>;
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let changed: ReturnType<typeof vi.fn<() => void>>;

  beforeEach(async () => {
    service = {
      createConnection: vi.fn().mockReturnValue(of(savedConnection)),
      updateConnection: vi.fn().mockReturnValue(of(savedConnection)),
      deleteConnection: vi.fn().mockReturnValue(of(undefined)),
      testConnection: vi.fn().mockReturnValue(of(reachable)),
      testSavedConnection: vi.fn().mockReturnValue(of(reachable)),
    };

    await TestBed.configureTestingModule({
      imports: [LlmConnectionsPanelComponent],
      providers: [{ provide: LlmSettingsService, useValue: service }],
    }).compileComponents();

    fixture = TestBed.createComponent(LlmConnectionsPanelComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('connections', [savedConnection]);
    changed = vi.fn<() => void>();
    component.changed.subscribe(() => changed());
    fixture.detectChanges();
  });

  it('creates a connection from the editor and notifies the dialog', () => {
    component.startAdd();
    component.form.patchValue({
      name: ' GPU box ',
      baseUrl: 'http://gpu-box:11434 ',
      ollamaCpuOnly: true,
      maxConcurrentRequests: 2,
      requestsPerMinute: 20,
    });

    component.save();

    expect(service['createConnection']).toHaveBeenCalledWith(
      expect.objectContaining({
        name: 'GPU box',
        provider: 'Ollama',
        baseUrl: 'http://gpu-box:11434',
        apiKey: null,
        ollamaCpuOnly: true,
        maxConcurrentRequests: 2,
        requestsPerMinute: 20,
        dailyRequestQuota: null,
      }),
    );
    expect(changed).toHaveBeenCalled();
    expect(component.isEditing()).toBe(false);
  });

  it('edits a saved connection without resending its stored API key', () => {
    component.startEdit(savedConnection);
    expect(component.hasSavedApiKey()).toBe(true);
    expect(component.form.controls.apiKey.value).toBe('');
    component.form.controls.name.setValue('Desk');

    component.save();

    expect(service['updateConnection']).toHaveBeenCalledWith(
      'desk',
      expect.objectContaining({ name: 'Desk', apiKey: null, clearApiKey: false }),
    );
  });

  it('switches the default base URL when the provider changes', () => {
    component.startAdd();

    component.form.controls.provider.setValue('OpenAiCompatible');
    expect(component.form.controls.baseUrl.value).toBe('http://localhost:1234/v1');

    component.form.controls.provider.setValue('Ollama');
    expect(component.form.controls.baseUrl.value).toBe('http://localhost:11434');
  });

  it.each([
    ['name', ''],
    ['maxConcurrentRequests', 6],
    ['requestsPerMinute', 1001],
    ['dailyRequestQuota', 0],
    ['dailyQuotaResetTime', '24:00'],
  ] as const)('blocks saving when %s is %s', (control, value) => {
    component.startAdd();
    component.form.controls[control].setValue(value as never);

    component.save();

    expect(service['createConnection']).not.toHaveBeenCalled();
  });

  it('tests a new connection without borrowing another connection API key', () => {
    component.startAdd();
    component.form.controls.baseUrl.setValue('http://gpu-box:11434');

    component.testEditor();

    expect(service['testConnection']).toHaveBeenCalledWith({
      connectionId: null,
      provider: 'Ollama',
      baseUrl: 'http://gpu-box:11434',
      apiKey: null,
      clearApiKey: true,
    });
    expect(component.editorTestResult()).toEqual(reachable);
  });

  it('tests an edited connection with its saved API key', () => {
    component.startEdit(savedConnection);

    component.testEditor();

    expect(service['testConnection']).toHaveBeenCalledWith(
      expect.objectContaining({ connectionId: 'desk', apiKey: null, clearApiKey: false }),
    );
  });

  it('records a failed card test with the API error', () => {
    service['testSavedConnection'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Host refused connection' } })),
    );

    component.testSaved(savedConnection);

    const result = component.cardTestResults()['desk'];
    expect(result.isReachable).toBe(false);
    expect(result.errorMessage).toBe('Host refused connection');
    expect(component.testingIds().has('desk')).toBe(false);
  });

  it('shows the conflict message when a connection is still in use', () => {
    service['deleteConnection'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Connection is used by 2 saved models.' } })),
    );

    component.delete(savedConnection);

    expect(component.errorMessage()).toBe('Connection is used by 2 saved models.');
    expect(changed).not.toHaveBeenCalled();
  });
});
