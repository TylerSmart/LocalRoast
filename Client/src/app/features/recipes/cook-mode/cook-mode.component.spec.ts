import { ComponentFixture, TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RecipeDetailDto } from '../../../core/models/recipe.models';
import { KitchenTimerService } from '../../../core/services/kitchen-timer/kitchen-timer.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { WakeLockService } from '../../../core/services/wake-lock/wake-lock.service';
import { SpeechNarrationService } from '../../../core/services/speech-narration/speech-narration.service';
import {
  VoiceCommandEvent,
  VoiceControlService,
} from '../../../core/services/voice-control/voice-control.service';
import { Subject } from 'rxjs';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { IngredientSubstitutionDialogComponent } from '../ingredient-substitution-dialog/ingredient-substitution-dialog.component';
import { CookModeComponent } from './cook-mode.component';

describe('CookModeComponent', () => {
  let component: CookModeComponent;
  let fixture: ComponentFixture<CookModeComponent>;

  let mockRecipeService: {
    getRecipeById: ReturnType<typeof vi.fn>;
  };
  let mockWakeLockService: {
    acquire: ReturnType<typeof vi.fn>;
    release: ReturnType<typeof vi.fn>;
    status: ReturnType<typeof vi.fn>;
  };
  let mockKitchenTimerService: {
    startTimer: ReturnType<typeof vi.fn>;
    pauseTimer: ReturnType<typeof vi.fn>;
    resumeTimer: ReturnType<typeof vi.fn>;
    addSeconds: ReturnType<typeof vi.fn>;
    resetTimer: ReturnType<typeof vi.fn>;
    dismissTimer: ReturnType<typeof vi.fn>;
    timers: ReturnType<typeof vi.fn>;
  };
  let mockDialog: {
    open: ReturnType<typeof vi.fn>;
  };
  let mockRouter: {
    navigate: ReturnType<typeof vi.fn>;
  };
  let isNarrationEnabledSignal: WritableSignal<boolean>;
  let isSpeakingSignal: WritableSignal<boolean>;
  let mockSpeechNarrationService: {
    isSupported: ReturnType<typeof vi.fn>;
    isEnabled: () => boolean;
    isSpeaking: () => boolean;
    toggleNarration: ReturnType<typeof vi.fn>;
    setEnabled: (val: boolean) => void;
    speakStep: ReturnType<typeof vi.fn>;
    speakText: ReturnType<typeof vi.fn>;
    stop: ReturnType<typeof vi.fn>;
  };
  let isVoiceListeningSignal: WritableSignal<boolean>;
  let commandSubject: Subject<VoiceCommandEvent>;
  let mockVoiceControlService: {
    isSupported: ReturnType<typeof vi.fn>;
    isListening: () => boolean;
    isEnabled: () => boolean;
    lastFeedback: ReturnType<typeof vi.fn>;
    errorMessage: ReturnType<typeof vi.fn>;
    toggleListening: ReturnType<typeof vi.fn>;
    startListening: ReturnType<typeof vi.fn>;
    stopListening: ReturnType<typeof vi.fn>;
    setFeedback: ReturnType<typeof vi.fn>;
    command$: Subject<VoiceCommandEvent>;
  };

  const mockRecipe: RecipeDetailDto = {
    id: 'rec-1',
    title: 'Beef Chili',
    slug: 'beef-chili',
    description: 'Hearty chili',
    status: 'Published',
    baseServings: 4,
    yieldDisplay: '4 bowls',
    prepTimeMinutes: 15,
    cookTimeMinutes: 45,
    totalTimeMinutes: 60,
    difficulty: 'Easy',
    sourceUrl: null,
    sourceName: null,
    sourceAuthor: null,
    sourceBookTitle: null,
    sourceBookPage: null,
    coverImagePath: null,
    thumbnailImagePath: null,
    timesCooked: 2,
    lastCookedAt: '2026-10-15T12:00:00Z',
    averageRating: 4.5,
    ratingCount: 2,
    cookLogs: [
      {
        id: 'cl-1',
        recipeId: 'rec-1',
        cookedByUserId: 'u1',
        cookedByDisplayName: 'Tyler',
        cookedAt: '2026-10-15T12:00:00Z',
        servingsMade: 4,
        rating: 5,
        notes: 'Added extra cumin, loved it!',
        photoImagePath: null,
        photoThumbnailPath: null,
        createdAt: '2026-10-15T12:00:00Z',
        modifiedAt: '2026-10-15T12:00:00Z',
      },
    ],
    ingredients: [
      {
        id: 'ing-1',
        sortOrder: 1,
        sectionName: 'Meat',
        quantity: 2,
        maxQuantity: null,
        unit: 'Lb',
        customUnitName: null,
        foodItemId: null,
        foodItemName: null,
        isPantryStaple: false,
        linkedRecipeId: null,
        linkedRecipeTitle: null,
        linkedRecipeSlug: null,
        displayName: 'Ground Beef',
        preparationNote: null,
        isOptional: false,
        originalRawText: null,
      },
      {
        id: 'ing-2',
        sortOrder: 2,
        sectionName: 'Seasoning',
        quantity: 2,
        maxQuantity: null,
        unit: 'Tbsp',
        customUnitName: null,
        foodItemId: null,
        foodItemName: null,
        isPantryStaple: true,
        linkedRecipeId: null,
        linkedRecipeTitle: null,
        linkedRecipeSlug: null,
        displayName: 'Chili Powder',
        preparationNote: null,
        isOptional: false,
        originalRawText: null,
      },
    ],
    steps: [
      {
        id: 'st-1',
        stepNumber: 1,
        sectionName: 'Brown Meat',
        instruction: 'Brown ground beef in a large pot over medium heat.',
        imagePath: null,
        ingredientIds: ['ing-1'],
        timers: [{ id: 't-1', sortOrder: 1, durationSeconds: 600, label: 'Brown Beef' }],
      },
      {
        id: 'st-2',
        stepNumber: 2,
        sectionName: 'Simmer',
        instruction: 'Stir in chili powder and simmer gently.',
        imagePath: null,
        ingredientIds: ['ing-2'],
        timers: [{ id: 't-2', sortOrder: 1, durationSeconds: 1800, label: 'Simmer Chili' }],
      },
    ],
    linkedSubRecipes: [],
    collections: [],
    tags: [],
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'system',
    modifiedAt: '2026-01-01T00:00:00Z',
    modifiedBy: 'system',
  };

  beforeEach(async () => {
    mockRecipeService = {
      getRecipeById: vi.fn().mockReturnValue(of(mockRecipe)),
    };
    mockWakeLockService = {
      acquire: vi.fn().mockResolvedValue(undefined),
      release: vi.fn().mockResolvedValue(undefined),
      status: vi.fn().mockReturnValue('active'),
    };
    mockKitchenTimerService = {
      startTimer: vi.fn().mockReturnValue('t-123'),
      pauseTimer: vi.fn(),
      resumeTimer: vi.fn(),
      addSeconds: vi.fn(),
      resetTimer: vi.fn(),
      dismissTimer: vi.fn(),
      timers: vi.fn().mockReturnValue([]),
    };
    mockDialog = {
      open: vi.fn().mockReturnValue({ afterClosed: () => of(null) }),
    };
    mockRouter = {
      navigate: vi.fn().mockResolvedValue(true),
    };
    isNarrationEnabledSignal = signal(false);
    isSpeakingSignal = signal(false);
    mockSpeechNarrationService = {
      isSupported: vi.fn().mockReturnValue(true),
      isEnabled: () => isNarrationEnabledSignal(),
      isSpeaking: () => isSpeakingSignal(),
      toggleNarration: vi.fn(() => {
        isNarrationEnabledSignal.update((v) => !v);
        return isNarrationEnabledSignal();
      }),
      setEnabled: (val: boolean) => isNarrationEnabledSignal.set(val),
      speakStep: vi.fn(),
      speakText: vi.fn(),
      stop: vi.fn(),
    };
    isVoiceListeningSignal = signal(false);
    commandSubject = new Subject<VoiceCommandEvent>();
    mockVoiceControlService = {
      isSupported: vi.fn().mockReturnValue(true),
      isListening: () => isVoiceListeningSignal(),
      isEnabled: () => isVoiceListeningSignal(),
      lastFeedback: vi.fn().mockReturnValue(null),
      errorMessage: vi.fn().mockReturnValue(null),
      toggleListening: vi.fn(() => {
        isVoiceListeningSignal.update((v) => !v);
        return isVoiceListeningSignal();
      }),
      startListening: vi.fn(() => {
        isVoiceListeningSignal.set(true);
        return true;
      }),
      stopListening: vi.fn(() => isVoiceListeningSignal.set(false)),
      setFeedback: vi.fn(),
      command$: commandSubject,
    };
    const mockAiHelperService = {
      checkStatus: vi
        .fn()
        .mockReturnValue(of({ isAvailable: true, provider: 'Ollama', latencyMs: 25 })),
      status: signal({ isAvailable: true, provider: 'Ollama', latencyMs: 25 }),
      isAvailable: signal(true),
    };

    await TestBed.configureTestingModule({
      imports: [CookModeComponent],
      providers: [
        { provide: RecipeService, useValue: mockRecipeService },
        { provide: WakeLockService, useValue: mockWakeLockService },
        { provide: KitchenTimerService, useValue: mockKitchenTimerService },
        { provide: SpeechNarrationService, useValue: mockSpeechNarrationService },
        { provide: VoiceControlService, useValue: mockVoiceControlService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: Router, useValue: mockRouter },
        { provide: AiKitchenHelperService, useValue: mockAiHelperService },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: { get: () => 'rec-1' },
              queryParamMap: {
                has: (k: string) => k === 'servings',
                get: (k: string) => (k === 'servings' ? '8' : null),
              },
            },
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CookModeComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads recipe, acquires wake lock, and applies servings query param', () => {
    expect(mockRecipeService.getRecipeById).toHaveBeenCalledWith('rec-1');
    expect(mockWakeLockService.acquire).toHaveBeenCalled();
    expect(component['recipe']()?.title).toBe('Beef Chili');
    expect(component['targetServings']()).toBe(8);
    expect(component['scaleFactor']()).toBe(2);
  });

  it('navigates through steps using nextStep and prevStep', () => {
    expect(component['activeStepIndex']()).toBe(0);

    component['nextStep']();
    expect(component['activeStepIndex']()).toBe(1);

    component['prevStep']();
    expect(component['activeStepIndex']()).toBe(0);
  });

  it('responds to keyboard ArrowRight and ArrowLeft', () => {
    component['onWindowKeydown'](new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    expect(component['activeStepIndex']()).toBe(1);

    component['onWindowKeydown'](new KeyboardEvent('keydown', { key: 'ArrowLeft' }));
    expect(component['activeStepIndex']()).toBe(0);
  });

  it('launches a step timer via KitchenTimerService', () => {
    const timer = mockRecipe.steps[0].timers[0];
    component['startStepTimer'](timer, 1);

    expect(mockKitchenTimerService.startTimer).toHaveBeenCalledWith(
      'rec-1',
      'Beef Chili',
      'Brown Beef',
      600,
      1,
    );
  });

  it('opens CookLogDialog when clicking Finish & Log Cook on last step', () => {
    component['goToStep'](1);
    component['nextStep']();

    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('releases wake lock on component destroy', () => {
    fixture.destroy();
    expect(mockWakeLockService.release).toHaveBeenCalled();
  });

  it('opens ingredient substitution dialog when requested', () => {
    const item = component['groupedIngredients']()[0].items[0];
    const event = new MouseEvent('click');
    const stopSpy = vi.spyOn(event, 'stopPropagation');

    component.openIngredientSubstitution(item, event);

    expect(stopSpy).toHaveBeenCalled();
    expect(mockDialog.open).toHaveBeenCalledWith(IngredientSubstitutionDialogComponent, {
      data: expect.objectContaining({
        recipeTitle: 'Beef Chili',
        ingredientDisplayName: item.ingredient.displayName,
      }),
      width: '540px',
      maxWidth: '95vw',
    });
  });

  it('toggles voice narration and speaks current step when enabled', () => {
    expect(mockSpeechNarrationService.isEnabled()).toBe(false);

    component['toggleNarration']();

    expect(mockSpeechNarrationService.toggleNarration).toHaveBeenCalled();
    expect(mockSpeechNarrationService.speakStep).toHaveBeenCalledWith(
      1,
      'Brown ground beef in a large pot over medium heat.',
      'Brown Meat',
    );
  });

  it('automatically narrates subsequent steps when moving next/prev if narration is enabled', () => {
    mockSpeechNarrationService.setEnabled(true);
    mockSpeechNarrationService.speakStep.mockClear();

    component['nextStep']();
    expect(component['activeStepIndex']()).toBe(1);
    expect(mockSpeechNarrationService.speakStep).toHaveBeenCalledWith(
      2,
      'Stir in chili powder and simmer gently.',
      'Simmer',
    );

    mockSpeechNarrationService.speakStep.mockClear();
    component['prevStep']();
    expect(component['activeStepIndex']()).toBe(0);
    expect(mockSpeechNarrationService.speakStep).toHaveBeenCalledWith(
      1,
      'Brown ground beef in a large pot over medium heat.',
      'Brown Meat',
    );
  });

  it('toggles manual step audio on the step card', () => {
    component['toggleStepAudioManual']();
    expect(mockSpeechNarrationService.speakStep).toHaveBeenCalledWith(
      1,
      'Brown ground beef in a large pot over medium heat.',
      'Brown Meat',
    );

    // If currently speaking, clicking manual audio button stops it
    isSpeakingSignal.set(true);
    component['toggleStepAudioManual']();
    expect(mockSpeechNarrationService.stop).toHaveBeenCalled();
  });

  it('stops speech narration when exiting cook mode or destroying component', () => {
    component['exitCookMode']();
    expect(mockSpeechNarrationService.stop).toHaveBeenCalled();

    mockSpeechNarrationService.stop.mockClear();
    fixture.destroy();
    expect(mockSpeechNarrationService.stop).toHaveBeenCalled();
  });

  it('toggles voice control listening via toggleVoiceControl()', () => {
    expect(mockVoiceControlService.isListening()).toBe(false);

    component['toggleVoiceControl']();
    expect(mockVoiceControlService.toggleListening).toHaveBeenCalled();
    expect(mockVoiceControlService.isListening()).toBe(true);
  });

  it('handles "next" voice command by advancing step', () => {
    expect(component['activeStepIndex']()).toBe(0);

    commandSubject.next({ action: 'next', transcript: 'next step', matchedKeyword: 'next' });
    expect(component['activeStepIndex']()).toBe(1);
  });

  it('handles "previous" voice command by returning to previous step', () => {
    component['goToStep'](1);
    expect(component['activeStepIndex']()).toBe(1);

    commandSubject.next({ action: 'previous', transcript: 'go back', matchedKeyword: 'previous' });
    expect(component['activeStepIndex']()).toBe(0);
  });

  it('handles "repeat" voice command by speaking the active step', () => {
    mockSpeechNarrationService.speakStep.mockClear();

    commandSubject.next({
      action: 'repeat',
      transcript: 'repeat please',
      matchedKeyword: 'repeat',
    });

    expect(mockSpeechNarrationService.speakStep).toHaveBeenCalledWith(
      1,
      'Brown ground beef in a large pot over medium heat.',
      'Brown Meat',
    );
  });

  it('handles "check" voice command by toggling active step checked', () => {
    const stepId = mockRecipe.steps[0].id;
    expect(component['checkedSteps']()[stepId]).toBeFalsy();

    commandSubject.next({ action: 'check', transcript: 'done', matchedKeyword: 'check' });
    expect(component['checkedSteps']()[stepId]).toBe(true);
  });

  it('handles "timer" voice command by starting active step timer', () => {
    commandSubject.next({ action: 'timer', transcript: 'start timer', matchedKeyword: 'timer' });

    expect(mockKitchenTimerService.startTimer).toHaveBeenCalledWith(
      'rec-1',
      'Beef Chili',
      'Brown Beef',
      600,
      1,
    );
    expect(mockVoiceControlService.setFeedback).toHaveBeenCalledWith('Started timer: Brown Beef');
  });

  it('handles "help" voice command by toggling help cheatsheet', () => {
    expect(component['showVoiceHelp']()).toBe(false);

    commandSubject.next({ action: 'help', transcript: 'help', matchedKeyword: 'help' });
    expect(component['showVoiceHelp']()).toBe(true);
  });

  it('stops voice listening and cleans up subscription on component destroy or exit', () => {
    component['exitCookMode']();
    expect(mockVoiceControlService.stopListening).toHaveBeenCalled();

    mockVoiceControlService.stopListening.mockClear();
    fixture.destroy();
    expect(mockVoiceControlService.stopListening).toHaveBeenCalled();
  });
});
