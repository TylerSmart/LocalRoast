import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import type {
  LlmModelConfigurationDto,
  LlmModelProfile,
} from '../../../core/models/ingestion.models';
import { LlmSettingsService } from '../../../core/services/llm-settings/llm-settings.service';
import {
  LlmModelProfileDialogComponent,
  type LlmModelProfileDialogData,
} from './llm-model-profile-dialog.component';

const existingProfile: LlmModelProfile = {
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
  recommendedAsRefineModel: false,
  notes: ['Small model.'],
};

const sampleConfig: LlmModelConfigurationDto = {
  id: 'cfg-text',
  name: 'gemma4:12b @ desk',
  connectionId: 'desk',
  connectionName: 'desk',
  provider: 'Ollama',
  model: 'gemma4:12b',
  profileId: null,
  enableThinking: true,
  temperature: 0.7,
  topP: 0.9,
  topK: 40,
  contextWindowTokens: 16384,
  requestTimeoutSeconds: 450,
  ollamaGpuLayers: null,
  assignedWorkflows: [],
  modifiedAt: '2026-11-13T00:00:00Z',
};

describe('LlmModelProfileDialogComponent', () => {
  let service: Record<string, ReturnType<typeof vi.fn>>;
  let dialogRef: { close: ReturnType<typeof vi.fn> };

  async function create(data: LlmModelProfileDialogData | null = null): Promise<{
    component: LlmModelProfileDialogComponent;
    fixture: ComponentFixture<LlmModelProfileDialogComponent>;
  }> {
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
      createModelProfile: vi.fn().mockImplementation((req) => of({ ...req, id: 'custom' })),
      updateModelProfile: vi.fn().mockImplementation((id, req) => of({ ...req, id })),
    };
  });

  it('creates a new profile and closes with the saved result', async () => {
    const { component } = await create();
    component.form.controls.displayName.setValue('My Profile');
    component.form.controls.modelTag.setValue('new-model:latest');
    component.form.controls.matchPrefixes.setValue('new-model, new');

    component.save();

    expect(service['createModelProfile']).toHaveBeenCalledWith(
      expect.objectContaining({
        displayName: 'My Profile',
        modelTag: 'new-model:latest',
        matchPrefixes: ['new-model', 'new'],
      }),
    );
    expect(dialogRef.close).toHaveBeenCalledWith(expect.objectContaining({ id: 'custom' }));
  });

  it('updates an existing profile', async () => {
    const { component } = await create({ profile: existingProfile });
    component.form.controls.summary.setValue('Updated');

    component.save();

    expect(service['updateModelProfile']).toHaveBeenCalledWith(
      'gemma4-e4b',
      expect.objectContaining({ summary: 'Updated' }),
    );
    expect(dialogRef.close).toHaveBeenCalledWith(expect.objectContaining({ id: 'gemma4-e4b' }));
  });

  it('pre-fills a new profile from a saved model configuration', async () => {
    const { component } = await create({ fromConfiguration: sampleConfig });

    expect(component.editingProfile()).toBeNull();
    const values = component.form.getRawValue();
    expect(values.displayName).toBe('gemma4:12b Profile');
    expect(values.modelTag).toBe('gemma4:12b');
    expect(values.enableThinking).toBe(true);
    expect(values.temperature).toBe(0.7);
    expect(values.contextWindowTokens).toBe(16384);
    expect(values.requestTimeoutSeconds).toBe(450);
  });

  it('shows API error when saving fails', async () => {
    service['createModelProfile'].mockReturnValue(
      throwError(() => ({ error: { detail: 'Profile already exists.' } })),
    );
    const { component } = await create();
    component.form.controls.displayName.setValue('Dup');
    component.form.controls.modelTag.setValue('dup:latest');

    component.save();

    expect(component.errorMessage()).toBe('Profile already exists.');
    expect(dialogRef.close).not.toHaveBeenCalled();
  });
});
