import {
  Component,
  ElementRef,
  afterRenderEffect,
  effect,
  input,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * Displays a recipe description at full width on desktop and clamps long text
 * to 3 lines on mobile viewports with an accessible Show more / Show less toggle.
 */
@Component({
  selector: 'app-recipe-description',
  imports: [MatIconModule],
  templateUrl: './recipe-description.component.html',
  styleUrl: './recipe-description.component.scss',
})
export class RecipeDescriptionComponent {
  private static nextId = 0;
  private static readonly COLLAPSED_LINES = 3;
  private static readonly JSDOM_CHAR_FALLBACK_THRESHOLD = 160;

  readonly description = input.required<string>();

  protected readonly descriptionId = `recipe-description-${++RecipeDescriptionComponent.nextId}`;
  protected readonly isExpanded = linkedSignal({
    source: this.description,
    computation: () => false,
  });
  protected readonly isOverflowing = signal<boolean>(false);

  private readonly descriptionEl = viewChild<ElementRef<HTMLParagraphElement>>('descriptionEl');

  constructor() {
    afterRenderEffect(() => {
      this.description();
      this.isExpanded();
      this.updateOverflowState();
    });

    effect((onCleanup) => {
      const el = this.descriptionEl()?.nativeElement;
      if (!el || typeof ResizeObserver === 'undefined') {
        return;
      }

      const observer = new ResizeObserver(() => this.updateOverflowState());
      observer.observe(el);
      onCleanup(() => observer.disconnect());
    });
  }

  protected toggleExpanded(): void {
    this.isExpanded.update((expanded) => !expanded);
  }

  protected updateOverflowState(): void {
    const el = this.descriptionEl()?.nativeElement;
    if (!el) {
      this.isOverflowing.set(false);
      return;
    }

    if (el.clientHeight === 0 && el.scrollHeight === 0) {
      const text = this.description();
      const lineCount = text.split(/\r?\n/).length;
      this.isOverflowing.set(
        text.length > RecipeDescriptionComponent.JSDOM_CHAR_FALLBACK_THRESHOLD ||
          lineCount > RecipeDescriptionComponent.COLLAPSED_LINES,
      );
      return;
    }

    if (!this.isExpanded()) {
      this.isOverflowing.set(el.scrollHeight > el.clientHeight + 1);
      return;
    }

    if (
      typeof globalThis.matchMedia === 'function' &&
      !globalThis.matchMedia('(max-width: 768px)').matches
    ) {
      this.isOverflowing.set(false);
      return;
    }

    const computedLineHeight =
      typeof globalThis.getComputedStyle === 'function'
        ? Number.parseFloat(globalThis.getComputedStyle(el).lineHeight)
        : Number.NaN;
    if (Number.isFinite(computedLineHeight) && computedLineHeight > 0) {
      this.isOverflowing.set(
        el.scrollHeight > computedLineHeight * RecipeDescriptionComponent.COLLAPSED_LINES + 4,
      );
    }
  }
}
