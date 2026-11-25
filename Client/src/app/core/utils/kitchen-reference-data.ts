export type KitchenReferenceTab = 'convert' | 'temps' | 'glossary' | 'swaps' | 'ratios';

export type ConverterSubMode = 'volume-weight' | 'temperature' | 'dimensions' | 'baking-pans';

export type ConverterVolumeUnit =
  'Tsp' | 'Tbsp' | 'FlOz' | 'Cup' | 'Pt' | 'Qt' | 'Gal' | 'Ml' | 'L';
export type ConverterWeightUnit = 'G' | 'Kg' | 'Oz' | 'Lb';
export type ConverterUnit = ConverterVolumeUnit | ConverterWeightUnit;

export type TemperatureCategory =
  | 'Beef & Lamb'
  | 'Pork'
  | 'Poultry'
  | 'Seafood'
  | 'Eggs & Baking'
  | 'Sugar Stages'
  | 'Oil Smoke Points';

export type GlossaryCategory =
  'Knife Cuts' | 'Cooking Methods' | 'Prep & Techniques' | 'Sauces & Classics' | 'Small Measures';

export type SubstitutionCategory =
  'Dairy & Eggs' | 'Baking & Flour' | 'Salts & Seasonings' | 'Acids, Wine & Pantry';

export interface ConverterUnitOption {
  value: ConverterUnit;
  label: string;
  shortLabel: string;
  kind: 'volume' | 'weight';
  system: 'us' | 'metric';
}

export interface IngredientDensityOption {
  id: string;
  label: string;
  gramsPerCup: number;
  notes?: string;
}

export interface QuickEquivalentItem {
  id: string;
  title: string;
  formula: string;
  detail: string;
  keywords: string[];
}

export interface BakingPanOption {
  id: string;
  label: string;
  areaSqIn: number;
  volumeCups: number;
  shape: 'round' | 'square' | 'rectangular' | 'loaf' | 'bundt' | 'muffin';
}

export interface FoodTemperatureEntry {
  id: string;
  category: TemperatureCategory;
  item: string;
  donenessOrStage: string;
  tempF: number;
  maxTempF?: number;
  usdaSafeF?: number;
  restNote?: string;
  cueNote: string;
  keywords: string[];
}

export interface GlossaryEntry {
  id: string;
  term: string;
  category: GlossaryCategory;
  pronunciation?: string;
  specBadge?: string;
  definition: string;
  chefTip?: string;
  keywords: string[];
}

export interface SubstitutionEntry {
  id: string;
  category: SubstitutionCategory;
  ingredient: string;
  baseAmount: string;
  replacement: string;
  notes: string;
  keywords: string[];
}

export interface CookingTimingEntry {
  id: string;
  group: 'Eggs' | 'Rice & Grains' | 'Oats & Legumes';
  item: string;
  ratioDisplay: string;
  cookTimeLabel: string;
  timerSeconds: number;
  methodNote: string;
  keywords: string[];
}

export interface ClassicRatioEntry {
  id: string;
  name: string;
  ratio: string;
  basis: string;
  example: string;
  note: string;
  keywords: string[];
}

export const CONVERTER_UNITS: ConverterUnitOption[] = [
  { value: 'Tsp', label: 'Teaspoon (tsp)', shortLabel: 'tsp', kind: 'volume', system: 'us' },
  { value: 'Tbsp', label: 'Tablespoon (tbsp)', shortLabel: 'tbsp', kind: 'volume', system: 'us' },
  {
    value: 'FlOz',
    label: 'Fluid Ounce (fl oz)',
    shortLabel: 'fl oz',
    kind: 'volume',
    system: 'us',
  },
  { value: 'Cup', label: 'Cup (cup)', shortLabel: 'cup', kind: 'volume', system: 'us' },
  { value: 'Pt', label: 'Pint (pt)', shortLabel: 'pt', kind: 'volume', system: 'us' },
  { value: 'Qt', label: 'Quart (qt)', shortLabel: 'qt', kind: 'volume', system: 'us' },
  { value: 'Gal', label: 'Gallon (gal)', shortLabel: 'gal', kind: 'volume', system: 'us' },
  { value: 'Ml', label: 'Milliliter (ml)', shortLabel: 'ml', kind: 'volume', system: 'metric' },
  { value: 'L', label: 'Liter (L)', shortLabel: 'L', kind: 'volume', system: 'metric' },
  { value: 'G', label: 'Gram (g)', shortLabel: 'g', kind: 'weight', system: 'metric' },
  { value: 'Kg', label: 'Kilogram (kg)', shortLabel: 'kg', kind: 'weight', system: 'metric' },
  { value: 'Oz', label: 'Ounce (oz)', shortLabel: 'oz', kind: 'weight', system: 'us' },
  { value: 'Lb', label: 'Pound (lb)', shortLabel: 'lb', kind: 'weight', system: 'us' },
];

export const INGREDIENT_DENSITIES: IngredientDensityOption[] = [
  { id: 'water', label: 'Water / Milk / Broth', gramsPerCup: 236.59 },
  {
    id: 'ap-flour',
    label: 'All-Purpose Flour (spooned & leveled)',
    gramsPerCup: 120,
    notes: 'Scooping straight from the bag packs ~140–150g.',
  },
  { id: 'bread-flour', label: 'Bread Flour', gramsPerCup: 127 },
  { id: 'cake-flour', label: 'Cake Flour', gramsPerCup: 114 },
  { id: 'whole-wheat-flour', label: 'Whole Wheat Flour', gramsPerCup: 113 },
  { id: 'granulated-sugar', label: 'Granulated White Sugar', gramsPerCup: 200 },
  { id: 'brown-sugar', label: 'Brown Sugar (packed)', gramsPerCup: 213 },
  { id: 'powdered-sugar', label: "Powdered / Confectioners' Sugar", gramsPerCup: 120 },
  {
    id: 'butter',
    label: 'Butter (1 US stick = 8 tbsp = 113g)',
    gramsPerCup: 227,
    notes: '1 US stick = 1/2 cup = 4 oz = 113.5g.',
  },
  { id: 'honey', label: 'Honey / Maple Syrup / Molasses', gramsPerCup: 340 },
  { id: 'oil', label: 'Olive / Vegetable / Neutral Oil', gramsPerCup: 218 },
  { id: 'oats', label: 'Rolled Old-Fashioned Oats', gramsPerCup: 90 },
  { id: 'rice', label: 'Uncooked White Rice', gramsPerCup: 185 },
  { id: 'cocoa', label: 'Unsweetened Cocoa Powder', gramsPerCup: 85 },
  {
    id: 'salt-dc',
    label: 'Diamond Crystal Kosher Salt',
    gramsPerCup: 135,
    notes: '~2.8g per tsp — lightest kosher salt flake.',
  },
  {
    id: 'salt-morton',
    label: 'Morton Coarse Kosher Salt',
    gramsPerCup: 230,
    notes: '~4.8g per tsp — denser than Diamond Crystal.',
  },
  {
    id: 'salt-table',
    label: 'Fine Table / Sea Salt',
    gramsPerCup: 288,
    notes: '~6.0g per tsp — use ~1/2 as much by volume vs. Diamond Crystal.',
  },
];

export const QUICK_EQUIVALENTS: QuickEquivalentItem[] = [
  {
    id: 'eq-smidgen',
    title: '1 Smidgen',
    formula: '≈ 1/32 tsp',
    detail: 'Half of a pinch (~0.15 ml)',
    keywords: ['smidgen', 'small measure', 'teaspoon', 'fraction'],
  },
  {
    id: 'eq-pinch',
    title: '1 Pinch',
    formula: '≈ 1/16 tsp',
    detail: 'Amount held between thumb & index finger (~0.3 ml)',
    keywords: ['pinch', 'small measure', 'teaspoon', 'salt'],
  },
  {
    id: 'eq-dash',
    title: '1 Dash',
    formula: '≈ 1/8 tsp',
    detail: 'About 6–8 drops liquid or 2 pinches dry (~0.6 ml)',
    keywords: ['dash', 'drops', 'small measure', 'bitters', 'hot sauce'],
  },
  {
    id: 'eq-tbsp',
    title: '1 Tablespoon',
    formula: '3 tsp = 15 ml',
    detail: '1/2 fl oz (14.8 ml exact)',
    keywords: ['tablespoon', 'tbsp', 'teaspoon', 'tsp', 'ml'],
  },
  {
    id: 'eq-floz',
    title: '1 Fluid Ounce',
    formula: '2 tbsp = 30 ml',
    detail: '6 tsp (29.6 ml exact)',
    keywords: ['fluid ounce', 'fl oz', 'tablespoon', 'ml'],
  },
  {
    id: 'eq-quarter-cup',
    title: '1/4 Cup',
    formula: '4 tbsp = 59 ml',
    detail: '2 fl oz = 12 tsp',
    keywords: ['quarter cup', 'tablespoon', 'ml', 'fl oz'],
  },
  {
    id: 'eq-cup',
    title: '1 US Cup',
    formula: '16 tbsp = 237 ml',
    detail: '8 fl oz = 48 tsp (240 ml nutrition label)',
    keywords: ['cup', 'tablespoon', 'fl oz', 'ml'],
  },
  {
    id: 'eq-butter-stick',
    title: '1 US Stick Butter',
    formula: '8 tbsp = 1/2 cup = 113 g',
    detail: '4 oz · 2 sticks = 1 cup = 227 g',
    keywords: ['butter', 'stick', 'tablespoon', 'cup', 'grams', 'oz'],
  },
  {
    id: 'eq-pint',
    title: '1 US Pint',
    formula: '2 cups = 473 ml',
    detail: '16 fl oz',
    keywords: ['pint', 'pt', 'cup', 'ml'],
  },
  {
    id: 'eq-quart',
    title: '1 US Quart',
    formula: '4 cups = 946 ml',
    detail: '2 pints = 32 fl oz (≈ 1 liter)',
    keywords: ['quart', 'qt', 'cup', 'pint', 'liter', 'ml'],
  },
  {
    id: 'eq-gallon',
    title: '1 US Gallon',
    formula: '4 qt = 3.79 L',
    detail: '16 cups = 128 fl oz',
    keywords: ['gallon', 'gal', 'quart', 'liter'],
  },
  {
    id: 'eq-weight',
    title: 'Ounces & Pounds',
    formula: '1 oz = 28.35 g · 1 lb = 454 g',
    detail: '1 lb = 16 oz · 1 kg = 2.205 lb',
    keywords: ['ounce', 'oz', 'pound', 'lb', 'gram', 'kilogram', 'kg'],
  },
];

export const BAKING_PANS: BakingPanOption[] = [
  { id: 'round-6', label: '6" Round Cake Pan', areaSqIn: 28.3, volumeCups: 4, shape: 'round' },
  { id: 'round-8', label: '8" Round Cake Pan', areaSqIn: 50.3, volumeCups: 6, shape: 'round' },
  { id: 'round-9', label: '9" Round Cake Pan', areaSqIn: 63.6, volumeCups: 8, shape: 'round' },
  {
    id: 'round-10',
    label: '10" Round / Springform Pan',
    areaSqIn: 78.5,
    volumeCups: 11,
    shape: 'round',
  },
  { id: 'square-8', label: '8×8" Square Pan', areaSqIn: 64, volumeCups: 8, shape: 'square' },
  { id: 'square-9', label: '9×9" Square Pan', areaSqIn: 81, volumeCups: 10, shape: 'square' },
  {
    id: 'rect-9x13',
    label: '9×13" Rectangular / Quarter Sheet',
    areaSqIn: 117,
    volumeCups: 14,
    shape: 'rectangular',
  },
  {
    id: 'rect-11x7',
    label: '11×7" Baking Dish',
    areaSqIn: 77,
    volumeCups: 9,
    shape: 'rectangular',
  },
  {
    id: 'loaf-8.5x4.5',
    label: '8.5×4.5" Loaf Pan',
    areaSqIn: 38.25,
    volumeCups: 6,
    shape: 'loaf',
  },
  { id: 'loaf-9x5', label: '9×5" Loaf Pan', areaSqIn: 45, volumeCups: 8, shape: 'loaf' },
  {
    id: 'bundt-10',
    label: '10" Bundt Pan (12-cup)',
    areaSqIn: 78.5,
    volumeCups: 12,
    shape: 'bundt',
  },
  {
    id: 'muffin-12',
    label: '12-Cup Standard Muffin Tin',
    areaSqIn: 75,
    volumeCups: 4.5,
    shape: 'muffin',
  },
];

export const TEMPERATURE_CATEGORIES: TemperatureCategory[] = [
  'Beef & Lamb',
  'Pork',
  'Poultry',
  'Seafood',
  'Eggs & Baking',
  'Sugar Stages',
  'Oil Smoke Points',
];

export const FOOD_TEMPERATURES: FoodTemperatureEntry[] = [
  // Beef & Lamb
  {
    id: 'beef-rare',
    category: 'Beef & Lamb',
    item: 'Beef / Lamb Steak & Roast',
    donenessOrStage: 'Rare',
    tempF: 120,
    maxTempF: 125,
    usdaSafeF: 145,
    restNote: 'Pull 5°F early; rest 5–10 min',
    cueNote: 'Cool red center, very soft yield when pressed.',
    keywords: ['steak', 'roast', 'prime rib', 'tenderloin', 'beef', 'lamb', 'rare'],
  },
  {
    id: 'beef-med-rare',
    category: 'Beef & Lamb',
    item: 'Beef / Lamb Steak & Roast',
    donenessOrStage: 'Medium-Rare',
    tempF: 130,
    maxTempF: 135,
    usdaSafeF: 145,
    restNote: 'Pull at 128–130°F; rest 5–10 min',
    cueNote: 'Warm red-pink center; ideal balance of tenderness and juice.',
    keywords: ['steak', 'ribeye', 'strip', 'roast', 'lamb', 'medium rare'],
  },
  {
    id: 'beef-medium',
    category: 'Beef & Lamb',
    item: 'Beef / Lamb Steak & Roast',
    donenessOrStage: 'Medium',
    tempF: 140,
    maxTempF: 145,
    usdaSafeF: 145,
    restNote: 'Rest 3–5 min (USDA whole-cut safe)',
    cueNote: 'Warm pink center, firmer spring.',
    keywords: ['steak', 'beef', 'lamb', 'medium'],
  },
  {
    id: 'beef-med-well',
    category: 'Beef & Lamb',
    item: 'Beef / Lamb Steak & Roast',
    donenessOrStage: 'Medium-Well',
    tempF: 150,
    maxTempF: 155,
    usdaSafeF: 145,
    restNote: 'Rest 3–5 min',
    cueNote: 'Slight trace of pink, firm texture.',
    keywords: ['steak', 'beef', 'lamb', 'medium well'],
  },
  {
    id: 'beef-ground',
    category: 'Beef & Lamb',
    item: 'Ground Beef, Lamb & Burgers',
    donenessOrStage: 'USDA Safe Minimum',
    tempF: 160,
    usdaSafeF: 160,
    cueNote: 'Instant kill for surface pathogens mixed throughout ground meat.',
    keywords: ['burger', 'ground beef', 'meatball', 'meatloaf', 'ground lamb'],
  },
  {
    id: 'beef-braise',
    category: 'Beef & Lamb',
    item: 'Brisket, Chuck & Short Ribs',
    donenessOrStage: 'Braised / Smoked Tender',
    tempF: 195,
    maxTempF: 205,
    restNote: 'Rest 20–45 min wrapped',
    cueNote: 'Thermometer probe slides in with almost zero resistance (collagen melted).',
    keywords: ['brisket', 'chuck', 'short rib', 'pot roast', 'braise', 'bbq', 'smoke'],
  },

  // Pork
  {
    id: 'pork-chop',
    category: 'Pork',
    item: 'Pork Chops, Loin & Tenderloin',
    donenessOrStage: 'Medium (Juicy & USDA Safe)',
    tempF: 145,
    usdaSafeF: 145,
    restNote: 'Pull at 140°F + 3 min carryover rest',
    cueNote: 'Light blush pink center with clear juices — never dry.',
    keywords: ['pork', 'chop', 'tenderloin', 'loin', 'roast'],
  },
  {
    id: 'pork-ground',
    category: 'Pork',
    item: 'Ground Pork & Fresh Sausage',
    donenessOrStage: 'USDA Safe Minimum',
    tempF: 160,
    usdaSafeF: 160,
    cueNote: 'No pink remaining inside sausages or patties.',
    keywords: ['sausage', 'ground pork', 'bratwurst', 'chorizo'],
  },
  {
    id: 'pork-shoulder',
    category: 'Pork',
    item: 'Pork Shoulder / Butt (Pulled Pork)',
    donenessOrStage: 'Pullable / Shredding',
    tempF: 195,
    maxTempF: 203,
    restNote: 'Rest 30 min before shredding',
    cueNote: 'Bone wiggles cleanly free; probe glides in like warm butter.',
    keywords: ['pulled pork', 'pork butt', 'pork shoulder', 'carnitas', 'braise'],
  },
  {
    id: 'pork-ham',
    category: 'Pork',
    item: 'Ham (Pre-Cooked Reheat / Fresh)',
    donenessOrStage: 'Reheat 140°F / Fresh 145°F',
    tempF: 140,
    maxTempF: 145,
    usdaSafeF: 140,
    restNote: 'Rest 3 min for fresh ham',
    cueNote: 'Store-bought cured/smoked ham only needs 140°F.',
    keywords: ['ham', 'cured', 'spiral ham'],
  },

  // Poultry
  {
    id: 'poultry-breast',
    category: 'Poultry',
    item: 'Chicken & Turkey Breast',
    donenessOrStage: 'USDA 165°F (Chef Pull 157–160°F)',
    tempF: 165,
    usdaSafeF: 165,
    restNote: 'Pull at 157–160°F and rest 5–10 min (pasteurizes via carryover)',
    cueNote:
      'At 165°F bacteria die instantaneously; holding 158°F for ~1 min achieves the same safety with far juicier breast meat.',
    keywords: ['chicken breast', 'turkey breast', 'poultry', 'white meat', 'roast chicken'],
  },
  {
    id: 'poultry-thigh',
    category: 'Poultry',
    item: 'Chicken & Turkey Thighs / Legs',
    donenessOrStage: 'Tender Dark Meat',
    tempF: 175,
    maxTempF: 185,
    usdaSafeF: 165,
    restNote: 'Rest 5 min',
    cueNote: 'Safe at 165°F, but dark meat becomes silky and loses chewiness at 175–185°F.',
    keywords: ['chicken thigh', 'drumstick', 'wings', 'dark meat', 'turkey leg'],
  },
  {
    id: 'poultry-ground',
    category: 'Poultry',
    item: 'Ground Chicken, Turkey & Stuffing',
    donenessOrStage: 'USDA Safe Minimum',
    tempF: 165,
    usdaSafeF: 165,
    cueNote: 'Measure center of patties, meatballs, or stuffing.',
    keywords: ['ground turkey', 'ground chicken', 'stuffing', 'dressing'],
  },
  {
    id: 'poultry-duck',
    category: 'Poultry',
    item: 'Duck Breast',
    donenessOrStage: 'Chef Medium-Rare',
    tempF: 130,
    maxTempF: 135,
    usdaSafeF: 165,
    restNote: 'Score skin, render slow, rest 5 min',
    cueNote: 'Rosy red-pink center with crisp rendered skin.',
    keywords: ['duck', 'duck breast', 'magret'],
  },

  // Seafood
  {
    id: 'seafood-salmon',
    category: 'Seafood',
    item: 'Salmon, Tuna & Fatty Fish',
    donenessOrStage: 'Chef Medium-Rare / Translucent',
    tempF: 120,
    maxTempF: 125,
    usdaSafeF: 145,
    cueNote: 'Moist, tender, flakes gently without chalky albumin.',
    keywords: ['salmon', 'tuna', 'trout', 'fish', 'seafood'],
  },
  {
    id: 'seafood-white',
    category: 'Seafood',
    item: 'Cod, Halibut & White Fish',
    donenessOrStage: 'Opaque & Flaky',
    tempF: 135,
    maxTempF: 140,
    usdaSafeF: 145,
    cueNote: 'Just opaque through center; flakes cleanly with a fork.',
    keywords: ['cod', 'halibut', 'tilapia', 'snapper', 'sea bass', 'white fish'],
  },
  {
    id: 'seafood-shellfish',
    category: 'Seafood',
    item: 'Shrimp, Scallops & Lobster',
    donenessOrStage: 'Opaque & Firm',
    tempF: 120,
    maxTempF: 140,
    cueNote: 'Shrimp curls into a loose "C" (tight "O" means overcooked).',
    keywords: ['shrimp', 'prawns', 'scallop', 'lobster', 'crab', 'shellfish'],
  },

  // Eggs & Baking
  {
    id: 'eggs-dish',
    category: 'Eggs & Baking',
    item: 'Quiche, Frittata & Egg Casseroles',
    donenessOrStage: 'USDA Safe Set',
    tempF: 160,
    usdaSafeF: 160,
    cueNote: 'Edges set, center barely jiggles.',
    keywords: ['egg', 'quiche', 'frittata', 'casserole'],
  },
  {
    id: 'eggs-custard',
    category: 'Eggs & Baking',
    item: 'Custard, Cheesecake & Crème Anglaise',
    donenessOrStage: 'Silky Set (Do Not Boil)',
    tempF: 170,
    maxTempF: 175,
    cueNote: 'Coats the back of a spoon (nappe); curdles above 185°F.',
    keywords: ['custard', 'cheesecake', 'ice cream base', 'creme anglaise', 'curd'],
  },
  {
    id: 'bake-lean-bread',
    category: 'Eggs & Baking',
    item: 'Artisan & Sourdough Bread',
    donenessOrStage: 'Baked Through',
    tempF: 205,
    maxTempF: 210,
    cueNote: 'Hollow thump on bottom crust; crumb fully gelatinized.',
    keywords: ['sourdough', 'boule', 'baguette', 'bread', 'loaf'],
  },
  {
    id: 'bake-enriched',
    category: 'Eggs & Baking',
    item: 'Brioche, Challah & Sandwich Bread',
    donenessOrStage: 'Baked Through',
    tempF: 190,
    cueNote: 'Lower target keeps butter/egg-rich crumb soft and moist.',
    keywords: ['brioche', 'challah', 'cinnamon roll', 'sandwich bread', 'enriched'],
  },
  {
    id: 'bake-cake',
    category: 'Eggs & Baking',
    item: 'Quick Breads, Muffins & Cakes',
    donenessOrStage: 'Baked Through',
    tempF: 200,
    maxTempF: 205,
    cueNote: 'Toothpick emerges with a few moist crumbs, no wet batter.',
    keywords: ['cake', 'banana bread', 'muffin', 'quick bread', 'brownie'],
  },
  {
    id: 'reheat-leftovers',
    category: 'Eggs & Baking',
    item: 'Leftovers, Soups & Casseroles',
    donenessOrStage: 'USDA Reheat Safe',
    tempF: 165,
    usdaSafeF: 165,
    cueNote: 'Bring sauces/soups to a rolling boil or 165°F throughout.',
    keywords: ['leftovers', 'reheat', 'casserole', 'soup', 'danger zone'],
  },

  // Sugar Stages
  {
    id: 'sugar-thread',
    category: 'Sugar Stages',
    item: 'Sugar Syrup — Thread',
    donenessOrStage: '230–235°F',
    tempF: 230,
    maxTempF: 235,
    cueNote: 'Spins a thin 2-inch thread in cold water (candied fruit, syrups).',
    keywords: ['sugar', 'candy', 'thread', 'syrup'],
  },
  {
    id: 'sugar-soft-ball',
    category: 'Sugar Stages',
    item: 'Sugar Syrup — Soft Ball',
    donenessOrStage: '235–240°F',
    tempF: 235,
    maxTempF: 240,
    cueNote: 'Forms a soft, pliable ball in ice water (fudge, Italian meringue, pralines).',
    keywords: ['sugar', 'candy', 'soft ball', 'fudge', 'italian meringue', 'frosting'],
  },
  {
    id: 'sugar-firm-ball',
    category: 'Sugar Stages',
    item: 'Sugar Syrup — Firm Ball',
    donenessOrStage: '245–250°F',
    tempF: 245,
    maxTempF: 250,
    cueNote: 'Holds shape when squeezed gently (soft caramels).',
    keywords: ['sugar', 'candy', 'firm ball', 'caramel'],
  },
  {
    id: 'sugar-hard-ball',
    category: 'Sugar Stages',
    item: 'Sugar Syrup — Hard Ball',
    donenessOrStage: '250–265°F',
    tempF: 250,
    maxTempF: 265,
    cueNote: 'Chewy, holds rigid ball shape (marshmallows, nougat, divinity).',
    keywords: ['sugar', 'candy', 'hard ball', 'marshmallow', 'nougat'],
  },
  {
    id: 'sugar-soft-crack',
    category: 'Sugar Stages',
    item: 'Sugar Syrup — Soft Crack',
    donenessOrStage: '270–290°F',
    tempF: 270,
    maxTempF: 290,
    cueNote: 'Separates into pliable threads in cold water (taffy, butterscotch).',
    keywords: ['sugar', 'candy', 'soft crack', 'taffy', 'butterscotch'],
  },
  {
    id: 'sugar-hard-crack',
    category: 'Sugar Stages',
    item: 'Sugar Syrup — Hard Crack',
    donenessOrStage: '300–310°F',
    tempF: 300,
    maxTempF: 310,
    cueNote: 'Snaps cleanly like glass when cooled (peanut brittle, toffee, lollipops).',
    keywords: ['sugar', 'candy', 'hard crack', 'brittle', 'toffee', 'lollipop'],
  },
  {
    id: 'sugar-caramel',
    category: 'Sugar Stages',
    item: 'Caramel (Light to Deep Amber)',
    donenessOrStage: '320–350°F',
    tempF: 320,
    maxTempF: 350,
    cueNote: 'Pull off heat around 340°F; burns bitter past 355–360°F.',
    keywords: ['caramel', 'amber', 'flan', 'praline', 'burnt sugar'],
  },

  // Oil Smoke Points
  {
    id: 'oil-butter',
    category: 'Oil Smoke Points',
    item: 'Whole Unsalted Butter',
    donenessOrStage: 'Smoke Point',
    tempF: 300,
    maxTempF: 350,
    cueNote: 'Milk solids brown at 250–300°F; add oil or clarify for high-heat searing.',
    keywords: ['butter', 'smoke point', 'brown butter', 'saute'],
  },
  {
    id: 'oil-evoo',
    category: 'Oil Smoke Points',
    item: 'Extra-Virgin Olive Oil',
    donenessOrStage: 'Smoke Point',
    tempF: 375,
    maxTempF: 410,
    cueNote: 'Great for sautéing, roasting & dressings; avoid blazing-hot wok searing.',
    keywords: ['olive oil', 'evoo', 'smoke point', 'saute', 'roast'],
  },
  {
    id: 'oil-canola',
    category: 'Oil Smoke Points',
    item: 'Canola / Vegetable Oil',
    donenessOrStage: 'Smoke Point',
    tempF: 400,
    cueNote: 'Neutral everyday frying, baking, and roasting fat.',
    keywords: ['canola', 'vegetable oil', 'frying', 'smoke point'],
  },
  {
    id: 'oil-peanut',
    category: 'Oil Smoke Points',
    item: 'Peanut / Corn / Safflower Oil',
    donenessOrStage: 'Smoke Point',
    tempF: 450,
    cueNote: 'Ideal for deep-frying (350–375°F) and pan-frying.',
    keywords: ['peanut oil', 'corn oil', 'deep fry', 'smoke point'],
  },
  {
    id: 'oil-ghee',
    category: 'Oil Smoke Points',
    item: 'Clarified Butter / Ghee',
    donenessOrStage: 'Smoke Point',
    tempF: 485,
    cueNote: 'Milk solids removed — rich butter flavor safe for high-heat searing.',
    keywords: ['ghee', 'clarified butter', 'sear', 'smoke point'],
  },
  {
    id: 'oil-avocado',
    category: 'Oil Smoke Points',
    item: 'Refined Avocado Oil',
    donenessOrStage: 'Smoke Point',
    tempF: 520,
    cueNote: 'Top choice for cast-iron steak searing and wok stir-fries.',
    keywords: ['avocado oil', 'cast iron', 'sear', 'wok', 'smoke point'],
  },
];

export const GLOSSARY_CATEGORIES: GlossaryCategory[] = [
  'Knife Cuts',
  'Cooking Methods',
  'Prep & Techniques',
  'Sauces & Classics',
  'Small Measures',
];

export const CULINARY_GLOSSARY: GlossaryEntry[] = [
  // Knife Cuts
  {
    id: 'cut-julienne',
    term: 'Julienne',
    category: 'Knife Cuts',
    pronunciation: 'zhoo-lee-EN',
    specBadge: '1/8" × 1/8" × 2" (3 mm × 5 cm)',
    definition:
      'Uniform matchstick strips cut by squaring off an ingredient into 2-inch planks, slicing 1/8-inch slabs, and stacking to slice 1/8-inch sticks.',
    chefTip: 'Even thickness guarantees vegetables cook in the exact same time.',
    keywords: ['julienne', 'matchstick', 'knife cut', 'carrot', 'pepper'],
  },
  {
    id: 'cut-fine-julienne',
    term: 'Fine Julienne',
    category: 'Knife Cuts',
    specBadge: '1/16" × 1/16" × 2" (1.5 mm × 5 cm)',
    definition:
      'Hair-thin matchstick strips half as wide as standard julienne, used for delicate garnishes and quick salads.',
    keywords: ['fine julienne', 'garnish', 'ginger', 'knife cut'],
  },
  {
    id: 'cut-batonnet',
    term: 'Bâtonnet',
    category: 'Knife Cuts',
    pronunciation: 'bah-toh-NAY',
    specBadge: '1/4" × 1/4" × 2" (6 mm × 5 cm)',
    definition:
      'Classic "little stick" french-fry cut twice the width of julienne; starting shape for small dice.',
    keywords: ['batonnet', 'fries', 'crudite', 'knife cut'],
  },
  {
    id: 'cut-brunoise',
    term: 'Brunoise',
    category: 'Knife Cuts',
    pronunciation: 'broo-NWAHZ',
    specBadge: '1/8" cube (3 mm)',
    definition:
      'Very fine uniform cubes created by gathering julienne sticks and slicing crosswise every 1/8 inch.',
    chefTip: 'Ideal for delicate pan sauces, garnishes, and quick-cooking aromatics.',
    keywords: ['brunoise', 'dice', 'fine cube', 'knife cut', 'shallot'],
  },
  {
    id: 'cut-small-dice',
    term: 'Small Dice (Macédoine)',
    category: 'Knife Cuts',
    specBadge: '1/4" cube (6 mm)',
    definition:
      'Neat quarter-inch cubes cut crosswise from bâtonnet sticks; standard for salsas, mirepoix in quick soups, and fillings.',
    keywords: ['small dice', 'macedoine', 'cube', 'knife cut'],
  },
  {
    id: 'cut-medium-dice',
    term: 'Medium Dice (Parmentier)',
    category: 'Knife Cuts',
    specBadge: '1/2" cube (12 mm)',
    definition: 'Half-inch cubes used for hearty soups, stews, and roasted vegetables.',
    keywords: ['medium dice', 'parmentier', 'cube', 'potato', 'knife cut'],
  },
  {
    id: 'cut-large-dice',
    term: 'Large Dice (Carré)',
    category: 'Knife Cuts',
    specBadge: '3/4" cube (20 mm)',
    definition:
      'Chunky three-quarter-inch cubes suited for long braises, slow-cooker stews, and stocks.',
    keywords: ['large dice', 'carre', 'stew', 'knife cut'],
  },
  {
    id: 'cut-chiffonade',
    term: 'Chiffonade',
    category: 'Knife Cuts',
    pronunciation: 'shih-fuh-NAHD',
    specBadge: 'Thin ribbons',
    definition:
      'Stack leafy greens or herbs (basil, sage, spinach), roll tightly into a cigar, and slice crosswise into fine ribbons.',
    chefTip:
      'Use a sharp slicing motion rather than pressing straight down so delicate herbs do not bruise black.',
    keywords: ['chiffonade', 'basil', 'ribbons', 'herbs', 'knife cut'],
  },
  {
    id: 'cut-mince',
    term: 'Mince',
    category: 'Knife Cuts',
    specBadge: '< 1/16" (< 1.5 mm)',
    definition:
      'Chopping ingredients (garlic, ginger, shallots) into tiny, irregular pieces so they melt into a sauce or paste.',
    keywords: ['mince', 'garlic', 'ginger', 'knife cut'],
  },
  {
    id: 'cut-paysanne',
    term: 'Paysanne',
    category: 'Knife Cuts',
    pronunciation: 'pay-ZAHN',
    specBadge: '1/2" × 1/2" × 1/8" tiles',
    definition:
      'Thin flat squares, triangles, or half-moons cut from 1/2-inch batons for rustic soups and quick sautés.',
    keywords: ['paysanne', 'tiles', 'rustic', 'knife cut'],
  },
  {
    id: 'cut-rondelle',
    term: 'Rondelle',
    category: 'Knife Cuts',
    specBadge: 'Round coins',
    definition:
      'Uniform crosswise slices of cylindrical vegetables like carrots, parsnips, or cucumbers.',
    keywords: ['rondelle', 'rounds', 'coins', 'slice', 'knife cut'],
  },
  {
    id: 'cut-bias',
    term: 'Bias / Roll Cut (Oblique)',
    category: 'Knife Cuts',
    specBadge: '45° diagonal cut',
    definition:
      'Cutting at a 45° angle (and rotating 90° between cuts for roll-cut) to maximize surface area for browning and stir-frying.',
    keywords: ['bias', 'oblique', 'roll cut', 'scallion', 'stir fry', 'knife cut'],
  },
  {
    id: 'cut-supreme',
    term: 'Suprême (Citrus)',
    category: 'Knife Cuts',
    pronunciation: 'soo-PREM',
    definition:
      'Removing citrus peel and pith with a knife, then slicing between membranes to release pure, membrane-free segments.',
    keywords: ['supreme', 'citrus', 'orange', 'grapefruit', 'segment'],
  },

  // Cooking Methods
  {
    id: 'method-blanch',
    term: 'Blanch (and Shock)',
    category: 'Cooking Methods',
    specBadge: 'Boiling water -> Ice bath',
    definition:
      'Briefly plunge food into salted boiling water (30 sec to a few min) to set bright color, tenderize slightly, or loosen skins, then immediately plunge ("shock") into ice water to stop carryover cooking.',
    chefTip: 'Use plenty of water so adding cold vegetables does not kill the boil.',
    keywords: ['blanch', 'shock', 'parboil', 'green beans', 'tomatoes', 'ice bath'],
  },
  {
    id: 'method-braise',
    term: 'Braise',
    category: 'Cooking Methods',
    specBadge: 'Sear + covered low simmer',
    definition:
      'Brown meat or hearty vegetables in fat first, then cook partially submerged in flavorful liquid, covered, at a gentle low heat until collagen melts.',
    keywords: ['braise', 'short ribs', 'pot roast', 'stew', 'low and slow'],
  },
  {
    id: 'method-deglaze',
    term: 'Deglaze',
    category: 'Cooking Methods',
    definition:
      'Pour wine, stock, vinegar, or water into a hot pan right after searing and scrape up the browned fond with a wooden spoon to build a pan sauce.',
    keywords: ['deglaze', 'fond', 'pan sauce', 'wine', 'stock'],
  },
  {
    id: 'method-poach',
    term: 'Poach',
    category: 'Cooking Methods',
    specBadge: '160–180°F (71–82°C)',
    definition:
      'Cook delicate proteins (eggs, fish, chicken breast, pears) submerged in barely trembling liquid with tiny bubbles clinging to the pot bottom — never bubbling.',
    keywords: ['poach', 'eggs', 'fish', 'gentle', 'temperature'],
  },
  {
    id: 'method-simmer',
    term: 'Simmer',
    category: 'Cooking Methods',
    specBadge: '185–205°F (85–96°C)',
    definition:
      'Gentle cooking liquid where small bubbles rise steadily and break the surface softly. A bare simmer shows just an occasional bubble.',
    keywords: ['simmer', 'gentle boil', 'soup', 'sauce', 'stock'],
  },
  {
    id: 'method-sweat',
    term: 'Sweat',
    category: 'Cooking Methods',
    definition:
      'Cook aromatics gently in fat over low-to-medium-low heat with a pinch of salt so they release moisture and turn translucent without browning.',
    keywords: ['sweat', 'onion', 'aromatics', 'translucent', 'soften'],
  },
  {
    id: 'method-saute',
    term: 'Sauté',
    category: 'Cooking Methods',
    pronunciation: 'saw-TAY',
    definition:
      'Cook food quickly in a small amount of hot fat over medium-high to high heat, tossing or stirring frequently to brown evenly.',
    keywords: ['saute', 'pan fry', 'toss', 'brown'],
  },
  {
    id: 'method-sear',
    term: 'Sear',
    category: 'Cooking Methods',
    definition:
      'Cook the surface of meat, fish, or vegetables over high heat without moving it until a deep brown Maillard crust forms.',
    chefTip:
      'Pat proteins bone-dry with paper towels first — surface moisture steams instead of browning.',
    keywords: ['sear', 'crust', 'maillard', 'steak', 'brown'],
  },
  {
    id: 'method-render',
    term: 'Render',
    category: 'Cooking Methods',
    definition:
      'Slowly melt solid fat out of meat or skin (like bacon, pancetta, or duck breast) over low-to-medium heat until crisp and golden.',
    keywords: ['render', 'bacon', 'duck', 'fat', 'crispy'],
  },
  {
    id: 'method-reduce',
    term: 'Reduce / Au Sec',
    category: 'Cooking Methods',
    definition:
      'Simmer a liquid uncovered to evaporate water, concentrating flavor and thickening body. "Au sec" means reduced until nearly dry.',
    keywords: ['reduce', 'reduction', 'au sec', 'glaze', 'thicken'],
  },
  {
    id: 'method-confit',
    term: 'Confit',
    category: 'Cooking Methods',
    pronunciation: 'kon-FEE',
    definition:
      'Slowly poach salted meat, garlic, or tomatoes completely submerged in fat at very low temperature until meltingly tender.',
    keywords: ['confit', 'duck', 'garlic', 'oil', 'slow'],
  },
  {
    id: 'method-steep',
    term: 'Steep / Infuse',
    category: 'Cooking Methods',
    definition:
      'Soak spices, tea, herbs, or aromatics in hot off-heat liquid to extract volatile oils and flavor without turning bitter.',
    keywords: ['steep', 'infuse', 'cream', 'herbs', 'spices'],
  },

  // Prep & Techniques
  {
    id: 'tech-fold',
    term: 'Fold',
    category: 'Prep & Techniques',
    definition:
      'Gently incorporate a light airy mixture (whipped cream or egg whites) into a heavier batter by cutting down the center, sweeping around the bowl bottom, and turning over.',
    keywords: ['fold', 'meringue', 'whipped cream', 'batter', 'deflate'],
  },
  {
    id: 'tech-temper',
    term: 'Temper',
    category: 'Prep & Techniques',
    definition:
      'Gradually warm egg yolks (or cool melted chocolate) by whisking in a small ladle of hot liquid first so the eggs do not scramble when added to the pot.',
    keywords: ['temper', 'eggs', 'custard', 'chocolate', 'scramble'],
  },
  {
    id: 'tech-emulsify',
    term: 'Emulsify',
    category: 'Prep & Techniques',
    definition:
      'Bind two liquids that normally separate (like oil and vinegar/water) into a smooth, unified sauce by whisking slowly with an emulsifier such as mustard or egg yolk.',
    keywords: ['emulsify', 'vinaigrette', 'mayo', 'hollandaise', 'pan sauce'],
  },
  {
    id: 'tech-nappe',
    term: 'Nappe',
    category: 'Prep & Techniques',
    pronunciation: 'nap-PAY',
    definition:
      'A sauce or custard thickened just enough to coat the back of a spoon; running a finger across leaves a clean path that does not run together.',
    keywords: ['nappe', 'coat spoon', 'custard', 'sauce'],
  },
  {
    id: 'tech-cream',
    term: 'Cream (Butter & Sugar)',
    category: 'Prep & Techniques',
    definition:
      'Beat cool-room-temperature butter (65–68°F) and sugar together until pale, fluffy, and aerated — sugar crystals carve microscopic air pockets for lift.',
    keywords: ['cream', 'butter', 'sugar', 'baking', 'cookies', 'cake'],
  },
  {
    id: 'tech-bloom',
    term: 'Bloom',
    category: 'Prep & Techniques',
    definition:
      'Hydrate powdered gelatin in cold liquid before melting, or briefly toast ground spices/cocoa in warm fat/liquid to unlock fat-soluble aromas.',
    keywords: ['bloom', 'gelatin', 'spices', 'cocoa', 'yeast'],
  },
  {
    id: 'tech-autolyse',
    term: 'Autolyse',
    category: 'Prep & Techniques',
    definition:
      'Mix just flour and water and rest 20–60 minutes before adding salt and yeast/starter so gluten forms effortlessly and dough stretches without tearing.',
    keywords: ['autolyse', 'bread', 'sourdough', 'dough', 'gluten'],
  },
  {
    id: 'tech-degorge',
    term: 'Dégorger (Salt & Drain)',
    category: 'Prep & Techniques',
    definition:
      'Toss sliced eggplant, zucchini, or cucumber with salt and drain 20–30 minutes to draw out excess water so they brown rather than steam or turn soggy.',
    keywords: ['degorge', 'salt', 'eggplant', 'cucumber', 'drain'],
  },
  {
    id: 'tech-velveting',
    term: 'Velveting',
    category: 'Prep & Techniques',
    definition:
      'Coat sliced stir-fry meat in egg white, cornstarch, and a splash of wine/oil before quick cooking so it stays impossibly silky and moist.',
    keywords: ['velveting', 'stir fry', 'cornstarch', 'tender', 'chinese'],
  },
  {
    id: 'tech-spatchcock',
    term: 'Spatchcock (Butterfly)',
    category: 'Prep & Techniques',
    definition:
      'Snip out a whole chicken or turkey backbone with kitchen shears and press flat so white and dark meat roast evenly in nearly half the time.',
    keywords: ['spatchcock', 'butterfly', 'chicken', 'turkey', 'roast'],
  },
  {
    id: 'tech-dry-brine',
    term: 'Dry Brine',
    category: 'Prep & Techniques',
    definition:
      'Salt meat evenly 12–48 hours ahead and rest uncovered on a rack in the fridge; salt seasons deep inside while the fridge air dries the skin for extra crispiness.',
    keywords: ['dry brine', 'salt', 'roast', 'crispy skin', 'turkey', 'steak'],
  },
  {
    id: 'tech-mise-en-place',
    term: 'Mise en Place',
    category: 'Prep & Techniques',
    pronunciation: 'meez ahn plahs',
    definition:
      '"Everything in its place" — wash, chop, measure, and arrange all ingredients and tools before turning on the heat.',
    keywords: ['mise en place', 'prep', 'organization'],
  },
  {
    id: 'tech-al-dente',
    term: 'Al Dente',
    category: 'Prep & Techniques',
    definition:
      '"To the tooth" — pasta or vegetables cooked until tender outside with a faint firm resistance at the very core (pasta finishes cooking in the sauce).',
    keywords: ['al dente', 'pasta', 'noodles'],
  },

  // Sauces & Classics
  {
    id: 'classic-roux',
    term: 'Roux',
    category: 'Sauces & Classics',
    pronunciation: 'roo',
    specBadge: '1:1 fat & flour by weight',
    definition:
      'Equal weights butter (or oil) and flour cooked together to thicken sauces/soups: White (2–3 min, béchamel), Blond (5 min, velouté), Brown/Dark (15–40 min, gumbo).',
    keywords: ['roux', 'bechamel', 'gravy', 'gumbo', 'thicken'],
  },
  {
    id: 'classic-mirepoix',
    term: 'Mirepoix',
    category: 'Sauces & Classics',
    pronunciation: 'meer-PWAH',
    specBadge: '2 : 1 : 1 (Onion : Carrot : Celery)',
    definition:
      'Classic French aromatic base of 50% onion, 25% carrot, and 25% celery by weight for stocks, soups, and braises.',
    keywords: ['mirepoix', 'aromatics', 'onion', 'carrot', 'celery', 'stock'],
  },
  {
    id: 'classic-trinity',
    term: 'Holy Trinity (Cajun / Creole)',
    category: 'Sauces & Classics',
    specBadge: '2 : 1 : 1 (Onion : Celery : Bell Pepper)',
    definition:
      'Louisiana aromatic base swapping carrots for green bell pepper; often joined by garlic ("the pope").',
    keywords: ['holy trinity', 'cajun', 'creole', 'gumbo', 'bell pepper'],
  },
  {
    id: 'classic-soffritto',
    term: 'Soffritto (Battuto)',
    category: 'Sauces & Classics',
    definition:
      'Italian finely minced onion, carrot, and celery cooked slowly in olive oil until sweet and deeply aromatic for ragù and soups.',
    keywords: ['soffritto', 'italian', 'ragu', 'bolognese'],
  },
  {
    id: 'classic-bouquet-garni',
    term: 'Bouquet Garni & Sachet d’Épices',
    category: 'Sauces & Classics',
    definition:
      'Fresh herbs (parsley stems, thyme, bay leaf) tied together or wrapped in leek/cheesecloth for easy removal before serving; a sachet adds peppercorns and cloves.',
    keywords: ['bouquet garni', 'sachet', 'thyme', 'bay leaf', 'stock'],
  },
  {
    id: 'classic-bain-marie',
    term: 'Bain-Marie (Water Bath)',
    category: 'Sauces & Classics',
    definition:
      'Gentle indirect heating by setting a baking dish or bowl inside a pan of hot water (for custards/cheesecake) or over simmering water (for chocolate/sabayon).',
    keywords: ['bain marie', 'water bath', 'double boiler', 'cheesecake', 'custard'],
  },
  {
    id: 'classic-beurre-manie',
    term: 'Beurre Manié',
    category: 'Sauces & Classics',
    pronunciation: 'burr mahn-YAY',
    specBadge: '1:1 soft butter & flour paste',
    definition:
      'Raw butter kneaded with equal flour and whisked bit-by-bit into simmering liquid at the end of cooking for lump-free thickening and shine.',
    keywords: ['beurre manie', 'thicken', 'stew', 'sauce', 'butter'],
  },
  {
    id: 'classic-monte',
    term: 'Monter au Beurre',
    category: 'Sauces & Classics',
    definition:
      'Swirling cold diced butter into a hot sauce off the heat so it emulsifies into a glossy, velvety finish without separating.',
    keywords: ['monter au beurre', 'pan sauce', 'butter', 'glossy'],
  },
  {
    id: 'classic-slurry',
    term: 'Cornstarch Slurry',
    category: 'Sauces & Classics',
    specBadge: '1 tbsp cornstarch : 1 tbsp cold water',
    definition:
      'Smooth cold-liquid paste whisked into boiling liquid to thicken glossy stir-fry sauces or gravies instantly. Boil 1 minute to activate and clear.',
    keywords: ['slurry', 'cornstarch', 'thicken', 'stir fry'],
  },

  // Small Measures
  {
    id: 'measure-dash',
    term: 'Dash',
    category: 'Small Measures',
    specBadge: '≈ 1/8 tsp (6–8 drops)',
    definition:
      'Quick flick of liquid (hot sauce, bitters, vinegar) or a small shake of dry spice equal to roughly 1/8 teaspoon or two pinches.',
    keywords: ['dash', '1/8 tsp', 'bitters', 'hot sauce', 'small measure'],
  },
  {
    id: 'measure-pinch',
    term: 'Pinch',
    category: 'Small Measures',
    specBadge: '≈ 1/16 tsp',
    definition:
      'Amount of dry seasoning (salt, cayenne, nutmeg) picked up between thumb and forefinger — half of a dash.',
    keywords: ['pinch', '1/16 tsp', 'salt', 'small measure'],
  },
  {
    id: 'measure-smidgen',
    term: 'Smidgen',
    category: 'Small Measures',
    specBadge: '≈ 1/32 tsp',
    definition:
      'Tiny half-pinch trace amount of potent spice or extract (saffron, cayenne, cloves).',
    keywords: ['smidgen', '1/32 tsp', 'small measure'],
  },
  {
    id: 'measure-scant-heaping',
    term: 'Scant vs. Heaping',
    category: 'Small Measures',
    definition:
      '"Scant" means just barely shy of the rim (a hair less than measured); "heaping" or "rounded" means piled slightly above the rim of the spoon or cup.',
    keywords: ['scant', 'heaping', 'rounded', 'measure'],
  },
];

export const SUBSTITUTION_CATEGORIES: SubstitutionCategory[] = [
  'Dairy & Eggs',
  'Baking & Flour',
  'Salts & Seasonings',
  'Acids, Wine & Pantry',
];

export const CULINARY_SUBSTITUTIONS: SubstitutionEntry[] = [
  // Dairy & Eggs
  {
    id: 'sub-buttermilk',
    category: 'Dairy & Eggs',
    ingredient: 'Buttermilk',
    baseAmount: '1 cup',
    replacement: '1 tbsp lemon juice or white vinegar + milk to 1 cup',
    notes:
      'Stir and let sit 5–10 min until slightly thickened/curdled. Plain yogurt thinned 3:1 with milk also works.',
    keywords: ['buttermilk', 'milk', 'lemon juice', 'vinegar', 'pancake', 'biscuit'],
  },
  {
    id: 'sub-heavy-cream',
    category: 'Dairy & Eggs',
    ingredient: 'Heavy Cream (cooking/sauces)',
    baseAmount: '1 cup',
    replacement: '3/4 cup whole milk + 1/4 cup melted unsalted butter',
    notes: 'Works great in soups, pasta sauces, and baking — will not whip into peaks.',
    keywords: ['heavy cream', 'whipping cream', 'milk', 'butter'],
  },
  {
    id: 'sub-half-and-half',
    category: 'Dairy & Eggs',
    ingredient: 'Half-and-Half',
    baseAmount: '1 cup',
    replacement: '1/2 cup whole milk + 1/2 cup heavy cream',
    notes: 'Or 3/4 cup whole milk + 1/4 cup melted butter in cooked dishes.',
    keywords: ['half and half', 'cream', 'milk'],
  },
  {
    id: 'sub-sour-cream',
    category: 'Dairy & Eggs',
    ingredient: 'Sour Cream',
    baseAmount: '1 cup',
    replacement: '1 cup full-fat plain Greek yogurt',
    notes:
      'Identical tang and richness in baking and dips; whisk off heat in hot sauces so yogurt does not split.',
    keywords: ['sour cream', 'greek yogurt', 'yogurt'],
  },
  {
    id: 'sub-salted-butter',
    category: 'Dairy & Eggs',
    ingredient: 'Unsalted ↔ Salted Butter',
    baseAmount: '1/2 cup (1 stick / 113g)',
    replacement: '1/2 cup salted butter; reduce recipe salt by 1/4 tsp',
    notes: 'Works identically in reverse: add 1/4 tsp fine salt per stick when using unsalted.',
    keywords: ['unsalted butter', 'salted butter', 'stick', 'salt'],
  },
  {
    id: 'sub-egg-baking',
    category: 'Dairy & Eggs',
    ingredient: 'Large Egg (baking binder)',
    baseAmount: '1 egg',
    replacement: '1 tbsp ground flaxseed + 3 tbsp warm water (rest 5 min)',
    notes: 'Or 1/4 cup unsweetened applesauce / mashed banana for quick breads and muffins.',
    keywords: ['egg', 'flax', 'applesauce', 'vegan', 'baking'],
  },

  // Baking & Flour
  {
    id: 'sub-cake-flour',
    category: 'Baking & Flour',
    ingredient: 'Cake Flour',
    baseAmount: '1 cup (114g)',
    replacement: '1 cup minus 2 tbsp all-purpose flour + 2 tbsp cornstarch',
    notes: 'Sift together twice to mimic low-protein cake flour for tender sponge cakes.',
    keywords: ['cake flour', 'all purpose flour', 'cornstarch', 'sponge'],
  },
  {
    id: 'sub-self-rising',
    category: 'Baking & Flour',
    ingredient: 'Self-Rising Flour',
    baseAmount: '1 cup',
    replacement: '1 cup all-purpose flour + 1 1/2 tsp baking powder + 1/4 tsp fine salt',
    notes: 'Whisk thoroughly before folding into biscuits or quick batters.',
    keywords: ['self rising flour', 'baking powder', 'biscuits'],
  },
  {
    id: 'sub-baking-powder',
    category: 'Baking & Flour',
    ingredient: 'Baking Powder',
    baseAmount: '1 tsp',
    replacement: '1/4 tsp baking soda + 1/2 tsp cream of tartar',
    notes: 'Mix right before baking; reacts as soon as it hits wet ingredients.',
    keywords: ['baking powder', 'baking soda', 'cream of tartar', 'leavener'],
  },
  {
    id: 'sub-brown-sugar',
    category: 'Baking & Flour',
    ingredient: 'Light / Dark Brown Sugar',
    baseAmount: '1 cup packed',
    replacement: '1 cup white sugar + 1 tbsp molasses (2 tbsp for dark)',
    notes: 'Rub together with fingers or pulse in a food processor until uniform.',
    keywords: ['brown sugar', 'molasses', 'white sugar', 'cookies'],
  },
  {
    id: 'sub-cornstarch',
    category: 'Baking & Flour',
    ingredient: 'Cornstarch (sauce thickener)',
    baseAmount: '1 tbsp',
    replacement: '2 tbsp all-purpose flour OR 1 tbsp arrowroot/tapioca starch',
    notes:
      'Flour needs 3–5 min simmering to cook out raw taste; arrowroot works best for acidic fruit glazes.',
    keywords: ['cornstarch', 'flour', 'arrowroot', 'thickener', 'slurry'],
  },
  {
    id: 'sub-yeast',
    category: 'Baking & Flour',
    ingredient: 'Active Dry ↔ Instant Yeast',
    baseAmount: '1 packet (2 1/4 tsp / 7g)',
    replacement: '2 1/4 tsp instant yeast (1:1 swap)',
    notes:
      'Bloom active dry yeast in warm liquid (105–110°F) first; instant yeast can go straight into dry flour.',
    keywords: ['yeast', 'active dry', 'instant yeast', 'rapid rise', 'bread'],
  },
  {
    id: 'sub-cocoa',
    category: 'Baking & Flour',
    ingredient: 'Dutch-Process vs. Natural Cocoa',
    baseAmount: '1/4 cup',
    replacement: 'Swap 1:1 in recipes using only baking powder',
    notes:
      'Natural cocoa is acidic and reacts with baking soda; if swapping Dutch-process into a baking-soda recipe, use baking powder instead.',
    keywords: ['cocoa powder', 'dutch process', 'chocolate', 'baking soda'],
  },

  // Salts & Seasonings
  {
    id: 'sub-salt-dc-morton',
    category: 'Salts & Seasonings',
    ingredient: 'Diamond Crystal → Morton Kosher Salt',
    baseAmount: '1 tbsp Diamond Crystal',
    replacement: '1 3/4 tsp Morton Kosher (or 1 1/2 tsp fine table salt)',
    notes:
      'By weight they are identical (1g = 1g). By volume, Diamond Crystal flakes are nearly twice as airy.',
    keywords: ['salt', 'diamond crystal', 'morton', 'kosher salt', 'table salt'],
  },
  {
    id: 'sub-salt-table-dc',
    category: 'Salts & Seasonings',
    ingredient: 'Fine Table Salt → Diamond Crystal Kosher',
    baseAmount: '1 tsp table salt (~6g)',
    replacement: '2 tsp Diamond Crystal Kosher (~5.6g)',
    notes: 'Or 1 1/4 tsp Morton Coarse Kosher.',
    keywords: ['table salt', 'kosher salt', 'diamond crystal', 'morton'],
  },
  {
    id: 'sub-herbs',
    category: 'Salts & Seasonings',
    ingredient: 'Fresh ↔ Dried Herbs',
    baseAmount: '1 tbsp fresh chopped',
    replacement: '1 tsp dried herbs (3:1 fresh-to-dried ratio)',
    notes:
      'Add dried herbs early in cooking to bloom; add delicate fresh herbs (basil, cilantro, parsley) at the end.',
    keywords: ['herbs', 'fresh', 'dried', 'thyme', 'oregano', 'rosemary'],
  },
  {
    id: 'sub-shallot',
    category: 'Salts & Seasonings',
    ingredient: 'Shallot',
    baseAmount: '1 medium (~3 tbsp minced)',
    replacement: '3 tbsp finely minced yellow onion + 1/2 small garlic clove',
    notes: 'Or white parts of 2 scallions for raw vinaigrettes.',
    keywords: ['shallot', 'onion', 'garlic', 'scallion'],
  },
  {
    id: 'sub-garlic',
    category: 'Salts & Seasonings',
    ingredient: 'Fresh Garlic Clove',
    baseAmount: '1 medium clove',
    replacement: '1/2 tsp jarred minced garlic OR 1/8 tsp garlic powder',
    notes: 'Garlic powder shines in dry rubs and marinades; bloom gently so it never scorches.',
    keywords: ['garlic', 'clove', 'garlic powder'],
  },

  // Acids, Wine & Pantry
  {
    id: 'sub-white-wine',
    category: 'Acids, Wine & Pantry',
    ingredient: 'Dry White Wine (deglazing/sauces)',
    baseAmount: '1/2 cup',
    replacement: '1/2 cup chicken/veggie broth + 1 tsp lemon juice or white wine vinegar',
    notes: 'Avoid "cooking wine" (heavily salted).',
    keywords: ['white wine', 'broth', 'alcohol free', 'deglaze', 'risotto'],
  },
  {
    id: 'sub-red-wine',
    category: 'Acids, Wine & Pantry',
    ingredient: 'Dry Red Wine (braising/ragù)',
    baseAmount: '1 cup',
    replacement: '1 cup beef broth + 1 tbsp red wine vinegar or balsamic',
    notes: 'Adds both savory depth and bright acidity without alcohol.',
    keywords: ['red wine', 'beef broth', 'braise', 'alcohol free'],
  },
  {
    id: 'sub-rice-vinegar',
    category: 'Acids, Wine & Pantry',
    ingredient: 'Unseasoned Rice Vinegar',
    baseAmount: '1 tbsp',
    replacement: '1 tbsp apple cider vinegar or white wine vinegar + pinch of sugar',
    notes: 'Milder acidity than distilled white vinegar.',
    keywords: ['rice vinegar', 'vinegar', 'acid'],
  },
  {
    id: 'sub-soy-sauce',
    category: 'Acids, Wine & Pantry',
    ingredient: 'Soy Sauce',
    baseAmount: '1 tbsp',
    replacement: '1 tbsp tamari (gluten-free) OR 1 tbsp coconut aminos + pinch salt',
    notes: 'Coconut aminos is slightly sweeter and less salty.',
    keywords: ['soy sauce', 'tamari', 'coconut aminos', 'gluten free'],
  },
  {
    id: 'sub-tomato-paste',
    category: 'Acids, Wine & Pantry',
    ingredient: 'Tomato Paste',
    baseAmount: '1 tbsp',
    replacement: '3 tbsp tomato sauce (reduce other recipe liquid by 2 tbsp)',
    notes: 'Caramelize paste in oil 1–2 minutes before adding liquids for umami depth.',
    keywords: ['tomato paste', 'tomato sauce', 'umami'],
  },
];

export const COOKING_RATIOS_AND_TIMINGS: CookingTimingEntry[] = [
  // Eggs
  {
    id: 'time-egg-soft',
    group: 'Eggs',
    item: 'Soft-Boiled Egg (Runny Yolk)',
    ratioDisplay: 'Into simmering water',
    cookTimeLabel: '6 min',
    timerSeconds: 360,
    methodNote:
      'Lower fridge-cold large eggs into gently boiling water; plunge straight into an ice bath.',
    keywords: ['egg', 'soft boiled', 'runny', 'ramen'],
  },
  {
    id: 'time-egg-jammy',
    group: 'Eggs',
    item: 'Jammy Ramen Egg',
    ratioDisplay: 'Into simmering water',
    cookTimeLabel: '7 min',
    timerSeconds: 420,
    methodNote:
      'Custardy, sliceable golden yolk and fully set whites; chill in ice water 5 min before peeling.',
    keywords: ['egg', 'jammy', 'ramen', 'soft boiled'],
  },
  {
    id: 'time-egg-medium',
    group: 'Eggs',
    item: 'Medium-Firm Boiled Egg',
    ratioDisplay: 'Into simmering water',
    cookTimeLabel: '9 min',
    timerSeconds: 540,
    methodNote: 'Rich, creamy set yolk with zero chalkiness — ideal for Niçoise salads.',
    keywords: ['egg', 'medium boiled', 'salad'],
  },
  {
    id: 'time-egg-hard',
    group: 'Eggs',
    item: 'Classic Hard-Boiled Egg',
    ratioDisplay: 'Into simmering water',
    cookTimeLabel: '11 min',
    timerSeconds: 660,
    methodNote: 'Firm peel-friendly yolk without gray sulfur ring; shock in ice water immediately.',
    keywords: ['egg', 'hard boiled', 'deviled eggs', 'egg salad'],
  },

  // Rice & Grains
  {
    id: 'time-white-rice',
    group: 'Rice & Grains',
    item: 'Long-Grain White / Jasmine Rice',
    ratioDisplay: '1 : 1.25 water',
    cookTimeLabel: '15 min',
    timerSeconds: 900,
    methodNote:
      'Rinse well, bring to a boil, cover tight on lowest heat 15 min, then rest covered 10 min off heat.',
    keywords: ['white rice', 'jasmine', 'rice', 'ratio', 'grain'],
  },
  {
    id: 'time-basmati-rice',
    group: 'Rice & Grains',
    item: 'Basmati Rice',
    ratioDisplay: '1 : 1.5 water',
    cookTimeLabel: '15 min',
    timerSeconds: 900,
    methodNote:
      'Rinse until clear (soak 20 min optional); simmer covered 15 min + 10 min covered rest.',
    keywords: ['basmati', 'rice', 'indian', 'grain'],
  },
  {
    id: 'time-sushi-rice',
    group: 'Rice & Grains',
    item: 'Short-Grain / Sushi Rice',
    ratioDisplay: '1 : 1.15 water',
    cookTimeLabel: '15 min',
    timerSeconds: 900,
    methodNote:
      'Rinse thoroughly, rest 10 min before boiling; cook covered 15 min + 10 min steam rest.',
    keywords: ['sushi rice', 'short grain', 'japanese', 'rice'],
  },
  {
    id: 'time-brown-rice',
    group: 'Rice & Grains',
    item: 'Brown Rice (Long or Short)',
    ratioDisplay: '1 : 2 water',
    cookTimeLabel: '45 min',
    timerSeconds: 2700,
    methodNote:
      'Simmer covered on low heat 45 min, rest covered 10 min off heat, then fluff with fork.',
    keywords: ['brown rice', 'whole grain', 'rice'],
  },
  {
    id: 'time-quinoa',
    group: 'Rice & Grains',
    item: 'Quinoa',
    ratioDisplay: '1 : 1.75 water',
    cookTimeLabel: '15 min',
    timerSeconds: 900,
    methodNote:
      'Rinse to remove bitter saponin; simmer covered 15 min until little spirals pop, rest 5 min.',
    keywords: ['quinoa', 'grain', 'bowl'],
  },
  {
    id: 'time-farro',
    group: 'Rice & Grains',
    item: 'Pearled / Semi-Pearled Farro',
    ratioDisplay: '1 : 3 water (pasta-style)',
    cookTimeLabel: '25 min',
    timerSeconds: 1500,
    methodNote: 'Boil uncovered in salted water 20–25 min until chewy-tender, then drain.',
    keywords: ['farro', 'wheat berry', 'grain', 'salad'],
  },
  {
    id: 'time-couscous',
    group: 'Rice & Grains',
    item: 'Moroccan Couscous',
    ratioDisplay: '1 : 1 boiling liquid',
    cookTimeLabel: '5 min',
    timerSeconds: 300,
    methodNote:
      'Pour boiling salted broth/water over couscous, cover tightly off heat 5 min, fluff with fork.',
    keywords: ['couscous', 'moroccan', 'quick grain'],
  },

  // Oats & Legumes
  {
    id: 'time-rolled-oats',
    group: 'Oats & Legumes',
    item: 'Old-Fashioned Rolled Oats',
    ratioDisplay: '1 : 2 water/milk',
    cookTimeLabel: '5 min',
    timerSeconds: 300,
    methodNote: 'Simmer uncovered 5 min stirring occasionally with a pinch of salt; rest 2 min.',
    keywords: ['oats', 'oatmeal', 'porridge', 'breakfast'],
  },
  {
    id: 'time-steel-cut-oats',
    group: 'Oats & Legumes',
    item: 'Steel-Cut Oats',
    ratioDisplay: '1 : 3 water',
    cookTimeLabel: '25 min',
    timerSeconds: 1500,
    methodNote:
      'Toast oats 2 min in butter optional; simmer gently uncovered 25–30 min stirring near the end.',
    keywords: ['steel cut oats', 'oatmeal', 'irish oats', 'breakfast'],
  },
  {
    id: 'time-green-lentils',
    group: 'Oats & Legumes',
    item: 'Green / Brown / French Lentils',
    ratioDisplay: '1 : 3 water/broth',
    cookTimeLabel: '25 min',
    timerSeconds: 1500,
    methodNote:
      'Gentle bare simmer uncovered (boiling splits skins); salt near the end, drain excess liquid.',
    keywords: ['lentils', 'green lentils', 'puy', 'legumes'],
  },
  {
    id: 'time-red-lentils',
    group: 'Oats & Legumes',
    item: 'Split Red / Yellow Lentils',
    ratioDisplay: '1 : 2.5 water/broth',
    cookTimeLabel: '12 min',
    timerSeconds: 720,
    methodNote: 'Simmer 12–15 min until creamy and broken down for dal or thick soups.',
    keywords: ['red lentils', 'dal', 'soup', 'legumes'],
  },
];

export const CLASSIC_CULINARY_RATIOS: ClassicRatioEntry[] = [
  {
    id: 'ratio-vinaigrette',
    name: 'Classic Vinaigrette',
    ratio: '3 : 1',
    basis: '3 parts oil : 1 part acid (by volume)',
    example: '6 tbsp olive oil + 2 tbsp vinegar + 1 tsp Dijon + salt & pepper',
    note: 'Mustard acts as an emulsifier so dressing clings to greens.',
    keywords: ['vinaigrette', 'salad dressing', 'oil', 'vinegar', 'ratio'],
  },
  {
    id: 'ratio-roux',
    name: 'Roux (Sauce Thickener)',
    ratio: '1 : 1',
    basis: '1 part fat : 1 part flour (by weight)',
    example: '30g (2 tbsp) butter + 30g (~1/4 cup) flour thickens ~2 cups liquid',
    note: 'Cook longer for nuttier flavor (darker roux has slightly less thickening power).',
    keywords: ['roux', 'bechamel', 'gravy', 'sauce', 'ratio'],
  },
  {
    id: 'ratio-pie-dough',
    name: 'Pâte Brisée / Pie Dough',
    ratio: '3 : 2 : 1',
    basis: '3 flour : 2 fat : 1 ice water (by weight)',
    example: '300g flour + 200g cold butter + 100g ice water + 1 tsp salt (makes 2 crusts)',
    note: 'Keep pea-sized cold butter chunks for flaky steam pockets.',
    keywords: ['pie dough', 'pastry', 'crust', '3 2 1', 'ratio'],
  },
  {
    id: 'ratio-pasta',
    name: 'Fresh Egg Pasta Dough',
    ratio: '3 : 2',
    basis: '3 parts flour : 2 parts whole egg (by weight)',
    example: '300g 00/AP flour + ~200g eggs (about 3–4 large eggs)',
    note: 'Knead 8–10 min until smooth; rest wrapped 30 min before rolling.',
    keywords: ['pasta', 'fresh pasta', 'dough', 'flour', 'egg', 'ratio'],
  },
  {
    id: 'ratio-brine',
    name: '5% Equilibrium Wet Brine',
    ratio: '50g salt / 1 L water',
    basis: '5% salt by water weight',
    example: '50g kosher salt (3 tbsp Morton or 5 tbsp Diamond Crystal) per 1 quart/liter water',
    note: 'Great for pork chops (2–4 hrs) or chicken pieces (1–2 hrs) without over-salting.',
    keywords: ['brine', 'wet brine', 'pork', 'chicken', 'salt', 'ratio'],
  },
  {
    id: 'ratio-syrup',
    name: 'Simple & Rich Syrup',
    ratio: '1 : 1 (Simple) · 2 : 1 (Rich)',
    basis: 'Sugar : Water (by volume or weight)',
    example: '1 cup sugar + 1 cup water — heat just until dissolved, cool and chill',
    note: '2:1 rich syrup stays shelf-stable longer and adds body to cocktails and cake soaks.',
    keywords: ['simple syrup', 'rich syrup', 'cocktails', 'cake soak', 'ratio'],
  },
];
