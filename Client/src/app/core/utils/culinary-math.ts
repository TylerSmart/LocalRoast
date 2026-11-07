import {
  MeasurementUnit,
  RecipeMeasurement,
  RecipeStepDto,
  UnitSystem,
} from '../models/recipe.models';

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

function measurementUnitFactor(from: RecipeMeasurement, to: RecipeMeasurement): number | null {
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
  return [scaled.formattedAmount, scaled.unitLabel, ingredient.displayName]
    .filter(Boolean)
    .join(' ');
}

export function stepIngredientCallouts(
  step: Pick<
    RecipeStepDto,
    'ingredientIds' | 'ingredientAmounts' | 'instruction' | 'inlineAmounts'
  >,
  ingredients: readonly (IngredientMeasurements & { id: string; displayName: string })[],
  factor: number,
  system: UnitSystem,
): { id: string; label: string }[] {
  const instruction = step.instruction ?? '';
  const spanned = new Set(
    (step.inlineAmounts ?? [])
      .filter(
        (a) =>
          a.kind === 'Ingredient' &&
          a.ingredientId &&
          a.start >= 0 &&
          a.length > 0 &&
          a.start + a.length <= instruction.length,
      )
      .map((a) => a.ingredientId),
  );
  return step.ingredientIds.flatMap((id) => {
    const ingredient = spanned.has(id) ? undefined : ingredients.find((i) => i.id === id);
    return ingredient
      ? [
          {
            id,
            label: formatStepIngredient(
              ingredient,
              step.ingredientAmounts?.find((a) => a.ingredientId === id)?.measurement,
              factor,
              system,
            ),
          },
        ]
      : [];
  });
}

export interface FormattedQuantityResult {
  quantity: number | null;
  maxQuantity: number | null;
  unit: MeasurementUnit | null;
  unitLabel: string;
  formattedAmount: string;
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

function convertUnit(
  qty: number,
  maxQty: number | null | undefined,
  unit: MeasurementUnit | null | undefined,
  targetSystem: UnitSystem,
): { qty: number; maxQty: number | null | undefined; unit: MeasurementUnit | null | undefined } {
  if (!unit || targetSystem === 'Original') {
    return { qty, maxQty, unit };
  }

  if (targetSystem === 'Metric') {
    const mlFactor = getVolumeMlFactor(unit);
    if (mlFactor !== null) {
      const ml = qty * mlFactor;
      const maxMl = maxQty !== null && maxQty !== undefined ? maxQty * mlFactor : null;
      if (ml >= 1000) {
        return { qty: ml / 1000, maxQty: maxMl !== null ? maxMl / 1000 : null, unit: 'L' };
      }
      return { qty: ml, maxQty: maxMl, unit: 'Ml' };
    }

    const gFactor = getWeightGFactor(unit);
    if (gFactor !== null) {
      const g = qty * gFactor;
      const maxG = maxQty !== null && maxQty !== undefined ? maxQty * gFactor : null;
      if (g >= 1000) {
        return { qty: g / 1000, maxQty: maxG !== null ? maxG / 1000 : null, unit: 'Kg' };
      }
      return { qty: g, maxQty: maxG, unit: 'G' };
    }
  } else if (targetSystem === 'UsCustomary') {
    if (unit === 'Ml' || unit === 'L') {
      const ml = unit === 'L' ? qty * 1000 : qty;
      const maxMl =
        maxQty !== null && maxQty !== undefined ? (unit === 'L' ? maxQty * 1000 : maxQty) : null;

      if (ml < 14.7868) {
        const f = 4.92892;
        return { qty: ml / f, maxQty: maxMl !== null ? maxMl / f : null, unit: 'Tsp' };
      }
      if (ml < 59.147) {
        const f = 14.7868;
        return { qty: ml / f, maxQty: maxMl !== null ? maxMl / f : null, unit: 'Tbsp' };
      }
      if (ml < 946.353) {
        const f = 236.588;
        return { qty: ml / f, maxQty: maxMl !== null ? maxMl / f : null, unit: 'Cup' };
      }
      const qtf = 946.353;
      return { qty: ml / qtf, maxQty: maxMl !== null ? maxMl / qtf : null, unit: 'Qt' };
    }

    if (unit === 'G' || unit === 'Kg') {
      const g = unit === 'Kg' ? qty * 1000 : qty;
      const maxG =
        maxQty !== null && maxQty !== undefined ? (unit === 'Kg' ? maxQty * 1000 : maxQty) : null;

      if (g < 453.592) {
        const ozf = 28.3495;
        return { qty: g / ozf, maxQty: maxG !== null ? maxG / ozf : null, unit: 'Oz' };
      }
      const lbf = 453.592;
      return { qty: g / lbf, maxQty: maxG !== null ? maxG / lbf : null, unit: 'Lb' };
    }
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
    };
  }

  const scaledQty = quantity * effectiveFactor;
  const scaledMaxQty =
    maxQuantity !== null && maxQuantity !== undefined ? maxQuantity * effectiveFactor : null;

  const converted = convertUnit(scaledQty, scaledMaxQty, unit, targetSystem);
  const isMetric = isMetricUnit(converted.unit);
  const unitLabel = getUnitLabel(converted.unit, customUnitName, converted.qty);
  const formattedAmount = formatAmount(converted.qty, converted.maxQty, isMetric);

  return {
    quantity: converted.qty,
    maxQuantity: converted.maxQty ?? null,
    unit: converted.unit ?? null,
    unitLabel,
    formattedAmount,
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
