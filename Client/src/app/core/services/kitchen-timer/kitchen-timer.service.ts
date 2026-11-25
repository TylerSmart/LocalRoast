import { Service, computed, signal } from '@angular/core';

export type KitchenTimerState = 'running' | 'paused' | 'completed';

export interface ActiveKitchenTimer {
  id: string;
  recipeId: string;
  recipeTitle: string;
  stepNumber: number | null;
  label: string;
  totalDurationSeconds: number;
  remainingSeconds: number;
  endsAtMs: number | null;
  completedAtMs?: number | null;
  overtimeSeconds?: number;
  state: KitchenTimerState;
}

let sharedAudioCtx: AudioContext | null = null;

function configureAudioSession(): void {
  try {
    if (typeof navigator !== 'undefined' && 'audioSession' in navigator) {
      const session = (navigator as Navigator & { audioSession?: { type: string } }).audioSession;
      if (session && session.type !== 'playback') {
        session.type = 'playback';
      }
    }
  } catch {
    // Unsupported audioSession API or restricted context
  }
}

function getOrCreateAudioContext(): AudioContext | null {
  try {
    if (typeof window === 'undefined') {
      return null;
    }
    const AudioCtx =
      window.AudioContext ||
      (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioCtx) {
      return null;
    }
    if (!sharedAudioCtx || sharedAudioCtx.state === 'closed') {
      sharedAudioCtx = new AudioCtx();
    }
    return sharedAudioCtx;
  } catch {
    return null;
  }
}

function scheduleChimeNotes(ctx: AudioContext): void {
  const notes = [523.25, 659.25, 783.99, 1046.5]; // C5, E5, G5, C6
  notes.forEach((freq, index) => {
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'sine';
    osc.frequency.setValueAtTime(freq, ctx.currentTime + index * 0.14);
    gain.gain.setValueAtTime(0.18, ctx.currentTime + index * 0.14);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + index * 0.14 + 0.35);
    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.start(ctx.currentTime + index * 0.14);
    osc.stop(ctx.currentTime + index * 0.14 + 0.4);
  });
}

function unlockAudioContext(): void {
  try {
    configureAudioSession();
    const ctx = getOrCreateAudioContext();
    if (!ctx) {
      return;
    }

    if ((ctx.state as string) !== 'running') {
      void ctx.resume().catch(() => undefined);
    }

    // Play a silent 1-frame buffer synchronously inside the user gesture so WebKit (iPadOS)
    // permanently unlocks the audio hardware pipeline for subsequent timer completions.
    if (typeof ctx.createBuffer === 'function' && typeof ctx.createBufferSource === 'function') {
      const buffer = ctx.createBuffer(1, 1, 22050);
      const source = ctx.createBufferSource();
      source.buffer = buffer;
      source.connect(ctx.destination);
      source.start(0);
    }
  } catch {
    // AudioContext blocked or unsupported in headless environments
  }
}

function playChime(): void {
  try {
    configureAudioSession();
    const ctx = getOrCreateAudioContext();
    if (!ctx) {
      return;
    }

    if ((ctx.state as string) === 'running') {
      scheduleChimeNotes(ctx);
      return;
    }

    void ctx
      .resume()
      .then(() => {
        if ((ctx.state as string) === 'running') {
          scheduleChimeNotes(ctx);
        }
      })
      .catch(() => undefined);
  } catch {
    // AudioContext blocked or unsupported in headless environments
  }
}

function showNotification(timer: ActiveKitchenTimer): void {
  try {
    if (typeof Notification === 'undefined' || Notification.permission !== 'granted') {
      return;
    }

    const title = `Timer Finished: ${timer.label}`;
    const options: NotificationOptions = {
      body: `${timer.recipeTitle} — your timer has completed!`,
      icon: '/icons/icon-192.png',
      badge: '/icons/icon-monochrome-192.png',
      tag: timer.id,
    };

    if (
      typeof navigator !== 'undefined' &&
      'serviceWorker' in navigator &&
      typeof navigator.serviceWorker?.getRegistration === 'function'
    ) {
      void navigator.serviceWorker
        .getRegistration()
        .then((registration) => {
          if (registration && typeof registration.showNotification === 'function') {
            return registration.showNotification(title, options);
          }
          new Notification(title, options);
          return undefined;
        })
        .catch(() => {
          try {
            new Notification(title, options);
          } catch {
            // Non-blocking fallback when Notification constructor is disallowed
          }
        });
      return;
    }

    new Notification(title, options);
  } catch {
    // Non-blocking
  }
}

@Service()
export class KitchenTimerService {
  private tickerId: ReturnType<typeof setInterval> | null = null;
  private alarmLoopId: ReturnType<typeof setInterval> | null = null;
  private gestureRecoveryBound = false;
  private readonly _timers = signal<ActiveKitchenTimer[]>([]);
  readonly timers = this._timers.asReadonly();

  readonly hasCompletedAlert = computed(() => this.timers().some((t) => t.state === 'completed'));

  startTimer(
    recipeId: string,
    recipeTitle: string,
    label: string,
    durationSeconds: number,
    stepNumber: number | null = null,
  ): string {
    unlockAudioContext();
    const id = `timer-${Date.now()}-${Math.random().toString(36).substring(2, 7)}`;
    const safeDuration = Math.max(1, durationSeconds);
    const endsAtMs = Date.now() + safeDuration * 1000;

    const timer: ActiveKitchenTimer = {
      id,
      recipeId,
      recipeTitle,
      stepNumber,
      label,
      totalDurationSeconds: safeDuration,
      remainingSeconds: safeDuration,
      endsAtMs,
      completedAtMs: null,
      overtimeSeconds: 0,
      state: 'running',
    };

    this._timers.update((current) => [...current, timer]);
    this.ensureTicker();
    this.syncGestureRecovery();
    return id;
  }

  pauseTimer(id: string): void {
    const now = Date.now();
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id || t.state !== 'running' || !t.endsAtMs) return t;
        const remaining = Math.max(0, Math.ceil((t.endsAtMs - now) / 1000));
        return {
          ...t,
          remainingSeconds: remaining,
          endsAtMs: null,
          state: 'paused',
        };
      }),
    );
    this.syncGestureRecovery();
  }

  resumeTimer(id: string): void {
    unlockAudioContext();
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id || t.state !== 'paused') return t;
        const endsAtMs = Date.now() + t.remainingSeconds * 1000;
        return {
          ...t,
          endsAtMs,
          completedAtMs: null,
          overtimeSeconds: 0,
          state: 'running',
        };
      }),
    );
    this.ensureTicker();
    this.syncGestureRecovery();
  }

  addSeconds(id: string, deltaSeconds = 60): void {
    unlockAudioContext();
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id) return t;
        const newRemaining = Math.max(0, t.remainingSeconds + deltaSeconds);
        const endsAtMs = Date.now() + newRemaining * 1000;
        return {
          ...t,
          remainingSeconds: newRemaining,
          endsAtMs,
          completedAtMs: null,
          overtimeSeconds: 0,
          state: 'running',
        };
      }),
    );
    this.syncAlarmLoop();
    this.ensureTicker();
    this.syncGestureRecovery();
  }

  resetTimer(id: string): void {
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id) return t;
        return {
          ...t,
          remainingSeconds: t.totalDurationSeconds,
          endsAtMs: null,
          completedAtMs: null,
          overtimeSeconds: 0,
          state: 'paused',
        };
      }),
    );
    this.syncAlarmLoop();
    this.syncGestureRecovery();
  }

  restartTimer(id: string): void {
    unlockAudioContext();
    const now = Date.now();
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id) return t;
        const endsAtMs = now + t.totalDurationSeconds * 1000;
        return {
          ...t,
          remainingSeconds: t.totalDurationSeconds,
          endsAtMs,
          completedAtMs: null,
          overtimeSeconds: 0,
          state: 'running',
        };
      }),
    );
    this.syncAlarmLoop();
    this.ensureTicker();
    this.syncGestureRecovery();
  }

  cancelTimer(id: string): void {
    this.dismissTimer(id);
  }

  dismissTimer(id: string): void {
    this._timers.update((timers) => timers.filter((t) => t.id !== id));
    this.syncAlarmLoop();
    this.syncGestureRecovery();
  }

  clearAll(): void {
    this._timers.set([]);
    this.stopTicker();
    this.stopAlarmLoop();
    this.removeGestureRecoveryListeners();
  }

  private ensureTicker(): void {
    if (this.tickerId !== null) return;
    this.tickerId = setInterval(this.tick, 250);
  }

  private stopTicker(): void {
    if (this.tickerId !== null) {
      clearInterval(this.tickerId);
      this.tickerId = null;
    }
  }

  private syncAlarmLoop(): void {
    const anyCompleted = this._timers().some((t) => t.state === 'completed');
    if (anyCompleted) {
      if (this.alarmLoopId === null) {
        this.alarmLoopId = setInterval(() => {
          if (this._timers().some((t) => t.state === 'completed')) {
            playChime();
          } else {
            this.stopAlarmLoop();
          }
        }, 1800);
      }
    } else {
      this.stopAlarmLoop();
    }
  }

  private stopAlarmLoop(): void {
    if (this.alarmLoopId !== null) {
      clearInterval(this.alarmLoopId);
      this.alarmLoopId = null;
    }
  }

  private readonly onUserGestureRecovery = (): void => {
    if (!sharedAudioCtx || (sharedAudioCtx.state as string) !== 'running') {
      unlockAudioContext();
    }
  };

  private syncGestureRecovery(): void {
    const hasActiveOrCompleted = this._timers().some(
      (t) => t.state === 'running' || t.state === 'completed',
    );
    if (hasActiveOrCompleted) {
      this.addGestureRecoveryListeners();
    } else {
      this.removeGestureRecoveryListeners();
    }
  }

  private addGestureRecoveryListeners(): void {
    if (this.gestureRecoveryBound || typeof window === 'undefined') {
      return;
    }
    this.gestureRecoveryBound = true;
    window.addEventListener('pointerdown', this.onUserGestureRecovery, { passive: true });
    window.addEventListener('touchend', this.onUserGestureRecovery, { passive: true });
    window.addEventListener('keydown', this.onUserGestureRecovery, { passive: true });
  }

  private removeGestureRecoveryListeners(): void {
    if (!this.gestureRecoveryBound || typeof window === 'undefined') {
      return;
    }
    this.gestureRecoveryBound = false;
    window.removeEventListener('pointerdown', this.onUserGestureRecovery);
    window.removeEventListener('touchend', this.onUserGestureRecovery);
    window.removeEventListener('keydown', this.onUserGestureRecovery);
  }

  private readonly tick = (): void => {
    const now = Date.now();
    let hasActiveTick = false;
    let newlyCompleted = false;

    this._timers.update((timers) => {
      let changed = false;
      const next = timers.map((timer) => {
        if (timer.state === 'running' && timer.endsAtMs !== null) {
          const remainingMs = timer.endsAtMs - now;
          if (remainingMs <= 0) {
            newlyCompleted = true;
            hasActiveTick = true;
            changed = true;
            playChime();
            showNotification(timer);
            const completedAtMs = timer.endsAtMs;
            return {
              ...timer,
              remainingSeconds: 0,
              endsAtMs: null,
              completedAtMs,
              overtimeSeconds: Math.max(0, Math.floor((now - completedAtMs) / 1000)),
              state: 'completed' as const,
            };
          }

          hasActiveTick = true;
          const nextRemaining = Math.ceil(remainingMs / 1000);
          if (nextRemaining === timer.remainingSeconds) {
            return timer;
          }
          changed = true;
          return {
            ...timer,
            remainingSeconds: nextRemaining,
          };
        }

        if (timer.state === 'completed') {
          hasActiveTick = true;
          const completedAtMs = timer.completedAtMs ?? now;
          const nextOvertime = Math.max(0, Math.floor((now - completedAtMs) / 1000));
          if (nextOvertime === (timer.overtimeSeconds ?? 0) && timer.completedAtMs != null) {
            return timer;
          }
          changed = true;
          return {
            ...timer,
            completedAtMs,
            overtimeSeconds: nextOvertime,
          };
        }

        return timer;
      });

      return changed ? next : timers;
    });

    if (newlyCompleted) {
      this.syncAlarmLoop();
    }

    if (!hasActiveTick) {
      this.stopTicker();
    }
  };
}
