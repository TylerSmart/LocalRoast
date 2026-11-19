import { parseIngredientLines } from './ingredient-line-parser';

describe('parseIngredientLines', () => {
  it('parses quantities, fractions, ranges, units, prep notes, optional flags, and sections', () => {
    const input = `
For the Crust:
- 2 1/2 cups all-purpose flour, sifted
- ½ tsp kosher salt
- 1-2 tbsp ice water

For the Filling:
• 6 cups fresh blueberries
• 3 cloves garlic, minced (optional)
• Black pepper to taste
    `;

    const parsed = parseIngredientLines(input);
    expect(parsed.length).toBe(6);

    expect(parsed[0]).toMatchObject({
      sectionName: 'For the Crust',
      quantity: 2.5,
      maxQuantity: null,
      unit: 'Cup',
      displayName: 'all-purpose flour',
      preparationNote: 'sifted',
      isOptional: false,
    });

    expect(parsed[1]).toMatchObject({
      sectionName: 'For the Crust',
      quantity: 0.5,
      unit: 'Tsp',
      displayName: 'kosher salt',
    });

    expect(parsed[2]).toMatchObject({
      sectionName: 'For the Crust',
      quantity: 1,
      maxQuantity: 2,
      unit: 'Tbsp',
      displayName: 'ice water',
    });

    expect(parsed[3]).toMatchObject({
      sectionName: 'For the Filling',
      quantity: 6,
      unit: 'Cup',
      displayName: 'fresh blueberries',
    });

    expect(parsed[4]).toMatchObject({
      sectionName: 'For the Filling',
      quantity: 3,
      unit: 'Clove',
      displayName: 'garlic',
      preparationNote: 'minced',
      isOptional: true,
    });

    expect(parsed[5]).toMatchObject({
      sectionName: 'For the Filling',
      quantity: null,
      unit: null,
      displayName: 'Black pepper to taste',
    });
  });
});
