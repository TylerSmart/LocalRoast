import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { KitchenTimerService } from './kitchen-timer.service';

describe('KitchenTimerService', () => {
  let service: KitchenTimerService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [KitchenTimerService],
    });
    service = TestBed.inject(KitchenTimerService);
  });

  afterEach(() => {
    service.clearAll();
    vi.useRealTimers();
  });

  it('starts a timer and ticks countdown', () => {
    const id = service.startTimer('rec-1', 'Chili', 'Simmer', 120, 2);
    expect(service.timers().length).toBe(1);
    expect(service.timers()[0].id).toBe(id);
    expect(service.timers()[0].remainingSeconds).toBe(120);
    expect(service.timers()[0].state).toBe('running');

    // Advance 30 seconds
    vi.advanceTimersByTime(30000);
    expect(service.timers()[0].remainingSeconds).toBe(90);
  });

  it('pauses and resumes timer', () => {
    const id = service.startTimer('rec-1', 'Chili', 'Simmer', 100);
    vi.advanceTimersByTime(20000);
    expect(service.timers()[0].remainingSeconds).toBe(80);

    service.pauseTimer(id);
    expect(service.timers()[0].state).toBe('paused');

    // Advancing timers while paused should not reduce remaining seconds
    vi.advanceTimersByTime(20000);
    expect(service.timers()[0].remainingSeconds).toBe(80);

    service.resumeTimer(id);
    expect(service.timers()[0].state).toBe('running');

    vi.advanceTimersByTime(10000);
    expect(service.timers()[0].remainingSeconds).toBe(70);
  });

  it('adds seconds and revives completed timer to running', () => {
    const id = service.startTimer('rec-1', 'Chili', 'Simmer', 10);
    vi.advanceTimersByTime(11000);

    expect(service.timers()[0].state).toBe('completed');
    expect(service.timers()[0].remainingSeconds).toBe(0);
    expect(service.hasCompletedAlert()).toBe(true);

    service.addSeconds(id, 60);
    expect(service.timers()[0].state).toBe('running');
    expect(service.timers()[0].remainingSeconds).toBe(60);
    expect(service.timers()[0].overtimeSeconds).toBe(0);
    expect(service.hasCompletedAlert()).toBe(false);
  });

  it('counts up overtimeSeconds after completion and resets overtime on restart or reset', () => {
    const id = service.startTimer('rec-1', 'Chili', 'Simmer', 5);
    vi.advanceTimersByTime(5000);
    expect(service.timers()[0].state).toBe('completed');
    expect(service.timers()[0].remainingSeconds).toBe(0);
    expect(service.timers()[0].overtimeSeconds).toBe(0);

    vi.advanceTimersByTime(1000);
    expect(service.timers()[0].overtimeSeconds).toBe(1);

    vi.advanceTimersByTime(64000);
    expect(service.timers()[0].overtimeSeconds).toBe(65);

    service.restartTimer(id);
    expect(service.timers()[0].state).toBe('running');
    expect(service.timers()[0].overtimeSeconds).toBe(0);
    expect(service.timers()[0].remainingSeconds).toBe(5);

    vi.advanceTimersByTime(6000);
    expect(service.timers()[0].state).toBe('completed');
    expect(service.timers()[0].overtimeSeconds).toBe(1);

    service.resetTimer(id);
    expect(service.timers()[0].state).toBe('paused');
    expect(service.timers()[0].overtimeSeconds).toBe(0);
  });

  it('pre-unlocks WebKit AudioContext on user gesture and awaits resume before playing chime', async () => {
    const originalAudioContext = window.AudioContext;
    const originalAudioSession = (navigator as Navigator & { audioSession?: { type: string } })
      .audioSession;

    const bufferStartSpy = vi.fn();
    const oscStartSpy = vi.fn();
    const audioSessionMock = { type: 'auto' };

    const mockAudioCtx = {
      state: 'interrupted' as string,
      currentTime: 0,
      destination: {},
      resume: vi.fn().mockImplementation(async () => {
        mockAudioCtx.state = 'running';
      }),
      createBuffer: vi.fn().mockReturnValue({}),
      createBufferSource: vi.fn().mockReturnValue({
        buffer: null,
        connect: vi.fn(),
        start: bufferStartSpy,
      }),
      createOscillator: vi.fn().mockReturnValue({
        type: 'sine',
        frequency: { setValueAtTime: vi.fn() },
        connect: vi.fn(),
        start: oscStartSpy,
        stop: vi.fn(),
      }),
      createGain: vi.fn().mockReturnValue({
        gain: { setValueAtTime: vi.fn(), exponentialRampToValueAtTime: vi.fn() },
        connect: vi.fn(),
      }),
    };

    Object.defineProperty(window, 'AudioContext', {
      value: function MockAudioContext() {
        return mockAudioCtx;
      },
      configurable: true,
    });
    Object.defineProperty(navigator, 'audioSession', {
      value: audioSessionMock,
      configurable: true,
    });

    try {
      service.startTimer('rec-1', 'Chili', 'Simmer', 2);

      // Synchronously during user gesture: audioSession configured, resume called, silent 1-frame buffer started
      expect(audioSessionMock.type).toBe('playback');
      expect(mockAudioCtx.resume).toHaveBeenCalled();
      expect(bufferStartSpy).toHaveBeenCalledWith(0);

      // Simulate WebKit moving back to suspended while timer runs, then resuming when chime fires
      await Promise.resolve();
      mockAudioCtx.state = 'suspended';
      vi.advanceTimersByTime(2200);
      expect(oscStartSpy).not.toHaveBeenCalled();

      await Promise.resolve();
      expect(mockAudioCtx.state).toBe('running');
      expect(oscStartSpy).toHaveBeenCalledTimes(4);
    } finally {
      Object.defineProperty(window, 'AudioContext', {
        value: originalAudioContext,
        configurable: true,
      });
      if (originalAudioSession === undefined) {
        delete (navigator as { audioSession?: unknown }).audioSession;
      } else {
        Object.defineProperty(navigator, 'audioSession', {
          value: originalAudioSession,
          configurable: true,
        });
      }
    }
  });

  it('resets and dismisses timer', () => {
    const id = service.startTimer('rec-1', 'Chili', 'Simmer', 60);
    vi.advanceTimersByTime(25000);

    service.resetTimer(id);
    expect(service.timers()[0].remainingSeconds).toBe(60);
    expect(service.timers()[0].state).toBe('paused');

    service.dismissTimer(id);
    expect(service.timers().length).toBe(0);
  });

  it('restarts a timer immediately into running state from total duration', () => {
    const id = service.startTimer('rec-1', 'Chili', 'Simmer', 60);
    vi.advanceTimersByTime(35000);
    expect(service.timers()[0].remainingSeconds).toBe(25);

    service.restartTimer(id);
    expect(service.timers()[0].remainingSeconds).toBe(60);
    expect(service.timers()[0].state).toBe('running');

    vi.advanceTimersByTime(10000);
    expect(service.timers()[0].remainingSeconds).toBe(50);
  });

  it('cancels timer via cancelTimer alias', () => {
    const id = service.startTimer('rec-1', 'Chili', 'Simmer', 60);
    expect(service.timers().length).toBe(1);

    service.cancelTimer(id);
    expect(service.timers().length).toBe(0);
  });

  it('uses ServiceWorkerRegistration.showNotification when a service worker is registered', async () => {
    const showNotificationSpy = vi.fn().mockResolvedValue(undefined);
    const originalNotification = globalThis.Notification;
    const originalSw = navigator.serviceWorker;

    Object.defineProperty(globalThis, 'Notification', {
      value: { permission: 'granted' },
      configurable: true,
    });
    Object.defineProperty(navigator, 'serviceWorker', {
      value: {
        getRegistration: vi.fn().mockResolvedValue({
          showNotification: showNotificationSpy,
        }),
      },
      configurable: true,
    });

    try {
      const id = service.startTimer('rec-1', 'Chili', 'Simmer', 5);
      vi.advanceTimersByTime(6000);
      await Promise.resolve();

      expect(showNotificationSpy).toHaveBeenCalledWith(
        'Timer Finished: Simmer',
        expect.objectContaining({
          body: 'Chili — your timer has completed!',
          icon: '/icons/icon-192.png',
          badge: '/icons/icon-monochrome-192.png',
          tag: id,
        }),
      );
    } finally {
      Object.defineProperty(globalThis, 'Notification', {
        value: originalNotification,
        configurable: true,
      });
      Object.defineProperty(navigator, 'serviceWorker', {
        value: originalSw,
        configurable: true,
      });
    }
  });
});
