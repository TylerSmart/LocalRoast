import { ComponentFixture, TestBed } from '@angular/core/testing';
import { InstructionIngredient, InstructionStep } from '../../../core/utils/step-instruction';
import { StepInstructionComponent } from './step-instruction.component';

describe('StepInstructionComponent', () => {
  let fixture: ComponentFixture<StepInstructionComponent>;

  const ingredients: InstructionIngredient[] = [
    { id: 'butter', displayName: 'butter', quantity: 1, unit: 'Cup' },
  ];
  const step: InstructionStep = {
    instruction: 'Melt 1 cup butter and bake at 350°F.',
    inlineAmounts: [
      {
        start: 5,
        length: 5,
        kind: 'Ingredient',
        ingredientId: 'butter',
        measurement: { quantity: 1, unit: 'Cup' },
        alternativeMeasurement: null,
      },
      {
        start: 30,
        length: 5,
        kind: 'Fixed',
        ingredientId: null,
        measurement: { quantity: 350, unit: 'Custom', customUnitName: '°F' },
        alternativeMeasurement: null,
      },
    ],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [StepInstructionComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(StepInstructionComponent);
    fixture.componentRef.setInput('step', step);
    fixture.componentRef.setInput('ingredients', ingredients);
  });

  it('renders the verbatim text with a chip for ingredient amounts only', () => {
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.textContent).toBe('Melt 1 cup butter and bake at 350°F.');
    const chips = el.querySelectorAll('.inline-amount');
    expect(chips.length).toBe(1);
    expect(chips[0].textContent).toBe('1 cup');
    expect(chips[0].getAttribute('title')).toBe('butter');
    expect(chips[0].classList).not.toContain('is-scaled');
  });

  it('scales ingredient amounts but keeps fixed amounts verbatim', () => {
    fixture.componentRef.setInput('factor', 2);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const chip = el.querySelector('.inline-amount')!;
    expect(chip.textContent).not.toBe('1 cup');
    expect(chip.textContent).toContain('2');
    expect(chip.classList).toContain('is-scaled');
    expect(el.textContent).toContain('350°F');
  });

  it('renders one combined highlight when an ingredient span is present', () => {
    const stepWithIngSpan: InstructionStep = {
      instruction: 'Combine 2 cans chickpeas and water.',
      inlineAmounts: [
        {
          start: 8,
          length: 6,
          kind: 'Ingredient',
          ingredientId: 'chickpeas',
          measurement: { quantity: 2, unit: 'Can' },
          ingredientSpanStart: 15,
          ingredientSpanLength: 9,
        },
      ],
    };
    fixture.componentRef.setInput('step', stepWithIngSpan);
    fixture.componentRef.setInput('ingredients', [
      { id: 'chickpeas', displayName: 'chickpeas', quantity: 2, unit: 'Can' },
    ]);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const amountChip = el.querySelector('.inline-amount');
    expect(amountChip?.textContent).toBe('2 cans chickpeas');
    expect(amountChip?.getAttribute('title')).toBe('chickpeas');
    expect(el.querySelectorAll('.inline-amount').length).toBe(1);
    expect(el.textContent).toBe('Combine 2 cans chickpeas and water.');
    fixture.componentRef.setInput('factor', 2);
    fixture.detectChanges();
    expect(el.querySelector('.inline-amount')?.textContent).toBe('4 cans chickpeas');
  });

  it('highlights a linked ingredient named without an amount, unscaled', () => {
    fixture.componentRef.setInput('step', {
      instruction: 'Chop the serrano finely.',
      ingredientIds: ['serrano'],
    } satisfies InstructionStep);
    fixture.componentRef.setInput('ingredients', [
      { id: 'serrano', displayName: 'serrano chile', quantity: 1, unit: 'Whole' },
    ]);
    fixture.componentRef.setInput('factor', 2);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const mention = el.querySelector('.inline-mention');
    expect(mention?.textContent).toBe('serrano');
    expect(mention?.getAttribute('title')).toBe('serrano chile');
    expect(el.querySelectorAll('.inline-amount').length).toBe(0);
    expect(el.textContent).toBe('Chop the serrano finely.');
  });
});
