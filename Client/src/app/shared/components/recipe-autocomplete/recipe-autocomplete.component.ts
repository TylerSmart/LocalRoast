import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  MatAutocompleteModule,
  MatAutocompleteSelectedEvent,
  MatAutocompleteTrigger,
} from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import {
  MatFormFieldAppearance,
  MatFormFieldModule,
  SubscriptSizing,
} from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Subject, of, timer } from 'rxjs';
import { catchError, map, switchMap, tap } from 'rxjs/operators';
import { RecipeAutocompleteItemDto } from '../../../core/models/recipe.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';

@Component({
  selector: 'app-recipe-autocomplete',
  templateUrl: './recipe-autocomplete.component.html',
  styleUrl: './recipe-autocomplete.component.scss',
  host: {
    '(click)': 'onHostClick($event)',
  },
  imports: [
    MatFormFieldModule,
    MatInputModule,
    MatAutocompleteModule,
    MatIconModule,
    MatButtonModule,
    MatProgressSpinnerModule,
  ],
})
export class RecipeAutocompleteComponent {
  private readonly recipeService = inject(RecipeService);
  private readonly destroyRef = inject(DestroyRef);

  // Inputs
  readonly linkedRecipeId = input<string | null>(null);
  readonly linkedRecipeTitle = input<string | null>(null);
  readonly textValue = input<string>('');
  readonly excludeRecipeId = input<string | null>(null);
  readonly label = input<string>('Linked Sub-Recipe');
  readonly placeholder = input<string>('Start typing a recipe title...');
  readonly hint = input<string>('');
  readonly appearance = input<MatFormFieldAppearance>('outline');
  readonly subscriptSizing = input<SubscriptSizing>('dynamic');
  readonly required = input<boolean>(true);
  readonly disabled = input<boolean>(false);
  readonly debounceMs = input<number>(150);
  readonly limit = input<number>(20);
  readonly ariaLabel = input<string>('');
  readonly emptySearchMessage = input<string>(
    'No matching cookbook recipes — will stay as an unlinked recipe reference',
  );
  readonly emptyCatalogMessage = input<string>('No other recipes in your cookbook yet');

  // Outputs
  readonly recipeSelected = output<RecipeAutocompleteItemDto>();
  readonly textChange = output<string>();
  readonly recipeCleared = output<void>();

  // Template refs
  readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('inputRef');
  readonly trigger = viewChild<MatAutocompleteTrigger>(MatAutocompleteTrigger);

  // State signals
  readonly inputText = signal<string>('');
  readonly suggestions = signal<RecipeAutocompleteItemDto[]>([]);
  readonly isSearching = signal<boolean>(false);
  readonly isLoading = signal<boolean>(false);
  readonly hasLoadedOnce = signal<boolean>(false);
  readonly isUserTyping = signal<boolean>(false);

  private readonly searchTrigger$ = new Subject<{ query: string; immediate: boolean }>();
  private suppressNextFocusOpen = false;
  private suppressFocusTimeoutId?: ReturnType<typeof setTimeout>;

  readonly isLinked = computed(() => !!this.linkedRecipeId());

  readonly hasText = computed(() => this.inputText().trim().length > 0);

  readonly displayedSuggestions = computed(() => {
    const items = this.suggestions();
    if (!this.isSearching()) {
      return items;
    }

    const queryTokens = this.inputText()
      .trim()
      .toLowerCase()
      .split(/\s+/)
      .filter((t) => t.length > 0);

    if (queryTokens.length === 0) {
      return items;
    }

    return items.filter((item) => {
      const lowerTitle = item.title.toLowerCase();
      return queryTokens.every((token) => lowerTitle.includes(token));
    });
  });

  readonly displayRecipeTitle = (val: RecipeAutocompleteItemDto | string | null): string => {
    if (typeof val === 'string') {
      return val;
    }
    return val?.title ?? '';
  };

  constructor() {
    effect(() => {
      const text = this.textValue();
      const linkedTitle = this.linkedRecipeTitle();
      if (this.isUserTyping()) {
        return;
      }

      const resolved = text || linkedTitle || '';
      if (this.inputText() !== resolved) {
        this.inputText.set(resolved);
      }
    });

    this.searchTrigger$
      .pipe(
        switchMap(({ query, immediate }) =>
          (immediate ? of(query) : timer(this.debounceMs()).pipe(map(() => query))).pipe(
            tap(() => this.isLoading.set(true)),
            switchMap((resolvedQuery) =>
              this.recipeService
                .autocompleteRecipes(resolvedQuery, this.excludeRecipeId(), this.limit())
                .pipe(catchError(() => of([] as RecipeAutocompleteItemDto[]))),
            ),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((results) => {
        this.suggestions.set(results);
        this.isLoading.set(false);
        this.isSearching.set(false);
        this.hasLoadedOnce.set(true);
      });

    this.destroyRef.onDestroy(() => {
      if (this.suppressFocusTimeoutId) {
        clearTimeout(this.suppressFocusTimeoutId);
      }
    });
  }

  onFocus(): void {
    if (this.disabled()) {
      return;
    }
    if (this.suppressNextFocusOpen) {
      this.suppressNextFocusOpen = false;
      return;
    }

    this.openAndSearch(this.inputText().trim(), true);
  }

  onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.isUserTyping.set(true);
    this.inputText.set(value);

    if (this.linkedRecipeId() && value.trim() !== (this.linkedRecipeTitle() || '').trim()) {
      this.recipeCleared.emit();
    }

    this.textChange.emit(value);
    this.isSearching.set(true);

    const trig = this.trigger();
    if (trig && !trig.panelOpen) {
      trig.openPanel();
    }

    this.searchTrigger$.next({ query: value.trim(), immediate: false });
  }

  onBlur(): void {
    this.isUserTyping.set(false);
  }

  onOptionSelected(event: MatAutocompleteSelectedEvent): void {
    const recipe = event.option.value as RecipeAutocompleteItemDto | string | null;
    if (!recipe || typeof recipe === 'string') {
      return;
    }

    this.isUserTyping.set(false);
    this.isSearching.set(false);
    this.inputText.set(recipe.title);
    this.recipeSelected.emit(recipe);

    this.suppressNextFocusOpen = true;
    this.trigger()?.closePanel();
    if (this.suppressFocusTimeoutId) {
      clearTimeout(this.suppressFocusTimeoutId);
    }
    this.suppressFocusTimeoutId = setTimeout(() => {
      this.suppressNextFocusOpen = false;
    }, 100);
  }

  onHostClick(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (
      target.closest(
        '.action-btn, mat-option, .mat-mdc-autocomplete-panel, .cdk-overlay-pane, .cdk-overlay-popover',
      )
    ) {
      return;
    }
    if (this.disabled()) {
      return;
    }

    this.inputRef()?.nativeElement.focus();
    const trig = this.trigger();
    if (trig && !trig.panelOpen) {
      this.openAndSearch(this.inputText().trim(), true);
    }
  }

  togglePanel(event: MouseEvent): void {
    event.stopPropagation();
    if (this.disabled()) {
      return;
    }

    const trig = this.trigger();
    if (!trig) {
      return;
    }

    if (trig.panelOpen) {
      trig.closePanel();
    } else {
      this.inputRef()?.nativeElement.focus();
      this.openAndSearch(this.inputText().trim(), true);
    }
  }

  clearInput(event: MouseEvent): void {
    event.stopPropagation();
    if (this.disabled()) {
      return;
    }

    this.isUserTyping.set(false);
    this.inputText.set('');
    this.recipeCleared.emit();
    this.textChange.emit('');
    this.inputRef()?.nativeElement.focus();
    this.openAndSearch('', true);
  }

  formatRecipeMeta(recipe: RecipeAutocompleteItemDto): string {
    const servingsLabel =
      recipe.baseServings === 1 ? '1 serving' : `${recipe.baseServings} servings`;
    if (recipe.yieldDisplay?.trim()) {
      const yieldText = recipe.yieldDisplay.trim();
      if (yieldText.toLowerCase().includes('serving')) {
        return yieldText;
      }
      return `${yieldText} · ${servingsLabel}`;
    }
    return servingsLabel;
  }

  private openAndSearch(query: string, immediate: boolean): void {
    const trig = this.trigger();
    if (trig && !trig.panelOpen) {
      trig.openPanel();
    }
    this.isSearching.set(true);
    this.searchTrigger$.next({ query, immediate });
  }
}
