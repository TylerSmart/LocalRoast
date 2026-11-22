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

    it('closes dialog with serialized token on save including fixed flag when checked', () => {
      component['ingredientForm'].patchValue({
        ingredient: 'Butter',
        amount: 1,
        unit: 'tbsp',
        fixed: true,
      });
      component['onSave']();

      expect(mockDialogRef.close).toHaveBeenCalledWith(
        expect.objectContaining({
          rawToken:
            '||{"type":"ingredient","ingredient":"Butter","amount":1,"unit":"tbsp","fixed":true}||',
        }),
      );
    });
  });

  describe('rich ingredient options', () => {
    beforeEach(async () => {
      await createComponent({
        mode: 'ingredient',
        availableIngredientOptions: [
          {
            index: 0,
            id: 'ing-1',
            displayName: 'Unsalted Butter',
            quantity: 4,
            unit: 'Tbsp',
          },
          {
            index: 1,
            displayName: 'Kosher Salt',
            quantity: null,
            unit: 'Custom',
            customUnitName: 'pinches',
          },
        ],
      });
    });

    it('pre-fills ingredientId, amount, and unit when selecting a rich option', () => {
      component['onSelectIngredientOption'](component['ingredientOptions'][0]);
      expect(component['ingredientForm'].get('ingredient')?.value).toBe('Unsalted Butter');
      expect(component['ingredientForm'].get('ingredientId')?.value).toBe('ing-1');
      expect(component['ingredientForm'].get('amount')?.value).toBe(4);
      expect(component['ingredientForm'].get('unit')?.value).toBe('tbsp');

      component['onSave']();
      expect(mockDialogRef.close).toHaveBeenCalledWith(
        expect.objectContaining({
          rawToken:
            '||{"type":"ingredient","ingredient":"Unsalted Butter","ingredientId":"ing-1","amount":4,"unit":"tbsp"}||',
        }),
      );
    });

    it('uses customUnitName when option unit is Custom', () => {
      component['onSelectIngredientOption'](component['ingredientOptions'][1]);
      expect(component['ingredientForm'].get('unit')?.value).toBe('pinches');
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

  describe('mixture, dimension, and fixed modes', () => {
    it('serializes mixture token on save including fixed flag when checked', async () => {
      await createComponent({ mode: 'mixture' });
      component['mixtureForm'].patchValue({
        name: 'herb butter',
        amount: 1,
        maxAmount: 2,
        unit: 'tbsp',
        fixed: true,
      });
      component['onSave']();

      expect(mockDialogRef.close).toHaveBeenCalledWith({
        rawToken:
          '||{"type":"mixture","name":"herb butter","amount":1,"maxAmount":2,"unit":"tbsp","fixed":true}||',
        token: {
          type: 'mixture',
          name: 'herb butter',
          amount: 1,
          maxAmount: 2,
          unit: 'tbsp',
          fixed: true,
        },
      });
    });

    it('serializes dimension token with hyphenated flag and range on save', async () => {
      await createComponent({ mode: 'dimension' });
      component['dimensionForm'].patchValue({
        value: 9,
        maxValue: 13,
        unit: 'inch',
        hyphenated: true,
      });
      component['onSave']();

      expect(mockDialogRef.close).toHaveBeenCalledWith({
        rawToken:
          '||{"type":"dimension","value":9,"maxValue":13,"unit":"inch","hyphenated":true}||',
        token: {
          type: 'dimension',
          value: 9,
          maxValue: 13,
          unit: 'inch',
          hyphenated: true,
        },
      });
    });

    it('serializes fixed token on save', async () => {
      await createComponent({ mode: 'fixed' });
      component['fixedForm'].patchValue({
        amount: 12,
        maxAmount: null,
        unit: 'cup',
        name: 'Bundt pan',
        hyphenated: true,
      });
      component['onSave']();

      expect(mockDialogRef.close).toHaveBeenCalledWith({
        rawToken:
          '||{"type":"fixed","amount":12,"unit":"cup","name":"Bundt pan","hyphenated":true}||',
        token: {
          type: 'fixed',
          amount: 12,
          unit: 'cup',
          name: 'Bundt pan',
          hyphenated: true,
        },
      });
    });
  });
});
