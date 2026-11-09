import { RecipeStepDto } from '../models/recipe.models';
import { stepIngredientCallouts } from './culinary-math';
import {
  InstructionIngredient,
  buildInstructionSegments,
  formatIngredientUses,
  ingredientUses,
  relocateInlineAmounts,
  renderScaledInstruction,
  trimInstructionWithAmounts,
} from './step-instruction';

const flour: InstructionIngredient = {
  id: 'flour',
  displayName: 'all-purpose flour',
  quantity: 3,
  unit: 'Cup',
};
const milk: InstructionIngredient = {
  id: 'milk',
  displayName: 'whole milk',
  quantity: 1,
  unit: 'Cup',
};
const ingredients = [flour, milk];

const instruction = 'Whisk 1 1/2 cups flour with the milk, then cook until reduced to 1 cup.';
const step = {
  stepNumber: 1,
  instruction,
  inlineAmounts: [
    {
      start: 6,
      length: 10,
      kind: 'Ingredient' as const,
      ingredientId: 'flour',
      measurement: { quantity: 1.5, unit: 'Cup' as const },
    },
    {
      start: instruction.lastIndexOf('1 cup'),
      length: 5,
      kind: 'Fixed' as const,
      ingredientId: null,
      measurement: { quantity: 1, unit: 'Cup' as const },
    },
  ],
};

describe('step-instruction utils', () => {
  it('renders the verbatim text at 1x in original units', () => {
    const segments = buildInstructionSegments(step, ingredients, 1, 'Original');
    expect(segments.map((s) => s.text).join('')).toBe(instruction);
    expect(segments[1]).toEqual({
      text: '1 1/2 cups',
      amount: {
        kind: 'Ingredient',
        ingredientId: 'flour',
        ingredientName: 'all-purpose flour',
        scaled: false,
      },
    });
  });

  it('scales ingredient spans at 2x and leaves fixed spans unchanged', () => {
    const segments = buildInstructionSegments(step, ingredients, 2, 'Original');
    expect(segments[1].text).toBe('3 cups');
    expect(segments[1].amount?.scaled).toBe(true);
    const fixed = segments.find((s) => s.amount?.kind === 'Fixed');
    expect(fixed?.text).toBe('1 cup');
    expect(fixed?.amount?.scaled).toBe(false);
    expect(renderScaledInstruction(step, ingredients, 2, 'Original')).toBe(
      'Whisk 3 cups flour with the milk, then cook until reduced to 1 cup.',
    );
  });

  it('converts mixture spans to metric', () => {
    const mixture = {
      instruction: 'Pour in 1 cup batter.',
      inlineAmounts: [
        {
          start: 8,
          length: 5,
          kind: 'Mixture' as const,
          ingredientId: null,
          measurement: { quantity: 1, unit: 'Cup' as const },
        },
      ],
    };
    expect(renderScaledInstruction(mixture, ingredients, 1, 'Metric')).toBe(
      'Pour in 237 ml batter.',
    );
  });

  it('appends the scaled alternative measurement', () => {
    const withAlt = {
      instruction: 'Add 2 cups (10 ounces) flour.',
      inlineAmounts: [
        {
          start: 4,
          length: 18,
          kind: 'Ingredient' as const,
          ingredientId: 'flour',
          measurement: { quantity: 2, unit: 'Cup' as const },
          alternativeMeasurement: { quantity: 10, unit: 'Oz' as const },
        },
      ],
    };
    expect(renderScaledInstruction(withAlt, ingredients, 2, 'Original')).toBe(
      'Add 4 cups (1 1/4 lb) flour.',
    );
  });

  it('ignores invalid and overlapping spans', () => {
    const invalid = {
      instruction: 'Add 1 cup milk.',
      inlineAmounts: [
        {
          start: 4,
          length: 5,
          kind: 'Ingredient' as const,
          ingredientId: 'milk',
          measurement: { quantity: 1, unit: 'Cup' as const },
        },
        {
          start: 6,
          length: 3,
          kind: 'Mixture' as const,
          ingredientId: null,
          measurement: { quantity: 1, unit: 'Cup' as const },
        },
        {
          start: 40,
          length: 5,
          kind: 'Mixture' as const,
          ingredientId: null,
          measurement: { quantity: 1, unit: 'Cup' as const },
        },
      ],
    };
    const segments = buildInstructionSegments(invalid, ingredients, 2, 'Original');
    expect(segments.filter((s) => s.amount).length).toBe(1);
    expect(segments.map((s) => s.text).join('')).toBe('Add 2 cups milk.');
  });

  it('lists the divided uses of an ingredient row', () => {
    const second = {
      stepNumber: 2,
      instruction: 'Stir in the remaining 1 1/2 cups flour.',
      inlineAmounts: [
        {
          start: 22,
          length: 10,
          kind: 'Ingredient' as const,
          ingredientId: 'flour',
          measurement: { quantity: 1.5, unit: 'Cup' as const },
        },
      ],
    };
    const uses = ingredientUses([step, second], 'flour', ingredients, 2, 'Original');
    expect(uses).toEqual([
      { stepNumber: 1, label: '3 cups' },
      { stepNumber: 2, label: '3 cups' },
    ]);
    expect(formatIngredientUses(uses)).toBe('Divided: 3 cups (step 1) · 3 cups (step 2)');
    expect(ingredientUses([step], 'milk', ingredients, 1, 'Original')).toEqual([]);
  });

  it('relocates spans after text is inserted before them', () => {
    const newText =
      'First, whisk 1 1/2 cups flour with the milk, then cook until reduced to 1 cup.';
    const result = relocateInlineAmounts(instruction, newText, step.inlineAmounts);
    expect(result.dropped).toBe(0);
    expect(result.amounts.map((a) => a.start)).toEqual([13, newText.lastIndexOf('1 cup')]);
  });

  it('drops spans whose text no longer exists', () => {
    const result = relocateInlineAmounts(
      instruction,
      'Whisk 2 cups flour with the milk, then cook until reduced to 1 cup.',
      step.inlineAmounts,
    );
    expect(result.dropped).toBe(1);
    expect(result.amounts.length).toBe(1);
    expect(result.amounts[0].kind).toBe('Fixed');
  });

  it('trims the instruction and shifts span offsets', () => {
    const result = trimInstructionWithAmounts('  Add 1 cup. ', [{ start: 6, length: 5 }]);
    expect(result).toEqual({ instruction: 'Add 1 cup.', amounts: [{ start: 4, length: 5 }] });
  });

  it('skips callouts for rows that have an ingredient span in the step', () => {
    const dto: RecipeStepDto = {
      id: 's1',
      stepNumber: 1,
      sectionName: null,
      instruction: step.instruction,
      imagePath: null,
      ingredientIds: ['flour', 'milk'],
      timers: [],
      inlineAmounts: step.inlineAmounts,
    };
    expect(stepIngredientCallouts(dto, ingredients, 1, 'Original').map((c) => c.id)).toEqual([
      'milk',
    ]);
  });
});
