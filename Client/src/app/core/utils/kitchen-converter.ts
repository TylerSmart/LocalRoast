import { DimensionUnit } from '../models/step-token.models';
import {
  formatNumber,
  getDimensionUnitLabel,
  getUnitLabel,
  isMetricUnit,
  measurementUnitFactor,
} from './culinary-math';
import {
  BAKING_PANS,
  BakingPanOption,
  CLASSIC_CULINARY_RATIOS,
  CONVERTER_UNITS,
  COOKING_RATIOS_AND_TIMINGS,
  CULINARY_GLOSSARY,
  CULINARY_SUBSTITUTIONS,
  ClassicRatioEntry,
  ConverterUnit,
  ConverterUnitOption,
  CookingTimingEntry,
  FOOD_TEMPERATURES,
  FoodTemperatureEntry,
  GlossaryEntry,
  INGREDIENT_DENSITIES,
  IngredientDensityOption,
  QUICK_EQUIVALENTS,
  QuickEquivalentItem,
  SubstitutionEntry,
} from './kitchen-reference-data';

const VOLUME_ML_PER_UNIT: Record<string, number> = {
  Tsp: 4.92892,
  Tbsp: 14.7868,
  FlOz: 29.5735,
  Cup: 236.588,
  Pt: 473.176,
  Qt: 946.353,
  Gal: 3785.41,
  Ml: 1,
  L: 1000,
};

const WEIGHT_G_PER_UNIT: Record<string, number> = {
  G: 1,
  Kg: 1000,
  Oz: 28.3495,
  Lb: 453.592,
};

const LENGTH_MM_PER_UNIT: Record<DimensionUnit, number> = {
  mm: 1,
  cm: 10,
  m: 1000,
  inch: 25.4,
  foot: 304.8,
  yard: 914.4,
};

const CONVERTER_UNIT_MAP = new Map<ConverterUnit, ConverterUnitOption>(
  CONVERTER_UNITS.map((u) => [u.value, u]),
);

const INGREDIENT_DENSITY_MAP = new Map<string, IngredientDensityOption>(
  INGREDIENT_DENSITIES.map((d) => [d.id, d]),
);

const BAKING_PAN_MAP = new Map<string, BakingPanOption>(BAKING_PANS.map((p) => [p.id, p]));

export interface EquivalentChip {
  unit: ConverterUnit;
  label: string;
  value: number;
  formatted: string;
}

export interface CulinaryConversionResult {
  amount: number;
  fromUnit: ConverterUnit;
  toUnit: ConverterUnit;
  convertedValue: number;
  formattedValue: string;
  formattedWithUnit: string;
  practicalUsVolume: string | null;
  butterSticksNote: string | null;
  usedDensityBridge: boolean;
  usedDefaultWaterDensity: boolean;
  ingredientLabel: string;
  ingredientNote: string | null;
  equivalents: EquivalentChip[];
}

export interface OvenTemperatureInfo {
  fahrenheit: number;
  celsius: number;
  convectionF: number;
  convectionC: number;
  heatLabel: string;
  gasMark: string | null;
}

export interface LengthEquivalentChip {
  unit: DimensionUnit;
  label: string;
  value: number;
  formatted: string;
}

export interface PanScalingResult {
  fromPan: BakingPanOption;
  toPan: BakingPanOption;
  areaMultiplier: number;
  volumeMultiplier: number;
  recommendedMultiplier: number;
  percentChange: number;
  advice: string;
}

export interface KitchenReferenceSearchResults {
  query: string;
  temperatures: FoodTemperatureEntry[];
  glossary: GlossaryEntry[];
  substitutions: SubstitutionEntry[];
  timings: CookingTimingEntry[];
  ratios: ClassicRatioEntry[];
  equivalents: QuickEquivalentItem[];
  totalCount: number;
}

function roundTo(val: number, decimals: number): number {
  const factor = Math.pow(10, decimals);
  return Math.round(val * factor) / factor;
}

function formatConverterNumber(value: number, unit: ConverterUnit): string {
  if (!Number.isFinite(value) || value <= 0) {
    return '0';
  }
  const metric = isMetricUnit(unit);
  if (metric) {
    if (value >= 100) return Math.round(value).toString();
    if (value >= 10) return roundTo(value, 1).toString();
    return roundTo(value, 2).toString();
  }
  if (value >= 100) {
    return roundTo(value, 1).toString();
  }
  return formatNumber(value, false);
}

function formatPracticalUsScoop(cupsTotal: number): string | null {
  if (!Number.isFinite(cupsTotal) || cupsTotal <= 0 || cupsTotal > 16) {
    return null;
  }
  const quarterTspTotal = Math.round(cupsTotal * 48 * 4);
  if (quarterTspTotal < 1 || quarterTspTotal > 1536) {
    return null;
  }
  // Only show if exact/near quarter-teaspoon precision makes a meaningful scoop hint
  const diff = Math.abs(cupsTotal * 48 * 4 - quarterTspTotal);
  if (diff > 0.25) {
    return null;
  }

  let rem = quarterTspTotal;
  const wholeCups = Math.floor(rem / 192);
  rem -= wholeCups * 192;

  // Prefer common measuring cup breakdown for the fractional cup remainder
  const parts: string[] = [];
  if (wholeCups > 0) {
    parts.push(`${wholeCups} ${wholeCups === 1 ? 'cup' : 'cups'}`);
  }

  const cupScoops: [number, string][] = [
    [144, '3/4 cup'],
    [128, '2/3 cup'],
    [96, '1/2 cup'],
    [64, '1/3 cup'],
    [48, '1/4 cup'],
  ];
  for (const [ticks, label] of cupScoops) {
    if (rem >= ticks) {
      parts.push(label);
      rem -= ticks;
      break;
    }
  }

  const tbspCount = Math.floor(rem / 12);
  rem -= tbspCount * 12;
  if (tbspCount > 0) {
    parts.push(`${tbspCount} tbsp`);
  }

  if (rem > 0) {
    const tspVal = rem / 4;
    parts.push(`${formatNumber(tspVal, false)} tsp`);
  }

  return parts.length > 1 ? parts.join(' + ') : null;
}

export function getConverterUnitKind(unit: ConverterUnit): 'volume' | 'weight' {
  return CONVERTER_UNIT_MAP.get(unit)?.kind ?? 'volume';
}

export function convertCulinaryMeasurement(
  amount: number,
  fromUnit: ConverterUnit,
  toUnit: ConverterUnit,
  ingredientId: string | null | undefined,
): CulinaryConversionResult {
  const safeAmount = Number.isFinite(amount) && amount > 0 ? amount : 0;
  const fromKind = getConverterUnitKind(fromUnit);
  const toKind = getConverterUnitKind(toUnit);
  const usedDensityBridge = fromKind !== toKind;

  const hasExplicitIngredient = Boolean(ingredientId && ingredientId !== 'none');
  const density =
    (hasExplicitIngredient ? INGREDIENT_DENSITY_MAP.get(ingredientId!) : undefined) ??
    INGREDIENT_DENSITY_MAP.get('water')!;
  const usedDefaultWaterDensity = usedDensityBridge && !hasExplicitIngredient;
  const gramsPerMl = density.gramsPerCup / VOLUME_ML_PER_UNIT['Cup'];

  let convertedValue = safeAmount;

  if (safeAmount > 0) {
    if (fromKind === toKind) {
      const exactFactor = measurementUnitFactor(
        { quantity: safeAmount, unit: fromUnit },
        { quantity: 1, unit: toUnit },
      );
      if (exactFactor !== null) {
        convertedValue = safeAmount * exactFactor;
      }
    } else if (fromKind === 'volume' && toKind === 'weight') {
      const ml = safeAmount * VOLUME_ML_PER_UNIT[fromUnit];
      const grams = ml * gramsPerMl;
      convertedValue = grams / WEIGHT_G_PER_UNIT[toUnit];
    } else {
      const grams = safeAmount * WEIGHT_G_PER_UNIT[fromUnit];
      const ml = grams / gramsPerMl;
      convertedValue = ml / VOLUME_ML_PER_UNIT[toUnit];
    }
  }

  const formattedValue = formatConverterNumber(convertedValue, toUnit);
  const unitLabel = getUnitLabel(toUnit, null, convertedValue);
  const formattedWithUnit = [formattedValue, unitLabel].filter(Boolean).join(' ');

  // Practical US scoop breakdown when converting into US volume
  const isUsVolumeTarget =
    toKind === 'volume' && CONVERTER_UNIT_MAP.get(toUnit)?.system === 'us' && convertedValue > 0;
  const targetMl =
    toKind === 'volume' ? convertedValue * VOLUME_ML_PER_UNIT[toUnit] : convertedValue * 0;
  const practicalUsVolume = isUsVolumeTarget
    ? formatPracticalUsScoop(targetMl / VOLUME_ML_PER_UNIT['Cup'])
    : null;

  // Butter stick helper when ingredient is butter
  let butterSticksNote: string | null = null;
  if (density.id === 'butter' && safeAmount > 0) {
    const grams =
      fromKind === 'weight'
        ? safeAmount * WEIGHT_G_PER_UNIT[fromUnit]
        : safeAmount * VOLUME_ML_PER_UNIT[fromUnit] * gramsPerMl;
    const sticks = grams / 113.5;
    if (sticks >= 0.125 && sticks <= 24) {
      const formattedSticks = formatNumber(sticks, false);
      const stickNoun = sticks > 1.05 ? 'US sticks' : 'US stick';
      butterSticksNote = `≈ ${formattedSticks} ${stickNoun} butter (8 tbsp / 113g each)`;
    }
  }

  // Compute all common equivalents for quick comparison table
  const equivalentUnits: ConverterUnit[] = [
    'Tsp',
    'Tbsp',
    'FlOz',
    'Cup',
    'Ml',
    'L',
    'G',
    'Oz',
    'Lb',
  ];
  const equivalents: EquivalentChip[] = equivalentUnits.map((u) => {
    const uKind = getConverterUnitKind(u);
    let val = 0;
    if (safeAmount > 0) {
      if (fromKind === uKind) {
        const f = measurementUnitFactor({ quantity: 1, unit: fromUnit }, { quantity: 1, unit: u });
        val = f !== null ? safeAmount * f : 0;
      } else if (fromKind === 'volume') {
        const grams = safeAmount * VOLUME_ML_PER_UNIT[fromUnit] * gramsPerMl;
        val = grams / WEIGHT_G_PER_UNIT[u];
      } else {
        const ml = (safeAmount * WEIGHT_G_PER_UNIT[fromUnit]) / gramsPerMl;
        val = ml / VOLUME_ML_PER_UNIT[u];
      }
    }
    const formatted = formatConverterNumber(val, u);
    const shortLabel = CONVERTER_UNIT_MAP.get(u)?.shortLabel ?? u;
    return {
      unit: u,
      label: shortLabel,
      value: val,
      formatted: `${formatted} ${shortLabel}`,
    };
  });

  return {
    amount: safeAmount,
    fromUnit,
    toUnit,
    convertedValue,
    formattedValue,
    formattedWithUnit,
    practicalUsVolume,
    butterSticksNote,
    usedDensityBridge,
    usedDefaultWaterDensity,
    ingredientLabel: density.label,
    ingredientNote: density.notes ?? null,
    equivalents,
  };
}

export function fahrenheitToCelsius(fahrenheit: number): number {
  if (!Number.isFinite(fahrenheit)) return 0;
  return roundTo(((fahrenheit - 32) * 5) / 9, 1);
}

export function celsiusToFahrenheit(celsius: number): number {
  if (!Number.isFinite(celsius)) return 32;
  return roundTo((celsius * 9) / 5 + 32, 1);
}

export function describeOvenTemperature(fahrenheit: number): OvenTemperatureInfo {
  const f = Number.isFinite(fahrenheit) ? fahrenheit : 350;
  const c = fahrenheitToCelsius(f);
  const convF = Math.round(f - 25);
  const convC = Math.round(fahrenheitToCelsius(convF));

  let heatLabel: string;
  if (f < 225) heatLabel = 'Warming / Proofing';
  else if (f < 275) heatLabel = 'Very Slow / Low Oven';
  else if (f < 325) heatLabel = 'Slow Oven';
  else if (f < 375) heatLabel = 'Moderate Oven';
  else if (f < 425) heatLabel = 'Moderately Hot Oven';
  else if (f < 475) heatLabel = 'Hot Oven';
  else heatLabel = 'Very Hot / Broil Range';

  let gasMark: string | null = null;
  if (f >= 265 && f < 290) gasMark = 'Gas Mark 1';
  else if (f >= 290 && f < 315) gasMark = 'Gas Mark 2';
  else if (f >= 315 && f < 340) gasMark = 'Gas Mark 3';
  else if (f >= 340 && f < 365) gasMark = 'Gas Mark 4';
  else if (f >= 365 && f < 390) gasMark = 'Gas Mark 5';
  else if (f >= 390 && f < 415) gasMark = 'Gas Mark 6';
  else if (f >= 415 && f < 440) gasMark = 'Gas Mark 7';
  else if (f >= 440 && f < 465) gasMark = 'Gas Mark 8';
  else if (f >= 465 && f <= 500) gasMark = 'Gas Mark 9';

  return {
    fahrenheit: roundTo(f, 1),
    celsius: c,
    convectionF: convF,
    convectionC: convC,
    heatLabel,
    gasMark,
  };
}

export function convertLengthDimension(
  amount: number,
  fromUnit: DimensionUnit,
): LengthEquivalentChip[] {
  const safeAmount = Number.isFinite(amount) && amount > 0 ? amount : 0;
  const mm = safeAmount * LENGTH_MM_PER_UNIT[fromUnit];
  const units: DimensionUnit[] = ['inch', 'foot', 'mm', 'cm', 'm'];

  return units.map((unit) => {
    const val = mm / LENGTH_MM_PER_UNIT[unit];
    const isMetric = unit === 'mm' || unit === 'cm' || unit === 'm';
    const formattedVal =
      val <= 0
        ? '0'
        : isMetric
          ? roundTo(val, val >= 100 ? 0 : val >= 10 ? 1 : 2).toString()
          : formatNumber(val, false);
    const label = getDimensionUnitLabel(unit, val);
    return {
      unit,
      label,
      value: val,
      formatted: `${formattedVal} ${label}`,
    };
  });
}

export function calculatePanScaling(fromPanId: string, toPanId: string): PanScalingResult {
  const fromPan = BAKING_PAN_MAP.get(fromPanId) ?? BAKING_PANS[1];
  const toPan = BAKING_PAN_MAP.get(toPanId) ?? BAKING_PANS[6];

  const areaMultiplier = roundTo(toPan.areaSqIn / fromPan.areaSqIn, 2);
  const volumeMultiplier = roundTo(toPan.volumeCups / fromPan.volumeCups, 2);
  const recommendedMultiplier = roundTo((areaMultiplier + volumeMultiplier) / 2, 2);
  const percentChange = Math.round((recommendedMultiplier - 1) * 100);

  let advice = 'Same batter capacity — keep the recipe’s baking temperature and time.';
  if (recommendedMultiplier >= 1.15) {
    advice =
      'Fill pan 1/2 to 2/3 full; check earlier if batter layer is thinner, or add 5–10 min if deeper.';
  } else if (recommendedMultiplier <= 0.85) {
    advice =
      'Smaller pan — avoid overfilling past 2/3 full (bake extra batter as cupcakes) and check doneness 5–10 min early.';
  } else if (fromPan.id !== toPan.id) {
    advice =
      'Very close in capacity — keep recipe quantities the same and start checking 3–5 min early if pan depth differs.';
  }

  return {
    fromPan,
    toPan,
    areaMultiplier,
    volumeMultiplier,
    recommendedMultiplier,
    percentChange,
    advice,
  };
}

function matchesAllTokens(haystack: string, tokens: string[]): boolean {
  const lower = haystack.toLowerCase();
  return tokens.every((t) => lower.includes(t));
}

export function searchKitchenReference(rawQuery: string): KitchenReferenceSearchResults {
  const normalized = rawQuery.trim().toLowerCase();
  if (!normalized) {
    return {
      query: '',
      temperatures: [],
      glossary: [],
      substitutions: [],
      timings: [],
      ratios: [],
      equivalents: [],
      totalCount: 0,
    };
  }

  const tokens = normalized.split(/\s+/).filter(Boolean);

  const temperatures = FOOD_TEMPERATURES.filter((t) =>
    matchesAllTokens(
      `${t.item} ${t.donenessOrStage} ${t.category} ${t.cueNote} ${t.tempF} ${t.keywords.join(' ')}`,
      tokens,
    ),
  );

  const glossary = CULINARY_GLOSSARY.filter((g) =>
    matchesAllTokens(
      `${g.term} ${g.category} ${g.specBadge ?? ''} ${g.definition} ${g.chefTip ?? ''} ${g.keywords.join(' ')}`,
      tokens,
    ),
  );

  const substitutions = CULINARY_SUBSTITUTIONS.filter((s) =>
    matchesAllTokens(
      `${s.ingredient} ${s.replacement} ${s.category} ${s.notes} ${s.keywords.join(' ')}`,
      tokens,
    ),
  );

  const timings = COOKING_RATIOS_AND_TIMINGS.filter((r) =>
    matchesAllTokens(
      `${r.item} ${r.group} ${r.ratioDisplay} ${r.cookTimeLabel} ${r.methodNote} ${r.keywords.join(' ')}`,
      tokens,
    ),
  );

  const ratios = CLASSIC_CULINARY_RATIOS.filter((r) =>
    matchesAllTokens(
      `${r.name} ${r.ratio} ${r.basis} ${r.example} ${r.note} ${r.keywords.join(' ')}`,
      tokens,
    ),
  );

  const equivalents = QUICK_EQUIVALENTS.filter((e) =>
    matchesAllTokens(`${e.title} ${e.formula} ${e.detail} ${e.keywords.join(' ')}`, tokens),
  );

  return {
    query: rawQuery.trim(),
    temperatures,
    glossary,
    substitutions,
    timings,
    ratios,
    equivalents,
    totalCount:
      temperatures.length +
      glossary.length +
      substitutions.length +
      timings.length +
      ratios.length +
      equivalents.length,
  };
}
