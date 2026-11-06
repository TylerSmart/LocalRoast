import { FormArray, FormControl, FormGroup } from '@angular/forms';
import {
  buildStepAmounts,
  createStepAmounts,
  getStepAmounts,
  remapStepIngredients,
  syncStepAmounts,
  toggleStepIngredient,
} from './recipe-measurement-forms';

describe('recipe measurement forms', () => {
  function step() {
    return new FormGroup({
      ingredientIndices: new FormControl([0, 2], { nonNullable: true }),
      ingredientAmounts: createStepAmounts(
        [0, 2],
        [{ ingredientIndex: 2, measurement: { quantity: 1, unit: 'Cup' } }],
      ),
    });
  }
  it('remaps amounts and legacy references when an ingredient is removed', () => {
    const form = step();
    remapStepIngredients(new FormArray<FormGroup>([form]), (i) => (i === 0 ? null : i - 1));
    expect(form.controls.ingredientIndices.value).toEqual([1]);
    expect(buildStepAmounts(form)).toEqual([
      { ingredientIndex: 1, measurement: { quantity: 1, unit: 'Cup' } },
    ]);
  });
  it('keeps an amount attached to the same ingredient after reordering', () => {
    const form = step();
    remapStepIngredients(new FormArray<FormGroup>([form]), (i) => (i === 2 ? 1 : i === 1 ? 2 : i));
    expect(form.controls.ingredientIndices.value).toEqual([0, 1]);
    expect(buildStepAmounts(form)[0].ingredientIndex).toBe(1);
  });
  it('clears deselected amounts and uses null for newly selected ingredients', () => {
    const form = step();
    toggleStepIngredient(form, 2);
    expect(buildStepAmounts(form)).toEqual([]);
    toggleStepIngredient(form, 1);
    expect(getStepAmounts(form).at(1).controls.measurement.value).toBeNull();
    form.controls.ingredientIndices.setValue([0]);
    syncStepAmounts(form);
    expect(getStepAmounts(form).length).toBe(1);
  });
});
