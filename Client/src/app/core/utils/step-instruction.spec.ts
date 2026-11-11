import {
  InstructionIngredient,
  buildInstructionSegments,
  renderScaledInstruction,
} from './step-instruction';

const flour: InstructionIngredient = {
  id: 'flour',
  displayName: 'all-purpose flour',
  quantity: 3,
  unit: 'Cup',
};
const butter: InstructionIngredient = {
  id: 'butter',
  displayName: 'butter',
  quantity: 4,
  unit: 'Tsp',
};
const ingredients = [flour, butter];

describe('step-instruction utils', () => {
  it('renders plain text steps without tokens', () => {
    const step = { instruction: 'Preheat the oven to 350°F.' };
    const segments = buildInstructionSegments(step, ingredients, 1, 'Original');
    expect(segments).toEqual([{ text: 'Preheat the oven to 350°F.' }]);
  });

  it('renders step with inline ingredient token at 1x', () => {
    const step = {
      instruction: 'Whisk ||{"ingredient":"flour","amount":1.5,"unit":"cup"}|| with milk.',
    };
    const segments = buildInstructionSegments(step, ingredients, 1, 'Original');
    expect(segments.length).toBe(3);
    expect(segments[0].text).toBe('Whisk ');
    expect(segments[1].text).toBe('1 1/2 cups flour');
    expect(segments[1].amount?.kind).toBe('Ingredient');
    expect(segments[1].amount?.scaled).toBe(false);
    expect(segments[2].text).toBe(' with milk.');
  });

  it('scales step ingredient token at 2x', () => {
    const step = {
      instruction: 'Whisk ||{"ingredient":"flour","amount":1.5,"unit":"cup"}|| with milk.',
    };
    const segments = buildInstructionSegments(step, ingredients, 2, 'Original');
    expect(segments[1].text).toBe('3 cups flour');
    expect(segments[1].amount?.scaled).toBe(true);
    expect(renderScaledInstruction(step, ingredients, 2, 'Original')).toBe(
      'Whisk 3 cups flour with milk.',
    );
  });

  it('handles per-item display amount and total amount', () => {
    const step = {
      instruction:
        'Top each pancake with ||{"ingredient":"butter","amount":4,"unit":"teaspoon","displayAmount":1,"displayUnit":"teaspoon"}||.',
    };
    const segments = buildInstructionSegments(step, ingredients, 1, 'Original');
    expect(segments[1].text).toBe('1 tsp butter');
    expect(segments[1].amount?.ingredientName).toBe('Total recipe: 4 tsp butter');
  });

  it('renders timer tokens', () => {
    const step = {
      instruction: 'Simmer for ||{"type":"timer","seconds":600,"label":"Simmer sauce"}||.',
    };
    const segments = buildInstructionSegments(step, ingredients, 1, 'Original');
    expect(segments[1].text).toBe('10m (Simmer sauce)');
    expect(segments[1].amount?.kind).toBe('Timer');
  });

  it('renders temperature tokens with unit conversion', () => {
    const step = {
      instruction: 'Preheat oven to ||{"type":"temperature","fahrenheit":325}||.',
    };

    // Original: renders °F
    const originalSegments = buildInstructionSegments(step, ingredients, 1, 'Original');
    expect(originalSegments.length).toBe(3);
    expect(originalSegments[1].text).toBe('325°F');
    expect(originalSegments[1].amount?.kind).toBe('Temperature');
    expect(originalSegments[1].amount?.scaled).toBe(false);

    // UsCustomary: renders °F
    const usSegments = buildInstructionSegments(step, ingredients, 1, 'UsCustomary');
    expect(usSegments[1].text).toBe('325°F');
    expect(usSegments[1].amount?.kind).toBe('Temperature');
    expect(usSegments[1].amount?.scaled).toBe(false);

    // Metric: converts 325°F to 163°C and marks scaled true
    const metricSegments = buildInstructionSegments(step, ingredients, 1, 'Metric');
    expect(metricSegments[1].text).toBe('163°C');
    expect(metricSegments[1].amount?.kind).toBe('Temperature');
    expect(metricSegments[1].amount?.scaled).toBe(true);

    // Plain scaled instruction renders the converted temperature
    expect(renderScaledInstruction(step, ingredients, 1, 'Metric')).toBe('Preheat oven to 163°C.');
  });
});
