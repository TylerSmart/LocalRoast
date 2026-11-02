import { Service, signal } from '@angular/core';

export type WakeLockStatus = 'active' | 'unsupported' | 'released';

@Service()
export class WakeLockService {
  private sentinel: WakeLockSentinel | null = null;
  private shouldBeActive = false;
  private readonly _status = signal<WakeLockStatus>('released');
  readonly status = this._status.asReadonly();

  private readonly onVisibilityChange = async (): Promise<void> => {
    if (document.visibilityState === 'visible' && this.shouldBeActive && !this.sentinel) {
      await this.requestSentinel();
    }
  };

  async acquire(): Promise<void> {
    if (typeof navigator === 'undefined' || !('wakeLock' in navigator)) {
      this._status.set('unsupported');
      return;
    }

    this.shouldBeActive = true;
    document.addEventListener('visibilitychange', this.onVisibilityChange);
    await this.requestSentinel();
  }

  async release(): Promise<void> {
    this.shouldBeActive = false;
    document.removeEventListener('visibilitychange', this.onVisibilityChange);
    if (this.sentinel) {
      try {
        await this.sentinel.release();
      } catch {
        // Best effort
      }
      this.sentinel = null;
    }
    this._status.set('released');
  }

  private async requestSentinel(): Promise<void> {
    try {
      this.sentinel = await navigator.wakeLock.request('screen');
      this._status.set('active');
      this.sentinel.addEventListener('release', () => {
        this.sentinel = null;
      });
    } catch {
      this._status.set('unsupported');
    }
  }
}
