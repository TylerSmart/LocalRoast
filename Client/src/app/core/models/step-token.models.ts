import { MeasurementUnit } from './recipe.models';

export type StepTokenType =
  'ingredient' | 'mixture' | 'timer' | 'temperature' | 'dimension' | 'fixed';

export type DimensionUnit = 'inch' | 'foot' | 'yard' | 'mm' | 'cm' | 'm';

export interface StepIngredientToken {
  type?: 'ingredient';
  ingredient: string;
  ingredientId?: string | null;
  amount?: number | null;
  maxAmount?: number | null;
  unit?: MeasurementUnit | string | null;
  customUnitName?: string | null;
  displayText?: string | null;
  notes?: string | null;
}

export interface StepMixtureToken {
  type: 'mixture';
  name: string;
  amount?: number | null;
  maxAmount?: number | null;
  unit?: MeasurementUnit | string | null;
  customUnitName?: string | null;
}

export interface StepFixedToken {
  type: 'fixed';
  amount?: number | null;
  maxAmount?: number | null;
  unit?: MeasurementUnit | string | null;
  customUnitName?: string | null;
  name?: string | null;
  hyphenated?: boolean;
}

export interface StepDimensionToken {
  type: 'dimension';
  value: number;
  maxValue?: number | null;
  unit: DimensionUnit;
  hyphenated?: boolean;
}

export interface StepTimerToken {
  type: 'timer';
  seconds: number;
  maxSeconds?: number | null;
  label?: string | null;
}

export interface StepTemperatureToken {
  type: 'temperature';
  fahrenheit?: number | null;
  celsius?: number | null;
  maxFahrenheit?: number | null;
  maxCelsius?: number | null;
}

export type StepToken =
  | StepIngredientToken
  | StepMixtureToken
  | StepFixedToken
  | StepDimensionToken
  | StepTimerToken
  | StepTemperatureToken;

export interface StepSegment {
  text: string;
  token?: StepToken | null;
  rawToken?: string | null;
}

export interface StepDialogIngredientOption {
  index: number;
  id?: string | null;
  displayName: string;
  sectionName?: string | null;
  quantity?: number | null;
  unit?: string | null;
  customUnitName?: string | null;
}
