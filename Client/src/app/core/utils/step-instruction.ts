import {
  InlineStepAmountKind,
  RecipeMeasurement,
  RecipeStepInlineAmountDto,
  UnitSystem,
} from '../models/recipe.models';
import { IngredientMeasurements, scaleAndConvert, scaleStepIngredient } from './culinary-math';

export interface InstructionSegment {
  text: string;
  amount?: {
    kind: InlineStepAmountKind;
    ingredientId: string | null;
    ingredientName: string | null;
    scaled: boolean;
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
    valid.push(amount);
    end = amount.start + amount.length;
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
    cursor = amount.start + amount.length;
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

/** Converts editor inputs (row indices) to the DTO shape (row ids) used by the renderers. */
export function inlineAmountInputsToDtos(
  amounts: readonly {
    start: number;
    length: number;
    kind: InlineStepAmountKind;
    ingredientIndex: number | null;
    measurement: RecipeMeasurement;
    alternativeMeasurement?: RecipeMeasurement | null;
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
    placed.push({ ...amount, start: best });
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
  const shifted = amounts.map((a) => ({ ...a, start: a.start - lead }));
  return { instruction: trimmed, amounts: validInlineSpans(trimmed, shifted) };
}
