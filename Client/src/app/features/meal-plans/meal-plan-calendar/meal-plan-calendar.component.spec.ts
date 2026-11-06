import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { CdkDrag } from '@angular/cdk/drag-drop';
import { signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { MealPlanEntryDto, MealPlanWeekDto } from '../../../core/models/meal-plan.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import {
  addDaysIso,
  getTodayIsoLocal,
  normalizeToSundayIso,
} from '../../../core/utils/meal-plan-date';
import { DraftMealPlanDialogComponent } from '../draft-meal-plan-dialog/draft-meal-plan-dialog.component';
import { MealPlanCalendarComponent } from './meal-plan-calendar.component';

describe('MealPlanCalendarComponent', () => {
  let component: MealPlanCalendarComponent;
  let fixture: ComponentFixture<MealPlanCalendarComponent>;

  const mockDialog = {
    open: vi.fn(),
  };

  const mockWeek: MealPlanWeekDto = {
    weekStartDate: '2026-11-01',
    weekEndDate: '2026-11-07',
    days: [
      {
        date: '2026-11-01',
        dayOffset: 0,
        entries: [
          {
            id: 'e-1',
            weekStartDate: '2026-11-01',
            isOnDeck: false,
            scheduledDate: '2026-11-01',
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
            createdAt: '2026-11-01T00:00:00Z',
            createdBy: 'chef',
            modifiedAt: '2026-11-01T00:00:00Z',
            modifiedBy: 'chef',
          },
        ],
      },
    ],
    onDeckEntries: [
      {
        id: 'e-2',
        weekStartDate: '2026-11-01',
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
        createdAt: '2026-11-01T00:00:00Z',
        createdBy: 'chef',
        modifiedAt: '2026-11-01T00:00:00Z',
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
    vi.clearAllMocks();
    mockMealPlanService.getRange.mockImplementation((start: string, end: string) =>
      of({
        startDate: start,
        endDate: end,
        scheduledEntries: [{ ...mockWeek.days[0].entries[0], scheduledDate: start }],
        onDeckEntries: mockWeek.onDeckEntries,
      }),
    );
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

  it('defaults to a rolling seven-day board starting today', () => {
    expect(component).toBeTruthy();
    const today = getTodayIsoLocal();
    expect(component['viewMode']()).toBe('next7');
    expect(mockMealPlanService.getRange).toHaveBeenCalledWith(today, addDaysIso(today, 6), true);
    expect(mockMealPlanService.getWeek).not.toHaveBeenCalled();
    expect(component['weekData']()!.days).toHaveLength(7);
    expect(component['weekData']()!.days[0].date).toBe(today);
    expect(component['weekData']()!.days[6].date).toBe(addDaysIso(today, 6));

    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Meal Plan');
    expect(el.textContent).toContain('Sunday Chili');
    expect(el.textContent).toContain('Cornbread');
  });

  it('preserves canonical Sunday-based week navigation', () => {
    component['setViewMode']('week');
    const currentSunday = normalizeToSundayIso(getTodayIsoLocal());
    const nextSunday = addDaysIso(currentSunday, 7);

    component['navigatePeriod'](1);
    expect(component['activeWeekStart']()).toBe(nextSunday);
    expect(mockMealPlanService.getWeek).toHaveBeenCalledWith(nextSunday);

    component['navigatePeriod'](-1);
    expect(component['activeWeekStart']()).toBe(currentSunday);
  });

  it('navigates rolling periods and resets to today without normalizing to Sunday', () => {
    const today = getTodayIsoLocal();
    component['navigatePeriod'](1);
    expect(component['activeRollingStart']()).toBe(addDaysIso(today, 7));
    component['navigatePeriod'](-1);
    expect(component['activeRollingStart']()).toBe(today);
    component['navigatePeriod'](-1);
    component['goToToday']();
    expect(component['activeRollingStart']()).toBe(today);
    expect(component['weekData']()!.weekStartDate).toBe(today);
  });

  it('cancels stale rolling loads when switching back to a canonical week', () => {
    const pending = new Subject<{
      startDate: string;
      endDate: string;
      scheduledEntries: MealPlanEntryDto[];
      onDeckEntries: MealPlanEntryDto[];
    }>();
    mockMealPlanService.getRange.mockReturnValue(pending);
    component.loadCurrentView();
    component['setViewMode']('week');
    pending.next({ startDate: '', endDate: '', scheduledEntries: [], onDeckEntries: [] });
    expect(component['weekData']()).toEqual(mockWeek);
  });

  it('clears stale rolling data and reports loading errors', () => {
    const snack = vi.spyOn(component['snackBar'], 'open');
    mockMealPlanService.getRange.mockReturnValue(
      throwError(() => ({ error: { detail: 'Unavailable' } })),
    );
    component.loadCurrentView();
    expect(component['weekData']()).toBeNull();
    expect(component['isLoading']()).toBe(false);
    expect(snack).toHaveBeenCalledWith('Unavailable', 'Close', { duration: 4000 });
  });

  it('opens generate shopping list dialog on openGenerateShoppingListDialog', () => {
    const currentSunday = getTodayIsoLocal();
    const currentSaturday = addDaysIso(currentSunday, 6);

    mockDialog.open.mockReturnValue({
      afterClosed: () => of(undefined),
    });

    component['openGenerateShoppingListDialog']();
    expect(mockDialog.open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({
        data: {
          startDate: currentSunday,
          endDate: currentSaturday,
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

  it('provides full date labels and empty states for the mobile month agenda', () => {
    component['setViewMode']('month');
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const dates = el.querySelectorAll('.month-agenda-date');
    expect(dates.length).toBeGreaterThanOrEqual(28);
    expect(dates[0].textContent).toMatch(/[A-Za-z]{3}, [A-Za-z]{3} \d{1,2}/);
    expect(el.querySelector('.month-empty-label')?.textContent).toContain('No meals planned');
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
      afterClosed: () => of({ weekStartDate: '2026-11-01' }),
    });

    draftBtn.click();

    expect(mockDialog.open).toHaveBeenCalledWith(
      DraftMealPlanDialogComponent,
      expect.objectContaining({
        data: {
          weekStartDate: component['activePeriodStart'](),
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
