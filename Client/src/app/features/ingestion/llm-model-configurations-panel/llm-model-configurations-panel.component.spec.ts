import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type {
  LlmConnectionDto,
  LlmModelConfigurationDto,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { LlmModelConfigurationDialogComponent } from '../llm-model-configuration-dialog/llm-model-configuration-dialog.component';
import { LlmModelConfigurationsPanelComponent } from './llm-model-configurations-panel.component';

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

const savedConfiguration: LlmModelConfigurationDto = {
  id: 'cfg-1',
  name: 'Desk text',
  connectionId: 'desk',
  connectionName: 'Desk GPU',
  provider: 'Ollama',
  model: 'qwen2.5:14b',
  ollamaGpuLayers: 24,
  maxConcurrentRequests: 1,
  requestsPerMinute: 12,
  dailyRequestQuota: 500,
  dailyQuotaResetTimezone: 'America/Los_Angeles',
  dailyQuotaResetTime: '00:00',
  defaultProfileId: 'prof-1',
  profileCount: 2,
  assignedWorkflows: ['Text'],
  modifiedAt: '2026-11-13T00:00:00Z',
};

describe('LlmModelConfigurationsPanelComponent', () => {
  let component: LlmModelConfigurationsPanelComponent;
  let fixture: ComponentFixture<LlmModelConfigurationsPanelComponent>;
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let dialog: { open: ReturnType<typeof vi.fn> };
  let changed: ReturnType<typeof vi.fn<() => void>>;
  const connections = [connection('desk', 'Desk GPU'), connection('laptop', 'Laptop')];

  beforeEach(async () => {
    service = {
      deleteModelConfiguration: vi.fn().mockReturnValue(of(undefined)),
    };
    dialog = {
      open: vi.fn().mockReturnValue({ afterClosed: () => of(savedConfiguration) }),
    };

    await TestBed.configureTestingModule({
      imports: [LlmModelConfigurationsPanelComponent],
      providers: [
        { provide: LlmSettingsService, useValue: service },
        { provide: MatDialog, useValue: dialog },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(LlmModelConfigurationsPanelComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('connections', connections);
    fixture.componentRef.setInput('configurations', [savedConfiguration]);
    changed = vi.fn<() => void>();
    component.changed.subscribe(() => changed());
    fixture.detectChanges();
  });

  it('opens the model configuration dialog to add a model and emits changed on save', () => {
    component.startAdd();

    expect(dialog.open).toHaveBeenCalledWith(
      LlmModelConfigurationDialogComponent,
      expect.objectContaining({
        data: { connections, existingConfigurations: [savedConfiguration] },
      }),
    );
    expect(changed).toHaveBeenCalled();
  });

  it('opens the model configuration dialog to edit a saved model', () => {
    component.startEdit(savedConfiguration);

    expect(dialog.open).toHaveBeenCalledWith(
      LlmModelConfigurationDialogComponent,
      expect.objectContaining({
        data: {
          configuration: savedConfiguration,
          connections,
          existingConfigurations: [savedConfiguration],
        },
      }),
    );
    expect(changed).toHaveBeenCalled();
  });

  it('renders model rate limit and GPU layer chips on the card', () => {
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('GPU layers: 24');
    expect(text).toContain('Concurrent: 1');
    expect(text).toContain('RPM: 12');
    expect(text).toContain('RPD: 500');
    expect(text).toContain('Reset: 00:00 (America/Los_Angeles)');
  });

  it('does not emit changed when the dialog is cancelled', () => {
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    component.startAdd();

    expect(changed).not.toHaveBeenCalled();
  });

  it('deletes an unassigned model with only its Defaults profile without confirmation', () => {
    const unusedConfig: LlmModelConfigurationDto = {
      ...savedConfiguration,
      id: 'cfg-unused',
      profileCount: 1,
      assignedWorkflows: [],
    };

    component.delete(unusedConfig);

    expect(dialog.open).not.toHaveBeenCalled();
    expect(service['deleteModelConfiguration']).toHaveBeenCalledWith('cfg-unused');
    expect(changed).toHaveBeenCalled();
  });

  it('confirms before deleting a model with custom profiles or assigned workflows', () => {
    dialog.open.mockReturnValueOnce({ afterClosed: () => of(false) });
    component.delete(savedConfiguration);

    expect(dialog.open).toHaveBeenCalledWith(
      ConfirmDialogComponent,
      expect.objectContaining({
        data: expect.objectContaining({
          title: 'Delete Model',
          confirmText: 'Delete Model',
          isDestructive: true,
          message:
            'Deleting "Desk text" will also delete its 1 custom profile and deselect it from the Text workflow. Continue?',
        }),
      }),
    );
    expect(service['deleteModelConfiguration']).not.toHaveBeenCalled();
    expect(changed).not.toHaveBeenCalled();

    dialog.open.mockReturnValueOnce({ afterClosed: () => of(true) });
    component.delete(savedConfiguration);

    expect(service['deleteModelConfiguration']).toHaveBeenCalledWith('cfg-1');
    expect(changed).toHaveBeenCalled();
  });

  it('displays API error when deleting fails after confirmation', () => {
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    service['deleteModelConfiguration'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Failed to delete model' } })),
    );

    component.delete(savedConfiguration);

    expect(component.errorMessage()).toBe('Failed to delete model');
    expect(changed).not.toHaveBeenCalled();
  });
});
