import { ComponentFixture, TestBed } from '@angular/core/testing';
import { InstructionIngredient, InstructionStep } from '../../../core/utils/step-instruction';
import { StepInstructionComponent } from './step-instruction.component';

describe('StepInstructionComponent', () => {
  let fixture: ComponentFixture<StepInstructionComponent>;

  const ingredients: InstructionIngredient[] = [
    { id: 'butter', displayName: 'butter', quantity: 1, unit: 'Cup' },
  ];
  const step: InstructionStep = {
    instruction: 'Melt ||{"ingredient":"butter","amount":1,"unit":"cup"}|| and bake at 350°F.',
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [StepInstructionComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(StepInstructionComponent);
    fixture.componentRef.setInput('step', step);
    fixture.componentRef.setInput('ingredients', ingredients);
  });

  it('renders the verbatim text with a chip for ingredient amounts', () => {
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.textContent).toBe('Melt 1 cup butter and bake at 350°F.');
    const chips = el.querySelectorAll('.inline-amount');
    expect(chips.length).toBe(1);
    expect(chips[0].textContent).toBe('1 cup butter');
    expect(chips[0].getAttribute('title')).toBe('butter');
    expect(chips[0].classList).not.toContain('is-scaled');
  });

  it('scales ingredient amounts but keeps plain text unchanged', () => {
    fixture.componentRef.setInput('factor', 2);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const chip = el.querySelector('.inline-amount')!;
    expect(chip.textContent).toContain('2 cups butter');
    expect(chip.classList).toContain('is-scaled');
    expect(el.textContent).toContain('350°F');
  });

  it('renders fixed timer tokens', () => {
    const timerStep: InstructionStep = {
      instruction: 'Simmer for ||{"type":"timer","seconds":300,"label":"Simmer"}||.',
    };
    fixture.componentRef.setInput('step', timerStep);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    const fixedChip = el.querySelector('.fixed-amount');
    expect(fixedChip?.textContent).toBe('5m (Simmer)');
  });
});
