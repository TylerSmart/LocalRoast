import { Service, signal } from '@angular/core';
import { APP_STORAGE_PREFIX } from '../../../app.branding';

export const COOK_NARRATION_STORAGE_KEY = `${APP_STORAGE_PREFIX}:cook-narration-enabled`;

@Service()
export class SpeechNarrationService {
  private readonly _isSupported = signal<boolean>(this.checkSupport());
  readonly isSupported = this._isSupported.asReadonly();

  private readonly _isEnabled = signal<boolean>(this.readInitialEnabledState());
  readonly isEnabled = this._isEnabled.asReadonly();

  private readonly _isSpeaking = signal<boolean>(false);
  readonly isSpeaking = this._isSpeaking.asReadonly();

  private activeUtterance: SpeechSynthesisUtterance | null = null;

  private checkSupport(): boolean {
    return (
      typeof window !== 'undefined' &&
      'speechSynthesis' in window &&
      typeof SpeechSynthesisUtterance !== 'undefined'
    );
  }

  private readInitialEnabledState(): boolean {
    if (typeof localStorage === 'undefined') return false;
    try {
      return localStorage.getItem(COOK_NARRATION_STORAGE_KEY) === 'true';
    } catch {
      return false;
    }
  }

  /**
   * Toggles the user narration preference on or off, updating signal and localStorage.
   * Cancels speech immediately if turning off.
   */
  toggleNarration(): boolean {
    const next = !this._isEnabled();
    this.setEnabled(next);
    return next;
  }

  /**
   * Sets narration preference explicitly and stores in localStorage.
   */
  setEnabled(enabled: boolean): void {
    this._isEnabled.set(enabled);
    if (typeof localStorage !== 'undefined') {
      try {
        localStorage.setItem(COOK_NARRATION_STORAGE_KEY, enabled ? 'true' : 'false');
      } catch {
        // Storage access may be blocked or restricted
      }
    }
    if (!enabled) {
      this.stop();
    }
  }

  /**
   * Speaks the given recipe step aloud if speech is supported.
   */
  speakStep(stepNumber: number, instruction: string, sectionName?: string | null): void {
    if (!this.checkSupport()) return;

    const cleanedText = this.cleanInstructionText(instruction);
    if (!cleanedText) return;

    let textToSpeak = `Step ${stepNumber}. ${cleanedText}`;
    if (sectionName && sectionName.trim().length > 0) {
      textToSpeak = `${sectionName.trim()}. ${textToSpeak}`;
    }

    this.speakUtterance(textToSpeak);
  }

  /**
   * Speaks arbitrary text aloud.
   */
  speakText(text: string): void {
    if (!this.checkSupport()) return;
    const cleaned = this.cleanInstructionText(text);
    if (!cleaned) return;
    this.speakUtterance(cleaned);
  }

  /**
   * Stops in-flight narration and resets speaking state.
   */
  stop(): void {
    if (!this.checkSupport()) return;
    try {
      window.speechSynthesis.cancel();
    } catch {
      // Ignored in headless/unsupported environments
    }
    this._isSpeaking.set(false);
    this.activeUtterance = null;
  }

  private speakUtterance(text: string): void {
    this.stop();

    try {
      const utterance = new SpeechSynthesisUtterance(text);
      utterance.rate = 1.0;
      utterance.pitch = 1.0;

      // Select natural voice if available
      const voices =
        typeof window.speechSynthesis.getVoices === 'function'
          ? window.speechSynthesis.getVoices()
          : [];
      if (voices && voices.length > 0) {
        const lang = (typeof navigator !== 'undefined' && navigator.language) || 'en-US';
        const preferredVoice =
          voices.find((v) => v.lang === lang) ||
          voices.find((v) => v.lang.startsWith(lang.slice(0, 2))) ||
          voices.find((v) => v.lang.startsWith('en')) ||
          voices[0];
        if (preferredVoice) {
          utterance.voice = preferredVoice;
        }
      }

      utterance.onstart = () => {
        this._isSpeaking.set(true);
      };

      utterance.onend = () => {
        this._isSpeaking.set(false);
        this.activeUtterance = null;
      };

      utterance.onerror = () => {
        this._isSpeaking.set(false);
        this.activeUtterance = null;
      };

      this.activeUtterance = utterance;
      window.speechSynthesis.speak(utterance);
    } catch {
      this._isSpeaking.set(false);
      this.activeUtterance = null;
    }
  }

  private cleanInstructionText(raw: string): string {
    if (!raw) return '';
    return raw
      .replace(/[*_#`~]/g, '') // remove markdown formatting
      .replace(/\[([^\]]+)\]\([^)]+\)/g, '$1') // remove markdown links, keeping text
      .replace(/\s+/g, ' ')
      .trim();
  }
}
