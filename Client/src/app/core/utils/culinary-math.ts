import { MeasurementUnit, RecipeMeasurement, UnitSystem } from '../models/recipe.models';

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
  const fromVolume = getVolumeMlFactor(from.unit);
  const toVolume = getVolumeMlFactor(to.unit);
  if (fromVolume !== null && toVolume !== null) return fromVolume / toVolume;
  const fromWeight = getWeightGFactor(from.unit);
  const toWeight = getWeightGFactor(to.unit);
  return fromWeight !== null && toWeight !== null ? fromWeight / toWeight : null;
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

export function formatStepIngredient(
  ingredient: IngredientMeasurements & { displayName: string },
  amount: RecipeMeasurement | null | undefined,
  factor: number,
  system: UnitSystem,
): string {
  const scaled = scaleStepIngredient(ingredient, amount, factor, system);
  return [scaled.formattedMeasurement, ingredient.displayName].filter(Boolean).join(' ');
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
