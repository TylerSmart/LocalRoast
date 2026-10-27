import { HttpClient, HttpParams } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import {
  IngredientMatchRequest,
  IngredientMatchResultDto,
  RecipeAttachmentDto,
  RecipeDetailDto,
  RecipeQueryParameters,
  RecipeSummaryDto,
  UpdateRecipeOrganizationRequest,
  UpsertRecipeRequest,
} from '../../models/recipe.models';

@Service()
export class RecipeService {
  private readonly http = inject(HttpClient);

  getRecipes(query?: RecipeQueryParameters): Observable<RecipeSummaryDto[]> {
    let params = new HttpParams();
    if (query?.search) {
      params = params.set('search', query.search);
    }
    if (query?.status) {
      params = params.set('status', query.status);
    }
    if (query?.difficulty) {
      params = params.set('difficulty', query.difficulty);
    }
    if (query?.maxTotalTimeMinutes !== undefined && query?.maxTotalTimeMinutes !== null) {
      params = params.set('maxTotalTimeMinutes', query.maxTotalTimeMinutes.toString());
    }
    if (query?.foodItemId) {
      params = params.set('foodItemId', query.foodItemId);
    }
    if (query?.collectionId) {
      params = params.set('collectionId', query.collectionId);
    }
    if (query?.tagIds && query.tagIds.length > 0) {
      for (const tagId of query.tagIds) {
        params = params.append('tagIds', tagId);
      }
    }

    return this.http.get<RecipeSummaryDto[]>(apiUrl('/recipes'), { params });
  }

  getRecipeById(id: string): Observable<RecipeDetailDto> {
    return this.http.get<RecipeDetailDto>(apiUrl(`/recipes/${id}`));
  }

  createRecipe(request: UpsertRecipeRequest): Observable<RecipeDetailDto> {
    return this.http.post<RecipeDetailDto>(apiUrl('/recipes'), request);
  }

  updateRecipe(id: string, request: UpsertRecipeRequest): Observable<RecipeDetailDto> {
    return this.http.put<RecipeDetailDto>(apiUrl(`/recipes/${id}`), request);
  }

  deleteRecipe(id: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/recipes/${id}`));
  }

  updateRecipeOrganization(
    id: string,
    request: UpdateRecipeOrganizationRequest,
  ): Observable<RecipeDetailDto> {
    return this.http.put<RecipeDetailDto>(apiUrl(`/recipes/${id}/organization`), request);
  }

  matchRecipesByIngredients(
    request: IngredientMatchRequest,
  ): Observable<IngredientMatchResultDto[]> {
    return this.http.post<IngredientMatchResultDto[]>(
      apiUrl('/recipes/match-ingredients'),
      request,
    );
  }
  addRecipeAttachment(
    recipeId: string,
    file: File,
    label?: string,
  ): Observable<RecipeAttachmentDto> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    if (label) {
      formData.append('label', label);
    }
    return this.http.post<RecipeAttachmentDto>(
      apiUrl(`/recipes/${recipeId}/attachments`),
      formData,
    );
  }

  deleteRecipeAttachment(recipeId: string, attachmentId: string): Observable<void> {
    return this.http.delete<void>(apiUrl(`/recipes/${recipeId}/attachments/${attachmentId}`));
  }
}
