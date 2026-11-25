import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { KitchenReferenceService } from '../../core/services/kitchen-reference/kitchen-reference.service';
import { KitchenTimerService } from '../../core/services/kitchen-timer/kitchen-timer.service';
import { KitchenReferenceDialogComponent } from './kitchen-reference-dialog.component';

describe('KitchenReferenceDialogComponent', () => {
  let component: KitchenReferenceDialogComponent;
  let fixture: ComponentFixture<KitchenReferenceDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };
  let timerService: KitchenTimerService;
  let referenceService: KitchenReferenceService;

  beforeEach(async () => {
    localStorage.clear();
    mockDialogRef = { close: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [KitchenReferenceDialogComponent],
      providers: [{ provide: MatDialogRef, useValue: mockDialogRef }],
    }).compileComponents();

    timerService = TestBed.inject(KitchenTimerService);
    timerService.clearAll();
    referenceService = TestBed.inject(KitchenReferenceService);
    referenceService.setActiveTab('convert');
    referenceService.setSearchQuery('');

    fixture = TestBed.createComponent(KitchenReferenceDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('converts 1 cup all-purpose flour to 120 g by default', () => {
    const resultEl = fixture.nativeElement.querySelector(
      '[data-testid="conversion-result-value"]',
    ) as HTMLElement;
    expect(resultEl.textContent?.trim()).toBe('120 g');
  });

  it('switches tabs to temps, glossary, swaps, and ratios & timers', () => {
    const el = fixture.nativeElement as HTMLElement;

    (el.querySelector('[data-testid="ref-tab-temps"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.textContent).toContain('Food Danger Zone');
    expect(el.querySelectorAll('[data-testid="temp-reference-card"]').length).toBeGreaterThan(10);

    (el.querySelector('[data-testid="ref-tab-glossary"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.textContent).toContain('Julienne');
    expect(el.textContent).toContain('Blanch');
    expect(el.textContent).toContain('Dash');

    (el.querySelector('[data-testid="ref-tab-swaps"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.textContent).toContain('Buttermilk');

    (el.querySelector('[data-testid="ref-tab-ratios"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.textContent).toContain('Quick Kitchen Timer');
    expect(el.textContent).toContain('Classic Vinaigrette');
  });

  it('filters across all categories when searching', () => {
    referenceService.setSearchQuery('julienne');
    fixture.detectChanges();

    const resultsEl = fixture.nativeElement.querySelector(
      '[data-testid="kitchen-reference-search-results"]',
    ) as HTMLElement;
    expect(resultsEl).toBeTruthy();
    expect(resultsEl.textContent).toContain('Julienne');
    expect(resultsEl.textContent).toContain('1/8" × 1/8" × 2"');
  });

  it('starts a standalone timer via KitchenTimerService from Ratios & Timers tab', () => {
    referenceService.setActiveTab('ratios');
    fixture.detectChanges();

    const startBtn = fixture.nativeElement.querySelector(
      '[data-testid="quick-timer-preset-5"]',
    ) as HTMLButtonElement;
    startBtn.click();
    fixture.detectChanges();

    expect(timerService.timers().length).toBe(1);
    expect(timerService.timers()[0].totalDurationSeconds).toBe(300);
    expect(timerService.timers()[0].recipeId).toBe('');
    expect(
      fixture.nativeElement.querySelector('[data-testid="reference-active-timers"]'),
    ).toBeTruthy();

    timerService.clearAll();
  });

  it('closes dialog when close button is clicked', () => {
    const closeBtn = fixture.nativeElement.querySelector(
      '[data-testid="close-kitchen-reference-btn"]',
    ) as HTMLButtonElement;
    closeBtn.click();
    expect(mockDialogRef.close).toHaveBeenCalledTimes(1);
    expect(component).toBeTruthy();
  });
});
