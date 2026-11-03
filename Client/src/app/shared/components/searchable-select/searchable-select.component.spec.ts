import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { of } from 'rxjs';
import { SearchableSelectComponent, SearchableSelectOption } from './searchable-select.component';

describe('SearchableSelectComponent', () => {
  let component: SearchableSelectComponent;
  let fixture: ComponentFixture<SearchableSelectComponent>;

  const mockOptions: SearchableSelectOption[] = [
    { value: 'apple', label: 'Apple', sublabel: 'Fruit', group: 'Produce' },
    { value: 'banana', label: 'Banana', sublabel: 'Fruit', group: 'Produce' },
    { value: 'carrot', label: 'Carrot', sublabel: 'Vegetable', group: 'Produce' },
    { value: 'chicken', label: 'Chicken Breast', sublabel: 'Poultry', group: 'Meat' },
  ];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SearchableSelectComponent, ReactiveFormsModule],
    }).compileComponents();

    fixture = TestBed.createComponent(SearchableSelectComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    fixture.destroy();
  });

  it('creates the component', () => {
    fixture.detectChanges();
    expect(component).toBeTruthy();
  });

  it('displays initial options and filters locally by label', async () => {
    fixture.componentRef.setInput('options', mockOptions);
    fixture.componentRef.setInput('debounceMs', 10);
    fixture.detectChanges();

    expect(component.displayedOptions().length).toBe(4);

    // Simulate typing "car"
    const inputEl: HTMLInputElement = fixture.nativeElement.querySelector('input');
    inputEl.value = 'car';
    inputEl.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    await new Promise((r) => setTimeout(r, 50));
    fixture.detectChanges();

    expect(component.displayedOptions().length).toBe(1);
    expect(component.displayedOptions()[0].value).toBe('carrot');
  });

  it('binds value via writeValue and displays option label', () => {
    fixture.componentRef.setInput('options', mockOptions);
    component.writeValue('banana');
    fixture.detectChanges();

    expect(component.selectedValue()).toBe('banana');
    expect(component.displayInputText()).toBe('Banana');
  });

  it('selects option and emits selectionChange', () => {
    fixture.componentRef.setInput('options', mockOptions);
    fixture.detectChanges();

    const spy = vi.fn();
    component.selectionChange.subscribe(spy);

    component.onOptionSelected({
      option: { value: 'apple' },
    } as unknown as MatAutocompleteSelectedEvent);

    expect(component.selectedValue()).toBe('apple');
    expect(component.displayInputText()).toBe('Apple');
    expect(spy).toHaveBeenCalledWith('apple');
  });

  it('clears selection when clearSelection is called', () => {
    fixture.componentRef.setInput('options', mockOptions);
    component.writeValue('chicken');
    fixture.detectChanges();

    const spy = vi.fn();
    component.selectionChange.subscribe(spy);

    const event = new MouseEvent('click');
    component.clearSelection(event);

    expect(component.selectedValue()).toBeNull();
    expect(component.displayInputText()).toBe('');
    expect(spy).toHaveBeenCalledWith(null);
  });

  it('resets display text on blur if invalid text was typed without selecting', async () => {
    fixture.componentRef.setInput('options', mockOptions);
    component.writeValue('apple');
    fixture.detectChanges();

    // User types random text
    const inputEl: HTMLInputElement = fixture.nativeElement.querySelector('input');
    inputEl.value = 'random gibberish';
    inputEl.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(component.displayInputText()).toBe('random gibberish');

    // Trigger blur
    component.onBlur();
    await new Promise((r) => setTimeout(r, 200));
    fixture.detectChanges();

    // Reverts to the selected option's label
    expect(component.displayInputText()).toBe('Apple');
  });

  it('handles async searchFn with debouncing', async () => {
    const searchFn = vi.fn().mockReturnValue(
      of([
        { value: 'rec-1', label: 'Spaghetti Carbonara' },
        { value: 'rec-2', label: 'Chicken Parmesan' },
      ]),
    );

    fixture.componentRef.setInput('searchFn', searchFn);
    fixture.componentRef.setInput('debounceMs', 10);
    fixture.detectChanges();

    const inputEl: HTMLInputElement = fixture.nativeElement.querySelector('input');
    inputEl.value = 'pasta';
    inputEl.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    await new Promise((r) => setTimeout(r, 50));
    fixture.detectChanges();

    expect(searchFn).toHaveBeenCalledWith('pasta');
    expect(component.asyncResults().length).toBe(2);
    expect(component.displayedOptions()[0].label).toBe('Spaghetti Carbonara');
  });

  it('supports multi-select mode and toggles selection', () => {
    fixture.componentRef.setInput('options', mockOptions);
    fixture.componentRef.setInput('multiple', true);
    fixture.detectChanges();

    const spy = vi.fn();
    component.selectionChange.subscribe(spy);

    // Select 'apple'
    component.onOptionSelected({
      option: { value: 'apple' },
    } as unknown as MatAutocompleteSelectedEvent);
    expect(component.selectedValues()).toEqual(['apple']);
    expect(component.displayInputText()).toBe('Apple');

    // Select 'banana'
    component.onOptionSelected({
      option: { value: 'banana' },
    } as unknown as MatAutocompleteSelectedEvent);
    expect(component.selectedValues()).toEqual(['apple', 'banana']);
    expect(component.displayInputText()).toBe('2 items selected');

    // Deselect 'apple'
    component.onOptionSelected({
      option: { value: 'apple' },
    } as unknown as MatAutocompleteSelectedEvent);
    expect(component.selectedValues()).toEqual(['banana']);
    expect(component.displayInputText()).toBe('Banana');
  });
});
