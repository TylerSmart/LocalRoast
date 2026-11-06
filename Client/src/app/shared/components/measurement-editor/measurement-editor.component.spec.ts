import { TestBed } from '@angular/core/testing';
import { MeasurementEditorComponent } from './measurement-editor.component';

describe('MeasurementEditorComponent', () => {
  it('round trips a measurement without emitting during writeValue', () => {
    const component = TestBed.createComponent(MeasurementEditorComponent).componentInstance;
    const changed = vi.fn();
    component.registerOnChange(changed);
    component.writeValue({ quantity: 25, unit: 'Oz', maxQuantity: 30 });
    expect(component.validate()).toBeNull();
    expect(changed).not.toHaveBeenCalled();
    component.form.controls.quantity.setValue(20);
    expect(changed).toHaveBeenLastCalledWith({
      quantity: 20,
      maxQuantity: 30,
      unit: 'Oz',
      customUnitName: null,
    });
    component.form.controls.enabled.setValue(false);
    expect(changed).toHaveBeenLastCalledWith(null);
    expect(component.validate()).toBeNull();
  });

  it('validates enabled quantities, ranges, and custom units', () => {
    const component = TestBed.createComponent(MeasurementEditorComponent).componentInstance;
    component.writeValue(null);
    expect(component.validate()).toBeNull();
    component.form.controls.enabled.setValue(true);
    expect(component.validate()).toEqual({ measurement: true });
    component.form.patchValue({ quantity: 2, unit: 'G', maxQuantity: 1 });
    expect(component.validate()).toEqual({ measurement: true });
    component.form.patchValue({ maxQuantity: 3, unit: 'Custom' });
    expect(component.validate()).toEqual({ measurement: true });
    component.form.controls.customUnitName.setValue('packet');
    expect(component.validate()).toBeNull();
  });
});
