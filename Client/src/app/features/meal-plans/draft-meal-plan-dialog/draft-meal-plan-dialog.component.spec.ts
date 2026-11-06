import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
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
