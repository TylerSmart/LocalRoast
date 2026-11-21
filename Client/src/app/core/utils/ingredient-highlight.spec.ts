import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createIngredientHighlightController } from './ingredient-highlight';

describe('createIngredientHighlightController', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function createHostElement(ids: string[]): HTMLElement {
    const host = document.createElement('div');
    for (const id of ids) {
      const li = document.createElement('li');
      li.setAttribute('data-ingredient-id', id);
      li.scrollIntoView = vi.fn();
      host.appendChild(li);
    }
    return host;
  }

  it('sets and clears highlight on hover enter and hover leave', () => {
    const controller = createIngredientHighlightController();

    expect(controller.highlightedIngredientId()).toBeNull();
    controller.highlight('ing-1');
    expect(controller.highlightedIngredientId()).toBe('ing-1');

    controller.highlight(null);
    expect(controller.highlightedIngredientId()).toBeNull();
  });

  it('preserves active click-scroll pulse against mouseleave until the 1800ms timer expires', () => {
    const controller = createIngredientHighlightController(() => 80);
    const host = createHostElement(['ing-1']);

    controller.scrollTo(host, 'ing-1');
    expect(controller.highlightedIngredientId()).toBe('ing-1');

    // Simulates mouseleave fired immediately when mobile drawer backdrop covers the clicked chip.
    controller.highlight(null);
    expect(controller.highlightedIngredientId()).toBe('ing-1');

    vi.advanceTimersByTime(1799);
    expect(controller.highlightedIngredientId()).toBe('ing-1');

    vi.advanceTimersByTime(1);
    expect(controller.highlightedIngredientId()).toBeNull();
  });

  it('cancels an active pulse immediately when hovering another ingredient', () => {
    const controller = createIngredientHighlightController();
    const host = createHostElement(['ing-1', 'ing-2']);

    controller.scrollTo(host, 'ing-1');
    expect(controller.highlightedIngredientId()).toBe('ing-1');

    controller.highlight('ing-2');
    expect(controller.highlightedIngredientId()).toBe('ing-2');

    // Once hover switches away from the pulse, hover leave clears normally.
    controller.highlight(null);
    expect(controller.highlightedIngredientId()).toBeNull();

    vi.advanceTimersByTime(2000);
    expect(controller.highlightedIngredientId()).toBeNull();
  });
});
