import {
  InlineStepAmountKind,
  RecipeMeasurement,
  RecipeStepInlineAmountDto,
  RecipeStepDto,
  UnitSystem,
} from '../models/recipe.models';
import {
  IngredientMeasurements,
  measurementUnitFactor,
  preferredIngredientMeasurement,
  scaleAndConvert,
  scaleStepIngredient,
  formatStepIngredient,
} from './culinary-math';

export interface InstructionSegment {
  text: string;
  amount?: {
    kind: InlineStepAmountKind;
    ingredientId: string | null;
    ingredientName: string | null;
    scaled: boolean;
  };
  // A linked ingredient named in the text without a tagged amount; never scaled.
  mention?: {
    ingredientId: string;
    ingredientName: string;
  };
}

export interface InstructionStep {
  instruction: string;
  inlineAmounts?: readonly RecipeStepInlineAmountDto[] | null;
  ingredientIds?: readonly string[] | null;
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

function joinAmount(amount: { formattedMeasurement: string }): string {
  return amount.formattedMeasurement;
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
  const amounts = validInlineSpans(text, step.inlineAmounts ?? []);
  const mentions = ingredientMentions(text, step.ingredientIds ?? [], amounts, ingredients);
  const pushText = (from: number, to: number) => {
    for (const mention of mentions) {
      if (mention.start < from || mention.end > to) continue;
      if (mention.start > from) segments.push({ text: text.slice(from, mention.start) });
      segments.push({
        text: text.slice(mention.start, mention.end),
        mention: {
          ingredientId: mention.ingredient.id,
          ingredientName: mention.ingredient.displayName,
        },
      });
      from = mention.end;
    }
    if (to > from) segments.push({ text: text.slice(from, to) });
  };
  let cursor = 0;
  for (const amount of amounts) {
    pushText(cursor, amount.start);
    const verbatim = text.slice(amount.start, amount.start + amount.length);
    const ingredient =
      amount.kind === 'Ingredient' && amount.ingredientId
        ? ingredients.find((i) => i.id === amount.ingredientId)
        : undefined;
    const scaledText =
      amount.kind === 'Fixed' || isOriginal
        ? null
        : scaledAmountText(amount, ingredient, factor, system, true);
    const amountEnd = amount.start + amount.length;
    const end = amountHighlightEnd(amount);
    segments.push({
      text: (scaledText ?? verbatim) + text.slice(amountEnd, end),
      amount: {
        kind: amount.kind,
        ingredientId: amount.kind === 'Ingredient' ? amount.ingredientId : null,
        ingredientName: ingredient?.displayName ?? null,
        scaled: scaledText !== null,
      },
    });

    cursor = end;
  }
  pushText(cursor, text.length);
  if (segments.length === 0) segments.push({ text });
  return segments;
}

function amountHighlightEnd(amount: RecipeStepInlineAmountDto): number {
  return amount.ingredientSpanStart != null && amount.ingredientSpanLength != null
    ? amount.ingredientSpanStart + amount.ingredientSpanLength
    : amount.start + amount.length;
}

interface WordToken {
  word: string;
  start: number;
  end: number;
}

function tokenizeWords(text: string): WordToken[] {
  return Array.from(text.matchAll(/[\p{L}\p{M}]+/gu), (match) => ({
    word: ingredientWords(match[0]).join(''),
    start: match.index,
    end: match.index + match[0].length,
  })).filter((token) => token.word);
}

// Whole names plus trailing words ("olive oil", "oil") unless another ingredient's name
// also ends with that tail, so "juice" never matches when both lemon and lime juice exist.
function ingredientAliases(
  ingredient: InstructionIngredient,
  ingredients: readonly InstructionIngredient[],
): string[][] {
  const others = ingredients
    .filter((other) => other.id !== ingredient.id)
    .flatMap((other) => ingredientNames(other).map((name) => name.join(' ')));
  return ingredientNames(ingredient)
    .flatMap((words) =>
      words.flatMap((_, index) => {
        const alias = words.slice(index);
        const tail = ` ${alias.join(' ')}`;
        return index === 0 ||
          !others.some((name) => name !== words.join(' ') && ` ${name}`.endsWith(tail))
          ? [alias]
          : [];
      }),
    )
    .sort((a, b) => b.length - a.length);
}

function findMention(
  tokens: readonly WordToken[],
  aliases: readonly string[][],
  isFree: (start: number, end: number) => boolean = () => true,
): { start: number; end: number } | null {
  for (let i = 0; i < tokens.length; i++) {
    for (const alias of aliases) {
      if (!alias.length || i + alias.length > tokens.length) continue;
      if (!alias.every((word, offset) => tokens[i + offset].word === word)) continue;
      const span = { start: tokens[i].start, end: tokens[i + alias.length - 1].end };
      if (isFree(span.start, span.end)) return span;
    }
  }
  return null;
}

function ingredientMentions(
  text: string,
  ingredientIds: readonly string[],
  amounts: readonly RecipeStepInlineAmountDto[],
  ingredients: readonly InstructionIngredient[],
): { start: number; end: number; ingredient: InstructionIngredient }[] {
  if (!ingredientIds.length) return [];
  const tokens = tokenizeWords(text);
  const taken = amounts.map((a) => ({ start: a.start, end: amountHighlightEnd(a) }));
  const tagged = new Set(amounts.filter((a) => a.kind === 'Ingredient').map((a) => a.ingredientId));
  const mentions: { start: number; end: number; ingredient: InstructionIngredient }[] = [];
  for (const id of new Set(ingredientIds)) {
    const ingredient = tagged.has(id) ? undefined : ingredients.find((i) => i.id === id);
    if (!ingredient) continue;
    const span = findMention(tokens, ingredientAliases(ingredient, ingredients), (start, end) =>
      taken.every((t) => end <= t.start || start >= t.end),
    );
    if (span) {
      taken.push(span);
      mentions.push({ ...span, ingredient });
    }
  }
  return mentions.sort((a, b) => a.start - b.start);
}

function ingredientWords(text: string): string[] {
  return (
    text
      .toLowerCase()
      .normalize('NFD')
      .replace(/[\u0300-\u036f]/g, '')
      .match(/\p{L}+/gu) ?? []
  ).map((word) =>
    word === 'chilies'
      ? 'chile'
      : word.endsWith('oes')
        ? word.slice(0, -2)
        : word.endsWith('ies')
          ? `${word.slice(0, -3)}y`
          : word.endsWith('s') && !word.endsWith('ss')
            ? word.slice(0, -1)
            : word,
  );
}

function ingredientNames(
  ingredient: InstructionIngredient & { foodItemName?: string | null },
): string[][] {
  return [ingredient.displayName, ingredient.foodItemName ?? ''].filter(Boolean).map((name) => {
    const words = ingredientWords(name);
    // These are often omitted in instructions: "garlic" for "garlic cloves", "serrano" for "serrano chile".
    return words.length > 1 && ['clove', 'chile'].includes(words[words.length - 1])
      ? words.slice(0, -1)
      : words;
  });
}

export function stepIngredientCallouts(
  step: InstructionStep & { ingredientIds: readonly string[] } & Pick<
      RecipeStepDto,
      'ingredientAmounts'
    >,
  ingredients: readonly InstructionIngredient[],
  factor: number,
  system: UnitSystem,
): { id: string; label: string }[] {
  const amounts = validInlineSpans(step.instruction ?? '', step.inlineAmounts ?? []).filter(
    (a) => a.kind === 'Ingredient' && a.ingredientId,
  );
  const ids = new Set([...step.ingredientIds, ...amounts.map((a) => a.ingredientId)]);
  const tokens = tokenizeWords(step.instruction ?? '');
  const collective = /\b(?:remaining|all|rest of the|other) ingredients\b/i.test(step.instruction);

  return ingredients.flatMap((ingredient) => {
    if (!ids.has(ingredient.id)) return [];
    const tagged = amounts.filter((a) => a.ingredientId === ingredient.id);
    const mentioned = findMention(tokens, ingredientAliases(ingredient, ingredients)) !== null;
    if (!tagged.length && !mentioned && !collective) return [];
    const first = tagged[0]?.measurement;
    const portions = tagged.flatMap((a) => {
      const conversion = first ? measurementUnitFactor(a.measurement, first) : null;
      return conversion != null && a.measurement.quantity != null
        ? [
            {
              quantity: a.measurement.quantity * conversion,
              maxQuantity: (a.measurement.maxQuantity ?? a.measurement.quantity) * conversion,
            },
          ]
        : [];
    });
    const total =
      first && portions.length === tagged.length
        ? {
            ...first,
            quantity: portions.reduce((sum, p) => sum + p.quantity, 0),
            maxQuantity: tagged.some((a) => a.measurement.maxQuantity != null)
              ? portions.reduce((sum, p) => sum + p.maxQuantity, 0)
              : null,
          }
        : null;
    const assigned = step.ingredientAmounts?.find(
      (a) => a.ingredientId === ingredient.id,
    )?.measurement;
    const label =
      tagged.length && !total
        ? tagged
            .map((a) => formatStepIngredient(ingredient, a.measurement, factor, system))
            .join(' + ')
        : formatStepIngredient(ingredient, total ?? assigned, factor, system);
    return [{ id: ingredient.id, label }];
  });
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
