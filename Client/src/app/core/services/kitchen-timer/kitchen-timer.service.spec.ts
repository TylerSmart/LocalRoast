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
    expect(service.hasCompletedAlert()).toBe(false);
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
});
