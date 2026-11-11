import { RecipeMeasurement, UnitSystem } from '../models/recipe.models';
import { formatStepIngredient, IngredientMeasurements } from './culinary-math';
import { formatSeconds, formatTokenDisplay, parseStepInstruction } from './step-token-parser';
import {
  StepIngredientToken,
  StepTemperatureToken,
  StepTimerToken,
} from '../models/step-token.models';

export interface InstructionSegment {
  text: string;
  amount?: {
    kind: string;
    ingredientId: string | null;
    ingredientName: string | null;
    scaled: boolean;
  };
  mention?: {
    ingredientId: string;
    ingredientName: string;
  };
}

export interface InstructionStep {
  instruction: string;
  ingredientIds?: readonly string[] | null;
}

export interface NumberedInstructionStep extends InstructionStep {
  stepNumber: number;
}

export type InstructionIngredient = IngredientMeasurements & { id: string; displayName: string };

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
      segments.push({
        text: `${formatSeconds(timer.seconds)}${timer.label ? ' (' + timer.label + ')' : ''}`,
        amount: {
          kind: 'Fixed',
          ingredientId: null,
          ingredientName: timer.label ?? null,
          scaled: false,
        },
      });
    } else if (seg.token.type === 'temperature') {
      const temp = seg.token as StepTemperatureToken;
      const tempText =
        temp.fahrenheit != null && temp.celsius != null
          ? `${temp.fahrenheit}°F (${temp.celsius}°C)`
          : temp.fahrenheit != null
            ? `${temp.fahrenheit}°F`
            : temp.celsius != null
              ? `${temp.celsius}°C`
              : '';
      segments.push({
        text: tempText,
        amount: {
          kind: 'Fixed',
          ingredientId: null,
          ingredientName: null,
          scaled: false,
        },
      });
    } else {
      const ing = seg.token as StepIngredientToken;
      const displayText = formatTokenDisplay(ing, factor, system);
      const totalText =
        ing.displayAmount != null && ing.amount != null
          ? `Total recipe: ${formatTokenDisplay({ ...ing, displayAmount: ing.amount }, factor, system)}`
          : ing.notes
            ? `Note: ${ing.notes}`
            : null;
      const isScaled =
        (factor !== 1 || system !== 'Original') && (ing.displayAmount ?? ing.amount) != null;
      segments.push({
        text: displayText,
        amount: {
          kind: 'Ingredient',
          ingredientId: ing.ingredientId ?? null,
          ingredientName: totalText ?? ing.ingredient,
          scaled: isScaled,
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

function matchIngredient(
  token: StepIngredientToken,
  ingredients: readonly InstructionIngredient[],
): InstructionIngredient | undefined {
  if (token.ingredientId) {
    const found = ingredients.find((i) => i.id === token.ingredientId);
    if (found) return found;
  }
  const clean = token.ingredient.trim().toLowerCase();
  return ingredients.find((i) => {
    const name = i.displayName.trim().toLowerCase();
    return name === clean || name.includes(clean) || clean.includes(name);
  });
}

export function stepIngredientCallouts(
  step: InstructionStep & { ingredientIds?: readonly string[] } & {
    ingredientAmounts?:
      readonly { ingredientId: string; measurement?: RecipeMeasurement | null }[] | null;
  },
  ingredients: readonly InstructionIngredient[],
  factor = 1,
  system: UnitSystem = 'Original',
): { id: string; label: string }[] {
  const result: { id: string; label: string }[] = [];
  const seenIds = new Set<string>();

  // 1. From step tokens
  const segments = parseStepInstruction(step.instruction ?? '');
  const hasTokens = segments.some((s) => !!s.token);

  for (const seg of segments) {
    if (seg.token && (seg.token.type === 'ingredient' || 'ingredient' in seg.token)) {
      const ingToken = seg.token as StepIngredientToken;
      const matched = matchIngredient(ingToken, ingredients);
      const id = matched?.id ?? ingToken.ingredientId ?? ingToken.ingredient;
      if (!seenIds.has(id)) {
        seenIds.add(id);
        const label = formatTokenDisplay(ingToken, factor, system);
        result.push({ id, label });
      }
    }
  }

  // 2. From assigned ingredient amounts
  if (step.ingredientAmounts) {
    for (const a of step.ingredientAmounts) {
      if (!seenIds.has(a.ingredientId)) {
        seenIds.add(a.ingredientId);
        const matched = ingredients.find((i) => i.id === a.ingredientId);
        if (matched) {
          const label = a.measurement
            ? formatStepIngredient(matched, a.measurement, factor, system)
            : matched.displayName;
          result.push({ id: a.ingredientId, label });
        }
      }
    }
  }

  // 3. From ingredientIds (only when no step tokens present)
  if (!hasTokens && step.ingredientIds) {
    for (const id of step.ingredientIds) {
      if (!seenIds.has(id)) {
        seenIds.add(id);
        const matched = ingredients.find((i) => i.id === id);
        if (matched) {
          result.push({ id, label: matched.displayName });
        }
      }
    }
  }

  return result;
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
        const matched = matchIngredient(ingToken, ingredients);
        if (matched?.id === ingredientId) {
          uses.push({
            stepNumber: step.stepNumber,
            label: formatTokenDisplay(ingToken, factor, system),
          });
        }
      }
    }
  }

  return uses;
}

export function formatIngredientUses(
  uses: readonly { stepNumber: number; label: string }[],
): string {
  return `Divided: ${uses.map((u) => `${u.label} (step ${u.stepNumber})`).join(' · ')}`;
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

export function inlineAmountInputsToDtos(
  _amounts: readonly unknown[] = [],
  _mapIngredientId?: (index: number) => string | undefined,
): unknown[] {
  return [];
}
