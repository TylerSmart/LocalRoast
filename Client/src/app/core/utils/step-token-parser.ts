import {
  MeasurementUnit,
  RecipeMeasurement,
  RecipeStepIngredientAmountInput,
  RecipeStepTimerInput,
  UnitSystem,
} from '../models/recipe.models';
import {
  StepDialogIngredientOption,
  StepIngredientToken,
  StepSegment,
  StepTemperatureToken,
  StepTimerToken,
  StepToken,
} from '../models/step-token.models';
import { IngredientMeasurements, scaleAndConvert, scaleStepIngredient } from './culinary-math';

const TOKEN_REGEX = /\|\|(\{.*?\})([.,:;!?]*)\|\|/g;

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

    const jsonStr = match[1];
    const punct = match[2];
    const rawToken = `||${jsonStr}||`;

    try {
      const parsed = JSON.parse(jsonStr) as Record<string, unknown>;
      let token: StepToken | null = null;

      const typeStr =
        typeof parsed['type'] === 'string' ? (parsed['type'] as string).toLowerCase() : '';

      if (typeStr === 'timer' || 'seconds' in parsed) {
        token = {
          type: 'timer',
          seconds: Number(parsed['seconds']) || 0,
          label: (parsed['label'] as string) ?? null,
        } as StepTimerToken;
      } else if (typeStr === 'temperature' || 'fahrenheit' in parsed || 'celsius' in parsed) {
        const tempToken: StepTemperatureToken = {
          type: 'temperature',
        };
        if (parsed['fahrenheit'] != null) {
          tempToken.fahrenheit = Number(parsed['fahrenheit']);
        }
        if (parsed['celsius'] != null) {
          tempToken.celsius = Number(parsed['celsius']);
        }
        token = tempToken;
      } else if (
        'ingredient' in parsed ||
        parsed['type'] === 'ingredient' ||
        'amount' in parsed ||
        'unit' in parsed ||
        'displayAmount' in parsed ||
        'displayUnit' in parsed
      ) {
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
      if (punct) {
        segments.push({ text: punct });
      }
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
  matchedIngredient?: IngredientMeasurements | null,
): string {
  const qty = token.displayAmount ?? token.amount;
  const unitStr = token.displayUnit ?? token.unit;
  const normalizedUnit = normalizeMeasurementUnit(unitStr);

  if (qty === null || qty === undefined || isNaN(qty)) {
    return token.ingredient;
  }

  const customUnitName =
    normalizedUnit === 'Custom' && typeof unitStr === 'string' ? unitStr : null;
  const formatted = matchedIngredient?.alternativeMeasurement?.quantity
    ? scaleStepIngredient(
        matchedIngredient,
        { quantity: qty, unit: normalizedUnit, customUnitName },
        factor,
        system,
      )
    : scaleAndConvert(qty, null, normalizedUnit, customUnitName, factor, system);
  const measurementStr = formatted.formattedMeasurement.trim();

  return measurementStr
    ? token.ingredient
      ? `${measurementStr} ${token.ingredient}`
      : measurementStr
    : token.ingredient;
}

export function fahrenheitToCelsius(f: number): number {
  return Math.round((f - 32) * (5 / 9));
}

export function celsiusToFahrenheit(c: number): number {
  return Math.round(c * (9 / 5) + 32);
}

export function formatTemperatureDisplay(
  temp: StepTemperatureToken,
  system: UnitSystem = 'Original',
): { text: string; scaled: boolean } {
  const f = temp.fahrenheit != null && !isNaN(temp.fahrenheit) ? temp.fahrenheit : null;
  const c = temp.celsius != null && !isNaN(temp.celsius) ? temp.celsius : null;

  if (f == null && c == null) {
    return { text: '', scaled: false };
  }

  if (system === 'Metric') {
    if (c != null) {
      return { text: `${c}°C`, scaled: false };
    }
    if (f != null) {
      return { text: `${fahrenheitToCelsius(f)}°C`, scaled: true };
    }
  }

  if (system === 'UsCustomary') {
    if (f != null) {
      return { text: `${f}°F`, scaled: false };
    }
    if (c != null) {
      return { text: `${celsiusToFahrenheit(c)}°F`, scaled: true };
    }
  }

  // Original
  if (f != null && c != null) {
    return { text: `${f}°F (${c}°C)`, scaled: false };
  }
  if (f != null) {
    return { text: `${f}°F`, scaled: false };
  }
  if (c != null) {
    return { text: `${c}°C`, scaled: false };
  }

  return { text: '', scaled: false };
}

const DURATION_LABEL_REGEX =
  /^\s*(?:about|approx\.?|approximately)?\s*(?:\d+(?:\.\d+)?|\d+\s*(?:to|[-\u2013\u2014])\s*\d+)\s*(?:seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h)\s*$/i;

export function isDurationLabel(label: string | null | undefined): boolean {
  if (!label || !label.trim()) {
    return false;
  }
  return DURATION_LABEL_REGEX.test(label.trim());
}

export function shouldShowTimerLabel(label: string | null | undefined): boolean {
  if (!label || !label.trim()) {
    return false;
  }
  const trimmed = label.trim();
  if (trimmed.toLowerCase() === 'timer') {
    return false;
  }
  if (/^step\s+\d+\s+timer(?:\s+\d+)?$/i.test(trimmed)) {
    return false;
  }
  return !isDurationLabel(trimmed);
}

export function formatTimerToken(timer: StepTimerToken): string {
  const showLabel = shouldShowTimerLabel(timer.label);
  return showLabel
    ? `${formatSeconds(timer.seconds)} (${timer.label})`
    : formatSeconds(timer.seconds);
}

export function formatTemperatureToken(
  temp: StepTemperatureToken,
  system: UnitSystem = 'Original',
): string {
  return formatTemperatureDisplay(temp, system).text;
}

export function stepInstructionToPlainText(
  instruction: string | null | undefined,
  system: UnitSystem = 'Original',
): string {
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
        return formatTimerToken(seg.token as StepTimerToken);
      }

      if (seg.token.type === 'temperature') {
        return formatTemperatureToken(seg.token as StepTemperatureToken, system);
      }

      // Ingredient
      return formatTokenDisplay(seg.token as StepIngredientToken, 1, 'Original');
    })
    .join('');
}

const TRAILING_MEASUREMENT_REGEX =
  /(?:(?:\b(?:\d+(?:[\s-]+\d+\/\d+|\/\d+|\.\d+)?|[½¼¾⅓⅔⅛⅜⅝⅞])\s*(?:teaspoons?|tsp\.?|tablespoons?|tbsp\.?|cups?|fl(?:uid)?\.?\s*oz\.?|pints?|pt\.?|quarts?|qt\.?|gallons?|gal\.?|ounces?|oz\.?|pounds?|lbs?\.?|grams?|g|kilograms?|kg|milliliters?|ml|liters?|l|pinches?|dashes?|cloves?|slices?|cans?|bunches?|heads?)?(?:\s+of)?)|(?:(?:\ba\s+)?\b(?:pinch(?:es)?|dash(?:es)?|clove(?:s)?|slice(?:s)?|can(?:s)?|bunch(?:es)?|head(?:s)?)\s+of))\s*$/i;

const RANGE_CONNECTOR_REGEX = /^\s*(?:to|or|[-\u2013\u2014])\s*$/i;
const DUAL_TEMP_SEPARATOR_REGEX = /^\s*(?:\/|\()\s*$/;

function isFahrenheitCelsiusMatch(f: number, c: number): boolean {
  const expectedC = ((f - 32) * 5) / 9;
  return Math.abs(expectedC - c) <= 3;
}

export function cleanStepInstruction(instruction: string | null | undefined): string {
  if (!instruction) return '';
  const segments = parseStepInstruction(instruction);
  if (!segments.length) return instruction;

  let out = '';
  for (let i = 0; i < segments.length; i++) {
    const seg = segments[i];
    if (!seg.token) {
      let text = seg.text;
      if (i + 1 < segments.length && segments[i + 1].token?.type === 'ingredient') {
        const nextToken = segments[i + 1].token as StepIngredientToken;
        if (
          nextToken.amount != null ||
          nextToken.displayAmount != null ||
          nextToken.unit != null ||
          nextToken.displayUnit != null
        ) {
          const match = TRAILING_MEASUREMENT_REGEX.exec(text);
          if (match) {
            text = text.slice(0, match.index);
          }
        }
      }
      out += text;
    } else if (seg.token.type === 'timer') {
      const timer = seg.token as StepTimerToken;
      if (
        i + 2 < segments.length &&
        !segments[i + 1].token &&
        RANGE_CONNECTOR_REGEX.test(segments[i + 1].text) &&
        segments[i + 2].token?.type === 'timer'
      ) {
        const timer2 = segments[i + 2].token as StepTimerToken;
        const minSeconds =
          timer.seconds > 0 && timer2.seconds > 0
            ? Math.min(timer.seconds, timer2.seconds)
            : Math.max(timer.seconds, timer2.seconds);
        const chosenLabel =
          timer.label && !isDurationLabel(timer.label)
            ? timer.label
            : timer2.label && !isDurationLabel(timer2.label)
              ? timer2.label
              : undefined;
        const collapsed: StepTimerToken = chosenLabel
          ? { type: 'timer', seconds: minSeconds, label: chosenLabel }
          : { type: 'timer', seconds: minSeconds };
        out += serializeStepToken(collapsed);
        i += 2;
        continue;
      }

      if (isDurationLabel(timer.label)) {
        out += `||${JSON.stringify({ type: 'timer', seconds: timer.seconds })}||`;
      } else {
        out += seg.rawToken || seg.text;
      }
    } else if (seg.token.type === 'temperature') {
      const temp = seg.token as StepTemperatureToken;
      if (
        temp.fahrenheit != null &&
        i + 2 < segments.length &&
        !segments[i + 1].token &&
        DUAL_TEMP_SEPARATOR_REGEX.test(segments[i + 1].text) &&
        segments[i + 2].token?.type === 'temperature'
      ) {
        const temp2 = segments[i + 2].token as StepTemperatureToken;
        const cCandidate = temp2.celsius ?? temp2.fahrenheit;
        if (cCandidate != null && isFahrenheitCelsiusMatch(temp.fahrenheit, cCandidate)) {
          out += serializeStepToken({ type: 'temperature', fahrenheit: temp.fahrenheit });
          const wasParen = segments[i + 1].text.includes('(');
          i += 2;
          if (
            wasParen &&
            i + 1 < segments.length &&
            !segments[i + 1].token &&
            segments[i + 1].text.startsWith(')')
          ) {
            out += segments[i + 1].text.slice(1);
            i += 1;
          }
          continue;
        }
      }
      out += seg.rawToken || seg.text;
    } else {
      out += seg.rawToken || seg.text;
    }
  }
  return out;
}

export interface DerivedStepMetadata {
  hasTokens: boolean;
  hasIngredientTokens: boolean;
  hasTimerTokens: boolean;
  ingredientIndices: number[];
  ingredientAmounts: RecipeStepIngredientAmountInput[];
  timers: RecipeStepTimerInput[];
}

export function replaceTokenSegmentAt(
  instruction: string,
  segmentIndex: number,
  replacementRawToken: string | null,
): string {
  const segments = parseStepInstruction(instruction);
  if (segmentIndex < 0 || segmentIndex >= segments.length) {
    return instruction;
  }

  const serializeSegment = (seg: StepSegment): string => seg.rawToken || seg.text;

  if (replacementRawToken !== null) {
    return segments
      .map((seg, idx) => (idx === segmentIndex ? replacementRawToken : serializeSegment(seg)))
      .join('');
  }

  let before = segments.slice(0, segmentIndex).map(serializeSegment).join('');
  let after = segments
    .slice(segmentIndex + 1)
    .map(serializeSegment)
    .join('');

  if (before.endsWith(' ') && after.startsWith(' ')) {
    after = after.slice(1);
  } else if (before.endsWith(' ') && /^[.,:;!?)]/.test(after)) {
    before = before.slice(0, -1);
  }

  return `${before}${after}`.trim();
}

export function insertTokenAtOffset(
  instruction: string,
  offset: number,
  rawToken: string,
): { nextInstruction: string; nextCaretOffset: number } {
  let safeOffset = Math.max(0, Math.min(offset, instruction.length));
  TOKEN_REGEX.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = TOKEN_REGEX.exec(instruction)) !== null) {
    const matchStart = match.index;
    const matchEnd = TOKEN_REGEX.lastIndex;
    if (safeOffset > matchStart && safeOffset < matchEnd) {
      safeOffset = matchEnd;
      break;
    }
  }

  const before = instruction.slice(0, safeOffset);
  const after = instruction.slice(safeOffset);
  const spaceBefore = before.length > 0 && !/[\s(]$/.test(before) ? ' ' : '';
  const spaceAfter = after.length === 0 ? ' ' : /^[\s.,:;!?)]/.test(after) ? '' : ' ';
  const nextInstruction = before + spaceBefore + rawToken + spaceAfter + after;
  const advancePastSpace =
    spaceAfter.length > 0 ? spaceAfter.length : after.length > 0 && after[0] === ' ' ? 1 : 0;
  const nextCaretOffset = before.length + spaceBefore.length + rawToken.length + advancePastSpace;
  return { nextInstruction, nextCaretOffset };
}

function findBestCandidateIndex(
  token: StepIngredientToken,
  ingredients: readonly (Pick<StepDialogIngredientOption, 'id' | 'displayName' | 'sectionName'> & {
    index?: number;
  })[],
  stepSectionName?: string | null,
  preferredIndices?: readonly number[],
): number {
  if (token.ingredientId) {
    const byIdPos = ingredients.findIndex((c) => c.id && c.id === token.ingredientId);
    if (byIdPos >= 0) {
      return ingredients[byIdPos].index ?? byIdPos;
    }
  }

  const clean = token.ingredient.trim().toLowerCase();
  if (!clean) {
    return -1;
  }

  const normalizedSection = stepSectionName?.trim().toLowerCase() ?? '';
  let bestIndex = -1;
  let bestScore = -1;

  for (let i = 0; i < ingredients.length; i++) {
    const cand = ingredients[i];
    const name = cand.displayName.trim().toLowerCase();
    if (!name) continue;
    if (name !== clean && !name.includes(clean) && !clean.includes(name)) continue;

    let score = 0;
    if (name === clean) score += 8;
    if (normalizedSection && (cand.sectionName?.trim().toLowerCase() ?? '') === normalizedSection) {
      score += 4;
    }
    const candIdx = cand.index ?? i;
    if (preferredIndices?.includes(candIdx)) {
      score += 2;
    }

    if (score > bestScore) {
      bestScore = score;
      bestIndex = candIdx;
    }
  }

  return bestIndex;
}

export function deriveStepMetadataFromInstruction(
  instruction: string | null | undefined,
  ingredients: readonly (Pick<StepDialogIngredientOption, 'id' | 'displayName' | 'sectionName'> & {
    index?: number;
  })[],
  stepSectionName?: string | null,
  preferredIndices?: readonly number[],
): DerivedStepMetadata {
  const segments = parseStepInstruction(instruction ?? '');
  const tokens = segments.map((s) => s.token).filter((t): t is StepToken => !!t);

  const ingredientIndices: number[] = [];
  const seenIndices = new Set<number>();
  const amountsMap = new Map<number, RecipeStepIngredientAmountInput>();
  const timers: RecipeStepTimerInput[] = [];
  const seenTimers = new Set<string>();

  let hasIngredientTokens = false;
  let hasTimerTokens = false;

  for (const token of tokens) {
    if (token.type === 'timer' || 'seconds' in token) {
      hasTimerTokens = true;
      const timerToken = token as StepTimerToken;
      if (timerToken.seconds >= 1) {
        const label = timerToken.label?.trim() || 'Timer';
        const dedupeKey = `${timerToken.seconds}|${label.toLowerCase()}`;
        if (!seenTimers.has(dedupeKey)) {
          seenTimers.add(dedupeKey);
          timers.push({
            durationSeconds: timerToken.seconds,
            label,
          });
        }
      }
      continue;
    }

    if (token.type === 'temperature' || 'fahrenheit' in token || 'celsius' in token) {
      continue;
    }

    hasIngredientTokens = true;
    const ingToken = token as StepIngredientToken;
    const matchIdx = findBestCandidateIndex(
      ingToken,
      ingredients,
      stepSectionName,
      preferredIndices,
    );

    if (matchIdx >= 0) {
      if (!seenIndices.has(matchIdx)) {
        seenIndices.add(matchIdx);
        ingredientIndices.push(matchIdx);
      }

      if (ingToken.amount != null && !Number.isNaN(ingToken.amount) && !amountsMap.has(matchIdx)) {
        const normalizedUnit = normalizeMeasurementUnit(ingToken.unit);
        const rawUnit = typeof ingToken.unit === 'string' ? ingToken.unit.trim() : '';
        const customUnitName =
          normalizedUnit === 'Custom' && rawUnit && rawUnit.toLowerCase() !== 'custom'
            ? rawUnit
            : null;
        const measurement: RecipeMeasurement = {
          quantity: ingToken.amount,
          maxQuantity: null,
          unit: normalizedUnit,
          customUnitName,
        };
        amountsMap.set(matchIdx, {
          ingredientIndex: matchIdx,
          measurement,
        });
      }
    }
  }

  return {
    hasTokens: tokens.length > 0,
    hasIngredientTokens,
    hasTimerTokens,
    ingredientIndices,
    ingredientAmounts: Array.from(amountsMap.values()),
    timers,
  };
}

export interface StepTokenOriginFlags {
  hasIngredientTokens: boolean;
  hasTimerTokens: boolean;
}

export function resolveStepMetadataForPayload(
  instruction: string | null | undefined,
  ingredients: readonly (Pick<StepDialogIngredientOption, 'id' | 'displayName' | 'sectionName'> & {
    index?: number;
  })[],
  sectionName: string | null | undefined,
  origin: StepTokenOriginFlags | undefined,
  fallbackIndices: number[],
  fallbackAmounts: RecipeStepIngredientAmountInput[],
  fallbackTimers: RecipeStepTimerInput[],
): {
  ingredientIndices: number[];
  ingredientAmounts: RecipeStepIngredientAmountInput[];
  timers: RecipeStepTimerInput[];
} {
  const meta = deriveStepMetadataFromInstruction(
    instruction,
    ingredients,
    sectionName,
    fallbackIndices,
  );
  const useDerivedIngredients = meta.hasIngredientTokens || !!origin?.hasIngredientTokens;
  const useDerivedTimers = meta.hasTimerTokens || !!origin?.hasTimerTokens;

  return {
    ingredientIndices: useDerivedIngredients ? meta.ingredientIndices : fallbackIndices,
    ingredientAmounts: useDerivedIngredients ? meta.ingredientAmounts : fallbackAmounts,
    timers: useDerivedTimers ? meta.timers : fallbackTimers,
  };
}
