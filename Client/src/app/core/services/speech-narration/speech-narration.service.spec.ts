import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { COOK_NARRATION_STORAGE_KEY, SpeechNarrationService } from './speech-narration.service';

describe('SpeechNarrationService', () => {
  let service: SpeechNarrationService;
  let mockSpeechSynthesis: {
    speak: ReturnType<typeof vi.fn>;
    cancel: ReturnType<typeof vi.fn>;
    getVoices: ReturnType<typeof vi.fn>;
  };

  class MockSpeechSynthesisUtterance {
    text: string;
    rate = 1;
    pitch = 1;
    voice: unknown = null;
    onstart: (() => void) | null = null;
    onend: (() => void) | null = null;
    onerror: (() => void) | null = null;

    constructor(text: string) {
      this.text = text;
    }
  }

  beforeEach(() => {
    localStorage.removeItem(COOK_NARRATION_STORAGE_KEY);

    mockSpeechSynthesis = {
      speak: vi.fn(),
      cancel: vi.fn(),
      getVoices: vi.fn().mockReturnValue([
        { lang: 'en-US', name: 'US English Voice' },
        { lang: 'es-ES', name: 'Spanish Voice' },
      ]),
    };

    vi.stubGlobal('speechSynthesis', mockSpeechSynthesis);
    vi.stubGlobal('SpeechSynthesisUtterance', MockSpeechSynthesisUtterance);

    TestBed.configureTestingModule({
      providers: [SpeechNarrationService],
    });
    service = TestBed.inject(SpeechNarrationService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.removeItem(COOK_NARRATION_STORAGE_KEY);
  });

  it('detects browser support and reads default disabled preference', () => {
    expect(service.isSupported()).toBe(true);
    expect(service.isEnabled()).toBe(false);
    expect(service.isSpeaking()).toBe(false);
  });

  it('hydrates initial state from localStorage if previously enabled', () => {
    localStorage.setItem(COOK_NARRATION_STORAGE_KEY, 'true');
    const hydratedService = new SpeechNarrationService();
    expect(hydratedService.isEnabled()).toBe(true);
  });

  it('toggles narration and persists to localStorage', () => {
    expect(service.isEnabled()).toBe(false);

    const firstToggle = service.toggleNarration();
    expect(firstToggle).toBe(true);
    expect(service.isEnabled()).toBe(true);
    expect(localStorage.getItem(COOK_NARRATION_STORAGE_KEY)).toBe('true');

    const secondToggle = service.toggleNarration();
    expect(secondToggle).toBe(false);
    expect(service.isEnabled()).toBe(false);
    expect(localStorage.getItem(COOK_NARRATION_STORAGE_KEY)).toBe('false');
  });

  it('cancels speech when narration is disabled', () => {
    service.setEnabled(true);
    service.speakStep(1, 'Preheat oven to 400 degrees.');

    service.setEnabled(false);
    expect(mockSpeechSynthesis.cancel).toHaveBeenCalled();
    expect(service.isSpeaking()).toBe(false);
  });

  it('speaks formatted step with instruction and section name', () => {
    service.speakStep(2, 'Simmer **until reduced** by half.', 'Sauce Prep');

    expect(mockSpeechSynthesis.cancel).toHaveBeenCalled();
    expect(mockSpeechSynthesis.speak).toHaveBeenCalledTimes(1);

    const calledUtterance = mockSpeechSynthesis.speak.mock
      .calls[0][0] as MockSpeechSynthesisUtterance;
    expect(calledUtterance.text).toBe('Sauce Prep. Step 2. Simmer until reduced by half.');
    expect(calledUtterance.voice).toEqual({ lang: 'en-US', name: 'US English Voice' });

    // Verify speech lifecycle callbacks
    calledUtterance.onstart?.();
    expect(service.isSpeaking()).toBe(true);

    calledUtterance.onend?.();
    expect(service.isSpeaking()).toBe(false);
  });

  it('cleans markdown links and formatting characters before speaking', () => {
    service.speakText('Check out the [tips guide](https://example.com) for *best results* #1.');

    const calledUtterance = mockSpeechSynthesis.speak.mock
      .calls[0][0] as MockSpeechSynthesisUtterance;
    expect(calledUtterance.text).toBe('Check out the tips guide for best results 1.');
  });

  it('handles onerror callback by resetting speaking state', () => {
    service.speakStep(1, 'Test step');
    const calledUtterance = mockSpeechSynthesis.speak.mock
      .calls[0][0] as MockSpeechSynthesisUtterance;

    calledUtterance.onstart?.();
    expect(service.isSpeaking()).toBe(true);

    calledUtterance.onerror?.();
    expect(service.isSpeaking()).toBe(false);
  });

  it('stop() cancels speech and resets speaking state', () => {
    service.speakStep(1, 'Test step');
    const calledUtterance = mockSpeechSynthesis.speak.mock
      .calls[0][0] as MockSpeechSynthesisUtterance;
    calledUtterance.onstart?.();
    expect(service.isSpeaking()).toBe(true);

    service.stop();
    expect(mockSpeechSynthesis.cancel).toHaveBeenCalled();
    expect(service.isSpeaking()).toBe(false);
  });
});
