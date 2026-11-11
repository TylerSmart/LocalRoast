import {
  celsiusToFahrenheit,
  cleanStepInstruction,
  fahrenheitToCelsius,
  formatSeconds,
  formatTemperatureDisplay,
  formatTimerToken,
  formatTokenDisplay,
  isDurationLabel,
  normalizeMeasurementUnit,
  parseStepInstruction,
  serializeStepToken,
  shouldShowTimerLabel,
  stepInstructionToPlainText,
} from './step-token-parser';
import {
  StepIngredientToken,
  StepTemperatureToken,
  StepTimerToken,
} from '../models/step-token.models';

describe('step-token-parser', () => {
  it('parses empty or null instruction into empty list', () => {
    expect(parseStepInstruction('')).toEqual([]);
    expect(parseStepInstruction(null)).toEqual([]);
    expect(parseStepInstruction(undefined)).toEqual([]);
  });

  it('parses plaintext instruction without tokens', () => {
    const segments = parseStepInstruction('Stir until golden.');
    expect(segments.length).toBe(1);
    expect(segments[0].text).toBe('Stir until golden.');
    expect(segments[0].token).toBeUndefined();
  });

  it('parses ingredient token with amount, unit and ingredient', () => {
    const raw = 'Whisk ||{"ingredient":"eggs","amount":2,"unit":"whole"}|| in a bowl.';
    const segments = parseStepInstruction(raw);
    expect(segments.length).toBe(3);
    expect(segments[0].text).toBe('Whisk ');
    expect(segments[1].token).toBeDefined();

    const token = segments[1].token as StepIngredientToken;
    expect(token.ingredient).toBe('eggs');
    expect(token.amount).toBe(2);
    expect(token.unit).toBe('whole');
    expect(segments[2].text).toBe(' in a bowl.');
  });

  it('parses ingredient token with per-item displayAmount', () => {
    const raw =
      'Top each pancake with ||{"ingredient":"butter","amount":4,"unit":"teaspoon","displayAmount":1}||.';
    const segments = parseStepInstruction(raw);
    expect(segments.length).toBe(3);

    const token = segments[1].token as StepIngredientToken;
    expect(token.ingredient).toBe('butter');
    expect(token.amount).toBe(4);
    expect(token.displayAmount).toBe(1);
    expect(token.unit).toBe('teaspoon');
  });

  it('parses timer token', () => {
    const raw = 'Simmer for ||{"type":"timer","seconds":300,"label":"Simmer sauce"}||.';
    const segments = parseStepInstruction(raw);
    expect(segments.length).toBe(3);

    const token = segments[1].token as StepTimerToken;
    expect(token.type).toBe('timer');
    expect(token.seconds).toBe(300);
    expect(token.label).toBe('Simmer sauce');
  });

  it('serializes token back to string', () => {
    const token: StepIngredientToken = {
      ingredient: 'flour',
      amount: 2,
      unit: 'Cup',
    };
    const serialized = serializeStepToken(token);
    expect(serialized).toBe('||{"ingredient":"flour","amount":2,"unit":"Cup"}||');
  });

  it('converts step instruction to plain text', () => {
    const raw =
      'Take ||{"ingredient":"eggs","amount":4,"unit":"whole"}|| and simmer for ||{"type":"timer","seconds":600,"label":"Boil"}||.';
    const plain = stepInstructionToPlainText(raw);
    expect(plain).toBe('Take 4 eggs and simmer for 10m (Boil).');
  });

  it('formats token display and respects scaling factors', () => {
    const token: StepIngredientToken = {
      ingredient: 'butter',
      amount: 4,
      unit: 'Tsp',
      displayAmount: 1,
    };

    // Factor 1 (display amount 1 tsp)
    expect(formatTokenDisplay(token, 1)).toBe('1 tsp butter');

    // Scaled by 2
    expect(formatTokenDisplay(token, 2)).toBe('2 tsp butter');
  });

  it('formats seconds into human readable duration', () => {
    expect(formatSeconds(45)).toBe('45s');
    expect(formatSeconds(90)).toBe('1m 30s');
    expect(formatSeconds(300)).toBe('5m');
    expect(formatSeconds(3600)).toBe('1h');
    expect(formatSeconds(5400)).toBe('1h 30m');
  });

  it('normalizes common unit strings', () => {
    expect(normalizeMeasurementUnit('teaspoon')).toBe('Tsp');
    expect(normalizeMeasurementUnit('tablespoons')).toBe('Tbsp');
    expect(normalizeMeasurementUnit('cups')).toBe('Cup');
    expect(normalizeMeasurementUnit('whole')).toBe('Whole');
  });

  it('parses tokens with trailing punctuation inside delimiters', () => {
    const raw = 'Process until combined, ||{"type":"timer","seconds":10}!!.|| and rest.';
    const segments = parseStepInstruction(raw);
    expect(segments.some((s) => s.token?.type === 'timer')).toBe(true);
    expect(segments.some((s) => s.text === '!!.')).toBe(true);
  });

  it('parses ingredient tokens missing the ingredient property', () => {
    const raw = 'Reserve ||{"amount":1,"unit":"tbsp"}|| fat.';
    const segments = parseStepInstruction(raw);
    expect(segments[1].token?.type).toBe('ingredient');
    const token = segments[1].token as StepIngredientToken;
    expect(token.amount).toBe(1);
    expect(token.unit).toBe('tbsp');
  });

  it('cleans duplicate preceding measurements in step text', () => {
    const raw =
      'Transfer to bowl and stir in 1 1/2 tablespoons ||{"ingredient":"lime juice","amount":1.5,"unit":"tbsp"}||.';
    const cleaned = cleanStepInstruction(raw);
    expect(cleaned).toBe(
      'Transfer to bowl and stir in ||{"ingredient":"lime juice","amount":1.5,"unit":"tbsp"}||.',
    );
  });

  it('cleans duplicate preceding measurements in step text while preserving words ending in unit characters', () => {
    const raw =
      'While onion cooks, whisk eggs, remaining ||{"ingredient":"pepper","amount":0.25,"unit":"tsp"}||, and cook in oil ||{"ingredient":"oil","amount":1,"unit":"tbsp"}||.';
    const cleaned = cleanStepInstruction(raw);
    expect(cleaned).toBe(
      'While onion cooks, whisk eggs, remaining ||{"ingredient":"pepper","amount":0.25,"unit":"tsp"}||, and cook in oil ||{"ingredient":"oil","amount":1,"unit":"tbsp"}||.',
    );
  });

  it('strips preceding measurements when remaining is present', () => {
    const raw =
      'While onion cooks, whisk eggs, remaining 1/8 teaspoon ||{"ingredient":"salt","amount":0.125,"unit":"tsp"}||.';
    const cleaned = cleanStepInstruction(raw);
    expect(cleaned).toBe(
      'While onion cooks, whisk eggs, remaining ||{"ingredient":"salt","amount":0.125,"unit":"tsp"}||.',
    );
  });

  it('parses and serializes temperature tokens', () => {
    const raw = 'Roast chicken until ||{"type":"temperature","fahrenheit":165}||.';
    const segments = parseStepInstruction(raw);
    expect(segments.length).toBe(3);
    expect(segments[1].token).toBeDefined();

    const token = segments[1].token as StepTemperatureToken;
    expect(token.type).toBe('temperature');
    expect(token.fahrenheit).toBe(165);

    const serialized = serializeStepToken(token);
    expect(serialized).toBe('||{"type":"temperature","fahrenheit":165}||');
  });

  it('converts between Fahrenheit and Celsius correctly', () => {
    expect(fahrenheitToCelsius(32)).toBe(0);
    expect(fahrenheitToCelsius(212)).toBe(100);
    expect(fahrenheitToCelsius(325)).toBe(163);
    expect(fahrenheitToCelsius(400)).toBe(204);

    expect(celsiusToFahrenheit(0)).toBe(32);
    expect(celsiusToFahrenheit(100)).toBe(212);
    expect(celsiusToFahrenheit(163)).toBe(325);
    expect(celsiusToFahrenheit(204)).toBe(399); // (204 * 9/5 + 32 = 399.2 -> 399)
  });

  it('formats temperature display respecting unit system', () => {
    const tokenF: StepTemperatureToken = { type: 'temperature', fahrenheit: 350 };
    expect(formatTemperatureDisplay(tokenF, 'Original')).toEqual({
      text: '350°F',
      scaled: false,
    });
    expect(formatTemperatureDisplay(tokenF, 'UsCustomary')).toEqual({
      text: '350°F',
      scaled: false,
    });
    expect(formatTemperatureDisplay(tokenF, 'Metric')).toEqual({
      text: '177°C',
      scaled: true,
    });

    const tokenC: StepTemperatureToken = { type: 'temperature', celsius: 180 };
    expect(formatTemperatureDisplay(tokenC, 'Original')).toEqual({
      text: '180°C',
      scaled: false,
    });
    expect(formatTemperatureDisplay(tokenC, 'Metric')).toEqual({
      text: '180°C',
      scaled: false,
    });
    expect(formatTemperatureDisplay(tokenC, 'UsCustomary')).toEqual({
      text: '356°F',
      scaled: true,
    });
  });

  it('formats temperature token in plain text conversion', () => {
    const raw = 'Preheat oven to ||{"type":"temperature","fahrenheit":425}||.';
    expect(stepInstructionToPlainText(raw, 'Original')).toBe('Preheat oven to 425°F.');
    expect(stepInstructionToPlainText(raw, 'Metric')).toBe('Preheat oven to 218°C.');
  });

  it('correctly identifies duration labels and suppresses redundant timer labels', () => {
    expect(isDurationLabel('5 minutes')).toBe(true);
    expect(isDurationLabel('3 to 4 minutes')).toBe(true);
    expect(isDurationLabel('30 seconds')).toBe(true);
    expect(isDurationLabel('1 to 2 minutes')).toBe(true);
    expect(isDurationLabel('about 5 minutes')).toBe(true);
    expect(isDurationLabel('approx. 10 mins')).toBe(true);
    expect(isDurationLabel('1 hour')).toBe(true);
    expect(isDurationLabel('5m')).toBe(true);
    expect(isDurationLabel('30s')).toBe(true);

    expect(isDurationLabel('Simmer')).toBe(false);
    expect(isDurationLabel('Simmer sauce')).toBe(false);
    expect(isDurationLabel('Cook onion')).toBe(false);
    expect(isDurationLabel('Rest')).toBe(false);
    expect(isDurationLabel('')).toBe(false);
    expect(isDurationLabel(null)).toBe(false);
    expect(isDurationLabel(undefined)).toBe(false);

    expect(shouldShowTimerLabel('5 minutes')).toBe(false);
    expect(shouldShowTimerLabel('Timer')).toBe(false);
    expect(shouldShowTimerLabel('Step 1 timer')).toBe(false);
    expect(shouldShowTimerLabel('Step 2 timer')).toBe(false);
    expect(shouldShowTimerLabel('Simmer')).toBe(true);
    expect(shouldShowTimerLabel('Cook (4m)')).toBe(true);
  });

  it('formats timer tokens cleanly without redundant duration labels', () => {
    const redundantTimer: StepTimerToken = {
      type: 'timer',
      seconds: 300,
      label: '5 minutes',
    };
    expect(formatTimerToken(redundantTimer)).toBe('5m');

    const actionTimer: StepTimerToken = {
      type: 'timer',
      seconds: 300,
      label: 'Simmer sauce',
    };
    expect(formatTimerToken(actionTimer)).toBe('5m (Simmer sauce)');

    const nullLabelTimer: StepTimerToken = {
      type: 'timer',
      seconds: 30,
      label: null,
    };
    expect(formatTimerToken(nullLabelTimer)).toBe('30s');
  });

  it('cleans redundant duration labels from timer tokens in step instructions', () => {
    const raw = 'Let sit for ||{"type":"timer","seconds":300,"label":"5 minutes"}||.';
    const cleaned = cleanStepInstruction(raw);
    expect(cleaned).toBe('Let sit for ||{"type":"timer","seconds":300}||.');

    const plain = stepInstructionToPlainText(raw);
    expect(plain).toBe('Let sit for 5m.');
  });
});
