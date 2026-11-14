import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type { LlmConnectionDto } from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  LlmConnectionDialogComponent,
  type LlmConnectionDialogData,
} from './llm-connection-dialog.component';

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

describe('LlmConnectionDialogComponent', () => {
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  async function create(data: LlmConnectionDialogData | null = null): Promise<{
    component: LlmConnectionDialogComponent;
    fixture: ComponentFixture<LlmConnectionDialogComponent>;
  }> {
    await TestBed.configureTestingModule({
      imports: [LlmConnectionDialogComponent],
      providers: [
        { provide: LlmSettingsService, useValue: service },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MAT_DIALOG_DATA, useValue: data },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(LlmConnectionDialogComponent);
    const component = fixture.componentInstance;
    fixture.detectChanges();
    return { component, fixture };
  }

  beforeEach(() => {
    dialogRef = { close: vi.fn() };
    service = {
      createConnection: vi.fn().mockReturnValue(of(savedConnection)),
      updateConnection: vi.fn().mockReturnValue(of(savedConnection)),
      testConnection: vi.fn().mockReturnValue(of(reachable)),
    };
  });

  it('creates a connection and closes with the saved result', async () => {
    const { component } = await create({ defaultName: 'Local Ollama' });
    expect(component.form.controls.name.value).toBe('Local Ollama');

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
    expect(dialogRef.close).toHaveBeenCalledWith(savedConnection);
  });

  it('edits a saved connection without resending its stored API key', async () => {
    const { component } = await create({ connection: savedConnection });
    expect(component.hasSavedApiKey()).toBe(true);
    expect(component.form.controls.apiKey.value).toBe('');
    component.form.controls.name.setValue('Desk');

    component.save();

    expect(service['updateConnection']).toHaveBeenCalledWith(
      'desk',
      expect.objectContaining({ name: 'Desk', apiKey: null, clearApiKey: false }),
    );
    expect(dialogRef.close).toHaveBeenCalledWith(savedConnection);
  });

  it('switches the default base URL when the provider changes', async () => {
    const { component } = await create();

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
  ] as const)('blocks saving when %s is %s', async (control, value) => {
    const { component } = await create();
    component.form.controls.name.setValue('Valid');
    component.form.controls[control].setValue(value as never);

    component.save();

    expect(service['createConnection']).not.toHaveBeenCalled();
  });

  it('tests a new connection without borrowing another connection API key', async () => {
    const { component } = await create();
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

  it('tests an edited connection with its saved API key', async () => {
    const { component } = await create({ connection: savedConnection });

    component.testEditor();

    expect(service['testConnection']).toHaveBeenCalledWith(
      expect.objectContaining({ connectionId: 'desk', apiKey: null, clearApiKey: false }),
    );
  });

  it('displays API error when saving fails', async () => {
    service['createConnection'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Duplicate connection name.' } })),
    );
    const { component } = await create();
    component.form.controls.name.setValue('Dup');

    component.save();

    expect(component.errorMessage()).toBe('Duplicate connection name.');
    expect(dialogRef.close).not.toHaveBeenCalled();
  });
});
