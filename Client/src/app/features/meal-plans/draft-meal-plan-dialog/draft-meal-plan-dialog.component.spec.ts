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
        rationale: 'Quick fresh weeknight dinner',
      },
    ],
  };

  beforeEach(async () => {
    vi.setSystemTime(new Date(2026, 10, 1, 12));
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
  afterEach(() => vi.useRealTimers());

  it('initializes with default values', () => {
    expect(component.prompt()).toBe('');
    expect(component.mealCount()).toBe(4);
    expect(component.defaultSlot()).toBe('Dinner');
    expect(component.placeOnDeck()).toBe(false);
    expect(component.candidateItems().length).toBe(0);
  });

  it('sets prompt when selecting a preset chip', () => {
    component.selectPreset('Quick weeknight dinners (<30 min)');
    expect(component.prompt()).toBe('Quick weeknight dinners (<30 min)');
  });

  it('calls aiHelper.draftWeeklyMealPlan on submit and populates candidates', () => {
    component.selectPreset('Cozy comfort food');
    component.submitDraft();

    expect(mockAiHelperService.draftWeeklyMealPlan).toHaveBeenCalledWith({
      weekStartDate: '2026-11-02',
      prompt: 'Cozy comfort food',
      mealCount: 4,
      defaultSlot: 'Dinner',
      placeOnDeck: false,
      servingsPerMeal: 4,
      todayDate: '2026-11-01',
    });

    expect(component.candidateItems().length).toBe(2);
    expect(component.candidateItems()[0].recipe.title).toBe('Beef Chili');
    expect(component.selectedCount()).toBe(2);
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

  it('shows the proposed meal slot and keeps selection edits in sync', async () => {
    component.prompt.set('Quick dinners');
    component.submitDraft();
    fixture.detectChanges();
    await fixture.whenStable();

    const select = (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
      '.candidate-slot-select',
    )!;
    expect(select.value).toBe('Dinner');

    select.value = 'Lunch';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(component.candidateItems()[0].slot).toBe('Lunch');
  });

  it('toggles candidate selection and updates selectedCount', () => {
    component.prompt.set('Quick weeknight dinners');
    component.submitDraft();
    expect(component.selectedCount()).toBe(2);

    const firstId = component.candidateItems()[0].id;
    component.toggleCandidate(firstId);

    expect(component.candidateItems()[0].selected).toBe(false);
    expect(component.selectedCount()).toBe(1);
  });

  it('clamps a past requested start to today', () => {
    component.data.weekStartDate = '2026-10-01';
    component.prompt.set('Dinner');
    component.submitDraft();
    expect(mockAiHelperService.draftWeeklyMealPlan).toHaveBeenCalledWith(
      expect.objectContaining({
        weekStartDate: '2026-11-01',
        todayDate: '2026-11-01',
      }),
    );
    component.data.weekStartDate = '2026-11-02';
  });

  it('retries only one recipe and preserves all selection, date, servings and leftover settings', () => {
    component.prompt.set('Dinner');
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
        placeOnDeck: true,
        excludedRecipeIds: ['rec-1', 'rec-2'],
      }),
    );
    component.applySelectedMeals();
    expect(mockMealPlanService.applyDraftEntries).not.toHaveBeenCalled();
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
    expect(component.candidateItems()[0]).toEqual({
      ...before[0],
      recipeId: 'rec-3',
      recipe: { ...mockDraftResponse.candidates[0].recipe, id: 'rec-3', title: 'New Dinner' },
    });
    expect(component.retryingCandidateId()).toBeNull();
  });

  it('preserves candidates and shows an error when individual retry fails', () => {
    component.prompt.set('Dinner');
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

  it('applies linked leftovers once and scales cooking servings when household portions change', () => {
    mockAiHelperService.draftWeeklyMealPlan.mockReturnValue(
      of({
        ...mockDraftResponse,
        candidates: [
          {
            ...mockDraftResponse.candidates[0],
            targetServings: 12,
            leftoverDates: ['2026-11-04', '2026-11-05'],
          },
        ],
      }),
    );
    component.prompt.set('Dinner');
    component.submitDraft();
    const item = component.candidateItems()[0];
    expect(component.selectedMealCount()).toBe(3);
    fixture.detectChanges();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('.apply-draft-btn')?.textContent,
    ).toContain('Add Selected Meals (3)');
    component.updateServings(item.id, 2);
    expect(component.candidateItems()[0].targetServings).toBe(6);
    component.updateScheduledDate(item.id, '2026-10-31');
    expect(component.candidateItems()[0].scheduledDate).toBe('2026-11-03');
    component.updateScheduledDate(item.id, '2026-11-05');
    expect(component.candidateItems()[0].scheduledDate).toBe('2026-11-03');
    component.applySelectedMeals();
    expect(mockMealPlanService.applyDraftEntries).not.toHaveBeenCalled();
    component.updateScheduledDate(item.id, '2026-11-03');
    component.applySelectedMeals();
    const applied = mockMealPlanService.applyDraftEntries.mock.calls[0][0];
    expect(applied.entries).toHaveLength(3);
    expect(applied.entries[0].targetServings).toBe(6);
    expect(applied.entries[1]).toEqual(
      expect.objectContaining({
        entryType: 'Leftovers',
        recipeId: 'rec-1',
        targetServings: 2,
        scheduledDate: '2026-11-04',
        sourceDraftEntryIndex: 0,
      }),
    );
    expect(applied.entries[2].sourceDraftEntryIndex).toBe(0);
  });

  it('does not apply stale servings after an invalid edit', () => {
    component.prompt.set('Dinner');
    component.submitDraft();
    const item = component.candidateItems()[0];
    component.updateServings(item.id, 0);
    component.applySelectedMeals();
    expect(mockMealPlanService.applyDraftEntries).not.toHaveBeenCalled();
    expect(component.hasInvalidSelection()).toBe(true);
    component.updateServings(item.id, 2);
    expect(component.hasInvalidSelection()).toBe(false);
  });

  it('refuses to apply dates that have become past while the dialog was open', () => {
    component.prompt.set('Dinner');
    component.submitDraft();
    vi.setSystemTime(new Date(2026, 10, 9, 12));
    component.applySelectedMeals();
    expect(mockMealPlanService.applyDraftEntries).not.toHaveBeenCalled();
    expect(component.errorMessage()).toContain('past dates');
  });

  it('applies selected meals, calls mealPlanService.applyDraftEntries, and closes dialog', () => {
    component.prompt.set('Quick weeknight dinners');
    component.submitDraft();
    // Deselect second item
    const secondId = component.candidateItems()[1].id;
    component.toggleCandidate(secondId);

    component.applySelectedMeals();

    expect(mockMealPlanService.applyDraftEntries).toHaveBeenCalledWith({
      weekStartDate: '2026-11-02',
      entries: [
        {
          entryType: 'Recipe',
          isOnDeck: false,
          weekStartDate: '2026-11-02',
          scheduledDate: '2026-11-03',
          slot: 'Dinner',
          recipeId: 'rec-1',
          targetServings: 4,
        },
      ],
    });

    expect(mockSnackBar.open).toHaveBeenCalledWith('Added 1 meal to plan!', 'Dismiss', {
      duration: 3000,
    });
    expect(mockDialogRef.close).toHaveBeenCalled();
  });
});
