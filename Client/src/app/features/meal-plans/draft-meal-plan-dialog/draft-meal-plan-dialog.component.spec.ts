import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subject, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DraftMealPlanResponseDto } from '../../../core/models/ai-helper.models';
import { MealPlanWeekDto } from '../../../core/models/meal-plan.models';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import {
  DRAFT_MEAL_PLAN_PREFS_STORAGE_KEY,
  DraftMealPlanDialogComponent,
  DraftMealPlanDialogData,
} from './draft-meal-plan-dialog.component';

describe('DraftMealPlanDialogComponent', () => {
  let component: DraftMealPlanDialogComponent;
  let fixture: ComponentFixture<DraftMealPlanDialogComponent>;

  let mockDialogRef: {
    close: ReturnType<typeof vi.fn>;
  };
  let mockSnackBar: {
    open: ReturnType<typeof vi.fn>;
  };
  let mockAiHelperService: {
    draftWeeklyMealPlan: ReturnType<typeof vi.fn>;
  };
  let mockMealPlanService: {
    applyDraftEntries: ReturnType<typeof vi.fn>;
  };

  const dialogData: DraftMealPlanDialogData = {
    weekStartDate: '2026-11-02',
  };

  const mockDraftResponse: DraftMealPlanResponseDto = {
    weekStartDate: '2026-11-02',
    weekEndDate: '2026-11-08',
    modelUsed: 'llama3:8b',
    consideredCookbookRecipeCount: 12,
    candidates: [
      {
        recipeId: 'rec-1',
        recipe: {
          id: 'rec-1',
          title: 'Beef Chili',
          slug: 'beef-chili',
          baseServings: 4,
          totalTimeMinutes: 60,
          difficulty: 'Easy',
          thumbnailImagePath: null,
        },
        isOnDeck: false,
        dayOffset: 1,
        scheduledDate: '2026-11-03',
        slot: 'Dinner',
        targetServings: 4,
        cookServings: 4,
        leftoverSlots: [],
        rationale: 'Hearty high protein dinner',
      },
      {
        recipeId: 'rec-2',
        recipe: {
          id: 'rec-2',
          title: 'Veggie Stir Fry',
          slug: 'veggie-stir-fry',
          baseServings: 2,
          totalTimeMinutes: 20,
          difficulty: 'Easy',
          thumbnailImagePath: null,
        },
        isOnDeck: false,
        dayOffset: 3,
        scheduledDate: '2026-11-05',
        slot: 'Dinner',
        targetServings: 2,
        cookServings: 2,
        leftoverSlots: [],
        rationale: 'Quick fresh weeknight dinner',
      },
    ],
  };

  beforeEach(async () => {
    vi.setSystemTime(new Date(2026, 10, 1, 12));
    localStorage.removeItem(DRAFT_MEAL_PLAN_PREFS_STORAGE_KEY);
    mockDialogRef = { close: vi.fn() };
    mockSnackBar = { open: vi.fn() };
    mockAiHelperService = {
      draftWeeklyMealPlan: vi.fn().mockReturnValue(of(mockDraftResponse)),
    };
    mockMealPlanService = {
      applyDraftEntries: vi
        .fn()
        .mockReturnValue(of({ weekStartDate: '2026-11-02' } as MealPlanWeekDto)),
    };

    await TestBed.configureTestingModule({
      imports: [DraftMealPlanDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: AiKitchenHelperService, useValue: mockAiHelperService },
        { provide: MealPlanService, useValue: mockMealPlanService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(DraftMealPlanDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    localStorage.removeItem(DRAFT_MEAL_PLAN_PREFS_STORAGE_KEY);
    vi.useRealTimers();
  });

  it('defaults open days to Dinner Cook and enables Draft Plan without a prompt', () => {
    expect(component.prompt()).toBe('');
    expect(component.defaultServings()).toBe(4);
    expect(component.onDeckCount()).toBe(0);
    expect(component.pairingAnalysis().cookCount).toBe(7);
    expect(component.pairingAnalysis().leftoverCount).toBe(0);

    const submitBtn = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      '.draft-submit-btn',
    );
    expect(submitBtn?.disabled).toBe(false);
  });

  it('auto-skips already-scheduled slots and persists grid preferences across reopens', async () => {
    component.toggleSlotRow('Dinner');
    expect(component.pairingAnalysis().cookCount).toBe(0);

    component.cycleCellMode('2026-11-02', 'Lunch');
    component.cycleCellMode('2026-11-02', 'Dinner');
    component.cycleCellMode('2026-11-02', 'Dinner'); // leftovers on same-day Dinner
    component.adjustCellServings('2026-11-02', 'Dinner', -2);
    component.adjustOnDeckCount(1);

    expect(component.pairingAnalysis().cookCount).toBe(1);
    expect(component.pairingAnalysis().leftoverCount).toBe(1);
    expect(component.pairingAnalysis().onDeckCount).toBe(1);

    // Re-create dialog with an already scheduled meal on 2026-11-03 Lunch
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [DraftMealPlanDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            weekStartDate: '2026-11-02',
            existingWeekData: {
              weekStartDate: '2026-11-01',
              weekEndDate: '2026-11-07',
              onDeckEntries: [],
              days: [
                {
                  date: '2026-11-02',
                  dayOffset: 1,
                  entries: [
                    {
                      id: 'e-existing',
                      weekStartDate: '2026-11-01',
                      isOnDeck: false,
                      scheduledDate: '2026-11-02',
                      slot: 'Lunch',
                      sortOrder: 0,
                      entryType: 'Recipe',
                      isLeftovers: false,
                      targetServings: 4,
                      displayTitle: 'Existing Lunch',
                      customTitle: null,
                      notes: null,
                      sourceMealPlanEntryId: null,
                      recipe: null,
                      createdAt: '2026-11-01T00:00:00Z',
                      createdBy: 'u',
                      modifiedAt: '2026-11-01T00:00:00Z',
                      modifiedBy: 'u',
                    },
                  ],
                },
              ],
            },
          },
        },
        { provide: MatSnackBar, useValue: mockSnackBar },
        { provide: AiKitchenHelperService, useValue: mockAiHelperService },
        { provide: MealPlanService, useValue: mockMealPlanService },
      ],
    }).compileComponents();

    const reopened = TestBed.createComponent(DraftMealPlanDialogComponent).componentInstance;
    // Occupied 2026-11-02 Lunch is auto-skipped, and its dependent 2026-11-02 Dinner leftover is cleaned up so dialog stays valid
    expect(reopened.getCell('2026-11-02', 'Lunch').mode).toBe('off');
    expect(reopened.getCell('2026-11-02', 'Dinner').mode).toBe('off');
    expect(reopened.onDeckCount()).toBe(1);
    expect(reopened.pairingAnalysis().orphanedLeftovers.length).toBe(0);
  });

  it('toggles whole rows and columns and pairs leftovers to nearest preceding cook in same meal group', () => {
    component.toggleSlotRow('Dinner'); // turn off all Dinners
    component.cycleCellMode('2026-11-02', 'Breakfast'); // Mon Breakfast = Cook
    component.cycleCellMode('2026-11-02', 'Dinner'); // Mon Dinner = Cook
    component.cycleCellMode('2026-11-03', 'Breakfast'); // Tue Breakfast = Cook -> Leftovers
    component.cycleCellMode('2026-11-03', 'Breakfast');
    component.cycleCellMode('2026-11-03', 'Lunch'); // Tue Lunch = Cook -> Leftovers
    component.cycleCellMode('2026-11-03', 'Lunch');

    const analysis = component.pairingAnalysis();
    expect(analysis.cookCount).toBe(2);
    expect(analysis.leftoverCount).toBe(2);
    // Tue Breakfast leftovers pairs with Mon Breakfast (not Mon Dinner)
    expect(analysis.cookTargetLabelByLeftoverKey.get('2026-11-03:Breakfast')).toContain(
      'Mon Bkfst',
    );
    // Tue Lunch leftovers pairs with Mon Dinner (same main meal group)
    expect(analysis.cookTargetLabelByLeftoverKey.get('2026-11-03:Lunch')).toContain('Mon Dinner');

    // Turning off Mon entire column creates orphaned leftovers and blocks Submit
    component.toggleDayColumn('2026-11-02');
    expect(component.pairingAnalysis().orphanedLeftovers.length).toBe(2);
    expect(component.canSubmitDraft()).toBe(false);
  });

  it('submits structured requestedSlots including On-Deck meals and custom per-cell servings', () => {
    component.toggleSlotRow('Dinner');
    component.updateDefaultServings(2);
    component.cycleCellMode('2026-11-02', 'Dinner');
    component.cycleCellMode('2026-11-03', 'Lunch');
    component.cycleCellMode('2026-11-03', 'Lunch'); // Leftovers from 2026-11-02 Dinner
    component.adjustCellServings('2026-11-03', 'Lunch', -1); // 1 serving for lunch leftover
    component.adjustOnDeckCount(1);

    component.selectPreset('Cozy comfort food');
    component.submitDraft();

    expect(mockAiHelperService.draftWeeklyMealPlan).toHaveBeenCalledWith(
      expect.objectContaining({
        weekStartDate: '2026-11-02',
        prompt: 'Cozy comfort food',
        mealCount: 2,
        servingsPerMeal: 2,
        todayDate: '2026-11-01',
        requestedSlots: [
          {
            isOnDeck: false,
            scheduledDate: '2026-11-02',
            slot: 'Dinner',
            servings: 2,
            leftovers: [{ scheduledDate: '2026-11-03', slot: 'Lunch', servings: 1 }],
          },
          {
            isOnDeck: true,
            scheduledDate: null,
            slot: 'Dinner',
            servings: 2,
            leftovers: [],
          },
        ],
      }),
    );
  });

  it('handles draft errors gracefully', () => {
    mockAiHelperService.draftWeeklyMealPlan.mockReturnValue(
      throwError(() => ({ error: { detail: 'LLM host unavailable' } })),
    );

    component.prompt.set('Some prompt');
    component.submitDraft();

    expect(component.isLoading()).toBe(false);
    expect(component.errorMessage()).toBe('LLM host unavailable');
  });

  it('reassigns leftover source between earlier cook meals and recalculates cook servings', () => {
    mockAiHelperService.draftWeeklyMealPlan.mockReturnValue(
      of({
        ...mockDraftResponse,
        candidates: [
          {
            ...mockDraftResponse.candidates[0],
            scheduledDate: '2026-11-03',
            slot: 'Dinner',
            cookServings: 2,
            targetServings: 6,
            leftoverSlots: [
              { scheduledDate: '2026-11-04', slot: 'Lunch', servings: 2 },
              { scheduledDate: '2026-11-06', slot: 'Dinner', servings: 2 },
            ],
          },
          {
            ...mockDraftResponse.candidates[1],
            scheduledDate: '2026-11-05',
            slot: 'Dinner',
            cookServings: 2,
            targetServings: 2,
            leftoverSlots: [],
          },
        ],
      }),
    );

    component.submitDraft();
    const [first, second] = component.candidateItems();
    expect(component.getTotalCookServings(first)).toBe(6);
    expect(component.getTotalCookServings(second)).toBe(2);

    const friLeftover = component
      .leftoverAssignments()
      .find((l) => l.scheduledDate === '2026-11-06')!;
    const eligible = component.getEligibleCookSourcesForLeftover(friLeftover);
    expect(eligible.map((e) => e.id)).toEqual([first.id, second.id]);

    component.reassignLeftoverSource(friLeftover.id, second.id);
    expect(component.getTotalCookServings(first)).toBe(4);
    expect(component.getTotalCookServings(second)).toBe(4);

    component.applySelectedMeals();
    const applied = mockMealPlanService.applyDraftEntries.mock.calls[0][0];
    expect(applied.entries).toHaveLength(4);
    expect(applied.entries[0].targetServings).toBe(4);
    expect(applied.entries[1].sourceDraftEntryIndex).toBe(0);
    expect(applied.entries[2].targetServings).toBe(4);
    expect(applied.entries[3].sourceDraftEntryIndex).toBe(2);
  });

  it('retries only one recipe and preserves selection, date, servings and leftover links', () => {
    mockAiHelperService.draftWeeklyMealPlan.mockReturnValue(
      of({
        ...mockDraftResponse,
        candidates: [
          {
            ...mockDraftResponse.candidates[0],
            cookServings: 2,
            targetServings: 4,
            leftoverSlots: [{ scheduledDate: '2026-11-04', slot: 'Lunch', servings: 2 }],
          },
          mockDraftResponse.candidates[1],
        ],
      }),
    );

    component.submitDraft();
    const [original, other] = component.candidateItems();
    component.updateServings(original.id, 3);
    component.toggleCandidate(other.id);
    const before = component.candidateItems();
    const pending = new Subject<DraftMealPlanResponseDto>();
    mockAiHelperService.draftWeeklyMealPlan.mockReturnValue(pending);

    component.retryCandidate(original.id);
    expect(component.retryingCandidateId()).toBe(original.id);
    expect(component.isBusy()).toBe(true);
    expect(mockAiHelperService.draftWeeklyMealPlan).toHaveBeenLastCalledWith(
      expect.objectContaining({
        mealCount: 1,
        excludedRecipeIds: ['rec-1', 'rec-2'],
        requestedSlots: [
          expect.objectContaining({
            isOnDeck: false,
            scheduledDate: '2026-11-03',
            slot: 'Dinner',
            servings: 3,
          }),
        ],
      }),
    );

    pending.next({
      ...mockDraftResponse,
      candidates: [
        {
          ...mockDraftResponse.candidates[0],
          recipeId: 'rec-3',
          recipe: { ...mockDraftResponse.candidates[0].recipe, id: 'rec-3', title: 'New Dinner' },
        },
      ],
    });

    expect(component.candidateItems()[1]).toEqual(before[1]);
    expect(component.candidateItems()[0].recipeId).toBe('rec-3');
    expect(component.getTotalCookServings(component.candidateItems()[0])).toBe(5);
    expect(component.retryingCandidateId()).toBeNull();
  });

  it('preserves candidates and shows an error when individual retry fails', () => {
    component.submitDraft();
    const before = component.candidateItems();
    mockAiHelperService.draftWeeklyMealPlan.mockReturnValue(
      throwError(() => ({
        error: { detail: 'No alternatives remain' },
      })),
    );
    component.retryCandidate(before[0].id);
    expect(component.candidateItems()).toEqual(before);
    expect(component.errorMessage()).toBe('No alternatives remain');
    expect(component.isBusy()).toBe(false);
  });

  it('validates invalid dates/servings and refuses applying past dates', () => {
    component.submitDraft();
    const item = component.candidateItems()[0];
    component.updateServings(item.id, 0);
    expect(component.hasInvalidSelection()).toBe(true);
    component.applySelectedMeals();
    expect(mockMealPlanService.applyDraftEntries).not.toHaveBeenCalled();

    component.updateServings(item.id, 2);
    expect(component.hasInvalidSelection()).toBe(false);

    vi.setSystemTime(new Date(2026, 10, 9, 12));
    component.applySelectedMeals();
    expect(mockMealPlanService.applyDraftEntries).not.toHaveBeenCalled();
    expect(component.errorMessage()).toContain('past dates');
  });

  it('displays unmet craving notes banner when returned by the backend', () => {
    mockAiHelperService.draftWeeklyMealPlan.mockReturnValue(
      of({
        ...mockDraftResponse,
        unmetCravingNotes: ['No authentic Thai curry recipes matched in your cookbook.'],
      }),
    );

    component.prompt.set('Thai curry and cozy chili');
    component.submitDraft();
    fixture.detectChanges();

    const banner = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="unmet-cravings-banner"]',
    );
    expect(banner).not.toBeNull();
    expect(banner?.textContent).toContain('Thai curry');
  });
});
