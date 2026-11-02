import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { WakeLockService } from './wake-lock.service';

describe('WakeLockService', () => {
  let service: WakeLockService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [WakeLockService],
    });
    service = TestBed.inject(WakeLockService);
  });

  it('initializes with released status', () => {
    expect(service.status()).toBe('released');
  });

  it('sets unsupported status if wakeLock API is absent', async () => {
    const originalWakeLock = (navigator as unknown as { wakeLock: unknown }).wakeLock;
    delete (navigator as unknown as { wakeLock?: unknown }).wakeLock;

    await service.acquire();
    expect(service.status()).toBe('unsupported');

    if (originalWakeLock !== undefined) {
      Object.defineProperty(navigator, 'wakeLock', {
        value: originalWakeLock,
        configurable: true,
      });
    }
  });

  it('acquires and releases sentinel when supported', async () => {
    const releaseSpy = vi.fn().mockResolvedValue(undefined);
    const mockSentinel = {
      release: releaseSpy,
      addEventListener: vi.fn(),
    } as unknown as WakeLockSentinel;

    const requestSpy = vi.fn().mockResolvedValue(mockSentinel);

    Object.defineProperty(navigator, 'wakeLock', {
      value: { request: requestSpy },
      configurable: true,
      writable: true,
    });

    await service.acquire();
    expect(requestSpy).toHaveBeenCalledWith('screen');
    expect(service.status()).toBe('active');

    await service.release();
    expect(releaseSpy).toHaveBeenCalled();
    expect(service.status()).toBe('released');
  });
});
