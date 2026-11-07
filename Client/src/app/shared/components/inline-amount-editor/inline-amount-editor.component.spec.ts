import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { RecipeStepInlineAmountInput } from '../../../core/models/recipe.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { InlineAmountEditorComponent } from './inline-amount-editor.component';

describe('InlineAmountEditorComponent', () => {
  let fixture: ComponentFixture<InlineAmountEditorComponent>;
  let component: InlineAmountEditorComponent;
  let emitted: RecipeStepInlineAmountInput[][];
  const recipeService = { detectStepAmounts: vi.fn() };
  const snackBar = { open: vi.fn() };

  const milk: RecipeStepInlineAmountInput = {
    start: 4,
    length: 13,
    kind: 'Ingredient',
    ingredientIndex: 1,
    measurement: { quantity: 3, unit: 'Tbsp' },
  };

  beforeEach(async () => {
    recipeService.detectStepAmounts.mockReset();
    snackBar.open.mockReset();
    await TestBed.configureTestingModule({
      imports: [InlineAmountEditorComponent],
      providers: [
        { provide: RecipeService, useValue: recipeService },
        { provide: MatSnackBar, useValue: snackBar },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(InlineAmountEditorComponent);
    component = fixture.componentInstance;
    emitted = [];
    component.amountsChange.subscribe((amounts) => emitted.push(amounts));
    fixture.componentRef.setInput('ingredientOptions', [
      { index: 0, label: '1. flour' },
      { index: 1, label: '2. whole milk' },
    ]);
  });

  const setState = (instruction: string, amounts: RecipeStepInlineAmountInput[]) => {
    fixture.componentRef.setInput('instruction', instruction);
    fixture.componentRef.setInput('amounts', amounts);
    fixture.detectChanges();
  };

  it('highlights tagged spans and lists them as rows', () => {
    setState('Add 3 tablespoons whole milk.', [milk]);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.tagged-span')?.textContent).toBe('3 tablespoons');
    expect(el.querySelectorAll('[data-testid="inline-amount-row"]').length).toBe(1);
    expect(el.querySelector('[data-testid="inline-amount-ingredient"]')).not.toBeNull();
  });

  it('adds detected, non-overlapping amounts with a unit as Fixed spans', () => {
    setState('Add 3 tablespoons whole milk and 2 eggs, bake 20 minutes.', [milk]);
    recipeService.detectStepAmounts.mockReturnValue(
      of([
        {
          start: 4,
          length: 13,
          text: '3 tablespoons',
          measurement: { quantity: 3, unit: 'Tbsp' },
          alternativeMeasurement: null,
        },
        {
          start: 33,
          length: 1,
          text: '2',
          measurement: { quantity: 2, unit: null },
          alternativeMeasurement: null,
        },
        {
          start: 46,
          length: 10,
          text: '20 minutes',
          measurement: { quantity: 20, unit: 'Custom', customUnitName: 'minutes' },
          alternativeMeasurement: null,
        },
      ]),
    );
    (
      fixture.nativeElement.querySelector('[data-testid="detect-amounts"]') as HTMLButtonElement
    ).click();
    expect(recipeService.detectStepAmounts).toHaveBeenCalledWith(
      'Add 3 tablespoons whole milk and 2 eggs, bake 20 minutes.',
    );
    expect(emitted.at(-1)).toEqual([
      milk,
      {
        start: 46,
        length: 10,
        kind: 'Fixed',
        ingredientIndex: null,
        measurement: { quantity: 20, unit: 'Custom', customUnitName: 'minutes' },
        alternativeMeasurement: null,
      },
    ]);
  });

  it('relocates spans when the text changes and reports dropped spans', () => {
    setState('Add 3 tablespoons whole milk.', [milk]);
    fixture.componentRef.setInput('instruction', 'Then add 3 tablespoons whole milk.');
    fixture.detectChanges();
    expect(emitted.at(-1)).toEqual([{ ...milk, start: 9 }]);
    expect(snackBar.open).not.toHaveBeenCalled();

    fixture.componentRef.setInput('amounts', emitted.at(-1));
    fixture.detectChanges();
    fixture.componentRef.setInput('instruction', 'Then add some whole milk.');
    fixture.detectChanges();
    expect(emitted.at(-1)).toEqual([]);
    expect(snackBar.open).toHaveBeenCalledWith(
      '1 tagged amount no longer matches the step text and was removed.',
      'Close',
      expect.anything(),
    );
  });

  it('does not relocate when text and amounts are replaced together', () => {
    setState('Add 3 tablespoons whole milk.', [milk]);
    setState('Stir in 3 tablespoons whole milk.', [{ ...milk, start: 8 }]);
    expect(emitted).toEqual([]);
  });

  it('changes kinds, guesses the ingredient and removes rows', () => {
    const fixed: RecipeStepInlineAmountInput = { ...milk, kind: 'Fixed', ingredientIndex: null };
    setState('Add 3 tablespoons whole milk.', [fixed]);
    const editor = component as unknown as {
      setKind(a: RecipeStepInlineAmountInput, k: string): void;
      remove(a: RecipeStepInlineAmountInput): void;
    };
    editor.setKind(fixed, 'Ingredient');
    expect(emitted.at(-1)).toEqual([{ ...milk }]);
    editor.setKind(fixed, 'Mixture');
    expect(emitted.at(-1)).toEqual([{ ...fixed, kind: 'Mixture' }]);
    editor.remove(fixed);
    expect(emitted.at(-1)).toEqual([]);
  });
});
