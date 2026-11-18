import { AbstractControl, ValidationErrors } from '@angular/forms';

export function ingredientMeasurementValidator(control: AbstractControl): ValidationErrors | null {
  if (!control.get('alternativeMeasurement')?.value) return null;
  const quantity = Number(control.get('quantity')?.value);
  const unit = control.get('unit')?.value;
  return !Number.isFinite(quantity) ||
    quantity < 0.0001 ||
    !unit ||
    (unit === 'Custom' && !control.get('customUnitName')?.value?.trim())
    ? { primaryMeasurement: true }
    : null;
}
