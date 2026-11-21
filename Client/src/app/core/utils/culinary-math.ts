import {
  MeasurementUnit,
  RecipeIngredientReplacementDto,
  RecipeMeasurement,
  UnitSystem,
} from '../models/recipe.models';
import { IngestionDraftReplacementDto } from '../models/ingestion.models';
import { DimensionUnit } from '../models/step-token.models';

export interface IngredientMeasurements extends Partial<RecipeMeasurement> {
  alternativeMeasurement?: RecipeMeasurement | null;
  preferAlternativeMeasurement?: boolean;
}

export function preferredIngredientMeasurement(
  ingredient: IngredientMeasurements,
): RecipeMeasurement {
  return ingredient.preferAlternativeMeasurement && ingredient.alternativeMeasurement
    ? ingredient.alternativeMeasurement
    : {
        quantity: ingredient.quantity ?? null,
        maxQuantity: ingredient.maxQuantity,
        unit: ingredient.unit ?? null,
        customUnitName: ingredient.customUnitName,
      };
}

export function scaleIngredient(
  ingredient: IngredientMeasurements,
  factor: number,
  system: UnitSystem,
): FormattedQuantityResult {
  const measurement = preferredIngredientMeasurement(ingredient);
  return scaleAndConvert(
    measurement.quantity,
    measurement.maxQuantity,
    measurement.unit,
    measurement.customUnitName,
    factor,
    system,
  );
}

const EXACT_INTRA_SYSTEM_FACTORS: Readonly<
  Partial<Record<MeasurementUnit, { system: string; factor: number }>>
> = {
  Tsp: { system: 'us-vol', factor: 1 },
  Tbsp: { system: 'us-vol', factor: 3 },
  FlOz: { system: 'us-vol', factor: 6 },
  Cup: { system: 'us-vol', factor: 48 },
  Pt: { system: 'us-vol', factor: 96 },
  Qt: { system: 'us-vol', factor: 192 },
  Gal: { system: 'us-vol', factor: 768 },
  Ml: { system: 'metric-vol', factor: 1 },
  L: { system: 'metric-vol', factor: 1000 },
  Oz: { system: 'us-wt', factor: 1 },
  Lb: { system: 'us-wt', factor: 16 },
  G: { system: 'metric-wt', factor: 1 },
  Kg: { system: 'metric-wt', factor: 1000 },
};

export function measurementUnitFactor(
  from: RecipeMeasurement,
  to: RecipeMeasurement,
): number | null {
  if (!from.unit || !to.unit) return null;
  if (
    from.unit === to.unit &&
    (from.unit !== 'Custom' ||
      from.customUnitName?.toLowerCase() === to.customUnitName?.toLowerCase())
  )
    return 1;
  const fromExact = EXACT_INTRA_SYSTEM_FACTORS[from.unit];
  const toExact = EXACT_INTRA_SYSTEM_FACTORS[to.unit];
  if (fromExact && toExact && fromExact.system === toExact.system) {
    return fromExact.factor / toExact.factor;
  }
  const fromVolume = getVolumeMlFactor(from.unit);
  const toVolume = getVolumeMlFactor(to.unit);
  if (fromVolume !== null && toVolume !== null) return roundTo(fromVolume / toVolume, 6);
  const fromWeight = getWeightGFactor(from.unit);
  const toWeight = getWeightGFactor(to.unit);
  if (fromWeight !== null && toWeight !== null) {
    return roundTo(fromWeight / toWeight, 6);
  }
  return null;
}

export interface ParsedYieldMeasurement {
  quantity: number;
  unit: MeasurementUnit | null;
  countNoun: string | null;
  isServingYield: boolean;
}

const YIELD_UNICODE_FRACTIONS: Record<string, string> = {
  '¼': ' 1/4',
  '½': ' 1/2',
  '¾': ' 3/4',
  '⅓': ' 1/3',
  '⅔': ' 2/3',
  '⅕': ' 1/5',
  '⅖': ' 2/5',
  '⅗': ' 3/5',
  '⅘': ' 4/5',
  '⅙': ' 1/6',
  '⅚': ' 5/6',
  '⅛': ' 1/8',
  '⅜': ' 3/8',
  '⅝': ' 5/8',
  '⅞': ' 7/8',
};

const YIELD_IGNORE_WORDS = new Set([
  'to',
  'or',
  'about',
  'season',
  'makes',
  'serves',
  'recipe',
  'recipes',
  'batch',
  'batches',
]);

function singularizeSimple(word: string): string {
  const lower = word.toLowerCase();
  if (lower.endsWith('ies') && lower.length > 4) {
    return `${lower.slice(0, -3)}y`;
  }
  if (/(?:oes|ses|xes|ches|shes)$/.test(lower) && lower.length > 4) {
    return lower.slice(0, -2);
  }
  if (lower.endsWith('s') && !lower.endsWith('ss') && lower.length > 3) {
    return lower.slice(0, -1);
  }
  return lower;
}

function normalizeYieldUnitWord(word: string): MeasurementUnit | null {
  switch (word.trim().toLowerCase()) {
    case 'tsp':
    case 'teaspoon':
    case 'teaspoons':
    case 't':
      return 'Tsp';
    case 'tbsp':
    case 'tablespoon':
    case 'tablespoons':
    case 'tb':
      return 'Tbsp';
    case 'fl oz':
    case 'fl_oz':
    case 'floz':
    case 'fluid ounce':
    case 'fluid ounces':
      return 'FlOz';
    case 'cup':
    case 'cups':
    case 'c':
      return 'Cup';
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
    case 'ml':
    case 'milliliter':
    case 'milliliters':
    case 'millilitre':
    case 'millilitres':
      return 'Ml';
    case 'l':
    case 'liter':
    case 'liters':
    case 'litre':
    case 'litres':
      return 'L';
    case 'oz':
    case 'ounce':
    case 'ounces':
      return 'Oz';
    case 'lb':
    case 'lbs':
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
    default:
      return null;
  }
}

function parseYieldNumber(raw: string): number | null {
  const parts = raw.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 2) {
    const whole = Number(parts[0]);
    const frac = parseSimpleFraction(parts[1]);
    return !Number.isNaN(whole) && frac != null ? whole + frac : null;
  }
  if (parts.length === 1) {
    if (parts[0].includes('/')) {
      return parseSimpleFraction(parts[0]);
    }
    const n = Number(parts[0]);
    return !Number.isNaN(n) && n > 0 ? n : null;
  }
  return null;
}

function parseSimpleFraction(tok: string): number | null {
  const slash = tok.indexOf('/');
  if (slash <= 0 || slash === tok.length - 1) return null;
  const num = Number(tok.slice(0, slash));
  const den = Number(tok.slice(slash + 1));
  if (Number.isNaN(num) || Number.isNaN(den) || den === 0) return null;
  const val = num / den;
  return val > 0 ? val : null;
}

function isServingUnitWord(word: string | null | undefined): boolean {
  if (!word) return false;
  const w = word.trim().toLowerCase();
  return w === 'serving' || w === 'servings' || w === 'portion' || w === 'portions';
}

export function parseYieldMeasurements(
  yieldDisplay: string | null | undefined,
): ParsedYieldMeasurement[] {
  if (!yieldDisplay || !yieldDisplay.trim()) {
    return [];
  }

  let expanded = '';
  for (const ch of yieldDisplay) {
    expanded += YIELD_UNICODE_FRACTIONS[ch] ?? ch;
  }

  const cleaned = expanded
    .replace(/\b(?:season|episode|ep|page|chapter)\s+\d+\b/gi, ' ')
    .replace(
      /\b\d+(?:\s+\d+\/\d+|\.\d+)?\s*(?:to|–|-)\s*(?:\d+\s+)?\d+(?:\.\d+|\/\d+)?-(?:inch|inches|cm|mm|ounce|oz|pound|lb)\b|\b\d+(?:\.\d+|\/\d+)?-(?:inch|inches|cm|mm|ounce|oz|pound|lb)\b/gi,
      ' ',
    )
    .replace(
      /(\d+\s+\d+\s*\/\s*\d+|\d+\s*\/\s*\d+|\d+(?:\.\d+)?)\s*(?:to|–|-)\s*(?:\d+\s+\d+\s*\/\s*\d+|\d+\s*\/\s*\d+|\d+(?:\.\d+)?)/gi,
      '$1',
    );

  const trimmedLower = cleaned.trim().toLowerCase();
  const startsWithServes = trimmedLower.startsWith('serves') || trimmedLower.startsWith('serve ');

  const results: ParsedYieldMeasurement[] = [];
  const tokenRegex =
    /(\d+\s+\d+\s*\/\s*\d+|\d+\s*\/\s*\d+|\d+(?:\.\d+)?)\s*([a-zA-Z]+(?:\s+ounces?)?)?/g;
  for (const m of cleaned.matchAll(tokenRegex)) {
    const qty = parseYieldNumber(m[1]);
    if (qty == null || qty <= 0) continue;

    const rawWord = (m[2] ?? '').trim();
    const firstWord = rawWord.split(/\s+/).filter(Boolean)[0] ?? '';
    const candidateWord = /^fluid ounces?$/i.test(rawWord) ? rawWord : firstWord;

    let unit: MeasurementUnit | null = null;
    let countNoun: string | null = null;
    let isServing = false;

    if (candidateWord) {
      if (isServingUnitWord(candidateWord)) {
        isServing = true;
      } else {
        unit = normalizeYieldUnitWord(candidateWord);
        if (!unit) {
          const lowerTok = candidateWord.toLowerCase();
          if (!YIELD_IGNORE_WORDS.has(lowerTok)) {
            countNoun = singularizeSimple(lowerTok);
          }
        }
      }
    }

    if (!isServing && startsWithServes && unit == null && countNoun == null) {
      isServing = true;
    }

    results.push({ quantity: qty, unit, countNoun, isServingYield: isServing });
  }

  return results;
}

export interface SubRecipeParentIngredientRef {
  quantity: number | null;
  maxQuantity?: number | null;
  unit: MeasurementUnit | null;
  customUnitName?: string | null;
  originalRawText?: string | null;
  alternativeMeasurement?: RecipeMeasurement | null;
  preferAlternativeMeasurement?: boolean;
}

export interface SubRecipeYieldInfo {
  baseServings: number;
  yieldDisplay?: string | null;
}

export function computeSubRecipeBatchMultiplier(
  parentIng: SubRecipeParentIngredientRef | null | undefined,
  subRecipe: SubRecipeYieldInfo | null | undefined,
): number {
  const rawQty = parentIng ? preferredIngredientMeasurement(parentIng).quantity : null;
  if (!parentIng || rawQty == null || rawQty <= 0) {
    return 1;
  }

  const pref = preferredIngredientMeasurement(parentIng);
  const unit = pref.unit ?? null;
  const customUnitName = pref.customUnitName ?? null;
  const origText = parentIng.originalRawText ?? null;
  const baseServings = subRecipe?.baseServings ?? 1;

  const leadingServingRegex =
    /^\s*(?:\d+\s+\d+\/\d+|\d+\/\d+|\d+(?:\.\d+)?)(?:\s*(?:to|–|-)\s*(?:\d+\s+\d+\/\d+|\d+\/\d+|\d+(?:\.\d+)?))?\s*(?:servings?|portions?)\b/i;
  if (
    (unit === 'Custom' && isServingUnitWord(customUnitName)) ||
    (origText != null && leadingServingRegex.test(origText))
  ) {
    return baseServings > 0 ? rawQty / baseServings : 1;
  }

  if (
    unit === 'Whole' &&
    origText != null &&
    /\b(?:recipe|recipes|batch|batches)\b/i.test(origText)
  ) {
    return rawQty;
  }

  const yieldEntries = parseYieldMeasurements(subRecipe?.yieldDisplay);
  const altMeasure = parentIng.preferAlternativeMeasurement
    ? {
        quantity: parentIng.quantity,
        maxQuantity: parentIng.maxQuantity,
        unit: parentIng.unit,
        customUnitName: parentIng.customUnitName,
      }
    : parentIng.alternativeMeasurement;

  for (const cand of [pref, altMeasure]) {
    if (!cand || cand.quantity == null || cand.quantity <= 0 || !cand.unit) continue;
    for (const entry of yieldEntries) {
      if (entry.isServingYield || !entry.unit || entry.quantity <= 0) continue;
      const factor = measurementUnitFactor(
        { quantity: cand.quantity, unit: cand.unit },
        { quantity: entry.quantity, unit: entry.unit },
      );
      if (factor !== null) {
        return roundTo((cand.quantity * factor) / entry.quantity, 6);
      }
    }
  }

  if (unit == null || unit === 'Custom') {
    const customNoun =
      unit === 'Custom' && customUnitName?.trim() ? singularizeSimple(customUnitName.trim()) : null;
    for (const entry of yieldEntries) {
      if (entry.isServingYield || entry.unit != null || entry.quantity <= 0) continue;
      if (customNoun !== null) {
        if (entry.countNoun && entry.countNoun.toLowerCase() === customNoun.toLowerCase()) {
          return rawQty / entry.quantity;
        }
      } else {
        return rawQty / entry.quantity;
      }
    }
  }

  if (unit === 'Whole') {
    return rawQty;
  }

  const isServesOnly = yieldEntries.length > 0 && yieldEntries.every((e) => e.isServingYield);
  if (unit == null && isServesOnly && baseServings > 1 && rawQty === baseServings) {
    return 1;
  }

  return rawQty;
}

export function scaleStepIngredient(
  ingredient: IngredientMeasurements,
  amount: RecipeMeasurement | null | undefined,
  factor: number,
  system: UnitSystem,
): FormattedQuantityResult {
  if (!amount) return scaleAndConvert(null, null, null, null, factor, system);
  const primary: RecipeMeasurement = {
    quantity: ingredient.quantity ?? null,
    maxQuantity: ingredient.maxQuantity,
    unit: ingredient.unit ?? null,
    customUnitName: ingredient.customUnitName,
  };
  const target = preferredIngredientMeasurement(ingredient);
  const other = ingredient.preferAlternativeMeasurement
    ? primary
    : ingredient.alternativeMeasurement;
  if (
    other?.quantity &&
    target.quantity &&
    (amount.unit !== target.unit ||
      (amount.unit === 'Custom' &&
        amount.customUnitName?.toLowerCase() !== target.customUnitName?.toLowerCase())) &&
    (amount.unit === other.unit ||
      measurementUnitFactor(amount, target) === null ||
      (isMetricUnit(amount.unit) === isMetricUnit(other.unit) &&
        isMetricUnit(other.unit) !== isMetricUnit(target.unit)))
  ) {
    const unitFactor = measurementUnitFactor(amount, other);
    if (unitFactor !== null) {
      const ratio = (unitFactor * target.quantity) / other.quantity;
      amount = {
        quantity: amount.quantity === null ? null : amount.quantity * ratio,
        maxQuantity: amount.maxQuantity == null ? null : amount.maxQuantity * ratio,
        unit: target.unit,
        customUnitName: target.customUnitName,
      };
    }
  }
  return scaleAndConvert(
    amount.quantity,
    amount.maxQuantity,
    amount.unit,
    amount.customUnitName,
    factor,
    system,
  );
}

export interface FormattedQuantityResult {
  quantity: number | null;
  maxQuantity: number | null;
  unit: MeasurementUnit | null;
  unitLabel: string;
  formattedAmount: string;
  formattedMeasurement: string;
}

// Sizes in quarter-teaspoons. Greedy change is not optimal: 2/3 cup needs two
// 1/3-cup scoops, not a 1/2 cup followed by spoons.
const MEASURING_SIZES = [192, 96, 64, 48, 12, 4, 2, 1];
const MEASURING_COUNTS: number[][] = [MEASURING_SIZES.map(() => 0)];
for (let total = 1; total < 384; total++) {
  let best = [...MEASURING_COUNTS[total - 1]];
  best[MEASURING_SIZES.length - 1]++;
  for (let index = 0; index < MEASURING_SIZES.length; index++) {
    if (MEASURING_SIZES[index] > total) continue;
    const candidate = [...MEASURING_COUNTS[total - MEASURING_SIZES[index]]];
    candidate[index]++;
    if (candidate.reduce((a, b) => a + b) < best.reduce((a, b) => a + b)) {
      best = candidate;
    }
  }
  MEASURING_COUNTS.push(best);
}

function practicalVolume(
  quantity: number,
  unit: MeasurementUnit | null | undefined,
): string | null {
  const teaspoonsPerUnit: Partial<Record<MeasurementUnit, number>> = {
    Cup: 48,
    Tbsp: 3,
    Tsp: 1,
    FlOz: 6,
    Pt: 96,
    Qt: 192,
    Gal: 768,
  };
  const multiplier = unit ? teaspoonsPerUnit[unit] : undefined;
  if (!multiplier || !Number.isFinite(quantity) || quantity <= 0) return null;
  const ticks = quantity * multiplier * 4;
  const rounded = Math.round(ticks);
  // Only snap conversion/decimal noise, never round arbitrary amounts to a scoop.
  if (!Number.isSafeInteger(rounded) || rounded < 1 || Math.abs(ticks - rounded) > 0.06)
    return null;
  // Keep a cup available for rebalancing: 1 1/6 cups is three scoops
  // (1/2 + 1/3 + 1/3), cheaper than a full cup plus four spoons.
  const fullCups = Math.max(0, Math.floor(rounded / 192) - 1);
  const counts = [...MEASURING_COUNTS[rounded - fullCups * 192]];
  counts[0] += fullCups;
  const cups = counts
    .slice(0, 4)
    .reduce((sum, count, i) => sum + (count * MEASURING_SIZES[i]) / 192, 0);
  const cupParts: string[] = [];
  if (cups > 0) {
    const fraction = cups % 1;
    if (CLEAN_CUP_FRACTIONS.some(([value]) => Math.abs(fraction - value) < 0.00001)) {
      cupParts.push(`${formatNumber(cups, false)} ${getUnitLabel('Cup', null, cups)}`);
    } else {
      counts.slice(0, 4).forEach((count, i) => {
        if (count) {
          const value = (count * MEASURING_SIZES[i]) / 192;
          cupParts.push(`${formatNumber(value, false)} ${getUnitLabel('Cup', null, value)}`);
        }
      });
    }
  }
  const tsp = counts
    .slice(5)
    .reduce((sum, count, i) => sum + (count * MEASURING_SIZES[i + 5]) / 4, 0);
  return [
    ...cupParts,
    counts[4] ? `${counts[4]} tbsp` : '',
    tsp ? `${formatNumber(tsp, false)} tsp` : '',
  ]
    .filter(Boolean)
    .join(' + ');
}

const KNOWN_FRACTIONS: { target: number; fraction: string }[] = [
  { target: 0.0625, fraction: '1/16' },
  { target: 0.125, fraction: '1/8' },
  { target: 0.25, fraction: '1/4' },
  { target: 0.3333333, fraction: '1/3' },
  { target: 0.375, fraction: '3/8' },
  { target: 0.5, fraction: '1/2' },
  { target: 0.625, fraction: '5/8' },
  { target: 0.6666667, fraction: '2/3' },
  { target: 0.75, fraction: '3/4' },
  { target: 0.875, fraction: '7/8' },
];

export function isMetricUnit(unit: MeasurementUnit | null | undefined): boolean {
  return unit === 'Ml' || unit === 'L' || unit === 'G' || unit === 'Kg';
}

function getVolumeMlFactor(unit: MeasurementUnit): number | null {
  switch (unit) {
    case 'Tsp':
      return 4.92892;
    case 'Tbsp':
      return 14.7868;
    case 'FlOz':
      return 29.5735;
    case 'Cup':
      return 236.588;
    case 'Pt':
      return 473.176;
    case 'Qt':
      return 946.353;
    case 'Gal':
      return 3785.41;
    case 'Ml':
      return 1;
    case 'L':
      return 1000;
    default:
      return null;
  }
}

function getWeightGFactor(unit: MeasurementUnit): number | null {
  switch (unit) {
    case 'Oz':
      return 28.3495;
    case 'Lb':
      return 453.592;
    case 'G':
      return 1;
    case 'Kg':
      return 1000;
    default:
      return null;
  }
}

export function getUnitLabel(
  unit: MeasurementUnit | null | undefined,
  customUnitName: string | null | undefined,
  qty: number | null | undefined,
): string {
  if (!unit) {
    return '';
  }

  const isPlural = qty !== null && qty !== undefined && qty > 1.05;

  switch (unit) {
    case 'Tsp':
      return 'tsp';
    case 'Tbsp':
      return 'tbsp';
    case 'FlOz':
      return 'fl oz';
    case 'Cup':
      return isPlural ? 'cups' : 'cup';
    case 'Pt':
      return 'pt';
    case 'Qt':
      return 'qt';
    case 'Gal':
      return 'gal';
    case 'Ml':
      return 'ml';
    case 'L':
      return 'l';
    case 'Oz':
      return 'oz';
    case 'Lb':
      return 'lb';
    case 'G':
      return 'g';
    case 'Kg':
      return 'kg';
    case 'Pinch':
      return isPlural ? 'pinches' : 'pinch';
    case 'Dash':
      return isPlural ? 'dashes' : 'dash';
    case 'Clove':
      return isPlural ? 'cloves' : 'clove';
    case 'Can':
      return isPlural ? 'cans' : 'can';
    case 'Bunch':
      return isPlural ? 'bunches' : 'bunch';
    case 'Slice':
      return isPlural ? 'slices' : 'slice';
    case 'Whole':
      return '';
    case 'Custom': {
      if (!customUnitName) {
        return '';
      }
      const lower = customUnitName.trim().toLowerCase();
      if (
        ['null', 'none', 'n/a', 'na', 'undefined', 'nil', '-', '--', '???', '?'].includes(lower)
      ) {
        return '';
      }
      return customUnitName;
    }
    default:
      return '';
  }
}

export function formatNumber(value: number, isMetric: boolean): string {
  if (value <= 0) {
    return '0';
  }

  if (isMetric) {
    if (value >= 10) {
      return Math.round(value).toString();
    }
    // trimmed to at most 2 decimal places
    const rounded = Number(value.toFixed(2));
    return rounded.toString();
  }

  const whole = Math.floor(value);
  const frac = value - whole;

  if (frac >= 0.96) {
    return (whole + 1).toString();
  }

  if (frac < 0.04) {
    return whole > 0 ? whole.toString() : Number(value.toFixed(2)).toString();
  }

  let matchedFraction: string | null = null;
  let minDiff = Number.MAX_VALUE;

  for (const { target, fraction } of KNOWN_FRACTIONS) {
    const diff = Math.abs(frac - target);
    if (diff <= 0.045 && diff < minDiff) {
      minDiff = diff;
      matchedFraction = fraction;
    }
  }

  if (matchedFraction !== null) {
    return whole > 0 ? `${whole} ${matchedFraction}` : matchedFraction;
  }

  const fallback = Number(value.toFixed(2));
  return fallback.toString();
}

export function formatAmount(
  min: number,
  max: number | null | undefined,
  isMetric: boolean,
): string {
  const minStr = formatNumber(min, isMetric);
  if (max === null || max === undefined || max <= min) {
    return minStr;
  }
  const maxStr = formatNumber(max, isMetric);
  return `${minStr}–${maxStr}`;
}

export function formatDuration(durationSeconds: number): string {
  if (durationSeconds <= 0) {
    return '0 sec';
  }

  if (durationSeconds < 60) {
    return `${durationSeconds} sec`;
  }

  if (durationSeconds < 3600) {
    const mins = Math.floor(durationSeconds / 60);
    const secs = durationSeconds % 60;
    return secs > 0 ? `${mins} min ${secs} sec` : `${mins} min`;
  }

  const hrs = Math.floor(durationSeconds / 3600);
  const remMins = Math.floor((durationSeconds % 3600) / 60);
  return remMins > 0 ? `${hrs} hr ${remMins} min` : `${hrs} hr`;
}

export function formatMinutes(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined || isNaN(minutes) || minutes < 0) {
    return '';
  }

  if (minutes < 60) {
    return `${minutes} min`;
  }

  const hrs = Math.floor(minutes / 60);
  const remMins = minutes % 60;
  return remMins > 0 ? `${hrs} hr ${remMins} min` : `${hrs} hr`;
}

const CLEAN_CUP_FRACTIONS: [number, number][] = [
  [0.0, 0.0],
  [0.25, 0.25],
  [1 / 3, 1 / 3],
  [0.5, 0.5],
  [2 / 3, 2 / 3],
  [0.75, 0.75],
  [1.0, 1.0],
];

function isCleanMultiple(val: number, step: number): { isClean: boolean; snappedVal: number } {
  if (step <= 0 || val <= 0) {
    return { isClean: false, snappedVal: val };
  }
  const ratio = val / step;
  const rounded = Math.round(ratio);
  if (Math.abs(ratio - rounded) <= 0.015) {
    return { isClean: true, snappedVal: rounded * step };
  }
  return { isClean: false, snappedVal: val };
}

function isCleanCupAmount(cups: number): { isClean: boolean; snappedCups: number } {
  const whole = Math.floor(cups);
  const frac = cups - whole;
  for (const [target, snapped] of CLEAN_CUP_FRACTIONS) {
    if (Math.abs(frac - target) <= 0.015) {
      return { isClean: true, snappedCups: whole + snapped };
    }
  }
  return { isClean: false, snappedCups: cups };
}

function simplifySingleUnit(
  qty: number,
  unit: MeasurementUnit,
): { qty: number; unit: MeasurementUnit } {
  switch (unit) {
    case 'Cup':
      if (qty < 0.25) {
        const tsp = qty * 48;
        const cleanTsp15 = isCleanMultiple(tsp, 1.5);
        if (cleanTsp15.isClean) {
          return { qty: cleanTsp15.snappedVal / 3, unit: 'Tbsp' };
        }
        const cleanTsp025 = isCleanMultiple(tsp, 0.25);
        if (cleanTsp025.isClean) {
          return { qty: cleanTsp025.snappedVal, unit: 'Tsp' };
        }
      }
      break;

    case 'Tbsp':
      if (qty < 1) {
        const tsp = qty * 3;
        const cleanTsp = isCleanMultiple(tsp, 0.25);
        if (cleanTsp.isClean) {
          return { qty: cleanTsp.snappedVal, unit: 'Tsp' };
        }
      } else if (qty >= 4) {
        const cups = qty / 16;
        if (cups >= 4) {
          const cleanQt = isCleanMultiple(cups / 4, 0.25);
          if (cleanQt.isClean) {
            return { qty: cleanQt.snappedVal, unit: 'Qt' };
          }
        }
        const cleanCups = isCleanCupAmount(cups);
        if (cleanCups.isClean) {
          return { qty: cleanCups.snappedCups, unit: 'Cup' };
        }
      }
      break;

    case 'Tsp':
      if (qty >= 3) {
        const cups = qty / 48;
        if (qty >= 12) {
          const cleanCups = isCleanCupAmount(cups);
          if (cleanCups.isClean) {
            if (cleanCups.snappedCups >= 4) {
              const cleanQt = isCleanMultiple(cleanCups.snappedCups / 4, 0.25);
              if (cleanQt.isClean) {
                return { qty: cleanQt.snappedVal, unit: 'Qt' };
              }
            }
            return { qty: cleanCups.snappedCups, unit: 'Cup' };
          }
        }
        const tbsp = qty / 3;
        const cleanTbsp = isCleanMultiple(tbsp, 0.5);
        if (cleanTbsp.isClean) {
          return { qty: cleanTbsp.snappedVal, unit: 'Tbsp' };
        }
      }
      break;

    case 'Qt':
      if (qty < 1) {
        const cups = qty * 4;
        if (cups < 4) {
          const cleanCups = isCleanCupAmount(cups);
          if (cleanCups.isClean) {
            if (cleanCups.snappedCups < 0.25) {
              const tsp = cleanCups.snappedCups * 48;
              const cleanTsp15 = isCleanMultiple(tsp, 1.5);
              if (cleanTsp15.isClean) {
                return { qty: cleanTsp15.snappedVal / 3, unit: 'Tbsp' };
              }
              const cleanTsp025 = isCleanMultiple(tsp, 0.25);
              if (cleanTsp025.isClean) {
                return { qty: cleanTsp025.snappedVal, unit: 'Tsp' };
              }
            }
            return { qty: cleanCups.snappedCups, unit: 'Cup' };
          }
        }
      }
      break;

    case 'Oz':
      if (qty >= 16) {
        const lbs = qty / 16;
        const cleanLbs = isCleanMultiple(lbs, 0.25);
        if (cleanLbs.isClean) {
          return { qty: cleanLbs.snappedVal, unit: 'Lb' };
        }
      }
      break;

    case 'Lb':
      if (qty < 0.25) {
        return { qty: qty * 16, unit: 'Oz' };
      }
      break;

    case 'L':
      if (qty < 0.1) {
        return { qty: qty * 1000, unit: 'Ml' };
      }
      break;

    case 'Ml':
      if (qty >= 1000) {
        return { qty: qty / 1000, unit: 'L' };
      }
      break;

    case 'Kg':
      if (qty < 0.1) {
        return { qty: qty * 1000, unit: 'G' };
      }
      break;

    case 'G':
      if (qty >= 1000) {
        return { qty: qty / 1000, unit: 'Kg' };
      }
      break;
  }
  return { qty, unit };
}

function simplifyIntraSystemUnit(
  qty: number,
  maxQty: number | null | undefined,
  unit: MeasurementUnit,
): { qty: number; maxQty: number | null | undefined; unit: MeasurementUnit } {
  const { qty: simplifiedQty, unit: targetUnit } = simplifySingleUnit(qty, unit);
  if (maxQty === null || maxQty === undefined || maxQty <= qty) {
    let simplifiedMaxQty: number | null = null;
    if (maxQty !== null && maxQty !== undefined) {
      simplifiedMaxQty = simplifySingleUnit(maxQty, unit).qty;
    }
    return { qty: simplifiedQty, maxQty: simplifiedMaxQty, unit: targetUnit };
  }

  const { qty: simplifiedMax, unit: maxTargetUnit } = simplifySingleUnit(maxQty, unit);
  if (targetUnit === maxTargetUnit) {
    return { qty: simplifiedQty, maxQty: simplifiedMax, unit: targetUnit };
  }

  return { qty, maxQty, unit };
}

function convertUnit(
  qty: number,
  maxQty: number | null | undefined,
  unit: MeasurementUnit | null | undefined,
  targetSystem: UnitSystem,
  scaleFactor: number,
): { qty: number; maxQty: number | null | undefined; unit: MeasurementUnit | null | undefined } {
  if (!unit) {
    return { qty, maxQty, unit };
  }

  if (targetSystem === 'Original') {
    if (scaleFactor === 1) {
      return { qty, maxQty, unit };
    }
    return simplifyIntraSystemUnit(qty, maxQty, unit);
  }

  if (targetSystem === 'Metric') {
    let convQty = qty;
    let convMaxQty = maxQty;
    let convUnit = unit;

    const mlFactor = getVolumeMlFactor(unit);
    if (mlFactor !== null) {
      const ml = qty * mlFactor;
      const maxMl = maxQty !== null && maxQty !== undefined ? maxQty * mlFactor : null;
      if (ml >= 1000) {
        convQty = ml / 1000;
        convMaxQty = maxMl !== null ? maxMl / 1000 : null;
        convUnit = 'L';
      } else {
        convQty = ml;
        convMaxQty = maxMl;
        convUnit = 'Ml';
      }
    } else {
      const gFactor = getWeightGFactor(unit);
      if (gFactor !== null) {
        const g = qty * gFactor;
        const maxG = maxQty !== null && maxQty !== undefined ? maxQty * gFactor : null;
        if (g >= 1000) {
          convQty = g / 1000;
          convMaxQty = maxG !== null ? maxG / 1000 : null;
          convUnit = 'Kg';
        } else {
          convQty = g;
          convMaxQty = maxG;
          convUnit = 'G';
        }
      }
    }
    return simplifyIntraSystemUnit(convQty, convMaxQty, convUnit);
  } else if (targetSystem === 'UsCustomary') {
    let convQty = qty;
    let convMaxQty = maxQty;
    let convUnit = unit;

    if (unit === 'Ml' || unit === 'L') {
      const ml = unit === 'L' ? qty * 1000 : qty;
      const maxMl =
        maxQty !== null && maxQty !== undefined ? (unit === 'L' ? maxQty * 1000 : maxQty) : null;

      if (ml < 14.7868) {
        const f = 4.92892;
        convQty = ml / f;
        convMaxQty = maxMl !== null ? maxMl / f : null;
        convUnit = 'Tsp';
      } else if (ml < 59.147) {
        const f = 14.7868;
        convQty = ml / f;
        convMaxQty = maxMl !== null ? maxMl / f : null;
        convUnit = 'Tbsp';
      } else if (ml < 946.353) {
        const f = 236.588;
        convQty = ml / f;
        convMaxQty = maxMl !== null ? maxMl / f : null;
        convUnit = 'Cup';
      } else {
        const qtf = 946.353;
        convQty = ml / qtf;
        convMaxQty = maxMl !== null ? maxMl / qtf : null;
        convUnit = 'Qt';
      }
    } else if (unit === 'G' || unit === 'Kg') {
      const g = unit === 'Kg' ? qty * 1000 : qty;
      const maxG =
        maxQty !== null && maxQty !== undefined ? (unit === 'Kg' ? maxQty * 1000 : maxQty) : null;

      if (g < 453.592) {
        const ozf = 28.3495;
        convQty = g / ozf;
        convMaxQty = maxG !== null ? maxG / ozf : null;
        convUnit = 'Oz';
      } else {
        const lbf = 453.592;
        convQty = g / lbf;
        convMaxQty = maxG !== null ? maxG / lbf : null;
        convUnit = 'Lb';
      }
    }
    return simplifyIntraSystemUnit(convQty, convMaxQty, convUnit);
  }

  return { qty, maxQty, unit };
}

export function scaleAndConvert(
  quantity: number | null | undefined,
  maxQuantity: number | null | undefined,
  unit: MeasurementUnit | null | undefined,
  customUnitName: string | null | undefined,
  scaleFactor: number,
  targetSystem: UnitSystem,
): FormattedQuantityResult {
  const effectiveFactor = scaleFactor <= 0 ? 1 : scaleFactor;

  if (quantity === null || quantity === undefined) {
    const emptyUnitLabel = getUnitLabel(unit, customUnitName, null);
    return {
      quantity: null,
      maxQuantity: null,
      unit: unit || null,
      unitLabel: emptyUnitLabel,
      formattedAmount: '',
      formattedMeasurement: emptyUnitLabel,
    };
  }

  const scaledQty = quantity * effectiveFactor;
  const scaledMaxQty =
    maxQuantity !== null && maxQuantity !== undefined ? maxQuantity * effectiveFactor : null;

  const converted = convertUnit(scaledQty, scaledMaxQty, unit, targetSystem, effectiveFactor);
  const isMetric = isMetricUnit(converted.unit);
  const unitLabel = getUnitLabel(converted.unit, customUnitName, converted.qty);
  const formattedAmount = formatAmount(converted.qty, converted.maxQty, isMetric);
  const practical =
    !isMetric &&
    (effectiveFactor !== 1 || targetSystem === 'UsCustomary') &&
    (scaledMaxQty == null || scaledMaxQty <= scaledQty)
      ? practicalVolume(
          unit === 'Ml' || unit === 'L'
            ? (scaledQty * (unit === 'L' ? 1000 : 1)) / 4.92892
            : scaledQty,
          unit === 'Ml' || unit === 'L' ? 'Tsp' : unit,
        )
      : null;

  return {
    quantity: converted.qty,
    maxQuantity: converted.maxQty ?? null,
    unit: converted.unit ?? null,
    unitLabel,
    formattedAmount,
    formattedMeasurement: practical ?? [formattedAmount, unitLabel].filter(Boolean).join(' '),
  };
}

export function formatCountdown(totalSeconds: number): string {
  const safeSeconds = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(safeSeconds / 3600);
  const minutes = Math.floor((safeSeconds % 3600) / 60);
  const seconds = safeSeconds % 60;

  const pad = (n: number) => n.toString().padStart(2, '0');

  if (hours > 0) {
    return `${hours}:${pad(minutes)}:${pad(seconds)}`;
  }
  return `${pad(minutes)}:${pad(seconds)}`;
}

export function getDimensionUnitLabel(
  unit: DimensionUnit,
  value: number,
  hyphenated = false,
): string {
  const singular = hyphenated || Math.abs(value - 1) < 1e-6;
  switch (unit) {
    case 'inch':
      return singular ? 'inch' : 'inches';
    case 'foot':
      return singular ? 'foot' : 'feet';
    case 'yard':
      return singular ? 'yard' : 'yards';
    case 'mm':
      return 'mm';
    case 'cm':
      return 'cm';
    case 'm':
      return 'm';
    default:
      return singular ? 'inch' : 'inches';
  }
}

function roundTo(val: number, decimals: number): number {
  const factor = Math.pow(10, decimals);
  return Math.round(val * factor) / factor;
}

export function convertDimension(
  value: number,
  maxValue: number | null | undefined,
  unit: DimensionUnit,
  targetSystem: UnitSystem,
): { value: number; maxValue: number | null; unit: DimensionUnit; converted: boolean } {
  const cleanMax = maxValue != null && maxValue > value ? maxValue : null;
  if (targetSystem === 'Original') {
    return { value, maxValue: cleanMax, unit, converted: false };
  }

  if (targetSystem === 'Metric') {
    switch (unit) {
      case 'inch': {
        const cm = value * 2.54;
        if (cm < 1) {
          return {
            value: roundTo(value * 25.4, 0),
            maxValue: cleanMax != null ? roundTo(cleanMax * 25.4, 0) : null,
            unit: 'mm',
            converted: true,
          };
        }
        return {
          value: roundTo(cm, 1),
          maxValue: cleanMax != null ? roundTo(cleanMax * 2.54, 1) : null,
          unit: 'cm',
          converted: true,
        };
      }
      case 'foot':
        return {
          value: roundTo(value * 30.48, 0),
          maxValue: cleanMax != null ? roundTo(cleanMax * 30.48, 0) : null,
          unit: 'cm',
          converted: true,
        };
      case 'yard':
        return {
          value: roundTo(value * 0.9144, 2),
          maxValue: cleanMax != null ? roundTo(cleanMax * 0.9144, 2) : null,
          unit: 'm',
          converted: true,
        };
      default:
        return { value, maxValue: cleanMax, unit, converted: false };
    }
  }

  if (targetSystem === 'UsCustomary') {
    switch (unit) {
      case 'mm':
        return {
          value: roundTo(value / 25.4, 2),
          maxValue: cleanMax != null ? roundTo(cleanMax / 25.4, 2) : null,
          unit: 'inch',
          converted: true,
        };
      case 'cm': {
        const inches = value / 2.54;
        if (inches >= 24) {
          return {
            value: roundTo(value / 30.48, 1),
            maxValue: cleanMax != null ? roundTo(cleanMax / 30.48, 1) : null,
            unit: 'foot',
            converted: true,
          };
        }
        return {
          value: roundTo(inches, 1),
          maxValue: cleanMax != null ? roundTo(cleanMax / 2.54, 1) : null,
          unit: 'inch',
          converted: true,
        };
      }
      case 'm':
        return {
          value: roundTo(value / 0.9144, 2),
          maxValue: cleanMax != null ? roundTo(cleanMax / 0.9144, 2) : null,
          unit: 'yard',
          converted: true,
        };
      default:
        return { value, maxValue: cleanMax, unit, converted: false };
    }
  }

  return { value, maxValue: cleanMax, unit, converted: false };
}

export function formatDimensionToken(
  value: number,
  maxValue: number | null | undefined,
  unit: DimensionUnit,
  hyphenated = false,
  targetSystem: UnitSystem = 'Original',
): { text: string; scaled: boolean } {
  const conv = convertDimension(value, maxValue, unit, targetSystem);
  const isMetric = conv.unit === 'mm' || conv.unit === 'cm' || conv.unit === 'm';
  const valText = formatAmount(conv.value, conv.maxValue, isMetric);
  const label = getDimensionUnitLabel(conv.unit, conv.maxValue ?? conv.value, hyphenated);
  return {
    text: hyphenated ? `${valText}-${label}` : `${valText} ${label}`,
    scaled: conv.converted,
  };
}

export function formatIngredientReplacement(
  rep: Pick<
    RecipeIngredientReplacementDto | IngestionDraftReplacementDto,
    'displayName' | 'quantity' | 'maxQuantity' | 'unit' | 'customUnitName' | 'preparationNote'
  >,
  scaleFactor = 1,
  targetSystem: UnitSystem = 'Original',
): string {
  const scaled =
    rep.quantity != null
      ? scaleAndConvert(
          rep.quantity,
          rep.maxQuantity ?? null,
          rep.unit ?? null,
          rep.customUnitName ?? null,
          scaleFactor,
          targetSystem,
        ).formattedMeasurement.trim()
      : '';
  const prep = rep.preparationNote?.trim() ? ` (${rep.preparationNote.trim()})` : '';
  return [scaled, rep.displayName].filter(Boolean).join(' ') + prep;
}
