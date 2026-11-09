import { describe, expect, it } from 'vitest';
import {
  formatAmount,
  formatCountdown,
  formatDuration,
  formatMinutes,
  formatNumber,
  getUnitLabel,
  scaleAndConvert,
  scaleIngredient,
  scaleStepIngredient,
  formatStepIngredient,
  IngredientMeasurements,
} from './culinary-math';

describe('culinary-math', () => {
  describe('recipe-specific equivalents and step portions', () => {
    const flour: IngredientMeasurements & { displayName: string } = {
      displayName: 'Bread flour',
      quantity: 5,
      unit: 'Cup',
      alternativeMeasurement: { quantity: 25, unit: 'Oz' },
      preferAlternativeMeasurement: true,
    };
    it.each([
      [0.5, '71'],
      [1, '142'],
      [2, '283'],
    ])('scales a one-cup step by %s using the authored weight ratio', (factor, grams) => {
      expect(
        formatStepIngredient(flour, { quantity: 1, unit: 'Cup' }, Number(factor), 'Metric'),
      ).toBe(`${grams} g Bread flour`);
    });
    it('keeps whole-recipe totals separate from step portions', () => {
      expect(scaleIngredient(flour, 1, 'Original').quantity).toBe(25);
      expect(scaleStepIngredient(flour, { quantity: 1, unit: 'Cup' }, 1, 'Original').quantity).toBe(
        5,
      );
      expect(formatStepIngredient(flour, null, 2, 'Metric')).toBe('Bread flour');
    });
    it('converts same-dimension units and the reverse source ratio', () => {
      expect(
        scaleStepIngredient(flour, { quantity: 16, unit: 'Tbsp' }, 1, 'Original').quantity,
      ).toBeCloseTo(5, 4);
      expect(
        scaleStepIngredient(
          { ...flour, preferAlternativeMeasurement: false },
          { quantity: 5, unit: 'Oz' },
          1,
          'Original',
        ).quantity,
      ).toBe(1);
    });
    it('does not invent density conversions or reinterpret incompatible amounts', () => {
      expect(
        scaleStepIngredient(
          { ...flour, alternativeMeasurement: null },
          { quantity: 1, unit: 'Cup' },
          1,
          'Metric',
        ).unit,
      ).toBe('Ml');
      expect(scaleStepIngredient(flour, { quantity: 1, unit: 'Pinch' }, 2, 'Metric').unit).toBe(
        'Pinch',
      );
    });
    it('honors authored cup equivalents without changing an amount already on the metric side', () => {
      const ingredient: IngredientMeasurements = {
        quantity: 1,
        unit: 'Cup',
        alternativeMeasurement: { quantity: 250, unit: 'Ml' },
        preferAlternativeMeasurement: true,
      };
      expect(
        scaleStepIngredient(ingredient, { quantity: 1, unit: 'Cup' }, 1, 'Metric').quantity,
      ).toBe(250);
      expect(
        scaleStepIngredient(ingredient, { quantity: 1, unit: 'L' }, 1, 'Original').quantity,
      ).toBe(1);
    });
  });
  describe('formatNumber & fractions', () => {
    it('should format whole numbers without fractional decimals', () => {
      expect(formatNumber(2, false)).toBe('2');
      expect(formatNumber(0, false)).toBe('0');
    });

    it('should snap near-fractions to standard culinary fractions', () => {
      expect(formatNumber(0.5, false)).toBe('1/2');
      expect(formatNumber(0.24, false)).toBe('1/4');
      expect(formatNumber(0.33, false)).toBe('1/3');
      expect(formatNumber(0.75, false)).toBe('3/4');
      expect(formatNumber(1.5, false)).toBe('1 1/2');
      expect(formatNumber(2.25, false)).toBe('2 1/4');
    });

    it('should carry over fractions >= 0.96 to the next integer', () => {
      expect(formatNumber(0.97, false)).toBe('1');
      expect(formatNumber(2.98, false)).toBe('3');
    });

    it('should format small non-zero amounts without rounding to 0', () => {
      expect(formatNumber(0.01, false)).toBe('0.01');
      expect(formatNumber(0.02, false)).toBe('0.02');
    });

    it('should format metric numbers with rounding >= 10 and decimals < 10', () => {
      expect(formatNumber(250.3, true)).toBe('250');
      expect(formatNumber(15.7, true)).toBe('16');
      expect(formatNumber(1.5, true)).toBe('1.5');
      expect(formatNumber(2.25, true)).toBe('2.25');
    });
  });

  describe('formatAmount with ranges', () => {
    it('should format single values', () => {
      expect(formatAmount(2, null, false)).toBe('2');
    });

    it('should format range when max is greater than min', () => {
      expect(formatAmount(1, 2, false)).toBe('1–2');
      expect(formatAmount(0.5, 1, false)).toBe('1/2–1');
    });
  });

  describe('formatDuration', () => {
    it('should format seconds', () => {
      expect(formatDuration(45)).toBe('45 sec');
    });

    it('should format minutes', () => {
      expect(formatDuration(60)).toBe('1 min');
      expect(formatDuration(90)).toBe('1 min 30 sec');
      expect(formatDuration(900)).toBe('15 min');
    });

    it('should format hours and minutes', () => {
      expect(formatDuration(3600)).toBe('1 hr');
      expect(formatDuration(5400)).toBe('1 hr 30 min');
      expect(formatDuration(7200)).toBe('2 hr');
    });
  });

  describe('formatMinutes', () => {
    it('should return empty string for null, undefined, or negative values', () => {
      expect(formatMinutes(null)).toBe('');
      expect(formatMinutes(undefined)).toBe('');
      expect(formatMinutes(-5)).toBe('');
    });

    it('should format minutes under 1 hour', () => {
      expect(formatMinutes(0)).toBe('0 min');
      expect(formatMinutes(15)).toBe('15 min');
      expect(formatMinutes(45)).toBe('45 min');
      expect(formatMinutes(59)).toBe('59 min');
    });

    it('should format times of 1 hour or more into hours and minutes', () => {
      expect(formatMinutes(60)).toBe('1 hr');
      expect(formatMinutes(90)).toBe('1 hr 30 min');
      expect(formatMinutes(120)).toBe('2 hr');
      expect(formatMinutes(140)).toBe('2 hr 20 min');
      expect(formatMinutes(465)).toBe('7 hr 45 min');
    });
  });

  describe('getUnitLabel', () => {
    it('should pluralize cups, pinches, etc. when qty > 1.05', () => {
      expect(getUnitLabel('Cup', null, 1)).toBe('cup');
      expect(getUnitLabel('Cup', null, 2)).toBe('cups');
      expect(getUnitLabel('Pinch', null, 1)).toBe('pinch');
      expect(getUnitLabel('Pinch', null, 2)).toBe('pinches');
      expect(getUnitLabel('Whole', null, 2)).toBe('');
      expect(getUnitLabel('Custom', 'heaping scoop', 1)).toBe('heaping scoop');
    });

    it('should suppress placeholder custom unit names like null, none, n/a', () => {
      expect(getUnitLabel('Custom', 'null', null)).toBe('');
      expect(getUnitLabel('Custom', 'none', null)).toBe('');
      expect(getUnitLabel('Custom', 'N/A', null)).toBe('');
      expect(getUnitLabel('Custom', 'undefined', null)).toBe('');
    });
  });

  describe('scaleAndConvert', () => {
    it('should double quantities when scaling 2x with Original unit system', () => {
      const res = scaleAndConvert(1, null, 'Cup', null, 2, 'Original');
      expect(res.quantity).toBe(2);
      expect(res.unit).toBe('Cup');
      expect(res.unitLabel).toBe('cups');
      expect(res.formattedAmount).toBe('2');
    });

    it('should convert Cups to Metric ml/l', () => {
      const res = scaleAndConvert(1, null, 'Cup', null, 1, 'Metric');
      expect(res.unit).toBe('Ml');
      expect(res.unitLabel).toBe('ml');
      // 1 cup is ~236.6 ml -> rounded >= 10 is 237
      expect(res.formattedAmount).toBe('237');
    });

    it('should convert Grams to US Oz/Lb', () => {
      const res = scaleAndConvert(500, null, 'G', null, 1, 'UsCustomary');
      expect(res.unit).toBe('Lb');
      expect(res.unitLabel).toBe('lb');
      // 500g is ~1.1 lb -> snaps near 1 1/8 lb or formatted
      expect(res.quantity).toBeCloseTo(1.102, 2);
    });

    it('should preserve count/culinary units without converting system', () => {
      const res = scaleAndConvert(2, null, 'Clove', null, 2, 'Metric');
      expect(res.quantity).toBe(4);
      expect(res.unit).toBe('Clove');
      expect(res.unitLabel).toBe('cloves');
      expect(res.formattedAmount).toBe('4');
    });
  });

  describe('formatCountdown', () => {
    it('should format seconds into MM:SS', () => {
      expect(formatCountdown(900)).toBe('15:00');
      expect(formatCountdown(65)).toBe('01:05');
      expect(formatCountdown(9)).toBe('00:09');
      expect(formatCountdown(0)).toBe('00:00');
      expect(formatCountdown(-5)).toBe('00:00');
    });

    it('should format hours into H:MM:SS', () => {
      expect(formatCountdown(3600)).toBe('1:00:00');
      expect(formatCountdown(3905)).toBe('1:05:05');
      expect(formatCountdown(7322)).toBe('2:02:02');
    });
  });
});
