import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { of } from 'rxjs';
import { RecipeAutocompleteItemDto } from '../../../core/models/recipe.models';
import { RecipeService } from '../../../core/services/recipe/recipe.service';
import { RecipeAutocompleteComponent } from './recipe-autocomplete.component';

describe('RecipeAutocompleteComponent', () => {
  let component: RecipeAutocompleteComponent;
  let fixture: ComponentFixture<RecipeAutocompleteComponent>;

  const sampleRecipes: RecipeAutocompleteItemDto[] = [
    {
      id: 'r-dough',
      title: 'Foolproof All-Butter Pie Dough',
      slug: 'foolproof-all-butter-pie-dough',
      status: 'Published',
      baseServings: 8,
      yieldDisplay: '1 double-crust pie',
    },
    {
      id: 'r-hummus',
      title: 'Ultracreamy Hummus',
      slug: 'ultracreamy-hummus',
      status: 'DraftReview',
      baseServings: 6,
      yieldDisplay: null,
    },
  ];

  const mockRecipeService = {
    autocompleteRecipes: vi.fn().mockReturnValue(of(sampleRecipes)),
  };

  beforeEach(async () => {
    mockRecipeService.autocompleteRecipes.mockClear();
    mockRecipeService.autocompleteRecipes.mockReturnValue(of(sampleRecipes));

    await TestBed.configureTestingModule({
      imports: [RecipeAutocompleteComponent],
      providers: [{ provide: RecipeService, useValue: mockRecipeService }],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeAutocompleteComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('fetches initial suggestions immediately on focus', () => {
    fixture.componentRef.setInput('excludeRecipeId', 'parent-id');
    fixture.detectChanges();

    component.onFocus();

    expect(mockRecipeService.autocompleteRecipes).toHaveBeenCalledWith('', 'parent-id', 20);
    expect(component.suggestions()).toEqual(sampleRecipes);
  });

  it('filters suggestions via backend autocomplete when typing and emits text changes', async () => {
    vi.useFakeTimers();
    try {
      const textSpy = vi.fn();
      component.textChange.subscribe(textSpy);

      mockRecipeService.autocompleteRecipes.mockReturnValue(of([sampleRecipes[0]]));

      const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
      input.value = 'pie dough';
      component.onInput({ target: input } as unknown as Event);

      expect(textSpy).toHaveBeenCalledWith('pie dough');
      expect(mockRecipeService.autocompleteRecipes).not.toHaveBeenCalled();

      await vi.advanceTimersByTimeAsync(150);

      expect(mockRecipeService.autocompleteRecipes).toHaveBeenCalledWith('pie dough', null, 20);
      expect(component.displayedSuggestions()).toEqual([sampleRecipes[0]]);
    } finally {
      vi.useRealTimers();
    }
  });

  it('emits recipeSelected and populates input text when an option is picked', () => {
    const selectedSpy = vi.fn();
    component.recipeSelected.subscribe(selectedSpy);

    component.onOptionSelected({
      option: { value: sampleRecipes[0] },
    } as unknown as MatAutocompleteSelectedEvent);

    expect(selectedSpy).toHaveBeenCalledWith(sampleRecipes[0]);
    expect(component.inputText()).toBe('Foolproof All-Butter Pie Dough');
  });

  it('clears linked state when editing text away from a linked recipe title without wiping text on blur', () => {
    const clearedSpy = vi.fn();
    const textSpy = vi.fn();
    component.recipeCleared.subscribe(clearedSpy);
    component.textChange.subscribe(textSpy);

    fixture.componentRef.setInput('linkedRecipeId', 'r-dough');
    fixture.componentRef.setInput('linkedRecipeTitle', 'Foolproof All-Butter Pie Dough');
    fixture.componentRef.setInput('textValue', 'Foolproof All-Butter Pie Dough');
    fixture.detectChanges();

    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    input.value = 'Homemade Graham Cracker Crust';
    component.onInput({ target: input } as unknown as Event);
    component.onBlur();

    expect(clearedSpy).toHaveBeenCalledTimes(1);
    expect(textSpy).toHaveBeenCalledWith('Homemade Graham Cracker Crust');
    expect(component.inputText()).toBe('Homemade Graham Cracker Crust');
  });

  it('clears selection and re-queries on clear button click', () => {
    const clearedSpy = vi.fn();
    const textSpy = vi.fn();
    component.recipeCleared.subscribe(clearedSpy);
    component.textChange.subscribe(textSpy);

    fixture.componentRef.setInput('textValue', 'Ultracreamy Hummus');
    fixture.detectChanges();

    component.clearInput(new MouseEvent('click'));

    expect(clearedSpy).toHaveBeenCalledTimes(1);
    expect(textSpy).toHaveBeenCalledWith('');
    expect(component.inputText()).toBe('');
    expect(mockRecipeService.autocompleteRecipes).toHaveBeenCalledWith('', null, 20);
  });

  it('formats recipe yield and servings cleanly', () => {
    expect(component.formatRecipeMeta(sampleRecipes[0])).toBe('1 double-crust pie · 8 servings');
    expect(component.formatRecipeMeta(sampleRecipes[1])).toBe('6 servings');
  });
});
