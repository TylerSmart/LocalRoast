import { describe, expect, it } from 'vitest';
import {
  calculatePanScaling,
  celsiusToFahrenheit,
  convertCulinaryMeasurement,
  convertLengthDimension,
  describeOvenTemperature,
  fahrenheitToCelsius,
  searchKitchenReference,
} from './kitchen-converter';

describe('kitchen-converter', () => {
  describe('convertCulinaryMeasurement', () => {
    it('converts intra-system US volume accurately with practical scoop hints', () => {
      const cupsToTbsp = convertCulinaryMeasurement(0.5, 'Cup', 'Tbsp', 'water');
      expect(cupsToTbsp.convertedValue).toBeCloseTo(8, 4);
      expect(cupsToTbsp.formattedWithUnit).toBe('8 tbsp');
      expect(cupsToTbsp.usedDensityBridge).toBe(false);

      const tbspToCup = convertCulinaryMeasurement(10, 'Tbsp', 'Cup', 'water');
      expect(tbspToCup.convertedValue).toBeCloseTo(0.625, 4);
      expect(tbspToCup.formattedWithUnit).toBe('5/8 cup');
      expect(tbspToCup.practicalUsVolume).toBe('1/2 cup + 2 tbsp');
    });

    it('converts weight to weight across metric and US customary', () => {
      const ozToG = convertCulinaryMeasurement(4, 'Oz', 'G', 'water');
      expect(ozToG.convertedValue).toBeCloseTo(113.398, 2);
      expect(ozToG.formattedWithUnit).toBe('113 g');

      const kgToLb = convertCulinaryMeasurement(1, 'Kg', 'Lb', 'water');
      expect(kgToLb.convertedValue).toBeCloseTo(2.2046, 2);
    });

    it('uses ingredient density bridge for volume to weight conversions', () => {
      const flour = convertCulinaryMeasurement(1, 'Cup', 'G', 'ap-flour');
      expect(flour.usedDensityBridge).toBe(true);
      expect(flour.usedDefaultWaterDensity).toBe(false);
      expect(flour.convertedValue).toBeCloseTo(120, 4);
      expect(flour.formattedWithUnit).toBe('120 g');

      const butter = convertCulinaryMeasurement(1, 'Cup', 'G', 'butter');
      expect(butter.convertedValue).toBeCloseTo(227, 4);
      expect(butter.butterSticksNote).toContain('2 US sticks');

      const dcSalt = convertCulinaryMeasurement(1, 'Tbsp', 'G', 'salt-dc');
      const mortonSalt = convertCulinaryMeasurement(1, 'Tbsp', 'G', 'salt-morton');
      expect(dcSalt.convertedValue).toBeLessThan(mortonSalt.convertedValue);
    });

    it('falls back to water density and flags usedDefaultWaterDensity when no ingredient selected', () => {
      const result = convertCulinaryMeasurement(1, 'Cup', 'G', 'none');
      expect(result.usedDensityBridge).toBe(true);
      expect(result.usedDefaultWaterDensity).toBe(true);
      expect(result.convertedValue).toBeCloseTo(236.59, 1);
    });
  });

  describe('temperature conversions', () => {
    it('converts between Fahrenheit and Celsius accurately', () => {
      expect(fahrenheitToCelsius(212)).toBe(100);
      expect(fahrenheitToCelsius(350)).toBe(176.7);
      expect(celsiusToFahrenheit(180)).toBe(356);
    });

    it('describes oven heat, convection offset, and Gas Mark', () => {
      const info = describeOvenTemperature(350);
      expect(info.heatLabel).toBe('Moderate Oven');
      expect(info.convectionF).toBe(325);
      expect(info.gasMark).toBe('Gas Mark 4');
    });
  });

  describe('dimension & baking pan conversions', () => {
    it('converts inches to centimeters and millimeters', () => {
      const dims = convertLengthDimension(9, 'inch');
      const cm = dims.find((d) => d.unit === 'cm');
      expect(cm?.value).toBeCloseTo(22.86, 1);
      expect(cm?.formatted).toBe('22.9 cm');
    });

    it('computes pan scaling multipliers between baking pans', () => {
      const scale = calculatePanScaling('round-8', 'round-9');
      expect(scale.areaMultiplier).toBeCloseTo(1.26, 2);
      expect(scale.volumeMultiplier).toBeCloseTo(1.33, 2);
      expect(scale.recommendedMultiplier).toBeCloseTo(1.3, 2);

      const same = calculatePanScaling('square-8', 'square-8');
      expect(same.recommendedMultiplier).toBe(1);
    });
  });

  describe('searchKitchenReference', () => {
    it('matches multi-word queries across glossary, temps, swaps, and ratios', () => {
      const julienne = searchKitchenReference('julienne');
      expect(julienne.glossary.some((g) => g.term === 'Julienne')).toBe(true);

      const blanch = searchKitchenReference('blanch');
      expect(blanch.glossary.some((g) => g.term.includes('Blanch'))).toBe(true);

      const chicken = searchKitchenReference('chicken breast');
      expect(chicken.temperatures.some((t) => t.id === 'poultry-breast')).toBe(true);

      const buttermilk = searchKitchenReference('buttermilk');
      expect(buttermilk.substitutions.some((s) => s.id === 'sub-buttermilk')).toBe(true);
    });
  });
});
