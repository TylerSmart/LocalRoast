import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
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
    CommonModule,
    ReactiveFormsModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
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

      this.ingredientForm = this.fb.group({
        ingredient: [ingToken?.ingredient ?? '', [Validators.required]],
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

  protected onSelectIngredient(name: string): void {
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

      if (val.amount != null && val.amount !== '') {
        token.amount = Number(val.amount);
      }
      if (val.unit) {
        token.unit = val.unit;
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
