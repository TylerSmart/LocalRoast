import { Component, DestroyRef, forwardRef, inject, input } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  ControlValueAccessor,
  FormBuilder,
  NG_VALIDATORS,
  NG_VALUE_ACCESSOR,
  ReactiveFormsModule,
  ValidationErrors,
  Validator,
  Validators,
} from '@angular/forms';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import {
  MEASUREMENT_UNITS,
  MeasurementUnit,
  RecipeMeasurement,
} from '../../../core/models/recipe.models';

@Component({
  selector: 'app-measurement-editor',
  imports: [
    ReactiveFormsModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
  ],
  templateUrl: './measurement-editor.component.html',
  styleUrl: './measurement-editor.component.scss',
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => MeasurementEditorComponent),
      multi: true,
    },
    {
      provide: NG_VALIDATORS,
      useExisting: forwardRef(() => MeasurementEditorComponent),
      multi: true,
    },
  ],
})
export class MeasurementEditorComponent implements ControlValueAccessor, Validator {
  readonly label = input('Use alternative measurement');
  readonly hint = input(
    'Enter a source-provided or recipe-specific equivalent, not an assumed density.',
  );
  readonly units = MEASUREMENT_UNITS;
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  readonly form = this.fb.group({
    enabled: this.fb.nonNullable.control(false),
    quantity: this.fb.control<number | null>(null, [
      Validators.required,
      Validators.min(0.0001),
      Validators.max(999999.9999),
    ]),
    maxQuantity: this.fb.control<number | null>(null, [
      Validators.min(0.0001),
      Validators.max(999999.9999),
    ]),
    unit: this.fb.control<MeasurementUnit | null>(null, Validators.required),
    customUnitName: this.fb.nonNullable.control('', Validators.maxLength(50)),
  });
  private onChange?: (value: RecipeMeasurement | null) => void;
  private onTouched?: () => void;
  private onValidatorChange?: () => void;

  constructor() {
    this.form.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      const value = this.form.getRawValue();
      this.onChange?.(
        value.enabled
          ? {
              quantity: value.quantity,
              maxQuantity: value.maxQuantity,
              unit: value.unit,
              customUnitName: value.unit === 'Custom' ? value.customUnitName.trim() : null,
            }
          : null,
      );
      this.onValidatorChange?.();
    });
  }

  writeValue(value: RecipeMeasurement | null): void {
    this.form.patchValue(
      {
        enabled: value !== null,
        quantity: value?.quantity ?? null,
        maxQuantity: value?.maxQuantity ?? null,
        unit: value?.unit ?? null,
        customUnitName: value?.customUnitName ?? '',
      },
      { emitEvent: false },
    );
  }

  registerOnChange(fn: (value: RecipeMeasurement | null) => void): void {
    this.onChange = fn;
  }
  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }
  registerOnValidatorChange(fn: () => void): void {
    this.onValidatorChange = fn;
  }
  setDisabledState(disabled: boolean): void {
    if (disabled) this.form.disable({ emitEvent: false });
    else this.form.enable({ emitEvent: false });
  }
  markTouched(): void {
    this.onTouched?.();
  }

  validate(): ValidationErrors | null {
    const value = this.form.getRawValue();
    if (!value.enabled) return null;
    return this.form.invalid ||
      (value.maxQuantity !== null &&
        value.quantity !== null &&
        value.maxQuantity < value.quantity) ||
      (value.unit === 'Custom' && !value.customUnitName.trim())
      ? { measurement: true }
      : null;
  }
}
