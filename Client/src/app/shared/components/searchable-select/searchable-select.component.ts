import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  computed,
  effect,
  forwardRef,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import {
  ControlValueAccessor,
  FormsModule,
  NG_VALUE_ACCESSOR,
  ReactiveFormsModule,
} from '@angular/forms';
import {
  MatAutocompleteModule,
  MatAutocompleteSelectedEvent,
  MatAutocompleteTrigger,
} from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  MatFormFieldAppearance,
  MatFormFieldModule,
  SubscriptSizing,
} from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Observable, Subject, Subscription } from 'rxjs';
import { debounceTime, distinctUntilChanged } from 'rxjs/operators';

export interface SearchableSelectOption<T = string> {
  value: T;
  label: string;
  sublabel?: string;
  group?: string;
  disabled?: boolean;
}

export interface SearchableSelectGroup<T = string> {
  name: string;
  items: SearchableSelectOption<T>[];
}

@Component({
  selector: 'app-searchable-select',
  templateUrl: './searchable-select.component.html',
  styleUrl: './searchable-select.component.scss',
  host: {
    '(click)': 'onHostClick($event)',
  },
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => SearchableSelectComponent),
      multi: true,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatAutocompleteModule,
    MatIconModule,
    MatButtonModule,
    MatProgressSpinnerModule,
    MatCheckboxModule,
  ],
})
export class SearchableSelectComponent implements OnInit, OnDestroy, ControlValueAccessor {
  // Inputs
  readonly label = input<string>('');
  readonly placeholder = input<string>('Search or select...');
  readonly options = input<SearchableSelectOption<unknown>[]>([]);
  readonly initialOptions = input<SearchableSelectOption<unknown>[]>([]);
  readonly selectedOption = input<SearchableSelectOption<unknown> | null>(null);
  readonly searchFn = input<
    ((query: string) => Observable<SearchableSelectOption<unknown>[]>) | undefined
  >(undefined);
  readonly debounceMs = input<number>(250);
  readonly clearable = input<boolean>(true);
  readonly multiple = input<boolean>(false);
  readonly hint = input<string>('');
  readonly appearance = input<MatFormFieldAppearance>('outline');
  readonly subscriptSizing = input<SubscriptSizing>('dynamic');
  readonly emptyMessage = input<string>('No matching options found');
  readonly panelClass = input<string>('');
  readonly ariaLabel = input<string>('');
  readonly disabledInput = input<boolean>(false);

  // Outputs
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly selectionChange = output<any>();
  readonly searchChange = output<string>();

  // Template refs
  readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('inputRef');
  readonly trigger = viewChild<MatAutocompleteTrigger>(MatAutocompleteTrigger);

  // Internal state signals
  readonly selectedValue = signal<unknown>(null);
  readonly selectedValues = signal<unknown[]>([]);
  readonly displayInputText = signal<string>('');
  readonly isUserTyping = signal<boolean>(false);
  readonly searchTerm = signal<string>('');
  readonly isLoading = signal<boolean>(false);
  readonly asyncResults = signal<SearchableSelectOption<unknown>[]>([]);
  readonly isDisabled = signal<boolean>(false);

  private readonly searchSubject = new Subject<string>();
  private searchSubscription?: Subscription;
  private blurTimeoutId?: ReturnType<typeof setTimeout>;
  private reopenTimeoutId?: ReturnType<typeof setTimeout>;
  private isDestroyed = false;

  // CVA callbacks
  private onChange: (val: unknown) => void = () => undefined;
  private onTouched: () => void = () => undefined;

  readonly disabled = computed(() => this.disabledInput() || this.isDisabled());

  // Combined options cache for fast label lookup
  readonly allKnownOptions = computed(() => {
    const map = new Map<unknown, SearchableSelectOption<unknown>>();

    const sel = this.selectedOption();
    if (sel) {
      map.set(sel.value, sel);
    }
    for (const opt of this.initialOptions()) {
      map.set(opt.value, opt);
    }
    for (const opt of this.options()) {
      map.set(opt.value, opt);
    }
    for (const opt of this.asyncResults()) {
      map.set(opt.value, opt);
    }
    return Array.from(map.values());
  });

  // Filtered options currently displayed in the dropdown
  readonly displayedOptions = computed(() => {
    const fn = this.searchFn();
    const query = this.searchTerm().trim().toLowerCase();

    if (fn) {
      // Async mode
      if (!query) {
        return this.initialOptions();
      }
      return this.asyncResults();
    }

    // Local mode
    const source = this.options();
    if (!query) {
      return source;
    }

    return source.filter((opt) => {
      const matchLabel = opt.label.toLowerCase().includes(query);
      const matchSub = opt.sublabel?.toLowerCase().includes(query) ?? false;
      const matchGroup = opt.group?.toLowerCase().includes(query) ?? false;
      return matchLabel || matchSub || matchGroup;
    });
  });

  readonly hasGroups = computed(() => {
    return this.displayedOptions().some((o) => !!o.group);
  });

  readonly groupedOptions = computed(() => {
    const groups: SearchableSelectGroup<unknown>[] = [];
    const map = new Map<string, SearchableSelectOption<unknown>[]>();

    for (const opt of this.displayedOptions()) {
      const groupName = opt.group || 'General';
      if (!map.has(groupName)) {
        const list: SearchableSelectOption<unknown>[] = [];
        map.set(groupName, list);
        groups.push({ name: groupName, items: list });
      }
      map.get(groupName)!.push(opt);
    }
    return groups;
  });

  readonly flatOptions = computed(() => {
    return this.displayedOptions();
  });

  readonly allGroupItemsCount = computed(() => {
    return this.groupedOptions().reduce((sum, g) => sum + g.items.length, 0);
  });

  constructor() {
    // Keep display input text synchronized when value or options change and user isn't actively typing
    effect(() => {
      const isMulti = this.multiple();
      const typing = this.isUserTyping();
      const known = this.allKnownOptions();

      if (typing) return;

      if (!isMulti) {
        const val = this.selectedValue();
        if (val !== null && val !== undefined && val !== '') {
          const match = known.find((o) => o.value === val);
          if (match) {
            this.displayInputText.set(match.label);
          }
        } else {
          this.displayInputText.set('');
        }
      } else {
        const vals = this.selectedValues();
        this.updateMultiInputDisplay(vals);
      }
    });
  }

  ngOnInit(): void {
    this.searchSubscription = this.searchSubject
      .pipe(debounceTime(this.debounceMs()), distinctUntilChanged())
      .subscribe((term) => {
        this.searchChange.emit(term);
        const fn = this.searchFn();
        if (fn) {
          const query = term.trim();
          if (!query) {
            this.isLoading.set(false);
            this.asyncResults.set([]);
            return;
          }
          this.isLoading.set(true);
          fn(query).subscribe({
            next: (results) => {
              if (this.isDestroyed) return;
              this.asyncResults.set(results);
              this.isLoading.set(false);
            },
            error: () => {
              if (this.isDestroyed) return;
              this.asyncResults.set([]);
              this.isLoading.set(false);
            },
          });
        }
      });
  }

  ngOnDestroy(): void {
    this.isDestroyed = true;
    if (this.blurTimeoutId) {
      clearTimeout(this.blurTimeoutId);
    }
    if (this.reopenTimeoutId) {
      clearTimeout(this.reopenTimeoutId);
    }
    this.searchSubscription?.unsubscribe();
  }

  // ControlValueAccessor methods
  writeValue(value: unknown): void {
    if (this.multiple()) {
      const arr = Array.isArray(value)
        ? value
        : value !== null && value !== undefined
          ? [value]
          : [];
      this.selectedValues.set(arr);
      if (!this.isUserTyping()) {
        this.updateMultiInputDisplay(arr);
      }
    } else {
      this.selectedValue.set(value ?? null);
      if (!this.isUserTyping()) {
        if (value !== null && value !== undefined && value !== '') {
          const match = this.allKnownOptions().find((o) => o.value === value);
          this.displayInputText.set(match ? match.label : '');
        } else {
          this.displayInputText.set('');
        }
      }
    }
  }

  registerOnChange(fn: (val: unknown) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.isDisabled.set(isDisabled);
  }

  // Template event handlers
  onInputChange(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    this.displayInputText.set(text);
    this.isUserTyping.set(true);
    this.searchTerm.set(text);
    this.searchSubject.next(text);
  }

  onFocus(): void {
    const trig = this.trigger();
    if (trig && !trig.panelOpen) {
      trig.openPanel();
    }
  }

  onBlur(): void {
    if (this.blurTimeoutId) {
      clearTimeout(this.blurTimeoutId);
    }
    this.blurTimeoutId = setTimeout(() => {
      if (this.isDestroyed) return;
      this.isUserTyping.set(false);
      this.searchTerm.set('');
      this.onTouched();

      if (!this.multiple()) {
        const val = this.selectedValue();
        if (val !== null && val !== undefined && val !== '') {
          const match = this.allKnownOptions().find((o) => o.value === val);
          this.displayInputText.set(match ? match.label : '');
        } else {
          this.displayInputText.set('');
        }
      } else {
        this.updateMultiInputDisplay(this.selectedValues());
      }
    }, 150);
  }

  onOptionSelected(event: MatAutocompleteSelectedEvent): void {
    const selectedValue = event.option.value;

    if (!this.multiple()) {
      this.isUserTyping.set(false);
      this.selectedValue.set(selectedValue);
      const match = this.allKnownOptions().find((o) => o.value === selectedValue);
      this.displayInputText.set(match ? match.label : '');
      this.searchTerm.set('');
      this.onChange(selectedValue);
      this.selectionChange.emit(selectedValue);
      this.trigger()?.closePanel();
    } else {
      const current = [...this.selectedValues()];
      const idx = current.indexOf(selectedValue);
      if (idx >= 0) {
        current.splice(idx, 1);
      } else {
        current.push(selectedValue);
      }
      this.selectedValues.set(current);
      this.onChange(current);
      this.selectionChange.emit(current);
      this.updateMultiInputDisplay(current);

      // Keep panel open for multi-select
      if (this.reopenTimeoutId) {
        clearTimeout(this.reopenTimeoutId);
      }
      this.reopenTimeoutId = setTimeout(() => {
        if (this.isDestroyed) return;
        const trig = this.trigger();
        if (trig) {
          trig.openPanel();
        }
      });
    }
  }

  onHostClick(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (target.closest('.action-btn')) return;
    if (this.disabled()) return;
    this.inputRef()?.nativeElement.focus();
    const trig = this.trigger();
    if (trig && !trig.panelOpen) {
      trig.openPanel();
    }
  }

  togglePanel(event: MouseEvent): void {
    event.stopPropagation();
    if (this.disabled()) return;

    const trig = this.trigger();
    if (!trig) return;

    if (trig.panelOpen) {
      trig.closePanel();
    } else {
      this.inputRef()?.nativeElement.focus();
      trig.openPanel();
    }
  }

  clearSelection(event: MouseEvent): void {
    event.stopPropagation();
    if (this.disabled()) return;

    this.isUserTyping.set(false);
    this.searchTerm.set('');
    this.displayInputText.set('');

    if (!this.multiple()) {
      this.selectedValue.set(null);
      this.onChange(null);
      this.selectionChange.emit(null);
    } else {
      this.selectedValues.set([]);
      this.onChange([]);
      this.selectionChange.emit([]);
    }

    this.onTouched();
  }

  isSelected(value: unknown): boolean {
    if (this.multiple()) {
      return this.selectedValues().includes(value);
    }
    return this.selectedValue() === value;
  }

  hasValue(): boolean {
    if (this.multiple()) {
      return this.selectedValues().length > 0;
    }
    const val = this.selectedValue();
    return val !== null && val !== undefined && val !== '';
  }

  private updateMultiInputDisplay(values: unknown[]): void {
    if (!values || values.length === 0) {
      this.displayInputText.set('');
      return;
    }

    const known = this.allKnownOptions();
    if (values.length === 1) {
      const match = known.find((o) => o.value === values[0]);
      this.displayInputText.set(match ? match.label : '1 item selected');
    } else {
      this.displayInputText.set(`${values.length} items selected`);
    }
  }
}
