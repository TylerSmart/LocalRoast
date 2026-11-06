import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  AiHelperStatusDto,
  AskKitchenQuestionRequest,
  DraftMealPlanResponseDto,
  DraftWeeklyMealPlanRequest,
  IngredientSubstitutionResponseDto,
  KitchenAnswerDto,
  SuggestIngredientSubstitutionsRequest,
} from '../../models/ai-helper.models';
import { AiKitchenHelperService } from './ai-kitchen-helper.service';

describe('AiKitchenHelperService', () => {
  let service: AiKitchenHelperService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), AiKitchenHelperService],
    });

    service = TestBed.inject(AiKitchenHelperService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches status and updates signal', async () => {
    const mockStatus: AiHelperStatusDto = {
      isAvailable: true,
      provider: 'Ollama',
      textModel: 'llama3',
      latencyMs: 35,
    };

    expect(service.status()).toBeNull();
    expect(service.isAvailable()).toBe(false);

    const promise = firstValueFrom(service.checkStatus());
    const req = httpMock.expectOne('/api/ai-helpers/status');
    expect(req.request.method).toBe('GET');
    req.flush(mockStatus);

    const result = await promise;
    expect(result).toEqual(mockStatus);
    expect(service.status()).toEqual(mockStatus);
    expect(service.isAvailable()).toBe(true);
  });

  it('handles error gracefully when probing status', async () => {
    const promise = firstValueFrom(service.checkStatus());
    const req = httpMock.expectOne('/api/ai-helpers/status');
    expect(req.request.method).toBe('GET');
    req.flush({ detail: 'Host unreachable' }, { status: 503, statusText: 'Service Unavailable' });

    const result = await promise;
    expect(result.isAvailable).toBe(false);
    expect(result.unavailableReason).toBe('Host unreachable');
    expect(service.isAvailable()).toBe(false);
  });

  it('posts substitution request', async () => {
    const request: SuggestIngredientSubstitutionsRequest = {
      recipeId: 'rec-1',
      recipeIngredientId: 'ing-1',
      targetServings: 4,
    };
    const mockResponse: IngredientSubstitutionResponseDto = {
      recipeId: 'rec-1',
      recipeTitle: 'Tacos',
      recipeIngredientId: 'ing-1',
      ingredientDisplayName: 'Sour Cream',
      scaledAmountDisplay: '1 cup',
      modelUsed: 'llama3',
      substitutions: [
        {
          label: 'Greek Yogurt',
          ratioAndAmount: '1:1 ratio',
          culinaryNotes: 'Tangy',
          flavorOrTextureImpact: 'Creamy',
        },
      ],
    };

    const promise = firstValueFrom(service.suggestSubstitutions(request));
    const req = httpMock.expectOne('/api/ai-helpers/ingredient-substitutions');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(mockResponse);

    const result = await promise;
    expect(result).toEqual(mockResponse);
  });

  it('posts draft meal plan request', async () => {
    const request: DraftWeeklyMealPlanRequest = {
      prompt: 'Quick meals',
      mealCount: 3,
    };
    const mockResponse: DraftMealPlanResponseDto = {
      weekStartDate: '2026-11-02',
      weekEndDate: '2026-11-08',
      modelUsed: 'llama3',
      consideredCookbookRecipeCount: 10,
      candidates: [],
    };

    const promise = firstValueFrom(service.draftWeeklyMealPlan(request));
    const req = httpMock.expectOne('/api/ai-helpers/draft-meal-plan');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(mockResponse);

    const result = await promise;
    expect(result).toEqual(mockResponse);
  });

  it('posts a cooking question with its recipe context', async () => {
    const request: AskKitchenQuestionRequest = {
      question: 'What is julienne?',
      recipeId: 'recipe-1',
      recipeStepId: 'step-1',
    };
    const response: KitchenAnswerDto = {
      answer: 'Cut into thin matchsticks.',
      modelUsed: 'llama3',
    };

    const promise = firstValueFrom(service.askKitchenQuestion(request));
    const req = httpMock.expectOne('/api/ai-helpers/kitchen-question');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(response);

    expect(await promise).toEqual(response);
  });
});
