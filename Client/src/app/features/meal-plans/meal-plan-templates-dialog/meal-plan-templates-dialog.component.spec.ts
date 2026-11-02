import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import {
  MealPlanTemplateDetailDto,
  MealPlanTemplateSummaryDto,
  MealPlanWeekDto,
} from '../../../core/models/meal-plan.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import { MealPlanTemplatesDialogComponent } from './meal-plan-templates-dialog.component';

describe('MealPlanTemplatesDialogComponent', () => {
  let component: MealPlanTemplatesDialogComponent;
  let fixture: ComponentFixture<MealPlanTemplatesDialogComponent>;

  const mockDialogRef = {
    close: vi.fn(),
  };

  const mockDialog = {
    open: vi.fn(),
  };

  const mockTemplates: MealPlanTemplateSummaryDto[] = [
    {
      id: 't-1',
      name: 'Busy Week',
      description: 'Quick meals',
      itemCount: 4,
      scheduledItemCount: 3,
      onDeckItemCount: 1,
      createdAt: '2026-11-02T00:00:00Z',
      modifiedAt: '2026-11-02T00:00:00Z',
    },
  ];

  const mockMealPlanService = {
    getTemplates: vi.fn().mockReturnValue(of(mockTemplates)),
    getTemplateById: vi.fn().mockReturnValue(
      of({
        id: 't-1',
        name: 'Busy Week',
        description: 'Quick meals',
        items: [],
      } as unknown as MealPlanTemplateDetailDto),
    ),
    applyTemplate: vi.fn().mockReturnValue(of({ weekStartDate: '2026-11-02' } as MealPlanWeekDto)),
    deleteTemplate: vi.fn().mockReturnValue(of(undefined)),
    createTemplateFromWeek: vi.fn().mockReturnValue(
      of({
        id: 't-2',
        name: 'New Template',
      } as MealPlanTemplateDetailDto),
    ),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MealPlanTemplatesDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            currentWeekStartDate: '2026-11-02',
            currentWeekLabel: 'Nov 2 – 8, 2026',
            currentWeekEntryCount: 3,
          },
        },
        { provide: MealPlanService, useValue: mockMealPlanService },
        { provide: MatDialog, useValue: mockDialog },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(MealPlanTemplatesDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads and displays templates on init', () => {
    expect(component).toBeTruthy();
    expect(mockMealPlanService.getTemplates).toHaveBeenCalled();
    expect(component['templates']().length).toBe(1);
    expect(component['templates']()[0].name).toBe('Busy Week');
  });

  it('applies template to current week', () => {
    component['applyTemplate']('t-1');
    expect(mockMealPlanService.applyTemplate).toHaveBeenCalledWith('t-1', {
      targetWeekStartDate: '2026-11-02',
      mode: 'Merge',
    });
    expect(mockDialogRef.close).toHaveBeenCalled();
  });

  it('submits save current week when form valid', () => {
    component['saveForm'].patchValue({
      name: 'Autumn Comforts',
      description: 'Soups and bakes',
    });

    component['onSaveCurrentWeek']();
    expect(mockMealPlanService.createTemplateFromWeek).toHaveBeenCalledWith({
      weekStartDate: '2026-11-02',
      name: 'Autumn Comforts',
      description: 'Soups and bakes',
      includeOnDeck: true,
    });
  });
});
