import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  COOK_VOICE_STORAGE_KEY,
  VoiceCommandEvent,
  VoiceControlService,
} from './voice-control.service';

describe('VoiceControlService', () => {
  let service: VoiceControlService;
  let mockRecognitionInstance: {
    continuous: boolean;
    interimResults: boolean;
    lang: string;
    start: ReturnType<typeof vi.fn>;
    stop: ReturnType<typeof vi.fn>;
    abort: ReturnType<typeof vi.fn>;
    onresult: ((event: unknown) => void) | null;
    onerror: ((event: { error: string }) => void) | null;
    onend: (() => void) | null;
    onstart: (() => void) | null;
  };

  function createMockRecognition() {
    const instance = {
      continuous: false,
      interimResults: false,
      lang: 'en-US',
      start: vi.fn(),
      stop: vi.fn(),
      abort: vi.fn(),
      onresult: null as ((event: unknown) => void) | null,
      onerror: null as ((event: { error: string }) => void) | null,
      onend: null as (() => void) | null,
      onstart: null as (() => void) | null,
    };
    mockRecognitionInstance = instance;
    return instance;
  }

  const MockSpeechRecognition = vi.fn().mockImplementation(createMockRecognition);

  beforeEach(() => {
    localStorage.removeItem(COOK_VOICE_STORAGE_KEY);

    vi.stubGlobal('SpeechRecognition', MockSpeechRecognition);
    vi.stubGlobal('speechSynthesis', {
      speaking: false,
    });

    TestBed.configureTestingModule({
      providers: [VoiceControlService],
    });
    service = TestBed.inject(VoiceControlService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.removeItem(COOK_VOICE_STORAGE_KEY);
  });

  it('detects browser support and reads default disabled state', () => {
    expect(service.isSupported()).toBe(true);
    expect(service.isListening()).toBe(false);
    expect(service.isEnabled()).toBe(false);
  });

  it('hydrates initial preference from localStorage', () => {
    localStorage.setItem(COOK_VOICE_STORAGE_KEY, 'true');
    const hydrated = new VoiceControlService();
    expect(hydrated.isEnabled()).toBe(true);
  });

  it('starts listening and updates state and localStorage', () => {
    const started = service.startListening();
    expect(started).toBe(true);
    expect(service.isListening()).toBe(true);
    expect(localStorage.getItem(COOK_VOICE_STORAGE_KEY)).toBe('true');
    expect(mockRecognitionInstance.start).toHaveBeenCalled();
    expect(mockRecognitionInstance.continuous).toBe(true);
  });

  it('stops listening and updates state and localStorage', () => {
    service.startListening();
    service.stopListening();

    expect(service.isListening()).toBe(false);
    expect(localStorage.getItem(COOK_VOICE_STORAGE_KEY)).toBe('false');
    expect(mockRecognitionInstance.stop).toHaveBeenCalled();
  });

  it('toggles listening mode on and off', () => {
    expect(service.isListening()).toBe(false);

    service.toggleListening();
    expect(service.isListening()).toBe(true);

    service.toggleListening();
    expect(service.isListening()).toBe(false);
  });

  it('matches "next", "next step", and "forward" commands', () => {
    const emitted: VoiceCommandEvent[] = [];
    const sub = service.command$.subscribe((c) => emitted.push(c));

    const res1 = service.processTranscript('next');
    expect(res1?.action).toBe('next');

    const res2 = service.processTranscript('go to next step please');
    expect(res2?.action).toBe('next');

    const res3 = service.processTranscript('forward');
    expect(res3?.action).toBe('next');

    expect(emitted.length).toBe(3);
    sub.unsubscribe();
  });

  it('matches "back", "previous", and "go back" commands', () => {
    const res1 = service.processTranscript('back');
    expect(res1?.action).toBe('previous');

    const res2 = service.processTranscript('previous step');
    expect(res2?.action).toBe('previous');

    const res3 = service.processTranscript('can you go back?');
    expect(res3?.action).toBe('previous');
  });

  it('matches "repeat", "read step", and "again" commands', () => {
    const res1 = service.processTranscript('repeat');
    expect(res1?.action).toBe('repeat');

    const res2 = service.processTranscript('read step');
    expect(res2?.action).toBe('repeat');

    const res3 = service.processTranscript('say that again');
    expect(res3?.action).toBe('repeat');
  });

  it('matches "start timer" and "timer" commands', () => {
    const res1 = service.processTranscript('timer');
    expect(res1?.action).toBe('timer');

    const res2 = service.processTranscript('start timer');
    expect(res2?.action).toBe('timer');
  });

  it('matches "pause timer" and "hold timer" commands', () => {
    const res1 = service.processTranscript('pause timer');
    expect(res1?.action).toBe('pause_timer');

    const res2 = service.processTranscript('pause the timer please');
    expect(res2?.action).toBe('pause_timer');

    const res3 = service.processTranscript('hold timer');
    expect(res3?.action).toBe('pause_timer');
  });

  it('matches "resume timer" and "unpause timer" commands', () => {
    const res1 = service.processTranscript('resume timer');
    expect(res1?.action).toBe('resume_timer');

    const res2 = service.processTranscript('unpause timer');
    expect(res2?.action).toBe('resume_timer');

    const res3 = service.processTranscript('continue timer');
    expect(res3?.action).toBe('resume_timer');
  });

  it('matches "restart timer", "reset timer", and "restart" commands', () => {
    const res1 = service.processTranscript('restart timer');
    expect(res1?.action).toBe('restart_timer');

    const res2 = service.processTranscript('reset timer');
    expect(res2?.action).toBe('restart_timer');

    const res3 = service.processTranscript('restart');
    expect(res3?.action).toBe('restart_timer');
  });

  it('matches "cancel timer", "stop timer", and "dismiss timer" commands', () => {
    const res1 = service.processTranscript('cancel timer');
    expect(res1?.action).toBe('cancel_timer');

    const res2 = service.processTranscript('stop timer');
    expect(res2?.action).toBe('cancel_timer');

    const res3 = service.processTranscript('dismiss timer');
    expect(res3?.action).toBe('cancel_timer');

    const res4 = service.processTranscript('clear timer');
    expect(res4?.action).toBe('cancel_timer');
  });

  it('distinguishes generic "stop" from "stop timer"', () => {
    const narrationStop = service.processTranscript('stop');
    expect(narrationStop?.action).toBe('stop');

    const timerCancel = service.processTranscript('stop timer');
    expect(timerCancel?.action).toBe('cancel_timer');
  });

  it('matches "done", "check", and "complete" commands', () => {
    const res1 = service.processTranscript('done');
    expect(res1?.action).toBe('check');

    const res2 = service.processTranscript('check');
    expect(res2?.action).toBe('check');

    const res3 = service.processTranscript('step complete');
    expect(res3?.action).toBe('check');
  });

  it('matches "help" and "commands" commands', () => {
    const res1 = service.processTranscript('help');
    expect(res1?.action).toBe('help');

    const res2 = service.processTranscript('show commands');
    expect(res2?.action).toBe('help');
  });

  it('handles unrecognized commands gracefully with feedback', () => {
    const res = service.processTranscript('bake chocolate chip cookies');
    expect(res).toBeNull();
    expect(service.lastFeedback()).toContain('unrecognized command');
  });

  it('handles microphone permission denied error', () => {
    service.startListening();
    expect(service.isListening()).toBe(true);

    mockRecognitionInstance.onerror?.({ error: 'not-allowed' });

    expect(service.isListening()).toBe(false);
    expect(service.errorMessage()).toContain('Microphone access was denied');
    expect(localStorage.getItem(COOK_VOICE_STORAGE_KEY)).toBe('false');
  });

  it('ignores recognition results when speech synthesis is actively speaking', () => {
    const emitted: VoiceCommandEvent[] = [];
    const sub = service.command$.subscribe((c) => emitted.push(c));

    service.startListening();

    // Simulate speech synthesis speaking
    vi.stubGlobal('speechSynthesis', {
      speaking: true,
    });

    // Trigger onresult with "next"
    mockRecognitionInstance.onresult?.({
      resultIndex: 0,
      results: [
        {
          0: { transcript: 'next', confidence: 0.95 },
          length: 1,
          isFinal: true,
        },
      ],
    });

    expect(emitted.length).toBe(0);
    sub.unsubscribe();
  });

  it('ignores interim results where isFinal is false', () => {
    const emitted: VoiceCommandEvent[] = [];
    const sub = service.command$.subscribe((c) => emitted.push(c));

    service.startListening();

    mockRecognitionInstance.onresult?.({
      resultIndex: 0,
      results: [
        {
          0: { transcript: 'next', confidence: 0.95 },
          length: 1,
          isFinal: false,
        },
      ],
    });

    expect(emitted.length).toBe(0);
    sub.unsubscribe();
  });

  it('enforces command cooldown window to prevent rapid multi-firing', () => {
    const emitted: VoiceCommandEvent[] = [];
    const sub = service.command$.subscribe((c) => emitted.push(c));

    service.startListening();

    // First final "next"
    mockRecognitionInstance.onresult?.({
      resultIndex: 0,
      results: [
        {
          0: { transcript: 'next', confidence: 0.95 },
          length: 1,
          isFinal: true,
        },
      ],
    });

    expect(emitted.length).toBe(1);

    // Rapid duplicate "next" within cooldown
    mockRecognitionInstance.onresult?.({
      resultIndex: 0,
      results: [
        {
          0: { transcript: 'next', confidence: 0.95 },
          length: 1,
          isFinal: true,
        },
      ],
    });

    // Still only 1 emission due to cooldown
    expect(emitted.length).toBe(1);

    // Reset cooldown to simulate cooldown elapsed
    service.resetCooldown();

    mockRecognitionInstance.onresult?.({
      resultIndex: 0,
      results: [
        {
          0: { transcript: 'previous', confidence: 0.95 },
          length: 1,
          isFinal: true,
        },
      ],
    });

    expect(emitted.length).toBe(2);
    sub.unsubscribe();
  });
});
