import { ValueProvider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CDK_DRAG_CONFIG } from '@angular/cdk/drag-drop';
import { Platform } from '@angular/cdk/platform';
import { MatDialog } from '@angular/material/dialog';
import { MAT_TOOLTIP_DEFAULT_OPTIONS } from '@angular/material/tooltip';
import { provideRouter, Router } from '@angular/router';
import { App } from './app.component';
import { appConfig, appDragDropConfig, appTooltipConfig } from './app.config';
import { KitchenReferenceService } from './core/services/kitchen-reference/kitchen-reference.service';
import { StepIngredientChipComponent } from './shared/components/step-ingredient-chip/step-ingredient-chip.component';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([])],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('handles Ctrl+/ and ? shortcuts to toggle or open Kitchen Reference outside inputs and login', () => {
    const refService = TestBed.inject(KitchenReferenceService);
    const toggleSpy = vi.spyOn(refService, 'toggleDialog').mockImplementation(() => undefined);
    const openSpy = vi.spyOn(refService, 'openDialog').mockImplementation(() => undefined);
    const router = TestBed.inject(Router);
    vi.spyOn(router, 'url', 'get').mockReturnValue('/recipes');

    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;

    const ctrlSlash = new KeyboardEvent('keydown', { key: '/', code: 'Slash', ctrlKey: true });
    vi.spyOn(ctrlSlash, 'preventDefault');
    app.onGlobalKeydown(ctrlSlash);
    expect(ctrlSlash.preventDefault).toHaveBeenCalled();
    expect(toggleSpy).toHaveBeenCalledTimes(1);

    const questionEvent = new KeyboardEvent('keydown', { key: '?' });
    vi.spyOn(questionEvent, 'preventDefault');
    app.onGlobalKeydown(questionEvent);
    expect(questionEvent.preventDefault).toHaveBeenCalled();
    expect(openSpy).toHaveBeenCalledTimes(1);

    // Ignore '?' when focus is inside an input
    const input = document.createElement('input');
    const inputEvent = new KeyboardEvent('keydown', { key: '?' });
    Object.defineProperty(inputEvent, 'target', { value: input });
    app.onGlobalKeydown(inputEvent);
    expect(openSpy).toHaveBeenCalledTimes(1);

    // Ignore when MatDialog is already open
    const matDialog = TestBed.inject(MatDialog);
    (matDialog.openDialogs as unknown[]).push({});
    app.onGlobalKeydown(new KeyboardEvent('keydown', { key: '?' }));
    expect(openSpy).toHaveBeenCalledTimes(1);
    (matDialog.openDialogs as unknown[]).length = 0;

    // Ignore on /login
    vi.spyOn(router, 'url', 'get').mockReturnValue('/login');
    app.onGlobalKeydown(new KeyboardEvent('keydown', { key: '/', code: 'Slash', ctrlKey: true }));
    expect(toggleSpy).toHaveBeenCalledTimes(1);
  });

  it('provides global CDK_DRAG_CONFIG with touch delay in appConfig', () => {
    expect(appDragDropConfig.dragStartDelay).toEqual({ touch: 250, mouse: 0 });
    const cdkDragProvider = appConfig.providers.find(
      (p): p is ValueProvider =>
        typeof p === 'object' && p !== null && 'provide' in p && p.provide === CDK_DRAG_CONFIG,
    );
    expect(cdkDragProvider).toBeDefined();
    expect(cdkDragProvider?.useValue).toEqual(appDragDropConfig);
  });

  it('provides global MAT_TOOLTIP_DEFAULT_OPTIONS with touchGestures off in appConfig', () => {
    expect(appTooltipConfig.touchGestures).toBe('off');
    const tooltipProvider = appConfig.providers.find(
      (p): p is ValueProvider =>
        typeof p === 'object' &&
        p !== null &&
        'provide' in p &&
        p.provide === MAT_TOOLTIP_DEFAULT_OPTIONS,
    );
    expect(tooltipProvider).toBeDefined();
    expect(tooltipProvider?.useValue).toEqual(appTooltipConfig);
  });

  it.each([
    { label: 'Android', IOS: false, ANDROID: true },
    { label: 'iOS', IOS: true, ANDROID: false },
  ])(
    'does not apply inline touch-action: none to matTooltip triggers on $label',
    async ({ IOS, ANDROID }) => {
      TestBed.resetTestingModule();
      await TestBed.configureTestingModule({
        imports: [StepIngredientChipComponent],
        providers: [
          { provide: MAT_TOOLTIP_DEFAULT_OPTIONS, useValue: appTooltipConfig },
          { provide: Platform, useValue: { isBrowser: true, IOS, ANDROID } },
        ],
      }).compileComponents();

      const fixture = TestBed.createComponent(StepIngredientChipComponent);
      fixture.componentRef.setInput('token', {
        type: 'ingredient',
        raw: '{{ing:1}}',
        ingredientId: '1',
        name: 'Butter',
        amount: 2,
        unit: 'tbsp',
        notes: 'Unsalted',
      });
      fixture.detectChanges();

      const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
      expect(button).toBeTruthy();
      expect(button.style.touchAction).not.toBe('none');
    },
  );
});
