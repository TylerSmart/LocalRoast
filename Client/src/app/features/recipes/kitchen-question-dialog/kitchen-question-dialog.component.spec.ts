import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Observable, Subject, of, throwError } from 'rxjs';
import { KitchenAnswerDto } from '../../../core/models/ai-helper.models';
import { AiKitchenHelperService } from '../../../core/services/ai-helpers/ai-kitchen-helper.service';
import {
  KitchenQuestionDialogComponent,
  KitchenQuestionDialogData,
} from './kitchen-question-dialog.component';

describe('KitchenQuestionDialogComponent', () => {
  let fixture: ComponentFixture<KitchenQuestionDialogComponent>;
  let component: KitchenQuestionDialogComponent;
  let data: KitchenQuestionDialogData;
  let closed: Subject<void>;
  let response: Subject<KitchenAnswerDto>;
  let aiHelper: { askKitchenQuestion: ReturnType<typeof vi.fn> };
  let dialogRef: { close: ReturnType<typeof vi.fn>; beforeClosed: () => Observable<void> };

  const answer: KitchenAnswerDto = {
    answer: 'Cut the vegetables into thin matchsticks.',
    modelUsed: 'test-model',
  };

  beforeEach(async () => {
    data = {};
    closed = new Subject<void>();
    response = new Subject<KitchenAnswerDto>();
    aiHelper = { askKitchenQuestion: vi.fn().mockReturnValue(response.asObservable()) };
    dialogRef = {
      close: vi.fn(() => closed.next()),
      beforeClosed: () => closed.asObservable(),
    };

    await TestBed.configureTestingModule({
      imports: [KitchenQuestionDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: AiKitchenHelperService, useValue: aiHelper },
      ],
    }).compileComponents();
  });

  function mount(): void {
    fixture = TestBed.createComponent(KitchenQuestionDialogComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('validates and asks a general question, then clears a stale answer on edit', () => {
    mount();
    component.question.setValue('   ');
    component.ask();
    expect(aiHelper.askKitchenQuestion).not.toHaveBeenCalled();

    component.question.setValue('x'.repeat(601));
    component.ask();
    expect(aiHelper.askKitchenQuestion).not.toHaveBeenCalled();

    component.question.setValue('  What is julienne?  ');
    component.ask();
    expect(aiHelper.askKitchenQuestion).toHaveBeenCalledWith({
      question: 'What is julienne?',
      recipeId: undefined,
      recipeIngredientId: undefined,
      recipeStepId: undefined,
    });
    expect(component.isLoading()).toBe(true);

    component.ask();
    expect(aiHelper.askKitchenQuestion).toHaveBeenCalledTimes(1);
    response.next(answer);
    response.complete();
    fixture.detectChanges();
    expect(component.isLoading()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain(answer.answer);
    expect(fixture.nativeElement.textContent).toContain('Verify important food-safety advice');

    component.question.setValue('How do I peel a kiwi?');
    expect(component.answer()).toBeNull();
  });

  it('submits through the form without a native page navigation', () => {
    mount();
    component.question.setValue('How do I peel a kiwi?');

    const form: HTMLFormElement = fixture.nativeElement.querySelector('form');
    const event = new Event('submit', { bubbles: true, cancelable: true });
    expect(form.dispatchEvent(event)).toBe(false);
    expect(aiHelper.askKitchenQuestion).toHaveBeenCalledWith(
      expect.objectContaining({ question: 'How do I peel a kiwi?' }),
    );
  });

  it('automatically asks about the selected ingredient and carries its context', () => {
    Object.assign(data, {
      recipeId: 'recipe-1',
      recipeTitle: 'Kiwi Salad',
      recipeIngredientId: 'ingredient-1',
      ingredientName: 'Kiwi',
    });
    mount();

    expect(aiHelper.askKitchenQuestion).toHaveBeenCalledWith(
      expect.objectContaining({
        question: 'What is this ingredient, and how should I prepare it for this recipe?',
        recipeId: 'recipe-1',
        recipeIngredientId: 'ingredient-1',
      }),
    );
    expect(fixture.nativeElement.textContent).toContain('Kiwi');
  });

  it('automatically asks about the selected step', () => {
    Object.assign(data, { recipeId: 'recipe-1', recipeStepId: 'step-1', stepNumber: 1 });
    mount();

    expect(aiHelper.askKitchenQuestion).toHaveBeenCalledWith(
      expect.objectContaining({
        question: 'Explain this step and any cooking techniques it uses.',
        recipeStepId: 'step-1',
      }),
    );
  });

  it('shows provider errors and retries without keeping a stale answer', () => {
    aiHelper.askKitchenQuestion
      .mockReturnValueOnce(
        throwError(
          () =>
            new HttpErrorResponse({
              status: 503,
              error: { detail: 'Host offline' },
            }),
        ),
      )
      .mockReturnValueOnce(of(answer));
    mount();
    component.question.setValue('What is julienne?');
    component.ask();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alert"]').textContent).toContain(
      'Host offline',
    );
    expect(component.answer()).toBeNull();

    component.ask();
    fixture.detectChanges();
    expect(component.errorMessage()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain(answer.answer);
  });

  it('renders model output as text, not markup', () => {
    aiHelper.askKitchenQuestion.mockReturnValue(
      of({ answer: '<img src=x onerror=alert(1)>', modelUsed: 'test-model' }),
    );
    Object.assign(data, { recipeId: 'recipe-1', recipeStepId: 'step-1' });
    mount();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.answer-text').textContent).toContain('<img');
    expect(fixture.nativeElement.querySelector('.answer-text img')).toBeNull();
  });

  it('cancels a pending request when the dialog closes', () => {
    let cancelled = false;
    aiHelper.askKitchenQuestion.mockReturnValue(
      new Observable<KitchenAnswerDto>(() => {
        return () => {
          cancelled = true;
        };
      }),
    );
    Object.assign(data, { recipeId: 'recipe-1', recipeStepId: 'step-1' });
    mount();
    expect(component.isLoading()).toBe(true);
    component.close();
    expect(cancelled).toBe(true);
    expect(dialogRef.close).toHaveBeenCalled();
  });
});
