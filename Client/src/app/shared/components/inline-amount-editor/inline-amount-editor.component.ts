import {
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InlineStepAmountKind,
  RecipeMeasurement,
  RecipeStepInlineAmountInput,
} from '../../../core/models/recipe.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { relocateInlineAmounts, validInlineSpans } from '../../../core/utils/step-instruction';

export interface InlineAmountIngredientOption {
  index: number;
  label: string;
}

interface PreviewSegment {
  text: string;
  kind: InlineStepAmountKind | null;
  isIngredient?: boolean;
}

@Component({
  selector: 'app-inline-amount-editor',
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSelectModule,
  ],
  templateUrl: './inline-amount-editor.component.html',
  styleUrl: './inline-amount-editor.component.scss',
})
export class InlineAmountEditorComponent {
  private readonly recipeService = inject(RecipeService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly instruction = input<string | null | undefined>('');
  readonly amounts = input<readonly RecipeStepInlineAmountInput[] | null | undefined>([]);
  readonly ingredientOptions = input<readonly InlineAmountIngredientOption[]>([]);
  readonly amountsChange = output<RecipeStepInlineAmountInput[]>();

  protected readonly isDetecting = signal(false);
  protected readonly kinds: { value: InlineStepAmountKind; label: string }[] = [
    { value: 'Ingredient', label: 'Ingredient' },
    { value: 'Mixture', label: 'Mixture' },
    { value: 'Fixed', label: 'Other' },
  ];

  protected readonly text = computed(() => this.instruction() ?? '');
  protected readonly rows = computed(() =>
    [...(this.amounts() ?? [])]
      .sort((a, b) => a.start - b.start)
      .map((amount) => ({
        amount,
        text: this.text().slice(amount.start, amount.start + amount.length),
        measurement: this.formatMeasurement(amount.measurement),
      })),
  );
  protected readonly preview = computed<PreviewSegment[]>(() => {
    const text = this.text();
    const segments: PreviewSegment[] = [];
    let cursor = 0;
    for (const amount of validInlineSpans(text, this.amounts() ?? [])) {
      if (amount.start > cursor)
        segments.push({ text: text.slice(cursor, amount.start), kind: null });
      segments.push({
        text: text.slice(amount.start, amount.start + amount.length),
        kind: amount.kind,
      });
      cursor = amount.start + amount.length;
      if (
        amount.ingredientSpanStart != null &&
        amount.ingredientSpanLength != null &&
        amount.ingredientSpanStart >= cursor &&
        amount.ingredientSpanStart + amount.ingredientSpanLength <= text.length
      ) {
        if (amount.ingredientSpanStart > cursor) {
          segments.push({ text: text.slice(cursor, amount.ingredientSpanStart), kind: null });
        }
        segments.push({
          text: text.slice(
            amount.ingredientSpanStart,
            amount.ingredientSpanStart + amount.ingredientSpanLength,
          ),
          kind: amount.kind,
          isIngredient: true,
        });
        cursor = amount.ingredientSpanStart + amount.ingredientSpanLength;
      }
    }
    if (cursor < text.length) segments.push({ text: text.slice(cursor), kind: null });
    return segments;
  });

  private lastText: string | undefined;
  private lastAmounts: readonly RecipeStepInlineAmountInput[] | null | undefined;

  constructor() {
    effect(() => {
      const text = this.text();
      const amounts = this.amounts();
      untracked(() => this.relocateIfTextChanged(text, amounts));
    });
  }

  protected detect(): void {
    const sent = this.text();
    if (!sent.trim() || this.isDetecting()) return;
    this.isDetecting.set(true);
    this.recipeService
      .detectStepAmounts(sent)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detected) => {
          this.isDetecting.set(false);
          const candidates: RecipeStepInlineAmountInput[] = detected
            .filter((d) => d.length > 0 && d.measurement?.unit && (d.measurement.quantity ?? 0) > 0)
            .map((d) => ({
              start: d.start,
              length: d.length,
              kind: 'Fixed' as const,
              ingredientIndex: null,
              measurement: d.measurement,
              alternativeMeasurement: d.alternativeMeasurement ?? null,
            }));
          const current = this.text();
          const located =
            current === sent
              ? candidates
              : relocateInlineAmounts(sent, current, candidates).amounts;
          const existing = [...(this.amounts() ?? [])];
          const added: RecipeStepInlineAmountInput[] = [];
          for (const candidate of located) {
            const all = [...existing, ...added];
            if (all.some((a) => this.overlaps(a, candidate))) continue;
            added.push(candidate);
          }
          if (!added.length) {
            this.snackBar.open('No new amounts detected in this step.', 'Close', {
              duration: 3000,
            });
            return;
          }
          this.emit([...existing, ...added]);
        },
        error: () => {
          this.isDetecting.set(false);
          this.snackBar.open('Could not detect amounts.', 'Close', { duration: 4000 });
        },
      });
  }

  protected setKind(amount: RecipeStepInlineAmountInput, kind: InlineStepAmountKind): void {
    const ingredientIndex =
      kind === 'Ingredient' ? (amount.ingredientIndex ?? this.guessIngredient(amount)) : null;
    this.replace(amount, { ...amount, kind, ingredientIndex });
  }

  protected setIngredient(amount: RecipeStepInlineAmountInput, index: number): void {
    this.replace(amount, { ...amount, ingredientIndex: index });
  }

  protected remove(amount: RecipeStepInlineAmountInput): void {
    this.emit((this.amounts() ?? []).filter((a) => a !== amount));
  }

  private replace(oldAmount: RecipeStepInlineAmountInput, newAmount: RecipeStepInlineAmountInput) {
    this.emit((this.amounts() ?? []).map((a) => (a === oldAmount ? newAmount : a)));
  }

  private emit(amounts: RecipeStepInlineAmountInput[]): void {
    const sorted = [...amounts].sort((a, b) => a.start - b.start);
    this.lastAmounts = sorted;
    this.lastText = this.text();
    this.amountsChange.emit(sorted);
  }

  private relocateIfTextChanged(
    text: string,
    amounts: readonly RecipeStepInlineAmountInput[] | null | undefined,
  ): void {
    const previousText = this.lastText;
    const previousAmounts = this.lastAmounts;
    this.lastText = text;
    this.lastAmounts = amounts;
    if (previousText === undefined || previousText === text || previousAmounts !== amounts) return;
    if (!amounts?.length) return;
    const result = relocateInlineAmounts(previousText, text, amounts);
    const moved =
      result.amounts.length !== amounts.length ||
      result.amounts.some((a, i) => a.start !== amounts[i]?.start);
    if (moved) this.emit(result.amounts);
    if (result.dropped > 0) {
      const noun =
        result.dropped === 1 ? 'tagged amount no longer matches' : 'tagged amounts no longer match';
      this.snackBar.open(
        `${result.dropped} ${noun} the step text and ${result.dropped === 1 ? 'was' : 'were'} removed.`,
        'Close',
        {
          duration: 5000,
        },
      );
    }
  }

  private overlaps(a: RecipeStepInlineAmountInput, b: RecipeStepInlineAmountInput): boolean {
    return a.start < b.start + b.length && b.start < a.start + a.length;
  }

  private guessIngredient(amount: RecipeStepInlineAmountInput): number | null {
    const options = this.ingredientOptions();
    if (!options.length) return null;
    const after = this.text()
      .slice(amount.start + amount.length)
      .toLowerCase();
    let best: { index: number; position: number } | null = null;
    for (const option of options) {
      const name = option.label
        .replace(/^\d+\.\s*/, '')
        .trim()
        .toLowerCase();
      if (!name) continue;
      const position = after.indexOf(name);
      if (position >= 0 && (best === null || position < best.position)) {
        best = { index: option.index, position };
      }
    }
    return best?.index ?? options[0].index;
  }

  private formatMeasurement(measurement: RecipeMeasurement | null | undefined): string {
    if (!measurement || measurement.quantity == null) return '';
    const range = measurement.maxQuantity == null ? '' : `–${measurement.maxQuantity}`;
    const unit = measurement.unit === 'Custom' ? measurement.customUnitName : measurement.unit;
    return `${measurement.quantity}${range} ${unit ?? ''}`.trim();
  }
}
