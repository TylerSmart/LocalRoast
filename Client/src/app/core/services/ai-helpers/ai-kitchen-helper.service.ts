import { HttpClient } from '@angular/common/http';
import { Service, computed, inject, signal } from '@angular/core';
import { Observable, catchError, of, tap } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  AiHelperStatusDto,
  AskKitchenQuestionRequest,
  DraftMealPlanResponseDto,
  DraftWeeklyMealPlanRequest,
  IngredientSubstitutionResponseDto,
  KitchenAnswerDto,
  SuggestIngredientSubstitutionsRequest,
} from '../../models/ai-helper.models';

const STATUS_CACHE_TTL_MS = 30_000;

@Service()
export class AiKitchenHelperService {
  private readonly http = inject(HttpClient);

  readonly status = signal<AiHelperStatusDto | null>(null);
  readonly isChecking = signal<boolean>(false);
  readonly isAvailable = computed(() => this.status()?.isAvailable ?? false);

  private lastCheckedTimestamp = 0;

  checkStatus(force = false): Observable<AiHelperStatusDto> {
    const now = Date.now();
    const current = this.status();

    if (!force && current && now - this.lastCheckedTimestamp < STATUS_CACHE_TTL_MS) {
      return of(current);
    }

    this.isChecking.set(true);
    return this.http.get<AiHelperStatusDto>(apiUrl('/ai-helpers/status')).pipe(
      tap((res) => {
        this.status.set(res);
        this.lastCheckedTimestamp = Date.now();
        this.isChecking.set(false);
      }),
      catchError((err) => {
        const fallback: AiHelperStatusDto = {
          isAvailable: false,
          provider: 'Ollama',
          latencyMs: 0,
          unavailableReason: err?.error?.detail ?? 'Unable to connect to AI helper service.',
        };
        this.status.set(fallback);
        this.lastCheckedTimestamp = Date.now();
        this.isChecking.set(false);
        return of(fallback);
      }),
    );
  }

  suggestSubstitutions(
    request: SuggestIngredientSubstitutionsRequest,
  ): Observable<IngredientSubstitutionResponseDto> {
    return this.http.post<IngredientSubstitutionResponseDto>(
      apiUrl('/ai-helpers/ingredient-substitutions'),
      request,
    );
  }

  askKitchenQuestion(request: AskKitchenQuestionRequest): Observable<KitchenAnswerDto> {
    return this.http.post<KitchenAnswerDto>(apiUrl('/ai-helpers/kitchen-question'), request);
  }

  draftWeeklyMealPlan(request: DraftWeeklyMealPlanRequest): Observable<DraftMealPlanResponseDto> {
    return this.http.post<DraftMealPlanResponseDto>(apiUrl('/ai-helpers/draft-meal-plan'), request);
  }
}
