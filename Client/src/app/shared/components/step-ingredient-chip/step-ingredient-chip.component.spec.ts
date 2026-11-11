import { ComponentFixture, TestBed } from '@angular/core/testing';
import { StepIngredientChipComponent } from './step-ingredient-chip.component';
import { StepIngredientToken } from '../../../core/models/step-token.models';

describe('StepIngredientChipComponent', () => {
  let component: StepIngredientChipComponent;
  let fixture: ComponentFixture<StepIngredientChipComponent>;

  const mockToken: StepIngredientToken = {
    ingredient: 'butter',
    amount: 4,
    unit: 'Tsp',
    displayAmount: 1,
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [StepIngredientChipComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(StepIngredientChipComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('token', mockToken);
    fixture.detectChanges();
  });

  it('renders display amount and ingredient label', () => {
    expect(component).toBeTruthy();
    const btn = fixture.nativeElement.querySelector('button');
    expect(btn.textContent).toContain('1 tsp butter');
  });

  it('shows total in tooltip when display amount is used', () => {
    expect(component['tooltipText']()).toContain('Total recipe uses: 4 tsp butter');
  });

  it('emits edit when editable and clicked', () => {
    fixture.componentRef.setInput('editable', true);
    fixture.detectChanges();

    const spy = vi.spyOn(component.edit, 'emit');
    const btn = fixture.nativeElement.querySelector('button');
    btn.click();

    expect(spy).toHaveBeenCalledWith(mockToken);
  });
});
