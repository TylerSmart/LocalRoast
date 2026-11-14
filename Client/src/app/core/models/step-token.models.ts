import { MeasurementUnit } from './recipe.models';

export type StepTokenType = 'ingredient' | 'timer' | 'temperature';

export interface StepIngredientToken {
  type?: 'ingredient';
  ingredient: string;
  ingredientId?: string | null;
  amount?: number | null;
  unit?: MeasurementUnit | string | null;
  displayAmount?: number | null;
  displayUnit?: MeasurementUnit | string | null;
  notes?: string | null;
}

export interface StepTimerToken {
  type: 'timer';
  seconds: number;
  label?: string | null;
}

export interface StepTemperatureToken {
  type: 'temperature';
  fahrenheit?: number | null;
  celsius?: number | null;
}

export type StepToken = StepIngredientToken | StepTimerToken | StepTemperatureToken;

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
