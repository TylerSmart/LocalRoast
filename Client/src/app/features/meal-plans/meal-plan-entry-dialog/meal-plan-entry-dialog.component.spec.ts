import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { MealPlanEntryDto } from '../../../core/models/meal-plan.models';
import { MealPlanService } from '../../../core/services/meal-plan/meal-plan.service';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { MealPlanEntryDialogComponent } from './meal-plan-entry-dialog.component';

describe('MealPlanEntryDialogComponent', () => {
  let component: MealPlanEntryDialogComponent;
  let fixture: ComponentFixture<MealPlanEntryDialogComponent>;

  const mockDialogRef = {
    close: vi.fn(),
  };

  const mockMealPlanService = {
    createEntry: vi.fn().mockReturnValue(of({ id: 'e-1' } as MealPlanEntryDto)),
    updateEntry: vi.fn().mockReturnValue(of({ id: 'e-1' } as MealPlanEntryDto)),
  };

  const mockRecipeService = {
    getRecipes: vi.fn().mockReturnValue(of([])),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MealPlanEntryDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: {} },
        { provide: MealPlanService, useValue: mockMealPlanService },
        { provide: RecipeService, useValue: mockRecipeService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(MealPlanEntryDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('initializes form with defaults', () => {
    expect(component).toBeTruthy();
    expect(mockRecipeService.getRecipes).toHaveBeenCalled();
  });

  it('adjusts servings stepper', () => {
    component['adjustServings'](1);
    expect(component['form'].controls.targetServings.value).toBe(5);

    component['adjustServings'](-2);
    expect(component['form'].controls.targetServings.value).toBe(3);
  });

  it('submits create entry when valid', () => {
    component['form'].patchValue({
      entryType: 'FreeformNote',
      customTitle: 'Eating Out',
    });

    component['onSubmit']();
    expect(mockMealPlanService.createEntry).toHaveBeenCalled();
    expect(mockDialogRef.close).toHaveBeenCalledWith({ id: 'e-1' });
  });
});
