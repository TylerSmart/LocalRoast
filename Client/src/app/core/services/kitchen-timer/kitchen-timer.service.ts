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
  state: KitchenTimerState;
}

let sharedAudioCtx: AudioContext | null = null;

function playChime(): void {
  try {
    const AudioCtx =
      window.AudioContext ||
      (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioCtx) return;
    if (!sharedAudioCtx || sharedAudioCtx.state === 'closed') {
      sharedAudioCtx = new AudioCtx();
    }
    const ctx = sharedAudioCtx;
    if (ctx.state === 'suspended') {
      void ctx.resume();
    }
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
  } catch {
    // AudioContext blocked or unsupported in headless environments
  }
}

function showNotification(timer: ActiveKitchenTimer): void {
  try {
    if (typeof Notification !== 'undefined' && Notification.permission === 'granted') {
      new Notification(`Timer Finished: ${timer.label}`, {
        body: `${timer.recipeTitle} — your timer has completed!`,
        icon: '/favicon.ico',
      });
    }
  } catch {
    // Non-blocking
  }
}

@Service()
export class KitchenTimerService {
  private tickerId: ReturnType<typeof setInterval> | null = null;
  private alarmLoopId: ReturnType<typeof setInterval> | null = null;
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
      state: 'running',
    };

    this._timers.update((current) => [...current, timer]);
    this.ensureTicker();
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
  }

  resumeTimer(id: string): void {
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id || t.state !== 'paused') return t;
        const endsAtMs = Date.now() + t.remainingSeconds * 1000;
        return {
          ...t,
          endsAtMs,
          state: 'running',
        };
      }),
    );
    this.ensureTicker();
  }

  addSeconds(id: string, deltaSeconds = 60): void {
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id) return t;
        const newRemaining = Math.max(0, t.remainingSeconds + deltaSeconds);
        const endsAtMs = Date.now() + newRemaining * 1000;
        return {
          ...t,
          remainingSeconds: newRemaining,
          endsAtMs,
          state: 'running',
        };
      }),
    );
    this.syncAlarmLoop();
    this.ensureTicker();
  }

  resetTimer(id: string): void {
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id) return t;
        return {
          ...t,
          remainingSeconds: t.totalDurationSeconds,
          endsAtMs: null,
          state: 'paused',
        };
      }),
    );
    this.syncAlarmLoop();
  }

  restartTimer(id: string): void {
    const now = Date.now();
    this._timers.update((timers) =>
      timers.map((t) => {
        if (t.id !== id) return t;
        const endsAtMs = now + t.totalDurationSeconds * 1000;
        return {
          ...t,
          remainingSeconds: t.totalDurationSeconds,
          endsAtMs,
          state: 'running',
        };
      }),
    );
    this.syncAlarmLoop();
    this.ensureTicker();
  }

  cancelTimer(id: string): void {
    this.dismissTimer(id);
  }

  dismissTimer(id: string): void {
    this._timers.update((timers) => timers.filter((t) => t.id !== id));
    this.syncAlarmLoop();
  }

  clearAll(): void {
    this._timers.set([]);
    this.stopTicker();
    this.stopAlarmLoop();
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

  private readonly tick = (): void => {
    const now = Date.now();
    let hasRunning = false;
    let newlyCompleted = false;
    this._timers.update((timers) => {
      return timers.map((timer) => {
        if (timer.state !== 'running' || timer.endsAtMs === null) {
          return timer;
        }

        const remainingMs = timer.endsAtMs - now;
        if (remainingMs <= 0) {
          newlyCompleted = true;
          playChime();
          showNotification(timer);
          return {
            ...timer,
            remainingSeconds: 0,
            endsAtMs: null,
            state: 'completed',
          };
        }

        hasRunning = true;
        return {
          ...timer,
          remainingSeconds: Math.ceil(remainingMs / 1000),
        };
      });
    });

    if (newlyCompleted) {
      this.syncAlarmLoop();
    }

    if (!hasRunning) {
      this.stopTicker();
    }
  };
}
