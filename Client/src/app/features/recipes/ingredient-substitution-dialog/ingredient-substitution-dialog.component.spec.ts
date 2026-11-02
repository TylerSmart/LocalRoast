import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of } from 'rxjs';
import { IngredientSubstitutionResponseDto } from '../../../core/models/ai-helper.models';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import {
  IngredientSubstitutionDialogComponent,
  IngredientSubstitutionDialogData,
} from './ingredient-substitution-dialog.component';

describe('IngredientSubstitutionDialogComponent', () => {
  let component: IngredientSubstitutionDialogComponent;
  let fixture: ComponentFixture<IngredientSubstitutionDialogComponent>;
  let mockDialogRef: { close: ReturnType<typeof vi.fn> };

  const mockResponse: IngredientSubstitutionResponseDto = {
    recipeId: 'rec-1',
    recipeTitle: 'Fluffy Pancakes',
    recipeIngredientId: 'ing-1',
    ingredientDisplayName: 'Buttermilk',
    scaledAmountDisplay: '1 cup',
    modelUsed: 'llama3',
    substitutions: [
      {
        label: 'Milk + Lemon Juice',
        ratioAndAmount: '1 cup milk + 1 tbsp lemon juice',
        culinaryNotes: 'Let stand 5 mins',
        flavorOrTextureImpact: 'Matches acidity and tenderizes crumb',
      },
    ],
  };

  const mockAiHelperService = {
    suggestSubstitutions: vi.fn().mockReturnValue(of(mockResponse)),
  };

  const dialogData: IngredientSubstitutionDialogData = {
    recipeId: 'rec-1',
    recipeIngredientId: 'ing-1',
    ingredientDisplayName: 'Buttermilk',
    scaledAmountDisplay: '1 cup',
    targetServings: 4,
  };

  beforeEach(async () => {
    mockDialogRef = { close: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [IngredientSubstitutionDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: dialogData },
        { provide: MatDialogRef, useValue: mockDialogRef },
        { provide: AiKitchenHelperService, useValue: mockAiHelperService },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(IngredientSubstitutionDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('automatically requests substitutions on init and renders cards', () => {
    expect(component).toBeTruthy();
    expect(mockAiHelperService.suggestSubstitutions).toHaveBeenCalledWith({
      recipeId: 'rec-1',
      recipeIngredientId: 'ing-1',
      targetServings: 4,
      dietaryOrPantryNote: null,
    });
    expect(component.response()).toEqual(mockResponse);
    expect(component.isLoading()).toBe(false);

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Milk + Lemon Juice');
    expect(text).toContain('Matches acidity');
  });

  it('allows copying substitution to clipboard', () => {
    const writeTextSpy = vi.fn();
    Object.assign(navigator, {
      clipboard: {
        writeText: writeTextSpy,
      },
    });

    component.copySubstitution(mockResponse.substitutions[0], 0);
    expect(writeTextSpy).toHaveBeenCalled();
    expect(component.copiedIndex()).toBe(0);
  });
});
