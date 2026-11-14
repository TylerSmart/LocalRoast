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
import {
  StepDialogIngredientOption,
  StepIngredientToken,
  StepTemperatureToken,
  StepTimerToken,
  StepToken,
} from '../../../core/models/step-token.models';
import {
  celsiusToFahrenheit,
  fahrenheitToCelsius,
  serializeStepToken,
} from '../../../core/utils/step-token-parser';

export interface StepTokenDialogData {
  mode: 'ingredient' | 'timer' | 'temperature';
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
  ],
  templateUrl: './step-token-dialog.component.html',
  styleUrl: './step-token-dialog.component.scss',
})
export class StepTokenDialogComponent implements OnInit {
  protected readonly data = inject<StepTokenDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<StepTokenDialogComponent>);
  private readonly fb = inject(FormBuilder);

  protected readonly mode = signal<'ingredient' | 'timer' | 'temperature'>(
    this.data?.mode ?? 'ingredient',
  );
  protected readonly commonUnits = COMMON_UNITS;
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
  protected timerForm!: FormGroup;
  protected temperatureForm!: FormGroup;
  protected readonly showDisplayAmount = signal<boolean>(false);

  ngOnInit(): void {
    const existing = this.data?.token;

    if (this.mode() === 'ingredient') {
      const ingToken = existing as StepIngredientToken | undefined;
      const hasDisplay = ingToken?.displayAmount != null;
      this.showDisplayAmount.set(hasDisplay);

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
        amount: [ingToken?.amount ?? null],
        unit: [ingToken?.unit ?? ''],
        useDisplayAmount: [hasDisplay],
        displayAmount: [ingToken?.displayAmount ?? null],
        displayUnit: [ingToken?.displayUnit ?? ''],
        notes: [ingToken?.notes ?? ''],
      });
    } else if (this.mode() === 'timer') {
      const timerToken = existing as StepTimerToken | undefined;
      const totalSecs = timerToken?.seconds ?? 60;
      const mins = Math.floor(totalSecs / 60);
      const secs = totalSecs % 60;

      this.timerForm = this.fb.group({
        minutes: [mins, [Validators.min(0)]],
        seconds: [secs, [Validators.min(0), Validators.max(59)]],
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
      this.temperatureForm = this.fb.group({
        fahrenheit: [f],
        celsius: [c],
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

  protected onToggleDisplayAmount(checked: boolean): void {
    this.showDisplayAmount.set(checked);
    if (!checked) {
      this.ingredientForm.patchValue({
        displayAmount: null,
        displayUnit: '',
      });
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
    if (this.mode() === 'ingredient') {
      if (this.ingredientForm.invalid) {
        return;
      }
      const val = this.ingredientForm.value;
      const token: StepIngredientToken = {
        ingredient: String(val.ingredient || '').trim(),
      };

      if (val.ingredientId) {
        token.ingredientId = String(val.ingredientId);
      }
      if (val.amount != null && val.amount !== '') {
        token.amount = Number(val.amount);
      }
      if (val.unit) {
        token.unit = String(val.unit).trim();
      }
      if (this.showDisplayAmount() && val.displayAmount != null && val.displayAmount !== '') {
        token.displayAmount = Number(val.displayAmount);
        token.displayUnit = val.displayUnit || val.unit || null;
      }
      if (val.notes) {
        token.notes = String(val.notes).trim();
      }

      const result: StepTokenDialogResult = {
        token,
        rawToken: serializeStepToken(token),
      };
      this.dialogRef.close(result);
    } else if (this.mode() === 'timer') {
      if (this.timerForm.invalid) {
        return;
      }
      const val = this.timerForm.value;
      const totalSeconds = (Number(val.minutes) || 0) * 60 + (Number(val.seconds) || 0);
      const token: StepTimerToken = {
        type: 'timer',
        seconds: totalSeconds > 0 ? totalSeconds : 60,
        label: val.label?.trim() ? String(val.label).trim() : null,
      };

      const result: StepTokenDialogResult = {
        token,
        rawToken: serializeStepToken(token),
      };
      this.dialogRef.close(result);
    } else {
      if (this.temperatureForm.invalid) {
        return;
      }
      const val = this.temperatureForm.value;
      const f = val.fahrenheit != null && val.fahrenheit !== '' ? Number(val.fahrenheit) : null;
      const c = val.celsius != null && val.celsius !== '' ? Number(val.celsius) : null;

      if (f == null && c == null) {
        return;
      }

      const token: StepTemperatureToken = {
        type: 'temperature',
        ...(f != null ? { fahrenheit: f } : { celsius: c! }),
      };

      const result: StepTokenDialogResult = {
        token,
        rawToken: serializeStepToken(token),
      };
      this.dialogRef.close(result);
    }
  }

  protected onDelete(): void {
    this.dialogRef.close({ delete: true });
  }

  protected onCancel(): void {
    this.dialogRef.close();
  }
}
