import {
  InstructionIngredient,
  buildInstructionSegments,
  formatIngredientUseLines,
  formatIngredientUses,
  ingredientUses,
  matchIngredient,
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

  it('handles ingredient notes', () => {
    const step = {
      instruction:
        'Top each pancake with ||{"ingredient":"butter","amount":1,"unit":"teaspoon","notes":"melted"}||.',
    };
    const segments = buildInstructionSegments(step, ingredients, 1, 'Original');
    expect(segments[1].text).toBe('1 tsp butter');
    expect(segments[1].amount?.ingredientName).toBe('Note: melted');
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

  it('disambiguates duplicate ingredients by step sectionName', () => {
    const hotSauce1: InstructionIngredient = {
      id: 'sauce-1',
      displayName: 'hot sauce',
      sectionName: 'Sauce',
      quantity: 0.5,
      unit: 'Cup',
    };
    const hotSauce2: InstructionIngredient = {
      id: 'sauce-2',
      displayName: 'hot sauce',
      sectionName: 'Glaze',
      quantity: 1,
      unit: 'Tbsp',
    };
    const multiIngs = [hotSauce1, hotSauce2];

    const token = {
      ingredient: 'hot sauce',
      amount: 1,
      unit: 'Tbsp',
    };

    // When step is in Glaze section, matchIngredient chooses sauce-2
    const match = matchIngredient(token, multiIngs, 'Glaze');
    expect(match?.id).toBe('sauce-2');

    // When step is in Sauce section, matchIngredient chooses sauce-1
    const match1 = matchIngredient(token, multiIngs, 'Sauce');
    expect(match1?.id).toBe('sauce-1');

    // ingredientUses attributes each step to its matching section ingredient
    const steps = [
      {
        stepNumber: 1,
        sectionName: 'Sauce',
        instruction: 'Whisk ||{"ingredient":"hot sauce","amount":0.5,"unit":"cup"}||.',
      },
      {
        stepNumber: 2,
        sectionName: 'Glaze',
        instruction: 'Whisk ||{"ingredient":"hot sauce","amount":1,"unit":"tbsp"}||.',
      },
    ];

    const uses1 = ingredientUses(steps, 'sauce-1', multiIngs);
    expect(uses1.length).toBe(1);
    expect(uses1[0].stepNumber).toBe(1);

    const uses2 = ingredientUses(steps, 'sauce-2', multiIngs);
    expect(uses2.length).toBe(1);
    expect(uses2[0].stepNumber).toBe(2);
  });

  it('scales mixture, keeps fixed unscaled, and converts dimension units without scaling', () => {
    const step = {
      instruction:
        'Spread ||{"type":"mixture","name":"herb oil","amount":2,"unit":"tbsp"}|| in a ||{"type":"dimension","value":9,"unit":"inch","hyphenated":true}|| pan using ||{"type":"fixed","name":"scoop","amount":2,"unit":"tbsp"}||.',
    };

    const scaled = buildInstructionSegments(step, ingredients, 2, 'Original');
    const mixSeg = scaled.find((s) => s.amount?.kind === 'Mixture');
    const dimSeg = scaled.find((s) => s.amount?.kind === 'Dimension');
    const fixSeg = scaled.find((s) => s.amount?.kind === 'Fixed');

    expect(mixSeg?.text).toBe('1/4 cup herb oil');
    expect(mixSeg?.amount?.scaled).toBe(true);
    expect(dimSeg?.text).toBe('9-inch');
    expect(dimSeg?.amount?.scaled).toBe(false);
    expect(fixSeg?.text).toBe('2 tbsp scoop');
    expect(fixSeg?.amount?.scaled).toBe(false);

    const metric = buildInstructionSegments(step, ingredients, 2, 'Metric');
    const metricDim = metric.find((s) => s.amount?.kind === 'Dimension');
    expect(metricDim?.text).toContain('cm');
    expect(metricDim?.amount?.scaled).toBe(true);
  });

  it('matches sub-recipe tokens by ingredientIndex, exposes linkedRecipeId/isUnquantified, and ignores unquantified mentions in divided uses', () => {
    const eggsSubRecipe: InstructionIngredient = {
      id: 'ing-eggs',
      displayName: 'Easy-Peel Hard-Cooked Eggs',
      quantity: 6,
      unit: null,
      linkedRecipeId: 'recipe-eggs-1',
      linkedRecipeTitle: 'Easy-Peel Hard-Cooked Eggs',
    };
    const salt: InstructionIngredient = {
      id: 'ing-salt',
      displayName: 'kosher salt',
      quantity: 0.5,
      unit: 'Tsp',
    };
    const list: InstructionIngredient[] = [salt, eggsSubRecipe];

    const steps = [
      {
        stepNumber: 1,
        instruction:
          'Peel ||{"type":"ingredient","ingredient":"eggs","ingredientIndex":1}|| and slice in half.',
      },
      {
        stepNumber: 2,
        instruction:
          'Mash yolks with ||{"type":"ingredient","ingredient":"salt","amount":0.5,"unit":"tsp","ingredientIndex":0}||, then season with ||{"type":"ingredient","ingredient":"salt","ingredientIndex":0}|| to taste.',
      },
    ];

    const segs1 = buildInstructionSegments(steps[0], list, 1, 'Original');
    const eggsSeg = segs1.find((s) => s.amount?.kind === 'Ingredient');
    expect(eggsSeg?.amount?.ingredientId).toBe('ing-eggs');
    expect(eggsSeg?.amount?.linkedRecipeId).toBe('recipe-eggs-1');
    expect(eggsSeg?.amount?.isUnquantified).toBe(true);

    const segs2 = buildInstructionSegments(steps[1], list, 1, 'Original');
    const quantifiedSalt = segs2.find(
      (s) => s.amount?.ingredientId === 'ing-salt' && !s.amount?.isUnquantified,
    );
    const unquantifiedSalt = segs2.find(
      (s) => s.amount?.ingredientId === 'ing-salt' && s.amount?.isUnquantified,
    );
    expect(quantifiedSalt).toBeDefined();
    expect(unquantifiedSalt).toBeDefined();

    expect(ingredientUses(steps, 'ing-salt', list).length).toBe(1);
    expect(ingredientUses(steps, 'ing-eggs', list).length).toBe(0);
  });

  it('keeps fixed ingredient measurements constant across serving scales while still converting units and linking', () => {
    const ingredients: InstructionIngredient[] = [
      { id: 'ing-butter', displayName: 'unsalted butter', quantity: 4, unit: 'Tbsp' },
    ];
    const step = {
      instruction:
        'Spread ||{"type":"ingredient","ingredient":"butter","amount":1,"unit":"tbsp","ingredientIndex":0,"fixed":true}|| on each roll.',
    };

    const twoX = buildInstructionSegments(step, ingredients, 2, 'Original');
    const twoXSeg = twoX.find((s) => s.amount?.kind === 'Ingredient');
    expect(twoXSeg?.text).toBe('1 tbsp butter');
    expect(twoXSeg?.amount?.ingredientId).toBe('ing-butter');
    expect(twoXSeg?.amount?.isFixed).toBe(true);
    expect(twoXSeg?.amount?.scaled).toBe(false);

    const twoXMetric = buildInstructionSegments(step, ingredients, 2, 'Metric');
    const metricSeg = twoXMetric.find((s) => s.amount?.kind === 'Ingredient');
    expect(metricSeg?.text).toBe('15 ml butter');
    expect(metricSeg?.amount?.scaled).toBe(true);
  });

  it('formats divided ingredient uses as separate lines without an inline Divided prefix', () => {
    const uses = [
      { stepNumber: 1, label: '1/4 tsp pepper' },
      { stepNumber: 2, label: '1/8 tsp pepper' },
      { stepNumber: 3, label: '1/4 tsp pepper' },
    ];
    expect(formatIngredientUseLines(uses)).toEqual([
      '1/4 tsp pepper (step 1)',
      '1/8 tsp pepper (step 2)',
      '1/4 tsp pepper (step 3)',
    ]);
    expect(formatIngredientUses(uses)).toBe(
      '1/4 tsp pepper (step 1)\n1/8 tsp pepper (step 2)\n1/4 tsp pepper (step 3)',
    );
  });
});
