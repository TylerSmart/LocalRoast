import { MeasurementUnit, UnitSystem } from '../models/recipe.models';
import {
  StepIngredientToken,
  StepSegment,
  StepTemperatureToken,
  StepTimerToken,
  StepToken,
} from '../models/step-token.models';
import { scaleAndConvert } from './culinary-math';

const TOKEN_REGEX = /\|\|(\{.*?\})\|\|/g;

export function normalizeMeasurementUnit(
  unit: string | MeasurementUnit | null | undefined,
): MeasurementUnit | null {
  if (!unit) return null;
  const s = unit.trim().toLowerCase();
  switch (s) {
    case 'tsp':
    case 'teaspoon':
    case 'teaspoons':
      return 'Tsp';
    case 'tbsp':
    case 'tablespoon':
    case 'tablespoons':
      return 'Tbsp';
    case 'cup':
    case 'cups':
      return 'Cup';
    case 'fl_oz':
    case 'floz':
    case 'fluid ounce':
    case 'fluid ounces':
      return 'FlOz';
    case 'pt':
    case 'pint':
    case 'pints':
      return 'Pt';
    case 'qt':
    case 'quart':
    case 'quarts':
      return 'Qt';
    case 'gal':
    case 'gallon':
    case 'gallons':
      return 'Gal';
    case 'oz':
    case 'ounce':
    case 'ounces':
      return 'Oz';
    case 'lb':
    case 'pound':
    case 'pounds':
      return 'Lb';
    case 'g':
    case 'gram':
    case 'grams':
      return 'G';
    case 'kg':
    case 'kilogram':
    case 'kilograms':
      return 'Kg';
    case 'ml':
    case 'milliliter':
    case 'milliliters':
      return 'Ml';
    case 'l':
    case 'liter':
    case 'liters':
      return 'L';
    case 'pinch':
    case 'pinches':
      return 'Pinch';
    case 'dash':
    case 'dashes':
      return 'Dash';
    case 'clove':
    case 'cloves':
      return 'Clove';
    case 'can':
    case 'cans':
      return 'Can';
    case 'bunch':
    case 'bunches':
      return 'Bunch';
    case 'slice':
    case 'slices':
      return 'Slice';
    case 'whole':
      return 'Whole';
    case 'custom':
      return 'Custom';
    default:
      return 'Custom';
  }
}

export function parseStepInstruction(instruction: string | null | undefined): StepSegment[] {
  if (!instruction) {
    return [];
  }

  const segments: StepSegment[] = [];
  let lastIndex = 0;
  TOKEN_REGEX.lastIndex = 0;

  let match: RegExpExecArray | null;
  while ((match = TOKEN_REGEX.exec(instruction)) !== null) {
    const start = match.index;
    if (start > lastIndex) {
      segments.push({ text: instruction.slice(lastIndex, start) });
    }

    const rawToken = match[0];
    const jsonStr = match[1];

    try {
      const parsed = JSON.parse(jsonStr) as Record<string, unknown>;
      let token: StepToken | null = null;

      if (parsed['type'] === 'timer') {
        token = {
          type: 'timer',
          seconds: Number(parsed['seconds']) || 0,
          label: (parsed['label'] as string) ?? null,
        } as StepTimerToken;
      } else if (parsed['type'] === 'temperature') {
        token = {
          type: 'temperature',
          fahrenheit: parsed['fahrenheit'] != null ? Number(parsed['fahrenheit']) : null,
          celsius: parsed['celsius'] != null ? Number(parsed['celsius']) : null,
        } as StepTemperatureToken;
      } else if ('ingredient' in parsed || parsed['type'] === 'ingredient') {
        token = {
          type: 'ingredient',
          ingredient: String(parsed['ingredient'] ?? '').trim(),
          ingredientId: (parsed['ingredientId'] as string) ?? null,
          amount: parsed['amount'] != null ? Number(parsed['amount']) : null,
          unit: (parsed['unit'] as string) ?? null,
          displayAmount: parsed['displayAmount'] != null ? Number(parsed['displayAmount']) : null,
          displayUnit: (parsed['displayUnit'] as string) ?? null,
          notes: (parsed['notes'] as string) ?? null,
        } as StepIngredientToken;
      }

      segments.push({
        text: rawToken,
        token: token ?? null,
        rawToken,
      });
    } catch {
      // Failed JSON: preserve as unparsed segment
      segments.push({
        text: rawToken,
        token: null,
        rawToken,
      });
    }

    lastIndex = TOKEN_REGEX.lastIndex;
  }

  if (lastIndex < instruction.length) {
    segments.push({ text: instruction.slice(lastIndex) });
  }

  return segments;
}

export function serializeStepToken(token: StepToken): string {
  return `||${JSON.stringify(token)}||`;
}

export function formatSeconds(seconds: number): string {
  if (seconds >= 3600) {
    const hours = Math.floor(seconds / 3600);
    const mins = Math.floor((seconds % 3600) / 60);
    return mins > 0 ? `${hours}h ${mins}m` : `${hours}h`;
  }
  if (seconds >= 60) {
    const mins = Math.floor(seconds / 60);
    const secs = seconds % 60;
    return secs > 0 ? `${mins}m ${secs}s` : `${mins}m`;
  }
  return `${seconds}s`;
}

export function formatTokenDisplay(
  token: StepIngredientToken,
  factor = 1,
  system: UnitSystem = 'Original',
): string {
  const qty = token.displayAmount ?? token.amount;
  const unitStr = token.displayUnit ?? token.unit;
  const normalizedUnit = normalizeMeasurementUnit(unitStr);

  if (qty === null || qty === undefined || isNaN(qty)) {
    return token.ingredient;
  }

  const customUnitName =
    normalizedUnit === 'Custom' && typeof unitStr === 'string' ? unitStr : null;
  const formatted = scaleAndConvert(qty, null, normalizedUnit, customUnitName, factor, system);
  const measurementStr = formatted.formattedMeasurement.trim();

  return measurementStr ? `${measurementStr} ${token.ingredient}` : token.ingredient;
}

export function stepInstructionToPlainText(instruction: string | null | undefined): string {
  if (!instruction) {
    return '';
  }

  const segments = parseStepInstruction(instruction);
  return segments
    .map((seg) => {
      if (!seg.token) {
        return seg.text;
      }

      if (seg.token.type === 'timer') {
        const timer = seg.token as StepTimerToken;
        return `${formatSeconds(timer.seconds)}${timer.label ? ' (' + timer.label + ')' : ''}`;
      }

      if (seg.token.type === 'temperature') {
        const temp = seg.token as StepTemperatureToken;
        if (temp.fahrenheit != null && temp.celsius != null) {
          return `${temp.fahrenheit}°F (${temp.celsius}°C)`;
        }
        if (temp.fahrenheit != null) {
          return `${temp.fahrenheit}°F`;
        }
        if (temp.celsius != null) {
          return `${temp.celsius}°C`;
        }
        return '';
      }

      // Ingredient
      return formatTokenDisplay(seg.token as StepIngredientToken, 1, 'Original');
    })
    .join('');
}
