import {
  InlineStepAmountKind,
  RecipeMeasurement,
  RecipeStepInlineAmountDto,
  UnitSystem,
} from '../models/recipe.models';
import {
  IngredientMeasurements,
  measurementUnitFactor,
  preferredIngredientMeasurement,
  scaleAndConvert,
  scaleStepIngredient,
} from './culinary-math';

export interface InstructionSegment {
  text: string;
  amount?: {
    kind: InlineStepAmountKind;
    ingredientId: string | null;
    ingredientName: string | null;
    scaled: boolean;
  };
  ingredientHighlight?: {
    kind: InlineStepAmountKind;
    ingredientId: string | null;
    ingredientName: string | null;
  };
}

export interface InstructionStep {
  instruction: string;
  inlineAmounts?: readonly RecipeStepInlineAmountDto[] | null;
}

export interface NumberedInstructionStep extends InstructionStep {
  stepNumber: number;
}

export type InstructionIngredient = IngredientMeasurements & { id: string; displayName: string };

export interface InlineSpan {
  start: number;
  length: number;
  ingredientSpanStart?: number | null;
  ingredientSpanLength?: number | null;
}

export function validInlineSpans<T extends InlineSpan>(text: string, amounts: readonly T[]): T[] {
  const sorted = [...amounts].sort((a, b) => a.start - b.start);
  const valid: T[] = [];
  let end = 0;
  for (const amount of sorted) {
    if (
      !Number.isInteger(amount.start) ||
      !Number.isInteger(amount.length) ||
      amount.start < end ||
      amount.length < 1 ||
      amount.start + amount.length > text.length
    )
      continue;

    const item: T = { ...amount };
    if (amount.ingredientSpanStart != null && amount.ingredientSpanLength != null) {
      if (
        !Number.isInteger(amount.ingredientSpanStart) ||
        !Number.isInteger(amount.ingredientSpanLength) ||
        amount.ingredientSpanLength < 1 ||
        amount.ingredientSpanStart < amount.start + amount.length ||
        amount.ingredientSpanStart + amount.ingredientSpanLength > text.length
      ) {
        item.ingredientSpanStart = null;
        item.ingredientSpanLength = null;
      }
    } else if (amount.ingredientSpanStart !== undefined) {
      item.ingredientSpanStart = null;
      item.ingredientSpanLength = null;
    }

    valid.push(item);
    end = amount.start + amount.length;
  }

  for (let i = 0; i < valid.length; i++) {
    const item = valid[i];
    if (item.ingredientSpanStart != null && item.ingredientSpanLength != null) {
      const ingEnd = item.ingredientSpanStart + item.ingredientSpanLength;
      const nextAmount = valid[i + 1];
      if (nextAmount && ingEnd > nextAmount.start) {
        item.ingredientSpanStart = null;
        item.ingredientSpanLength = null;
      }
    }
  }

  return valid;
}

function joinAmount(amount: { formattedAmount: string; unitLabel: string }): string {
  return [amount.formattedAmount, amount.unitLabel].filter(Boolean).join(' ');
}

function scaleMeasurement(
  measurement: RecipeMeasurement,
  factor: number,
  system: UnitSystem,
): string {
  return joinAmount(
    scaleAndConvert(
      measurement.quantity,
      measurement.maxQuantity,
      measurement.unit,
      measurement.customUnitName,
      factor,
      system,
    ),
  );
}

function scaledAmountText(
  amount: RecipeStepInlineAmountDto,
  ingredient: InstructionIngredient | undefined,
  factor: number,
  system: UnitSystem,
  includeAlternative: boolean,
): string | null {
  if (amount.measurement?.quantity == null) return null;
  const primary =
    amount.kind === 'Ingredient' && ingredient
      ? joinAmount(scaleStepIngredient(ingredient, amount.measurement, factor, system))
      : scaleMeasurement(amount.measurement, factor, system);
  if (!primary) return null;
  const alternative =
    includeAlternative && amount.alternativeMeasurement?.quantity != null
      ? scaleMeasurement(amount.alternativeMeasurement, factor, system)
      : '';
  return alternative ? `${primary} (${alternative})` : primary;
}

export function buildInstructionSegments(
  step: InstructionStep,
  ingredients: readonly InstructionIngredient[],
  factor: number,
  system: UnitSystem,
): InstructionSegment[] {
  const text = step.instruction ?? '';
  const segments: InstructionSegment[] = [];
  const isOriginal = factor === 1 && system === 'Original';
  let cursor = 0;
  for (const amount of validInlineSpans(text, step.inlineAmounts ?? [])) {
    if (amount.start > cursor) segments.push({ text: text.slice(cursor, amount.start) });
    const verbatim = text.slice(amount.start, amount.start + amount.length);
    const ingredient =
      amount.kind === 'Ingredient' && amount.ingredientId
        ? ingredients.find((i) => i.id === amount.ingredientId)
        : undefined;
    const scaledText =
      amount.kind === 'Fixed' || isOriginal
        ? null
        : scaledAmountText(amount, ingredient, factor, system, true);
    segments.push({
      text: scaledText ?? verbatim,
      amount: {
        kind: amount.kind,
        ingredientId: amount.kind === 'Ingredient' ? amount.ingredientId : null,
        ingredientName: ingredient?.displayName ?? null,
        scaled: scaledText !== null,
      },
    });

    const amountEnd = amount.start + amount.length;
    if (
      amount.ingredientSpanStart != null &&
      amount.ingredientSpanLength != null &&
      amount.ingredientSpanStart >= amountEnd &&
      amount.ingredientSpanStart + amount.ingredientSpanLength <= text.length
    ) {
      if (amount.ingredientSpanStart > amountEnd) {
        segments.push({ text: text.slice(amountEnd, amount.ingredientSpanStart) });
      }
      segments.push({
        text: text.slice(
          amount.ingredientSpanStart,
          amount.ingredientSpanStart + amount.ingredientSpanLength,
        ),
        ingredientHighlight: {
          kind: amount.kind,
          ingredientId: amount.kind === 'Ingredient' ? amount.ingredientId : null,
          ingredientName: ingredient?.displayName ?? null,
        },
      });
      cursor = amount.ingredientSpanStart + amount.ingredientSpanLength;
    } else {
      cursor = amountEnd;
    }
  }
  if (cursor < text.length || segments.length === 0) segments.push({ text: text.slice(cursor) });
  return segments;
}

export function renderScaledInstruction(
  step: InstructionStep,
  ingredients: readonly InstructionIngredient[],
  factor: number,
  system: UnitSystem,
): string {
  return buildInstructionSegments(step, ingredients, factor, system)
    .map((s) => s.text)
    .join('');
}

export function ingredientUses(
  steps: readonly NumberedInstructionStep[],
  ingredientId: string,
  ingredients: readonly InstructionIngredient[],
  factor: number,
  system: UnitSystem,
): { stepNumber: number; label: string }[] {
  const ingredient = ingredients.find((i) => i.id === ingredientId);
  return steps.flatMap((step) =>
    validInlineSpans(step.instruction ?? '', step.inlineAmounts ?? [])
      .filter((a) => a.kind === 'Ingredient' && a.ingredientId === ingredientId)
      .map((a) => ({
        stepNumber: step.stepNumber,
        label:
          scaledAmountText(a, ingredient, factor, system, false) ??
          step.instruction.slice(a.start, a.start + a.length),
      })),
  );
}

export function formatIngredientUses(uses: readonly { stepNumber: number; label: string }[]) {
  return `Divided: ${uses.map((u) => `${u.label} (step ${u.stepNumber})`).join(' · ')}`;
}

export function convertToIngredientUnit(
  amount: RecipeMeasurement,
  ingredient: IngredientMeasurements,
): number | null {
  if (amount.quantity == null) return null;
  const target = preferredIngredientMeasurement(ingredient);
  if (!target.unit) return null;

  if (
    amount.unit === target.unit &&
    (amount.unit !== 'Custom' ||
      amount.customUnitName?.toLowerCase() === target.customUnitName?.toLowerCase())
  ) {
    return amount.quantity;
  }

  const directFactor = measurementUnitFactor(amount, target);
  if (directFactor !== null) {
    return amount.quantity * directFactor;
  }

  const primary: RecipeMeasurement = {
    quantity: ingredient.quantity ?? null,
    maxQuantity: ingredient.maxQuantity,
    unit: ingredient.unit ?? null,
    customUnitName: ingredient.customUnitName,
  };
  const other = ingredient.preferAlternativeMeasurement
    ? primary
    : ingredient.alternativeMeasurement;

  if (other?.quantity && target.quantity && other.unit) {
    const otherFactor = measurementUnitFactor(amount, other);
    if (otherFactor !== null) {
      const ratio = (otherFactor * target.quantity) / other.quantity;
      return amount.quantity * ratio;
    }
  }

  return null;
}

export function isIngredientDivided(
  steps: readonly InstructionStep[],
  ingredientId: string,
  ingredients: readonly InstructionIngredient[],
  factor = 1,
  _system: UnitSystem = 'Original',
): boolean {
  const matchingSpans = steps.flatMap((step) =>
    validInlineSpans(step.instruction ?? '', step.inlineAmounts ?? []).filter(
      (a) => a.kind === 'Ingredient' && a.ingredientId === ingredientId,
    ),
  );

  if (matchingSpans.length >= 2) return true;
  if (matchingSpans.length !== 1) return false;

  const ingredient = ingredients.find((i) => i.id === ingredientId);
  if (!ingredient) return false;

  const target = preferredIngredientMeasurement(ingredient);
  if (target.quantity == null || target.quantity <= 0) return false;

  const useMeasurement = matchingSpans[0].measurement;
  if (useMeasurement?.quantity == null || useMeasurement.quantity <= 0) return false;

  const convertedQuantity = convertToIngredientUnit(useMeasurement, ingredient);
  if (convertedQuantity == null) return false;

  const scaledTargetQuantity = target.quantity * factor;
  const scaledUseQuantity = convertedQuantity * factor;

  return scaledUseQuantity < scaledTargetQuantity * 0.99;
}

/** Converts editor inputs (row indices) to the DTO shape (row ids) used by the renderers. */
export function inlineAmountInputsToDtos(
  amounts: readonly {
    start: number;
    length: number;
    kind: InlineStepAmountKind;
    ingredientIndex: number | null;
    measurement: RecipeMeasurement;
    alternativeMeasurement?: RecipeMeasurement | null;
    ingredientSpanStart?: number | null;
    ingredientSpanLength?: number | null;
  }[],
  ingredientIdAt: (index: number) => string | null | undefined,
): RecipeStepInlineAmountDto[] {
  return amounts.map((a) => ({
    start: a.start,
    length: a.length,
    kind: a.kind,
    ingredientId:
      a.kind === 'Ingredient' && a.ingredientIndex !== null
        ? (ingredientIdAt(a.ingredientIndex) ?? null)
        : null,
    measurement: a.measurement,
    alternativeMeasurement: a.alternativeMeasurement ?? null,
    ingredientSpanStart: a.ingredientSpanStart ?? null,
    ingredientSpanLength: a.ingredientSpanLength ?? null,
  }));
}

function commonPrefixLength(a: string, b: string): number {
  const max = Math.min(a.length, b.length);
  let i = 0;
  while (i < max && a[i] === b[i]) i++;
  return i;
}

function commonSuffixLength(a: string, b: string, prefix: number): number {
  const max = Math.min(a.length, b.length) - prefix;
  let i = 0;
  while (i < max && a[a.length - 1 - i] === b[b.length - 1 - i]) i++;
  return i;
}

export function relocateInlineAmounts<T extends InlineSpan>(
  oldText: string,
  newText: string,
  amounts: readonly T[],
): { amounts: T[]; dropped: number } {
  if (oldText === newText) {
    const valid = validInlineSpans(newText, amounts);
    return { amounts: valid, dropped: amounts.length - valid.length };
  }
  const prefix = commonPrefixLength(oldText, newText);
  const suffix = commonSuffixLength(oldText, newText, prefix);
  const delta = newText.length - oldText.length;
  const placed: T[] = [];
  let dropped = 0;
  const overlaps = (start: number, length: number) =>
    placed.some((p) => start < p.start + p.length && p.start < start + length);

  for (const amount of [...amounts].sort((a, b) => a.start - b.start)) {
    const spanText =
      amount.start >= 0 && amount.length > 0 && amount.start + amount.length <= oldText.length
        ? oldText.slice(amount.start, amount.start + amount.length)
        : '';
    if (!spanText) {
      dropped++;
      continue;
    }
    const expected =
      amount.start + amount.length <= prefix
        ? amount.start
        : amount.start >= oldText.length - suffix
          ? amount.start + delta
          : amount.start;
    let best: number | null = null;
    for (let i = newText.indexOf(spanText); i >= 0; i = newText.indexOf(spanText, i + 1)) {
      if (overlaps(i, spanText.length)) continue;
      if (best === null || Math.abs(i - expected) < Math.abs(best - expected)) best = i;
    }
    if (best === null) {
      dropped++;
      continue;
    }

    const placedItem: T = { ...amount, start: best };
    if (
      amount.ingredientSpanStart != null &&
      amount.ingredientSpanLength != null &&
      amount.ingredientSpanStart >= amount.start + amount.length &&
      amount.ingredientSpanStart + amount.ingredientSpanLength <= oldText.length
    ) {
      const ingText = oldText.slice(
        amount.ingredientSpanStart,
        amount.ingredientSpanStart + amount.ingredientSpanLength,
      );
      if (ingText) {
        const ingExpected =
          amount.ingredientSpanStart <= prefix
            ? amount.ingredientSpanStart
            : amount.ingredientSpanStart >= oldText.length - suffix
              ? amount.ingredientSpanStart + delta
              : amount.ingredientSpanStart + (best - amount.start);

        let bestIng: number | null = null;
        for (
          let j = newText.indexOf(ingText, best + spanText.length);
          j >= 0;
          j = newText.indexOf(ingText, j + 1)
        ) {
          if (overlaps(j, ingText.length)) continue;
          if (bestIng === null || Math.abs(j - ingExpected) < Math.abs(bestIng - ingExpected)) {
            bestIng = j;
          }
        }
        if (bestIng !== null && bestIng >= best + spanText.length) {
          placedItem.ingredientSpanStart = bestIng;
          placedItem.ingredientSpanLength = ingText.length;
        } else {
          placedItem.ingredientSpanStart = null;
          placedItem.ingredientSpanLength = null;
        }
      } else {
        placedItem.ingredientSpanStart = null;
        placedItem.ingredientSpanLength = null;
      }
    } else if (amount.ingredientSpanStart !== undefined) {
      placedItem.ingredientSpanStart = null;
      placedItem.ingredientSpanLength = null;
    }

    placed.push(placedItem);
  }
  return { amounts: placed.sort((a, b) => a.start - b.start), dropped };
}

/** Trims the instruction and shifts span offsets; spans outside the trimmed text are dropped. */
export function trimInstructionWithAmounts<T extends InlineSpan>(
  instruction: string,
  amounts: readonly T[],
): { instruction: string; amounts: T[] } {
  const trimmed = instruction.trim();
  const lead = instruction.length - instruction.trimStart().length;
  const shifted = amounts.map((a) => {
    const item: T = { ...a, start: a.start - lead };
    if (a.ingredientSpanStart != null) {
      item.ingredientSpanStart = a.ingredientSpanStart - lead;
    }
    return item;
  });
  return { instruction: trimmed, amounts: validInlineSpans(trimmed, shifted) };
}
