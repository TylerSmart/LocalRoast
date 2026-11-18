import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import {
  DimensionUnit,
  StepDialogIngredientOption,
  StepDimensionToken,
  StepFixedToken,
  StepIngredientToken,
  StepMixtureToken,
  StepTemperatureToken,
  StepTimerToken,
  StepToken,
  StepTokenType,
} from '../../../core/models/step-token.models';
import {
  celsiusToFahrenheit,
  fahrenheitToCelsius,
  serializeStepToken,
} from '../../../core/utils/step-token-parser';

export interface StepTokenDialogData {
  mode: StepTokenType;
  token?: StepToken | null;
  availableIngredients?: string[];
  availableIngredientOptions?: StepDialogIngredientOption[];
}

export interface StepTokenDialogResult {
  token?: StepToken;
  rawToken?: string;
  delete?: boolean;
}

export const COMMON_UNITS = [
  'tsp',
  'tbsp',
  'cup',
  'fl_oz',
  'pt',
  'qt',
  'gal',
  'oz',
  'lb',
  'g',
  'kg',
  'ml',
  'l',
  'pinch',
  'dash',
  'clove',
  'can',
  'bunch',
  'slice',
  'whole',
];

export const DIMENSION_UNITS: readonly { value: DimensionUnit; label: string }[] = [
  { value: 'inch', label: 'inch / inches' },
  { value: 'foot', label: 'foot / feet' },
  { value: 'yard', label: 'yard / yards' },
  { value: 'mm', label: 'millimeter (mm)' },
  { value: 'cm', label: 'centimeter (cm)' },
  { value: 'm', label: 'meter (m)' },
];

@Component({
  selector: 'app-step-token-dialog',
  imports: [
    ReactiveFormsModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatAutocompleteModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
    MatSelectModule,
  ],
  templateUrl: './step-token-dialog.component.html',
  styleUrl: './step-token-dialog.component.scss',
})
export class StepTokenDialogComponent implements OnInit {
  protected readonly data = inject<StepTokenDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<StepTokenDialogComponent>);
  private readonly fb = inject(FormBuilder);

  protected readonly mode = signal<StepTokenType>(this.data?.mode ?? 'ingredient');
  protected readonly commonUnits = COMMON_UNITS;
  protected readonly dimensionUnits = DIMENSION_UNITS;
  protected readonly availableIngredients = this.data?.availableIngredients ?? [];
  protected readonly ingredientOptions: StepDialogIngredientOption[] =
    this.data?.availableIngredientOptions && this.data.availableIngredientOptions.length > 0
      ? this.data.availableIngredientOptions
      : this.availableIngredients.map((displayName, index) => ({
          index,
          displayName,
        }));
  protected readonly selectedOption = signal<StepDialogIngredientOption | null>(null);

  protected ingredientForm!: FormGroup;
  protected mixtureForm!: FormGroup;
  protected fixedForm!: FormGroup;
  protected dimensionForm!: FormGroup;
  protected timerForm!: FormGroup;
  protected temperatureForm!: FormGroup;

  ngOnInit(): void {
    const existing = this.data?.token;

    if (this.mode() === 'ingredient') {
      const ingToken = existing as StepIngredientToken | undefined;

      const matchedOption =
        this.ingredientOptions.find(
          (o) =>
            (ingToken?.ingredientId && o.id === ingToken.ingredientId) ||
            (ingToken?.ingredient &&
              o.displayName.trim().toLowerCase() === ingToken.ingredient.trim().toLowerCase()),
        ) ?? null;
      this.selectedOption.set(matchedOption);

      this.ingredientForm = this.fb.group({
        ingredient: [ingToken?.ingredient ?? '', [Validators.required]],
        ingredientId: [ingToken?.ingredientId ?? null],
        amount: [ingToken?.amount ?? null, [Validators.required, Validators.min(0.0001)]],
        maxAmount: [ingToken?.maxAmount ?? null],
        unit: [ingToken?.unit ?? ingToken?.customUnitName ?? ''],
        notes: [ingToken?.notes ?? ''],
      });
    } else if (this.mode() === 'mixture') {
      const mixToken = existing as StepMixtureToken | undefined;
      this.mixtureForm = this.fb.group({
        name: [mixToken?.name ?? '', [Validators.required]],
        amount: [mixToken?.amount ?? null, [Validators.required]],
        maxAmount: [mixToken?.maxAmount ?? null],
        unit: [mixToken?.unit ?? mixToken?.customUnitName ?? ''],
      });
    } else if (this.mode() === 'fixed') {
      const fixedToken = existing as StepFixedToken | undefined;
      this.fixedForm = this.fb.group({
        name: [fixedToken?.name ?? ''],
        amount: [fixedToken?.amount ?? null, [Validators.required]],
        maxAmount: [fixedToken?.maxAmount ?? null],
        unit: [fixedToken?.unit ?? fixedToken?.customUnitName ?? ''],
        hyphenated: [fixedToken?.hyphenated ?? false],
      });
    } else if (this.mode() === 'dimension') {
      const dimToken = existing as StepDimensionToken | undefined;
      this.dimensionForm = this.fb.group({
        value: [dimToken?.value ?? null, [Validators.required]],
        maxValue: [dimToken?.maxValue ?? null],
        unit: [dimToken?.unit ?? 'inch', [Validators.required]],
        hyphenated: [dimToken?.hyphenated ?? false],
      });
    } else if (this.mode() === 'timer') {
      const timerToken = existing as StepTimerToken | undefined;
      const totalSecs = timerToken?.seconds ?? 60;
      const mins = Math.floor(totalSecs / 60);
      const secs = totalSecs % 60;
      const maxTotal = timerToken?.maxSeconds ?? null;
      const maxMins = maxTotal != null && maxTotal > totalSecs ? Math.floor(maxTotal / 60) : null;
      const maxSecs = maxTotal != null && maxTotal > totalSecs ? maxTotal % 60 : null;

      this.timerForm = this.fb.group({
        minutes: [mins, [Validators.min(0)]],
        seconds: [secs, [Validators.min(0), Validators.max(59)]],
        maxMinutes: [maxMins, [Validators.min(0)]],
        maxSeconds: [maxSecs, [Validators.min(0), Validators.max(59)]],
        label: [timerToken?.label ?? ''],
      });
    } else {
      const tempToken = existing as StepTemperatureToken | undefined;
      let f = tempToken?.fahrenheit ?? null;
      let c = tempToken?.celsius ?? null;
      if (f != null && c == null) {
        c = fahrenheitToCelsius(f);
      } else if (c != null && f == null) {
        f = celsiusToFahrenheit(c);
      }
      let maxF = tempToken?.maxFahrenheit ?? null;
      let maxC = tempToken?.maxCelsius ?? null;
      if (maxF != null && maxC == null) {
        maxC = fahrenheitToCelsius(maxF);
      } else if (maxC != null && maxF == null) {
        maxF = celsiusToFahrenheit(maxC);
      }
      this.temperatureForm = this.fb.group({
        fahrenheit: [f],
        celsius: [c],
        maxFahrenheit: [maxF],
        maxCelsius: [maxC],
      });
    }
  }

  protected onFahrenheitInput(event?: Event): void {
    const rawVal = event
      ? (event.target as HTMLInputElement).value
      : this.temperatureForm.get('fahrenheit')?.value;
    if (rawVal !== '' && rawVal != null) {
      const f = Number(rawVal);
      if (!isNaN(f)) {
        this.temperatureForm.patchValue({ celsius: fahrenheitToCelsius(f) }, { emitEvent: false });
      }
    } else {
      this.temperatureForm.patchValue({ celsius: null }, { emitEvent: false });
    }
  }

  protected onCelsiusInput(event?: Event): void {
    const rawVal = event
      ? (event.target as HTMLInputElement).value
      : this.temperatureForm.get('celsius')?.value;
    if (rawVal !== '' && rawVal != null) {
      const c = Number(rawVal);
      if (!isNaN(c)) {
        this.temperatureForm.patchValue(
          { fahrenheit: celsiusToFahrenheit(c) },
          { emitEvent: false },
        );
      }
    } else {
      this.temperatureForm.patchValue({ fahrenheit: null }, { emitEvent: false });
    }
  }

  protected onMaxFahrenheitInput(event?: Event): void {
    const rawVal = event
      ? (event.target as HTMLInputElement).value
      : this.temperatureForm.get('maxFahrenheit')?.value;
    if (rawVal !== '' && rawVal != null) {
      const f = Number(rawVal);
      if (!isNaN(f)) {
        this.temperatureForm.patchValue(
          { maxCelsius: fahrenheitToCelsius(f) },
          { emitEvent: false },
        );
      }
    } else {
      this.temperatureForm.patchValue({ maxCelsius: null }, { emitEvent: false });
    }
  }

  protected onMaxCelsiusInput(event?: Event): void {
    const rawVal = event
      ? (event.target as HTMLInputElement).value
      : this.temperatureForm.get('maxCelsius')?.value;
    if (rawVal !== '' && rawVal != null) {
      const c = Number(rawVal);
      if (!isNaN(c)) {
        this.temperatureForm.patchValue(
          { maxFahrenheit: celsiusToFahrenheit(c) },
          { emitEvent: false },
        );
      }
    } else {
      this.temperatureForm.patchValue({ maxFahrenheit: null }, { emitEvent: false });
    }
  }

  protected formatUnitForToken(unit?: string | null, customUnitName?: string | null): string {
    if (!unit) return '';
    if (unit === 'Custom') return customUnitName?.trim() ?? '';
    if (unit === 'FlOz') return 'fl_oz';
    return unit.toLowerCase();
  }

  protected formatIngredientOptionLabel(option: StepDialogIngredientOption): string {
    const qty = option.quantity != null ? String(option.quantity) : '';
    const unitStr = this.formatUnitForToken(option.unit, option.customUnitName);
    if (qty && unitStr) return `${qty} ${unitStr}`;
    if (qty) return qty;
    if (unitStr) return unitStr;
    return '';
  }

  protected getFilteredUnits(query: string | null | undefined): string[] {
    const q = (query ?? '').trim().toLowerCase();
    if (!q || this.commonUnits.includes(q)) {
      return this.commonUnits;
    }
    const matches = this.commonUnits.filter((u) => u.includes(q));
    return matches.length > 0 ? matches : this.commonUnits;
  }

  protected onSelectIngredientOption(option: StepDialogIngredientOption): void {
    this.selectedOption.set(option);
    const patch: Record<string, unknown> = {
      ingredient: option.displayName,
      ingredientId: option.id ?? null,
    };

    const isEditingToken = !!this.data?.token;
    const amountCtrl = this.ingredientForm.get('amount');
    const currentAmount = amountCtrl?.value;
    const hasEmptyAmount =
      currentAmount === null || currentAmount === undefined || currentAmount === '';
    if (!amountCtrl?.dirty && (!isEditingToken || hasEmptyAmount)) {
      patch['amount'] = option.quantity ?? null;
    }

    const unitCtrl = this.ingredientForm.get('unit');
    const currentUnit = String(unitCtrl?.value || '').trim();
    const tokenUnit = this.formatUnitForToken(option.unit, option.customUnitName);
    if (!unitCtrl?.dirty && (!isEditingToken || !currentUnit)) {
      patch['unit'] = tokenUnit;
    }

    this.ingredientForm.patchValue(patch);
  }

  protected onSelectIngredient(name: string): void {
    const match = this.ingredientOptions.find((o) => o.displayName === name);
    if (match) {
      this.onSelectIngredientOption(match);
      return;
    }
    this.ingredientForm.patchValue({ ingredient: name });
  }

  protected onSave(): void {
    const currentMode = this.mode();
    if (currentMode === 'ingredient') {
      if (this.ingredientForm.invalid) return;
      const val = this.ingredientForm.value;
      const token: StepIngredientToken = {
        type: 'ingredient',
        ingredient: String(val.ingredient || '').trim(),
      };

      if (val.ingredientId) {
        token.ingredientId = String(val.ingredientId);
      }
      if (val.amount != null && val.amount !== '') {
        token.amount = Number(val.amount);
      }
      if (
        val.maxAmount != null &&
        val.maxAmount !== '' &&
        Number(val.maxAmount) > (token.amount ?? 0)
      ) {
        token.maxAmount = Number(val.maxAmount);
      }
      if (val.unit) {
        token.unit = String(val.unit).trim();
      }
      if (val.notes) {
        token.notes = String(val.notes).trim();
      }

      this.dialogRef.close({
        token,
        rawToken: serializeStepToken(token),
      } satisfies StepTokenDialogResult);
    } else if (currentMode === 'mixture') {
      if (this.mixtureForm.invalid) return;
      const val = this.mixtureForm.value;
      const amount = Number(val.amount);
      const token: StepMixtureToken = {
        type: 'mixture',
        name: String(val.name || '').trim(),
        amount,
        ...(val.maxAmount != null && val.maxAmount !== '' && Number(val.maxAmount) > amount
          ? { maxAmount: Number(val.maxAmount) }
          : {}),
        ...(val.unit ? { unit: String(val.unit).trim() } : {}),
      };
      this.dialogRef.close({
        token,
        rawToken: serializeStepToken(token),
      } satisfies StepTokenDialogResult);
    } else if (currentMode === 'fixed') {
      if (this.fixedForm.invalid) return;
      const val = this.fixedForm.value;
      const amount = Number(val.amount);
      const token: StepFixedToken = {
        type: 'fixed',
        amount,
        ...(val.maxAmount != null && val.maxAmount !== '' && Number(val.maxAmount) > amount
          ? { maxAmount: Number(val.maxAmount) }
          : {}),
        ...(val.unit ? { unit: String(val.unit).trim() } : {}),
        ...(val.name?.trim() ? { name: String(val.name).trim() } : {}),
        ...(val.hyphenated ? { hyphenated: true } : {}),
      };
      this.dialogRef.close({
        token,
        rawToken: serializeStepToken(token),
      } satisfies StepTokenDialogResult);
    } else if (currentMode === 'dimension') {
      if (this.dimensionForm.invalid) return;
      const val = this.dimensionForm.value;
      const value = Number(val.value);
      const token: StepDimensionToken = {
        type: 'dimension',
        value,
        ...(val.maxValue != null && val.maxValue !== '' && Number(val.maxValue) > value
          ? { maxValue: Number(val.maxValue) }
          : {}),
        unit: val.unit as DimensionUnit,
        ...(val.hyphenated ? { hyphenated: true } : {}),
      };
      this.dialogRef.close({
        token,
        rawToken: serializeStepToken(token),
      } satisfies StepTokenDialogResult);
    } else if (currentMode === 'timer') {
      if (this.timerForm.invalid) return;
      const val = this.timerForm.value;
      const totalSeconds = (Number(val.minutes) || 0) * 60 + (Number(val.seconds) || 0);
      const baseSeconds = totalSeconds > 0 ? totalSeconds : 60;
      const maxTotal = (Number(val.maxMinutes) || 0) * 60 + (Number(val.maxSeconds) || 0);
      const token: StepTimerToken = {
        type: 'timer',
        seconds: baseSeconds,
        ...(maxTotal > baseSeconds ? { maxSeconds: maxTotal } : {}),
        label: val.label?.trim() ? String(val.label).trim() : null,
      };
      this.dialogRef.close({
        token,
        rawToken: serializeStepToken(token),
      } satisfies StepTokenDialogResult);
    } else {
      if (this.temperatureForm.invalid) return;
      const val = this.temperatureForm.value;
      const f = val.fahrenheit != null && val.fahrenheit !== '' ? Number(val.fahrenheit) : null;
      const c = val.celsius != null && val.celsius !== '' ? Number(val.celsius) : null;
      const maxF =
        val.maxFahrenheit != null && val.maxFahrenheit !== '' ? Number(val.maxFahrenheit) : null;
      const maxC = val.maxCelsius != null && val.maxCelsius !== '' ? Number(val.maxCelsius) : null;

      if (f == null && c == null) return;

      const token: StepTemperatureToken = {
        type: 'temperature',
        ...(f != null ? { fahrenheit: f } : { celsius: c! }),
        ...(f != null && maxF != null && maxF > f ? { maxFahrenheit: maxF } : {}),
        ...(f == null && c != null && maxC != null && maxC > c ? { maxCelsius: maxC } : {}),
      };

      this.dialogRef.close({
        token,
        rawToken: serializeStepToken(token),
      } satisfies StepTokenDialogResult);
    }
  }

  protected onDelete(): void {
    this.dialogRef.close({ delete: true });
  }

  protected onCancel(): void {
    this.dialogRef.close();
  }
}
