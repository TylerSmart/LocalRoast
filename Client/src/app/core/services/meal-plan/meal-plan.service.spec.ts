import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import {
  CreateMealPlanEntryRequest,
  MealPlanEntryDto,
  MealPlanWeekDto,
  UpsertMealPlanTemplateRequest,
} from '../../models/meal-plan.models';
import { MealPlanService } from './meal-plan.service';

describe('MealPlanService', () => {
  let service: MealPlanService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), MealPlanService],
    });

    service = TestBed.inject(MealPlanService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches week meal plan via GET /api/meal-plans/week', async () => {
    const mockWeek: MealPlanWeekDto = {
      weekStartDate: '2026-11-02',
      weekEndDate: '2026-11-08',
      days: [],
      onDeckEntries: [],
    };

    const promise = firstValueFrom(service.getWeek('2026-11-02'));
    const req = httpMock.expectOne('/api/meal-plans/week?weekStart=2026-11-02');
    expect(req.request.method).toBe('GET');
    req.flush(mockWeek);

    await expect(promise).resolves.toEqual(mockWeek);
  });

  it('creates entry via POST /api/meal-plans/entries', async () => {
    const request: CreateMealPlanEntryRequest = {
      entryType: 'Recipe',
      isOnDeck: true,
      weekStartDate: '2026-11-02',
      recipeId: 'rec-1',
    };

    const mockEntry: MealPlanEntryDto = {
      id: 'entry-1',
      weekStartDate: '2026-11-02',
      isOnDeck: true,
      scheduledDate: null,
      slot: null,
      sortOrder: 0,
      entryType: 'Recipe',
      isLeftovers: false,
      targetServings: 4,
      displayTitle: 'Chili',
      customTitle: null,
      notes: null,
      sourceMealPlanEntryId: null,
      recipe: null,
      createdAt: '2026-11-02T00:00:00Z',
      createdBy: 'chef',
      modifiedAt: '2026-11-02T00:00:00Z',
      modifiedBy: 'chef',
    };

    const promise = firstValueFrom(service.createEntry(request));
    const req = httpMock.expectOne('/api/meal-plans/entries');
    expect(req.request.method).toBe('POST');
    req.flush(mockEntry);

    await expect(promise).resolves.toEqual(mockEntry);
  });

  it('moves entry via POST /api/meal-plans/entries/:id/move', async () => {
    const promise = firstValueFrom(
      service.moveEntry('entry-1', {
        isOnDeck: false,
        scheduledDate: '2026-11-04',
        slot: 'Dinner',
      }),
    );
    const req = httpMock.expectOne('/api/meal-plans/entries/entry-1/move');
    expect(req.request.method).toBe('POST');
    req.flush({} as MealPlanEntryDto);

    await expect(promise).resolves.toBeDefined();
  });

  it('cooks tonight via POST /api/meal-plans/entries/:id/cook-tonight', async () => {
    const promise = firstValueFrom(service.cookTonight('entry-1', { slot: 'Dinner' }));
    const req = httpMock.expectOne('/api/meal-plans/entries/entry-1/cook-tonight');
    expect(req.request.method).toBe('POST');
    req.flush({} as MealPlanEntryDto);

    await expect(promise).resolves.toBeDefined();
  });

  it('deletes entry via DELETE /api/meal-plans/entries/:id', async () => {
    const promise = firstValueFrom(service.deleteEntry('entry-1'));
    const req = httpMock.expectOne('/api/meal-plans/entries/entry-1');
    expect(req.request.method).toBe('DELETE');
    req.flush(null);

    await expect(promise).resolves.toBeNull();
  });

  it('creates and applies templates', async () => {
    const reqCreate: UpsertMealPlanTemplateRequest = { name: 'Quick Week' };
    const createPromise = firstValueFrom(service.createTemplate(reqCreate));
    const req1 = httpMock.expectOne('/api/meal-plan-templates');
    expect(req1.request.method).toBe('POST');
    req1.flush({ id: 'tpl-1', name: 'Quick Week' });
    await expect(createPromise).resolves.toBeDefined();

    const applyPromise = firstValueFrom(
      service.applyTemplate('tpl-1', { targetWeekStartDate: '2026-11-09' }),
    );
    const req2 = httpMock.expectOne('/api/meal-plan-templates/tpl-1/apply');
    expect(req2.request.method).toBe('POST');
    req2.flush({ weekStartDate: '2026-11-09' });
    await expect(applyPromise).resolves.toBeDefined();
  });
});
