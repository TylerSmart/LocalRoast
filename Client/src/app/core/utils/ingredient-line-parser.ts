import { MeasurementUnit } from '../models/recipe.models';
import { stripOuterParentheses } from './culinary-math';

export interface ParsedIngredientLine {
  sectionName: string | null;
  quantity: number | null;
  maxQuantity: number | null;
  unit: MeasurementUnit | null;
  customUnitName: string | null;
  displayName: string;
  preparationNote: string | null;
  isOptional: boolean;
  originalRawText: string;
}

const UNICODE_FRACTIONS: Record<string, string> = {
  '¼': '1/4',
  '½': '1/2',
  '¾': '3/4',
  '⅐': '1/7',
  '⅑': '1/9',
  '⅒': '1/10',
  '⅓': '1/3',
  '⅔': '2/3',
  '⅕': '1/5',
  '⅖': '2/5',
  '⅗': '3/5',
  '⅘': '4/5',
  '⅙': '1/6',
  '⅚': '5/6',
  '⅛': '1/8',
  '⅜': '3/8',
  '⅝': '5/8',
  '⅞': '7/8',
};

const UNIT_ALIASES: Record<string, MeasurementUnit> = {
  tsp: 'Tsp',
  'tsp.': 'Tsp',
  teaspoon: 'Tsp',
  teaspoons: 'Tsp',
  tbsp: 'Tbsp',
  'tbsp.': 'Tbsp',
  tbs: 'Tbsp',
  tablespoon: 'Tbsp',
  tablespoons: 'Tbsp',
  'fl oz': 'FlOz',
  'fl. oz.': 'FlOz',
  'fluid ounce': 'FlOz',
  'fluid ounces': 'FlOz',
  cup: 'Cup',
  cups: 'Cup',
  c: 'Cup',
  'c.': 'Cup',
  pt: 'Pt',
  pint: 'Pt',
  pints: 'Pt',
  qt: 'Qt',
  quart: 'Qt',
  quarts: 'Qt',
  gal: 'Gal',
  gallon: 'Gal',
  gallons: 'Gal',
  ml: 'Ml',
  milliliter: 'Ml',
  milliliters: 'Ml',
  l: 'L',
  liter: 'L',
  liters: 'L',
  litre: 'L',
  litres: 'L',
  oz: 'Oz',
  'oz.': 'Oz',
  ounce: 'Oz',
  ounces: 'Oz',
  lb: 'Lb',
  'lb.': 'Lb',
  lbs: 'Lb',
  'lbs.': 'Lb',
  pound: 'Lb',
  pounds: 'Lb',
  g: 'G',
  gram: 'G',
  grams: 'G',
  kg: 'Kg',
  kilogram: 'Kg',
  kilograms: 'Kg',
  pinch: 'Pinch',
  pinches: 'Pinch',
  dash: 'Dash',
  dashes: 'Dash',
  clove: 'Clove',
  cloves: 'Clove',
  can: 'Can',
  cans: 'Can',
  bunch: 'Bunch',
  bunches: 'Bunch',
  slice: 'Slice',
  slices: 'Slice',
  whole: 'Whole',
};

function replaceUnicodeFractions(input: string): string {
  return input.replace(/[¼½¾⅐⅑⅒⅓⅔⅕⅖⅗⅘⅙⅚⅛⅜⅝⅞]/g, (ch) => ` ${UNICODE_FRACTIONS[ch]} `);
}

function parseNumberToken(raw: string): number | null {
  const cleaned = raw.trim().replace(/\s+/g, ' ');
  if (!cleaned) return null;

  const mixedMatch = /^(\d+)\s+(\d+)\/(\d+)$/.exec(cleaned);
  if (mixedMatch) {
    const whole = Number(mixedMatch[1]);
    const num = Number(mixedMatch[2]);
    const den = Number(mixedMatch[3]);
    return den > 0 ? Math.round((whole + num / den) * 10000) / 10000 : null;
  }

  const fracMatch = /^(\d+)\/(\d+)$/.exec(cleaned);
  if (fracMatch) {
    const num = Number(fracMatch[1]);
    const den = Number(fracMatch[2]);
    return den > 0 ? Math.round((num / den) * 10000) / 10000 : null;
  }

  const val = Number(cleaned);
  return Number.isFinite(val) && val > 0 ? val : null;
}

const NUM_EXPR = String.raw`(?:\d+\s+\d+\/\d+|\d+\/\d+|\d+(?:\.\d+)?)`;
const LEADING_QTY_REGEX = new RegExp(
  `^(${NUM_EXPR})(?:\\s*(?:-|–|—|to)\\s*(${NUM_EXPR}))?(?:\\s+|$)`,
  'i',
);

/**
 * Parses multi-line pasted ingredient text into structured rows.
 * Lines ending with ":" (e.g. "For the Crust:") act as section headers for subsequent rows.
 */
export function parseIngredientLines(rawBlock: string): ParsedIngredientLine[] {
  const results: ParsedIngredientLine[] = [];
  let currentSection: string | null = null;

  for (const rawLine of rawBlock.split(/\r?\n/)) {
    const original = rawLine.trim();
    if (!original) continue;

    // Strip leading bullets or checkboxes
    const stripped = original.replace(/^[-*•▪◦☐☑✓]+\s*/, '').trim();
    if (!stripped) continue;

    // Section heading detection (e.g., "For the Sauce:" or "Crust:")
    if (stripped.endsWith(':') && stripped.length <= 80 && !/^\d/.test(stripped)) {
      currentSection = stripped.slice(0, -1).trim() || null;
      continue;
    }

    let work = replaceUnicodeFractions(stripped).replace(/\s+/g, ' ').trim();
    let isOptional = false;

    if (/\(\s*optional\s*\)/i.test(work)) {
      isOptional = true;
      work = work.replace(/\(\s*optional\s*\)/gi, '').trim();
    } else if (/,\s*optional\.?$/i.test(work)) {
      isOptional = true;
      work = work.replace(/,\s*optional\.?$/i, '').trim();
    }

    let quantity: number | null = null;
    let maxQuantity: number | null = null;
    let unit: MeasurementUnit | null = null;

    const qtyMatch = LEADING_QTY_REGEX.exec(work);
    if (qtyMatch) {
      quantity = parseNumberToken(qtyMatch[1]);
      if (qtyMatch[2]) {
        const parsedMax = parseNumberToken(qtyMatch[2]);
        if (parsedMax !== null && (quantity === null || parsedMax >= quantity)) {
          maxQuantity = parsedMax;
        }
      }
      work = work.slice(qtyMatch[0].length).trim();

      // Check two-word unit first (e.g. "fl oz", "fluid ounces"), then single-word unit
      const twoWordMatch = /^([a-z]+\.?\s+[a-z]+\.?)\b\s*/i.exec(work);
      if (twoWordMatch) {
        const mapped = UNIT_ALIASES[twoWordMatch[1].toLowerCase()];
        if (mapped) {
          unit = mapped;
          work = work.slice(twoWordMatch[0].length).trim();
        }
      }

      if (!unit) {
        const oneWordMatch = /^([a-z]+\.?)\b\s*/i.exec(work);
        if (oneWordMatch) {
          const mapped = UNIT_ALIASES[oneWordMatch[1].toLowerCase()];
          if (mapped) {
            unit = mapped;
            work = work.slice(oneWordMatch[0].length).trim();
          }
        }
      }

      // Drop leading "of " after unit (e.g. "2 cups of flour")
      if (unit && /^of\s+/i.test(work)) {
        work = work.replace(/^of\s+/i, '').trim();
      }
    }

    let displayName = work;
    let preparationNote: string | null = null;

    const commaIndex = work.indexOf(',');
    if (commaIndex > 0) {
      displayName = work.slice(0, commaIndex).trim();
      preparationNote = stripOuterParentheses(work.slice(commaIndex + 1));
    }

    if (!displayName) {
      displayName = stripped;
    }

    results.push({
      sectionName: currentSection,
      quantity,
      maxQuantity,
      unit,
      customUnitName: null,
      displayName,
      preparationNote,
      isOptional,
      originalRawText: original,
    });
  }

  return results;
}
