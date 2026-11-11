import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { StepTokenDialogComponent, StepTokenDialogData } from './step-token-dialog.component';

describe('StepTokenDialogComponent', () => {
  let component: StepTokenDialogComponent;
  let fixture: ComponentFixture<StepTokenDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  async function createComponent(data: StepTokenDialogData) {
    mockDialogRef = { close: vi.fn() };
    await TestBed.configureTestingModule({
      imports: [StepTokenDialogComponent],
      providers: [
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: MAT_DIALOG_DATA, useValue: data },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(StepTokenDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  describe('ingredient mode', () => {
    beforeEach(async () => {
      await createComponent({
        mode: 'ingredient',
        availableIngredients: ['Butter', 'Flour'],
      });
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

  describe('temperature mode', () => {
    beforeEach(async () => {
      await createComponent({
        mode: 'temperature',
        token: {
          type: 'temperature',
          fahrenheit: 350,
        },
      });
    });

    it('initializes temperature form from token and auto-computes celsius', () => {
      expect(component['mode']()).toBe('temperature');
      expect(component['temperatureForm'].get('fahrenheit')?.value).toBe(350);
      expect(component['temperatureForm'].get('celsius')?.value).toBe(177);
    });

    it('syncs celsius when fahrenheit input changes', () => {
      component['temperatureForm'].get('fahrenheit')?.setValue(400);
      component['onFahrenheitInput']();
      expect(component['temperatureForm'].get('celsius')?.value).toBe(204);
    });

    it('syncs fahrenheit when celsius input changes', () => {
      component['temperatureForm'].get('celsius')?.setValue(100);
      component['onCelsiusInput']();
      expect(component['temperatureForm'].get('fahrenheit')?.value).toBe(212);
    });

    it('saves temperature token with fahrenheit value', () => {
      component['temperatureForm'].patchValue({ fahrenheit: 325, celsius: 163 });
      component['onSave']();

      expect(mockDialogRef.close).toHaveBeenCalledWith({
        rawToken: '||{"type":"temperature","fahrenheit":325}||',
        token: {
          type: 'temperature',
          fahrenheit: 325,
        },
      });
    });
  });
});
