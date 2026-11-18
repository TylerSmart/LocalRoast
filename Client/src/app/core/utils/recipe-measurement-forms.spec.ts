import { FormControl, FormGroup } from '@angular/forms';
import { ingredientMeasurementValidator } from './recipe-measurement-forms';

describe('recipe measurement forms', () => {
  it('validates primary measurement when alternativeMeasurement is present', () => {
    const valid = new FormGroup({
      quantity: new FormControl(5),
      unit: new FormControl('Cup'),
      customUnitName: new FormControl(''),
      alternativeMeasurement: new FormControl({ quantity: 25, unit: 'Oz' }),
    });
    expect(ingredientMeasurementValidator(valid)).toBeNull();

    const invalidCustom = new FormGroup({
      quantity: new FormControl(1),
      unit: new FormControl('Custom'),
      customUnitName: new FormControl('   '),
      alternativeMeasurement: new FormControl({ quantity: 100, unit: 'G' }),
    });
    expect(ingredientMeasurementValidator(invalidCustom)).toEqual({ primaryMeasurement: true });
  });
});
