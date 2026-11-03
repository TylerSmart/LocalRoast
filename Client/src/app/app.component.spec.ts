import { ValueProvider } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CDK_DRAG_CONFIG } from '@angular/cdk/drag-drop';
import { provideRouter } from '@angular/router';
import { App } from './app.component';
import { appConfig, appDragDropConfig } from './app.config';

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
});
