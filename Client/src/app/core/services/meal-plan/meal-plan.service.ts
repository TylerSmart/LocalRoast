import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  ApplyMealPlanTemplateRequest,
  CookTonightRequest,
  CreateMealPlanEntryRequest,
  CreateTemplateFromWeekRequest,
  MealPlanEntryDto,
  MealPlanRangeResponseDto,
  MealPlanTemplateDetailDto,
  MealPlanTemplateSummaryDto,
  MealPlanWeekDto,
  MoveMealPlanEntryRequest,
  UpdateMealPlanEntryRequest,
  UpsertMealPlanTemplateRequest,
} from '../../models/meal-plan.models';

@Service()
export class MealPlanService {
  private readonly http = inject(HttpClient);

  getWeek(weekStartIso?: string): Observable<MealPlanWeekDto> {
    let params = new HttpParams();
    if (weekStartIso) {
      params = params.set('weekStart', weekStartIso);
    }
    return this.http.get<MealPlanWeekDto>(apiUrl('/meal-plans/week'), { params });
  }

  getRange(
    startDateIso: string,
    endDateIso: string,
    includeOnDeck = true,
  ): Observable<MealPlanRangeResponseDto> {
    const params = new HttpParams()
      .set('startDate', startDateIso)
      .set('endDate', endDateIso)
      .set('includeOnDeck', String(includeOnDeck));

    return this.http.get<MealPlanRangeResponseDto>(apiUrl('/meal-plans'), { params });
  }

  getEntryById(id: string): Observable<MealPlanEntryDto> {
    return this.http.get<MealPlanEntryDto>(apiUrl(`/meal-plans/entries/${id}`));
  }

  createEntry(request: CreateMealPlanEntryRequest): Observable<MealPlanEntryDto> {
    return this.http.post<MealPlanEntryDto>(apiUrl('/meal-plans/entries'), request);
  }

  updateEntry(id: string, request: UpdateMealPlanEntryRequest): Observable<MealPlanEntryDto> {
    return this.http.put<MealPlanEntryDto>(apiUrl(`/meal-plans/entries/${id}`), request);
  }

  moveEntry(id: string, request: MoveMealPlanEntryRequest): Observable<MealPlanEntryDto> {
    return this.http.post<MealPlanEntryDto>(apiUrl(`/meal-plans/entries/${id}/move`), request);
  }

  cookTonight(id: string, request?: CookTonightRequest): Observable<MealPlanEntryDto> {
    return this.http.post<MealPlanEntryDto>(
      apiUrl(`/meal-plans/entries/${id}/cook-tonight`),
      request ?? {},
    );
  }

  deleteEntry(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/meal-plans/entries/${id}`));
  }

  clearWeek(weekStartIso: string, includeOnDeck = false): Observable<void> {
    const params = new HttpParams()
      .set('weekStart', weekStartIso)
      .set('includeOnDeck', String(includeOnDeck));

    return this.http.delete<void>(apiUrl('/meal-plans/week'), { params });
  }

  getTemplates(): Observable<MealPlanTemplateSummaryDto[]> {
    return this.http.get<MealPlanTemplateSummaryDto[]>(apiUrl('/meal-plan-templates'));
  }

  getTemplateById(id: string): Observable<MealPlanTemplateDetailDto> {
    return this.http.get<MealPlanTemplateDetailDto>(apiUrl(`/meal-plan-templates/${id}`));
  }

  createTemplate(request: UpsertMealPlanTemplateRequest): Observable<MealPlanTemplateDetailDto> {
    return this.http.post<MealPlanTemplateDetailDto>(apiUrl('/meal-plan-templates'), request);
  }

  createTemplateFromWeek(
    request: CreateTemplateFromWeekRequest,
  ): Observable<MealPlanTemplateDetailDto> {
    return this.http.post<MealPlanTemplateDetailDto>(
      apiUrl('/meal-plan-templates/from-week'),
      request,
    );
  }

  updateTemplate(
    id: string,
    request: UpsertMealPlanTemplateRequest,
  ): Observable<MealPlanTemplateDetailDto> {
    return this.http.put<MealPlanTemplateDetailDto>(apiUrl(`/meal-plan-templates/${id}`), request);
  }

  deleteTemplate(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/meal-plan-templates/${id}`));
  }

  applyTemplate(
    templateId: string,
    request: ApplyMealPlanTemplateRequest,
  ): Observable<MealPlanWeekDto> {
    return this.http.post<MealPlanWeekDto>(
      apiUrl(`/meal-plan-templates/${templateId}/apply`),
      request,
    );
  }
}
