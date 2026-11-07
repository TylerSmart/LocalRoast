import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ValidationErrors,
} from '@angular/forms';
import {
  RecipeMeasurement,
  RecipeStepIngredientAmountInput,
  RecipeStepInlineAmountInput,
} from '../models/recipe.models';

interface StepAmountControls {
  ingredientIndex: FormControl<number>;
  measurement: FormControl<RecipeMeasurement | null>;
}
export type StepAmountForm = FormGroup<StepAmountControls>;

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

export function createStepAmount(
  ingredientIndex: number,
  measurement: RecipeMeasurement | null = null,
): StepAmountForm {
  return new FormGroup({
    ingredientIndex: new FormControl(ingredientIndex, { nonNullable: true }),
    measurement: new FormControl<RecipeMeasurement | null>(measurement),
  });
}

export function createStepAmounts(
  indices: number[],
  amounts: RecipeStepIngredientAmountInput[] = [],
): FormArray<StepAmountForm> {
  const allIndices = [...new Set([...indices, ...amounts.map((a) => a.ingredientIndex)])];
  return new FormArray(
    allIndices.map((index) =>
      createStepAmount(index, amounts.find((a) => a.ingredientIndex === index)?.measurement),
    ),
  );
}

export function getStepAmounts(step: FormGroup): FormArray<StepAmountForm> {
  const control = step.get('ingredientAmounts');
  if (control instanceof FormArray) return control;
  const amounts = createStepAmounts(step.get('ingredientIndices')?.value ?? []);
  step.addControl('ingredientAmounts', amounts);
  return amounts;
}

export function buildStepAmounts(step: FormGroup): RecipeStepIngredientAmountInput[] {
  const indices: number[] = step.get('ingredientIndices')?.value ?? [];
  return getStepAmounts(step).controls.flatMap((row) => {
    const { ingredientIndex, measurement } = row.getRawValue();
    return measurement && indices.includes(ingredientIndex)
      ? [{ ingredientIndex, measurement }]
      : [];
  });
}

export function syncStepAmounts(step: FormGroup): void {
  const indices: number[] = step.get('ingredientIndices')?.value ?? [];
  const amounts = getStepAmounts(step);
  for (let i = amounts.length - 1; i >= 0; i--) {
    if (!indices.includes(amounts.at(i).controls.ingredientIndex.value)) amounts.removeAt(i);
  }
  for (const index of indices) {
    if (!amounts.controls.some((a) => a.controls.ingredientIndex.value === index))
      amounts.push(createStepAmount(index));
  }
}

export function toggleStepIngredient(step: FormGroup, index: number): void {
  const current: number[] = step.get('ingredientIndices')?.value ?? [];
  const amounts = getStepAmounts(step);
  if (current.includes(index)) {
    step.patchValue({ ingredientIndices: current.filter((i) => i !== index) });
    const row = amounts.controls.findIndex((a) => a.controls.ingredientIndex.value === index);
    if (row >= 0) amounts.removeAt(row);
  } else {
    step.patchValue({ ingredientIndices: [...current, index] });
    amounts.push(createStepAmount(index));
  }
}

export function remapStepIngredients(
  steps: FormArray<FormGroup>,
  mapIndex: (index: number) => number | null,
): void {
  for (const step of steps.controls) {
    const amounts = getStepAmounts(step);
    const indices: number[] = step.get('ingredientIndices')?.value ?? [];
    step.patchValue({
      ingredientIndices: indices.map(mapIndex).filter((i): i is number => i !== null),
    });
    for (let i = amounts.length - 1; i >= 0; i--) {
      const row = amounts.at(i);
      const mapped = mapIndex(row.controls.ingredientIndex.value);
      if (mapped === null) amounts.removeAt(i);
      else row.controls.ingredientIndex.setValue(mapped);
    }
    const inline = step.get('inlineAmounts');
    const spans: RecipeStepInlineAmountInput[] | null = inline?.value ?? null;
    if (inline && spans?.length) {
      inline.setValue(
        spans.map((span) => {
          if (span.kind !== 'Ingredient' || span.ingredientIndex === null) return span;
          const mapped = mapIndex(span.ingredientIndex);
          return mapped === null
            ? { ...span, kind: 'Fixed' as const, ingredientIndex: null }
            : { ...span, ingredientIndex: mapped };
        }),
      );
    }
  }
}
