import { UnitSystem } from '../models/recipe.models';
import { IngredientMeasurements } from './culinary-math';
import {
  formatDimensionDisplay,
  formatFixedDisplay,
  formatMixtureDisplay,
  formatTemperatureDisplay,
  formatTimerToken,
  formatTokenDisplay,
  parseStepInstruction,
  shouldShowTimerLabel,
} from './step-token-parser';
import {
  StepDimensionToken,
  StepFixedToken,
  StepIngredientToken,
  StepMixtureToken,
  StepTemperatureToken,
  StepTimerToken,
} from '../models/step-token.models';

export interface InstructionSegment {
  text: string;
  amount?: {
    kind: 'Ingredient' | 'Mixture' | 'Timer' | 'Temperature' | 'Dimension' | 'Fixed' | string;
    ingredientId: string | null;
    ingredientName: string | null;
    scaled: boolean;
    linkedRecipeId?: string | null;
    isUnquantified?: boolean;
    isFixed?: boolean;
  };
  mention?: {
    ingredientId: string;
    ingredientName: string;
  };
}

export interface InstructionStep {
  instruction: string;
  sectionName?: string | null;
}

export interface NumberedInstructionStep extends InstructionStep {
  stepNumber: number;
}

export type InstructionIngredient = IngredientMeasurements & {
  id: string;
  displayName: string;
  sectionName?: string | null;
  linkedRecipeId?: string | null;
  linkedRecipeTitle?: string | null;
  linkedSubRecipeId?: string | null;
  linkedSubRecipeTitle?: string | null;
};

export function buildInstructionSegments(
  step: InstructionStep,
  _ingredients: readonly InstructionIngredient[] = [],
  factor = 1,
  system: UnitSystem = 'Original',
): InstructionSegment[] {
  const text = step.instruction ?? '';
  if (!text) {
    return [];
  }

  const rawSegments = parseStepInstruction(text);
  const segments: InstructionSegment[] = [];

  for (const seg of rawSegments) {
    if (!seg.token) {
      if (seg.text) {
        segments.push({ text: seg.text });
      }
      continue;
    }

    if (seg.token.type === 'timer') {
      const timer = seg.token as StepTimerToken;
      const showLabel = shouldShowTimerLabel(timer.label);
      segments.push({
        text: formatTimerToken(timer),
        amount: {
          kind: 'Timer',
          ingredientId: null,
          ingredientName: showLabel ? (timer.label ?? null) : null,
          scaled: false,
        },
      });
    } else if (seg.token.type === 'temperature') {
      const temp = seg.token as StepTemperatureToken;
      const { text: tempText, scaled: isScaled } = formatTemperatureDisplay(temp, system);
      segments.push({
        text: tempText,
        amount: {
          kind: 'Temperature',
          ingredientId: null,
          ingredientName: null,
          scaled: isScaled,
        },
      });
    } else if (seg.token.type === 'dimension') {
      const dim = seg.token as StepDimensionToken;
      const { text: dimText, scaled: isScaled } = formatDimensionDisplay(dim, system);
      segments.push({
        text: dimText,
        amount: {
          kind: 'Dimension',
          ingredientId: null,
          ingredientName: null,
          scaled: isScaled,
        },
      });
    } else if (seg.token.type === 'fixed') {
      const fx = seg.token as StepFixedToken;
      const { text: fxText, scaled: isScaled } = formatFixedDisplay(fx, system);
      segments.push({
        text: fxText,
        amount: {
          kind: 'Fixed',
          ingredientId: null,
          ingredientName: fx.name ?? null,
          scaled: isScaled,
        },
      });
    } else if (seg.token.type === 'mixture') {
      const mix = seg.token as StepMixtureToken;
      const { text: mixText, scaled: isScaled } = formatMixtureDisplay(mix, factor, system);
      const isFixed = Boolean(mix.fixed);
      segments.push({
        text: mixText,
        amount: {
          kind: 'Mixture',
          ingredientId: null,
          ingredientName: isFixed ? `${mix.name} (fixed per item)` : mix.name,
          scaled: isScaled,
          isFixed,
        },
      });
    } else {
      const ing = seg.token as StepIngredientToken;
      const matched = matchIngredient(ing, _ingredients, step.sectionName);
      const matchedId = matched?.id ?? ing.ingredientId ?? null;
      const linkedRecipeId = matched?.linkedRecipeId ?? matched?.linkedSubRecipeId ?? null;
      const displayText = formatTokenDisplay(ing, factor, system, matched);
      const noteText = ing.notes ? `Note: ${ing.notes}` : null;
      const isUnquantified = ing.amount == null || Number.isNaN(ing.amount);
      const isFixed = Boolean(ing.fixed);
      const isScaled = isFixed
        ? system !== 'Original' && !isUnquantified
        : (factor !== 1 || system !== 'Original') && !isUnquantified;
      const tooltipBase = noteText ?? ing.ingredient;
      segments.push({
        text: displayText,
        amount: {
          kind: 'Ingredient',
          ingredientId: matchedId,
          ingredientName: isFixed ? `${tooltipBase} (fixed per item)` : tooltipBase,
          scaled: isScaled,
          linkedRecipeId,
          isUnquantified,
          isFixed,
        },
      });
    }
  }

  return segments;
}

export function renderScaledInstruction(
  step: InstructionStep,
  ingredients: readonly InstructionIngredient[] = [],
  factor = 1,
  system: UnitSystem = 'Original',
): string {
  return buildInstructionSegments(step, ingredients, factor, system)
    .map((s) => s.text)
    .join('');
}

export function matchIngredient(
  token: StepIngredientToken,
  ingredients: readonly InstructionIngredient[],
  stepSectionName?: string | null,
): InstructionIngredient | undefined {
  if (token.ingredientId) {
    const found = ingredients.find((i) => i.id === token.ingredientId);
    if (found) return found;
  }
  if (
    token.ingredientIndex != null &&
    Number.isInteger(token.ingredientIndex) &&
    token.ingredientIndex >= 0 &&
    token.ingredientIndex < ingredients.length
  ) {
    return ingredients[token.ingredientIndex];
  }
  const clean = token.ingredient.trim().toLowerCase();
  const nameMatches = ingredients.filter((i) => {
    const name = i.displayName.trim().toLowerCase();
    return name === clean || name.includes(clean) || clean.includes(name);
  });

  if (nameMatches.length === 0) {
    return undefined;
  }

  if (nameMatches.length === 1) {
    return nameMatches[0];
  }

  const normalizedSection = stepSectionName?.trim().toLowerCase() ?? '';
  if (normalizedSection) {
    const inSection = nameMatches.find(
      (i) => (i.sectionName?.trim().toLowerCase() ?? '') === normalizedSection,
    );
    if (inSection) {
      return inSection;
    }
  }

  // Exact display name match
  const exact = nameMatches.find((i) => i.displayName.trim().toLowerCase() === clean);
  if (exact) {
    return exact;
  }

  return nameMatches[0];
}

export function ingredientUses(
  steps: readonly NumberedInstructionStep[],
  ingredientId: string,
  ingredients: readonly InstructionIngredient[],
  factor = 1,
  system: UnitSystem = 'Original',
): { stepNumber: number; label: string }[] {
  const ingredient = ingredients.find((i) => i.id === ingredientId);
  if (!ingredient) return [];

  const uses: { stepNumber: number; label: string }[] = [];

  for (const step of steps) {
    const segments = parseStepInstruction(step.instruction ?? '');
    for (const seg of segments) {
      if (seg.token && (seg.token.type === 'ingredient' || 'ingredient' in seg.token)) {
        const ingToken = seg.token as StepIngredientToken;
        if (ingToken.amount == null || Number.isNaN(ingToken.amount)) {
          continue;
        }
        const matched = matchIngredient(ingToken, ingredients, step.sectionName);
        if (matched?.id === ingredientId) {
          uses.push({
            stepNumber: step.stepNumber,
            label: formatTokenDisplay(ingToken, factor, system, matched),
          });
        }
      }
    }
  }

  return uses;
}

export function formatIngredientUseLines(
  uses: readonly { stepNumber: number; label: string }[],
): string[] {
  return uses.map((u) => `${u.label} (step ${u.stepNumber})`);
}

export function formatIngredientUses(
  uses: readonly { stepNumber: number; label: string }[],
): string {
  return formatIngredientUseLines(uses).join('\n');
}

export function isIngredientDivided(
  steps: readonly NumberedInstructionStep[],
  ingredientId: string,
  ingredients: readonly InstructionIngredient[],
  factor = 1,
  system: UnitSystem = 'Original',
): boolean {
  const uses = ingredientUses(steps, ingredientId, ingredients, factor, system);
  return uses.length > 1;
}
