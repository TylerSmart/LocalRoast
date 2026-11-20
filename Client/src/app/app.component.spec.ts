import { ValueProvider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CDK_DRAG_CONFIG } from '@angular/cdk/drag-drop';
import { Platform } from '@angular/cdk/platform';
import { MAT_TOOLTIP_DEFAULT_OPTIONS } from '@angular/material/tooltip';
import { provideRouter } from '@angular/router';
import { App } from './app.component';
import { appConfig, appDragDropConfig, appTooltipConfig } from './app.config';
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
