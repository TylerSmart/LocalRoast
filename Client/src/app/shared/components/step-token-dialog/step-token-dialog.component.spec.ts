import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { StepTokenDialogComponent, StepTokenDialogData } from './step-token-dialog.component';

describe('StepTokenDialogComponent', () => {
  let component: StepTokenDialogComponent;
  let fixture: ComponentFixture<StepTokenDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };
    const dialogData: StepTokenDialogData = {
      mode: 'ingredient',
      availableIngredients: ['Butter', 'Flour'],
    };

    await TestBed.configureTestingModule({
      imports: [StepTokenDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(StepTokenDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('initializes with ingredient mode and available ingredients', () => {
    expect(component).toBeTruthy();
    expect(component['availableIngredients'].length).toBe(2);
  });

  it('selects quick ingredient into form', () => {
    component['onSelectIngredient']('Butter');
    expect(component['ingredientForm'].get('ingredient')?.value).toBe('Butter');
  });

  it('closes dialog with serialized token on save', () => {
    component['ingredientForm'].patchValue({
      ingredient: 'Eggs',
      amount: 4,
      unit: 'whole',
    });
    component['onSave']();

    expect(mockDialogRef.close).toHaveBeenCalledWith(
      expect.objectContaining({
        rawToken: '||{"ingredient":"Eggs","amount":4,"unit":"whole"}||',
      }),
    );
  });
});
