import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RecipeDetailPreviewComponent } from './recipe-detail-preview.component';
import { RecipePreviewData } from '../../../core/models/recipe.models';

describe('RecipeDetailPreviewComponent', () => {
  let component: RecipeDetailPreviewComponent;
  let fixture: ComponentFixture<RecipeDetailPreviewComponent>;

  const mockRecipeData: RecipePreviewData = {
    title: 'Test Roasted Garlic Pasta',
    description: 'A comforting rich pasta dish with slow-roasted sweet garlic.',
    baseServings: 4,
    yieldDisplay: '4 servings',
    prepTimeMinutes: 15,
    cookTimeMinutes: 45,
    totalTimeMinutes: 60,
    difficulty: 'Easy',
    sourceName: "America's Test Kitchen",
    sourceBookTitle: 'The Complete Autumn Cookbook',
    sourceBookPage: 42,
    sourceUrl: null,
    coverImagePath: 'https://example.com/pasta.jpg',
    extractedModel: 'gemini-1.5-pro',
    collections: [{ id: 'col-1', name: 'Weeknight Dinners' }],
    tags: [{ id: 'tag-1', name: 'Pasta', category: 'Cuisine' }],
    ingredients: [
      {
        id: 'ing-0',
        index: 0,
        sectionName: 'For the Sauce',
        quantity: 2,
        maxQuantity: null,
        unit: 'Tbsp',
        customUnitName: null,
        displayName: 'Olive Oil',
        preparationNote: 'extra virgin',
        isOptional: false,
        isPantryStaple: true,
        matchedFoodItemName: 'Olive Oil',
      },
      {
        id: 'ing-1',
        index: 1,
        sectionName: 'For the Sauce',
        quantity: 6,
        maxQuantity: null,
        unit: 'Clove',
        customUnitName: null,
        displayName: 'Garlic',
        preparationNote: 'minced',
        isOptional: false,
        isPantryStaple: true,
        matchedFoodItemName: 'Garlic',
      },
      {
        id: 'ing-2',
        index: 2,
        sectionName: 'For the Pasta',
        quantity: 1,
        maxQuantity: null,
        unit: 'Lb',
        customUnitName: null,
        displayName: 'Fettuccine',
        preparationNote: null,
        isOptional: false,
        isPantryStaple: false,
        matchedFoodItemName: null,
      },
    ],
    steps: [
      {
        id: 'step-0',
        stepNumber: 1,
        sectionName: 'Pasta',
        instruction:
          'Bring a large pot of salted water to a rolling boil and cook fettuccine until al dente.',
        imagePath: null,
        ingredientIndices: [2],
        timers: [{ durationSeconds: 600, label: 'Boil Pasta' }],
      },
      {
        id: 'step-1',
        stepNumber: 2,
        sectionName: 'Sauce',
        instruction:
          'In a skillet over medium heat, warm olive oil and sauté minced garlic until fragrant.',
        imagePath: null,
        ingredientIndices: [0, 1],
        timers: [{ durationSeconds: 180, label: 'Sauté Garlic' }],
      },
    ],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RecipeDetailPreviewComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(RecipeDetailPreviewComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('recipe', mockRecipeData);
    fixture.componentRef.setInput('showPreviewBanner', true);
    fixture.componentRef.setInput('showPublishButton', true);
    fixture.detectChanges();
  });

  it('renders recipe title, difficulty badge, and description', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.recipe-title')?.textContent).toContain('Test Roasted Garlic Pasta');
    expect(el.querySelector('.diff-badge')?.textContent).toContain('Easy');
    expect(el.querySelector('.recipe-description')?.textContent).toContain(
      'A comforting rich pasta dish',
    );
  });

  it('renders collections and tags', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.hero-collection-badge')?.textContent).toContain('Weeknight Dinners');
    expect(el.querySelector('.hero-tag-badge')?.textContent).toContain('Pasta');
  });

  it('renders stats for times, yield, and cookbook source', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.recipe-stats-strip')?.textContent).toContain('15 min');
    expect(el.querySelector('.recipe-stats-strip')?.textContent).toContain('45 min');
    expect(el.querySelector('.recipe-stats-strip')?.textContent).toContain('1 hr');
    expect(el.querySelector('.recipe-stats-strip')?.textContent).toContain('4 servings');
    expect(el.querySelector('.source-stat')?.textContent).toContain("America's Test Kitchen");
    expect(el.querySelector('.source-stat')?.textContent).toContain('The Complete Autumn Cookbook');
    expect(el.querySelector('.source-stat')?.textContent).toContain('p. 42');
  });

  it('renders grouped ingredients and ingredient details', () => {
    const el = fixture.nativeElement as HTMLElement;
    const ingText = el.querySelector('.ingredients-column')?.textContent;
    expect(ingText).toContain('For the Sauce');
    expect(ingText).toContain('Olive Oil');
    expect(ingText).toContain('Garlic');
    expect(ingText).toContain('For the Pasta');
    expect(ingText).toContain('Fettuccine');
  });

  it('renders instructions with step numbers, ingredient callouts, and timers', () => {
    const el = fixture.nativeElement as HTMLElement;
    const stepsText = el.querySelector('.instructions-column')?.textContent;
    expect(stepsText).toContain('Bring a large pot of salted water');
    expect(stepsText).toContain('In a skillet over medium heat');
    expect(stepsText).toContain('10 mins');
    expect(stepsText).toContain('3 mins');
    expect(stepsText).toContain('Used in this step:');
    expect(stepsText).toContain('Fettuccine');
  });

  it('scales ingredient quantities when servings are adjusted', () => {
    component.adjustServings(4); // from 4 to 8 (2x factor)
    fixture.detectChanges();

    expect(component.targetServings()).toBe(8);
    expect(component.scaleFactor()).toBe(2);

    const map = component.scaledIngredientsMap();
    expect(map.get(0)?.scaled.formattedAmount).toBe('1/4'); // 2 tbsp * 2 = 4 tbsp -> 1/4 cup
    expect(map.get(0)?.scaled.unit).toBe('Cup');
  });

  it('scales a one-serving starter to half with the assigned step amount and authored weight equivalent', () => {
    const ingredient = {
      ...mockRecipeData.ingredients[0],
      quantity: 5,
      unit: 'Cup' as const,
      alternativeMeasurement: { quantity: 25, unit: 'Oz' as const },
      preferAlternativeMeasurement: true,
    };
    const step = {
      ...mockRecipeData.steps[0],
      instruction: `Mix ${ingredient.displayName}.`,
      ingredientIndices: [ingredient.index],
      ingredientAmounts: [
        { ingredientIndex: ingredient.index, measurement: { quantity: 1, unit: 'Cup' as const } },
      ],
    };
    fixture.componentRef.setInput('recipe', {
      ...mockRecipeData,
      baseServings: 1,
      ingredients: [ingredient],
      steps: [step],
    });
    component.targetSystem.set('Metric');
    component.setMultiplier(0.5);
    expect(component.scaleFactor()).toBe(0.5);
    expect(component.groupedSteps()[0].items[0].ingredientCallouts[0].label).toBe(
      `71 g ${ingredient.displayName}`,
    );
  });

  it('maps inline step tokens into scaled chips and a divided breakdown', () => {
    fixture.componentRef.setInput('recipe', {
      ...mockRecipeData,
      steps: [
        {
          ...mockRecipeData.steps[1],
          instruction:
            'Warm ||{"ingredient":"Olive Oil","amount":1,"unit":"tbsp"}||, then add ||{"ingredient":"Olive Oil","amount":1,"unit":"tbsp"}|| more.',
          ingredientIndices: [0, 1],
        },
      ],
    });
    component.setMultiplier(2);
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.step-instruction-text')?.textContent?.trim()).toBe(
      'Warm 2 tbsp Olive Oil, then add 2 tbsp Olive Oil more.',
    );
    expect(el.querySelector('.ingredient-uses')?.textContent).toBe(
      'Divided: 2 tbsp Olive Oil (step 2) · 2 tbsp Olive Oil (step 2)',
    );
    expect(component.groupedSteps()[0].items[0].ingredientCallouts.map((c) => c.id)).toEqual([
      'ing-0',
    ]);
  });

  it('emits publishRequested when publish button is clicked', () => {
    let emitted = false;
    component.publishRequested.subscribe(() => {
      emitted = true;
    });

    component.onPublish();
    expect(emitted).toBe(true);
  });
});
