import { Service, signal } from '@angular/core';
import { Subject } from 'rxjs';
import { APP_STORAGE_PREFIX } from '../../../app.branding';

export const COOK_VOICE_STORAGE_KEY = `${APP_STORAGE_PREFIX}:cook-voice-control-enabled`;

export type VoiceCommandAction =
  'next' | 'previous' | 'repeat' | 'timer' | 'check' | 'help' | 'stop';

export interface VoiceCommandEvent {
  action: VoiceCommandAction;
  transcript: string;
  matchedKeyword: string;
}

// Ambient typings for Web Speech Recognition API
interface SpeechRecognitionResultItem {
  transcript: string;
  confidence: number;
}

interface SpeechRecognitionResult {
  readonly length: number;
  [index: number]: SpeechRecognitionResultItem;
  isFinal: boolean;
}

interface SpeechRecognitionResultList {
  readonly length: number;
  [index: number]: SpeechRecognitionResult;
}

interface SpeechRecognitionEvent extends Event {
  resultIndex: number;
  results: SpeechRecognitionResultList;
}

interface SpeechRecognitionInstance {
  continuous: boolean;
  interimResults: boolean;
  lang: string;
  start(): void;
  stop(): void;
  abort(): void;
  onresult: ((event: SpeechRecognitionEvent) => void) | null;
  onerror: ((event: { error: string }) => void) | null;
  onend: (() => void) | null;
  onstart: (() => void) | null;
}

type SpeechRecognitionCtor = new () => SpeechRecognitionInstance;

declare global {
  interface Window {
    SpeechRecognition?: SpeechRecognitionCtor;
    webkitSpeechRecognition?: SpeechRecognitionCtor;
  }
}

@Service()
export class VoiceControlService {
  private recognition: SpeechRecognitionInstance | null = null;
  private feedbackTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly _isSupported = signal<boolean>(this.checkSupport());
  readonly isSupported = this._isSupported.asReadonly();

  private readonly _isListening = signal<boolean>(false);
  readonly isListening = this._isListening.asReadonly();

  private readonly _isEnabled = signal<boolean>(this.readInitialEnabledState());
  readonly isEnabled = this._isEnabled.asReadonly();

  private readonly _lastFeedback = signal<string | null>(null);
  readonly lastFeedback = this._lastFeedback.asReadonly();

  private readonly _errorMessage = signal<string | null>(null);
  readonly errorMessage = this._errorMessage.asReadonly();

  readonly command$ = new Subject<VoiceCommandEvent>();

  private checkSupport(): boolean {
    return (
      typeof window !== 'undefined' &&
      ('SpeechRecognition' in window || 'webkitSpeechRecognition' in window)
    );
  }

  private readInitialEnabledState(): boolean {
    if (typeof localStorage === 'undefined') return false;
    try {
      return localStorage.getItem(COOK_VOICE_STORAGE_KEY) === 'true';
    } catch {
      return false;
    }
  }

  private persistEnabled(enabled: boolean): void {
    this._isEnabled.set(enabled);
    if (typeof localStorage !== 'undefined') {
      try {
        localStorage.setItem(COOK_VOICE_STORAGE_KEY, enabled ? 'true' : 'false');
      } catch {
        // Storage access may be restricted
      }
    }
  }

  toggleListening(): boolean {
    if (this._isListening()) {
      this.stopListening();
      return false;
    } else {
      return this.startListening();
    }
  }

  startListening(): boolean {
    if (!this.checkSupport()) {
      this._errorMessage.set('Voice recognition is not supported in this browser.');
      return false;
    }

    if (this._isListening()) {
      return true;
    }

    try {
      const Ctor = window.SpeechRecognition || window.webkitSpeechRecognition;
      if (!Ctor) return false;

      this.recognition = new Ctor();
      this.recognition.continuous = true;
      this.recognition.interimResults = false;
      this.recognition.lang = (typeof navigator !== 'undefined' && navigator.language) || 'en-US';

      this.recognition.onstart = () => {
        this._isListening.set(true);
        this._errorMessage.set(null);
      };

      this.recognition.onresult = (event: SpeechRecognitionEvent) => {
        this.handleRecognitionResult(event);
      };

      this.recognition.onerror = (event: { error: string }) => {
        if (event.error === 'not-allowed' || event.error === 'service-not-allowed') {
          this._isListening.set(false);
          this._errorMessage.set(
            'Microphone access was denied. Please allow microphone in browser settings.',
          );
          this.persistEnabled(false);
        } else if (event.error !== 'no-speech') {
          this._errorMessage.set(`Voice error: ${event.error}`);
        }
      };

      this.recognition.onend = () => {
        // In continuous mode, browsers might terminate on silence; automatically resume if still listening
        if (this._isListening()) {
          try {
            this.recognition?.start();
          } catch {
            // Best effort restart
          }
        }
      };

      this.recognition.start();
      this._isListening.set(true);
      this.persistEnabled(true);
      this.setFeedback(
        'Listening for commands (e.g. "next", "back", "repeat", "timer", "done")...',
        3500,
      );
      return true;
    } catch {
      this._isListening.set(false);
      this._errorMessage.set('Failed to start voice recognition.');
      return false;
    }
  }

  stopListening(): void {
    this._isListening.set(false);
    this.persistEnabled(false);
    if (this.recognition) {
      try {
        this.recognition.stop();
      } catch {
        // Ignored
      }
      this.recognition = null;
    }
    this.setFeedback('Voice control stopped', 2000);
  }

  private handleRecognitionResult(event: SpeechRecognitionEvent): void {
    // If speech synthesis is currently reading aloud, ignore microphone to prevent feedback loop
    if (typeof window !== 'undefined' && window.speechSynthesis?.speaking) {
      return;
    }

    for (let i = event.resultIndex; i < event.results.length; i++) {
      const res = event.results[i];
      if (res && res[0] && res[0].transcript) {
        const transcript = res[0].transcript;
        this.processTranscript(transcript);
      }
    }
  }

  processTranscript(transcript: string): VoiceCommandEvent | null {
    const raw = transcript.trim();
    if (!raw) return null;

    // Lowercase and strip punctuation
    const normalized = raw
      .toLowerCase()
      .replace(/[.,!?;:]/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();
    const command = this.matchCommand(normalized, raw);

    if (command) {
      this.setFeedback(`Heard: "${raw}" → ${this.formatActionLabel(command.action)}`, 2500);
      this.command$.next(command);
      return command;
    } else {
      this.setFeedback(`Heard: "${raw}" (unrecognized command)`, 2500);
      return null;
    }
  }

  private matchCommand(normalized: string, raw: string): VoiceCommandEvent | null {
    const words = normalized.split(' ');

    // 1. Next step: "next", "forward", "advance", "next step", "go next"
    if (
      words.includes('next') ||
      words.includes('forward') ||
      words.includes('advance') ||
      normalized.includes('next step')
    ) {
      return { action: 'next', transcript: raw, matchedKeyword: 'next' };
    }

    // 2. Previous step: "back", "previous", "prior", "last step", "go back"
    if (
      words.includes('back') ||
      words.includes('previous') ||
      words.includes('prior') ||
      normalized.includes('go back') ||
      normalized.includes('previous step')
    ) {
      return { action: 'previous', transcript: raw, matchedKeyword: 'previous' };
    }

    // 3. Repeat / Read step: "repeat", "read", "read step", "say again", "again"
    if (
      words.includes('repeat') ||
      words.includes('again') ||
      words.includes('read') ||
      words.includes('say') ||
      normalized.includes('read step') ||
      normalized.includes('say again')
    ) {
      return { action: 'repeat', transcript: raw, matchedKeyword: 'repeat' };
    }

    // 4. Timer: "timer", "start timer", "set timer"
    if (words.includes('timer') || normalized.includes('start timer')) {
      return { action: 'timer', transcript: raw, matchedKeyword: 'timer' };
    }

    // 5. Complete / Done / Check: "done", "check", "complete", "finished"
    if (
      words.includes('done') ||
      words.includes('check') ||
      words.includes('complete') ||
      words.includes('finished') ||
      normalized.includes('mark done')
    ) {
      return { action: 'check', transcript: raw, matchedKeyword: 'check' };
    }

    // 6. Help: "help", "commands"
    if (words.includes('help') || words.includes('command') || words.includes('commands')) {
      return { action: 'help', transcript: raw, matchedKeyword: 'help' };
    }

    // 7. Stop: "stop", "pause"
    if (words.includes('stop') || words.includes('pause')) {
      return { action: 'stop', transcript: raw, matchedKeyword: 'stop' };
    }

    return null;
  }

  private formatActionLabel(action: VoiceCommandAction): string {
    switch (action) {
      case 'next':
        return 'Next Step';
      case 'previous':
        return 'Previous Step';
      case 'repeat':
        return 'Repeat Step';
      case 'timer':
        return 'Start Timer';
      case 'check':
        return 'Toggle Step Done';
      case 'help':
        return 'Help';
      case 'stop':
        return 'Stop';
    }
  }

  setFeedback(message: string, durationMs = 2500): void {
    if (this.feedbackTimer) {
      clearTimeout(this.feedbackTimer);
      this.feedbackTimer = null;
    }
    this._lastFeedback.set(message);
    if (durationMs > 0) {
      this.feedbackTimer = setTimeout(() => {
        this._lastFeedback.set(null);
        this.feedbackTimer = null;
      }, durationMs);
    }
  }

  clearFeedback(): void {
    if (this.feedbackTimer) {
      clearTimeout(this.feedbackTimer);
      this.feedbackTimer = null;
    }
    this._lastFeedback.set(null);
  }
}
