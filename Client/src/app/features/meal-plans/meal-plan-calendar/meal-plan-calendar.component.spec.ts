import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { CdkDrag } from '@angular/cdk/drag-drop';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { MealPlanEntryDto, MealPlanWeekDto } from '../../../core/models/meal-plan.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { DraftMealPlanDialogComponent } from '../draft-meal-plan-dialog/draft-meal-plan-dialog.component';
import { MealPlanCalendarComponent } from './meal-plan-calendar.component';

describe('MealPlanCalendarComponent', () => {
  let component: MealPlanCalendarComponent;
  let fixture: ComponentFixture<MealPlanCalendarComponent>;

  const mockDialog = {
    open: vi.fn(),
  };

  const mockWeek: MealPlanWeekDto = {
    weekStartDate: '2026-11-02',
    weekEndDate: '2026-11-08',
    days: [
      {
        date: '2026-11-02',
        dayOffset: 0,
        entries: [
          {
            id: 'e-1',
            weekStartDate: '2026-11-02',
            isOnDeck: false,
            scheduledDate: '2026-11-02',
            slot: 'Dinner',
            sortOrder: 0,
            entryType: 'Recipe',
            isLeftovers: false,
            targetServings: 4,
            displayTitle: 'Sunday Chili',
            customTitle: null,
            notes: null,
            sourceMealPlanEntryId: null,
            recipe: {
              id: 'r-1',
              title: 'Sunday Chili',
              slug: 'sunday-chili',
              baseServings: 4,
              totalTimeMinutes: 45,
              difficulty: 'Easy',
              thumbnailImagePath: null,
            },
            createdAt: '2026-11-02T00:00:00Z',
            createdBy: 'chef',
            modifiedAt: '2026-11-02T00:00:00Z',
            modifiedBy: 'chef',
          },
        ],
      },
    ],
    onDeckEntries: [
      {
        id: 'e-2',
        weekStartDate: '2026-11-02',
        isOnDeck: true,
        scheduledDate: null,
        slot: null,
        sortOrder: 0,
        entryType: 'Recipe',
        isLeftovers: false,
        targetServings: 4,
        displayTitle: 'Cornbread',
        customTitle: null,
        notes: null,
        sourceMealPlanEntryId: null,
        recipe: null,
        createdAt: '2026-11-02T00:00:00Z',
        createdBy: 'chef',
        modifiedAt: '2026-11-02T00:00:00Z',
        modifiedBy: 'chef',
      },
    ],
  };

  const mockMealPlanService = {
    getWeek: vi.fn().mockReturnValue(of(mockWeek)),
    getRange: vi
      .fn()
      .mockReturnValue(of({ startDate: '', endDate: '', scheduledEntries: [], onDeckEntries: [] })),
    cookTonight: vi.fn().mockReturnValue(of({} as MealPlanEntryDto)),
    moveEntry: vi.fn().mockReturnValue(of({} as MealPlanEntryDto)),
    deleteEntry: vi.fn().mockReturnValue(of(undefined)),
    clearWeek: vi.fn().mockReturnValue(of(undefined)),
  };

  const mockAiHelperService = {
    checkStatus: vi
      .fn()
      .mockReturnValue(of({ isAvailable: true, provider: 'Ollama', latencyMs: 25 })),
    status: signal({ isAvailable: true, provider: 'Ollama', latencyMs: 25 }),
    isAvailable: signal(true),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MealPlanCalendarComponent],
      providers: [
        provideRouter([]),
        { provide: MealPlanService, useValue: mockMealPlanService },
        { provide: MatDialog, useValue: mockDialog },
        { provide: AiKitchenHelperService, useValue: mockAiHelperService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(MealPlanCalendarComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('renders calendar toolbar and loads week data', () => {
    expect(component).toBeTruthy();
    expect(mockMealPlanService.getWeek).toHaveBeenCalled();
    expect(component['weekData']()).toEqual(mockWeek);

    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Meal Plan');
    expect(el.textContent).toContain('Sunday Chili');
    expect(el.textContent).toContain('Cornbread');
  });

  it('navigates next and previous week', () => {
    component['navigatePeriod'](1);
    expect(component['activeWeekStart']()).toBe('2026-11-09');
    expect(mockMealPlanService.getWeek).toHaveBeenCalledWith('2026-11-09');

    component['navigatePeriod'](-1);
    expect(component['activeWeekStart']()).toBe('2026-11-02');
  });

  it('opens generate shopping list dialog on openGenerateShoppingListDialog', () => {
    mockDialog.open.mockReturnValue({
      afterClosed: () => of(undefined),
    });

    component['openGenerateShoppingListDialog']();
    expect(mockDialog.open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: {
          startDate: '2026-11-02',
          endDate: '2026-11-08',
        },
      }),
    );
  });

  it('switches between week and month views', () => {
    component['setViewMode']('month');
    expect(component['viewMode']()).toBe('month');
    expect(mockMealPlanService.getRange).toHaveBeenCalled();

    component['setViewMode']('week');
    expect(component['viewMode']()).toBe('week');
  });

  it('calls cookTonight on entry', () => {
    const entry = mockWeek.onDeckEntries[0];
    component['cookTonight'](entry);
    expect(mockMealPlanService.cookTonight).toHaveBeenCalledWith('e-2', {
      targetDate: component['todayIso'],
      slot: 'Dinner',
    });
  });

  it('navigates to cook mode on startCooking(entry)', () => {
    const navSpy = vi.spyOn(component['router'], 'navigate').mockResolvedValue(true);
    const entry = mockWeek.days[0].entries[0];
    component['startCooking'](entry);

    expect(navSpy).toHaveBeenCalledWith(['/recipes', 'r-1', 'cook'], {
      queryParams: {
        servings: 4,
        from: 'meal-plan',
      },
    });
  });

  it('opens cook log dialog on logCook(entry)', () => {
    const entry = mockWeek.days[0].entries[0];
    component['logCook'](entry);
    expect(mockDialog.open).toHaveBeenCalled();
  });

  it('renders Draft Week button when aiHelper is available and opens dialog', () => {
    const el = fixture.nativeElement as HTMLElement;
    const draftBtn = el.querySelector('.draft-week-ai-btn') as HTMLButtonElement;
    expect(draftBtn).toBeTruthy();

    mockDialog.open.mockReturnValue({
      afterClosed: () => of({ weekStartDate: '2026-11-02' }),
    });

    draftBtn.click();

    expect(mockDialog.open).toHaveBeenCalledWith(
      DraftMealPlanDialogComponent,
      expect.objectContaining({
        data: {
          weekStartDate: component['activeWeekStart'](),
        },
      }),
    );
  });

  it('configures cdkDragStartDelay with touch delay and zero mouse delay for mobile scroll mitigation', () => {
    expect(component['dragStartDelay']).toEqual({ touch: 250, mouse: 0 });

    const dragDirectives = fixture.debugElement.queryAll(By.directive(CdkDrag));
    expect(dragDirectives.length).toBeGreaterThan(0);
    for (const dragDebugEl of dragDirectives) {
      const cdkDrag = dragDebugEl.injector.get(CdkDrag);
      expect(cdkDrag.dragStartDelay).toEqual({ touch: 250, mouse: 0 });
    }
  });
});
