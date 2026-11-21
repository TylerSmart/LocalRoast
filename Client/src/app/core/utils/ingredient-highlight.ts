import { signal } from '@angular/core';

/** Shared hover/click ingredient highlight & scroll-to-row state for recipe views. */
export interface IngredientHighlightController {
  readonly highlightedIngredientId: ReturnType<typeof signal<string | null>>;
  highlight: (id: string | null) => void;
  scrollTo: (hostEl: HTMLElement, id: string) => void;
  clear: () => void;
}

export function createIngredientHighlightController(
  beforeScroll?: (hostEl: HTMLElement, id: string) => number,
): IngredientHighlightController {
  const highlightedIngredientId = signal<string | null>(null);
  let pulseTimer: ReturnType<typeof setTimeout> | null = null;
  let scrollTimer: ReturnType<typeof setTimeout> | null = null;

  const cancelTimers = (): void => {
    if (pulseTimer !== null) {
      clearTimeout(pulseTimer);
      pulseTimer = null;
    }
    if (scrollTimer !== null) {
      clearTimeout(scrollTimer);
      scrollTimer = null;
    }
  };

  const scrollIntoView = (hostEl: HTMLElement, id: string): void => {
    const escapedId =
      typeof CSS !== 'undefined' && typeof CSS.escape === 'function' ? CSS.escape(id) : id;
    const candidates = Array.from(
      hostEl.querySelectorAll<HTMLElement>(`li[data-ingredient-id="${escapedId}"]`),
    );
    if (candidates.length === 0) return;
    const target = candidates.find((el) => el.offsetParent !== null) ?? candidates[0];
    target.scrollIntoView?.({ behavior: 'smooth', block: 'center' });
  };

  return {
    highlightedIngredientId,
    highlight(id: string | null): void {
      if (pulseTimer !== null) {
        clearTimeout(pulseTimer);
        pulseTimer = null;
      }
      highlightedIngredientId.set(id);
    },
    scrollTo(hostEl: HTMLElement, id: string): void {
      cancelTimers();
      highlightedIngredientId.set(id);
      const delayMs = beforeScroll ? beforeScroll(hostEl, id) : 0;
      if (delayMs > 0) {
        scrollTimer = setTimeout(() => scrollIntoView(hostEl, id), delayMs);
      } else {
        scrollIntoView(hostEl, id);
      }
      pulseTimer = setTimeout(() => {
        if (highlightedIngredientId() === id) {
          highlightedIngredientId.set(null);
        }
        pulseTimer = null;
      }, 1800);
    },
    clear: cancelTimers,
  };
}
