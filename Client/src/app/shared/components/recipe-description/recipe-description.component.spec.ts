import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RecipeDescriptionComponent } from './recipe-description.component';

describe('RecipeDescriptionComponent', () => {
  let fixture: ComponentFixture<RecipeDescriptionComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RecipeDescriptionComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeDescriptionComponent);
  });

  it('renders short description without a toggle button', () => {
    fixture.componentRef.setInput('description', 'Quick weeknight pasta.');
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const descEl = el.querySelector('.recipe-description');
    expect(descEl?.textContent).toBe('Quick weeknight pasta.');
    expect(el.querySelector('.description-toggle-btn')).toBeNull();
  });

  it('renders Show more toggle for long description and toggles expanded state', () => {
    const longDescription =
      'A rich slow-simmered ragu made with sweet aromatics, plum tomatoes, red wine, and fresh herbs that fills the kitchen with an incredible aroma while remaining approachable for a weekend dinner.';
    fixture.componentRef.setInput('description', longDescription);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const descEl = el.querySelector('.recipe-description') as HTMLParagraphElement;
    const toggleBtn = el.querySelector('.description-toggle-btn') as HTMLButtonElement;

    expect(descEl.classList.contains('is-expanded')).toBe(false);
    expect(toggleBtn).not.toBeNull();
    expect(toggleBtn.textContent).toContain('Show more');
    expect(toggleBtn.getAttribute('aria-expanded')).toBe('false');
    expect(toggleBtn.getAttribute('aria-controls')).toBe(descEl.id);

    toggleBtn.click();
    fixture.detectChanges();

    expect(descEl.classList.contains('is-expanded')).toBe(true);
    expect(toggleBtn.textContent).toContain('Show less');
    expect(toggleBtn.getAttribute('aria-expanded')).toBe('true');

    toggleBtn.click();
    fixture.detectChanges();

    expect(descEl.classList.contains('is-expanded')).toBe(false);
    expect(toggleBtn.textContent).toContain('Show more');
    expect(toggleBtn.getAttribute('aria-expanded')).toBe('false');
  });

  it('resets expanded state when description input changes', () => {
    const firstLong =
      'First long description with plenty of characters to exceed the fallback threshold so the toggle button appears reliably in unit tests without browser layout measurement.';
    const secondLong =
      'Second long description with plenty of characters to exceed the fallback threshold so the toggle button stays visible while verifying that expanded state resets to false.';

    fixture.componentRef.setInput('description', firstLong);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('.description-toggle-btn') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('.recipe-description')?.classList.contains('is-expanded')).toBe(true);

    fixture.componentRef.setInput('description', secondLong);
    fixture.detectChanges();

    expect(el.querySelector('.recipe-description')?.classList.contains('is-expanded')).toBe(false);
    expect(el.querySelector('.description-toggle-btn')?.getAttribute('aria-expanded')).toBe(
      'false',
    );
  });

  it('uses DOM overflow measurements when layout dimensions are available', () => {
    const longText = 'A'.repeat(250);
    fixture.componentRef.setInput('description', longText);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    const descEl = el.querySelector('.recipe-description') as HTMLParagraphElement;

    // Simulate desktop unclamped layout where clientHeight === scrollHeight > 0
    Object.defineProperty(descEl, 'clientHeight', { configurable: true, value: 120 });
    Object.defineProperty(descEl, 'scrollHeight', { configurable: true, value: 120 });
    (fixture.componentInstance as unknown as { updateOverflowState(): void }).updateOverflowState();
    fixture.detectChanges();

    expect(el.querySelector('.description-toggle-btn')).toBeNull();

    // Simulate mobile clamped layout where scrollHeight > clientHeight + 1
    Object.defineProperty(descEl, 'clientHeight', { configurable: true, value: 75 });
    Object.defineProperty(descEl, 'scrollHeight', { configurable: true, value: 180 });
    (fixture.componentInstance as unknown as { updateOverflowState(): void }).updateOverflowState();
    fixture.detectChanges();

    expect(el.querySelector('.description-toggle-btn')).not.toBeNull();
  });
});
